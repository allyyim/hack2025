using System.ClientModel;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using Azure.Identity;

class Program
{
    static async Task Main(string[] args)
    {
        // Start HTTP server in background
        var server = new HttpListener();
        server.Prefixes.Add("http://localhost:5000/");
        server.Start();
        Console.WriteLine("HTTP Server started on http://localhost:5000/");

        // Handle requests in a background task
        _ = Task.Run(async () => await HandleHttpRequests(server));

        // Keep the application running
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }

    static async Task RunApplication()
    {
        #region Azure OpenAI Setup
        // Azure OpenAI Client Setup
        var deploymentName = "gpt-5-nano";
        var endpointUrl = "https://yimal-mfssuu7z-swedencentral.openai.azure.com/";
        var key = "";

        var client = new AzureOpenAIClient(
            new Uri(endpointUrl), 
            new DefaultAzureCredential()
        );      

        var chatClient = client.GetChatClient(deploymentName);
        #endregion

        #region Azure DevOps Setup
        // Azure DevOps API Setup
        Console.WriteLine("Fetching Pull Request Comments...");
        string organization = "One";
        string repositoryName = "EngSys-MDA-AMCS";
        string personalAccessToken = "6YDTIVeyHGnRp03Gr3KDMGQF0KbK5TsG4UpmBZnoAyHDIlAe1fMoJQQJ99BLACAAAAAAArohAAASAZDO1fPo";
        string startDate = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ssZ");

        // Azure DevOps API URL to fetch active PRs created in the last 30 days
        string pullRequestsUrl = $"https://msazure.visualstudio.com/DefaultCollection/{organization}/_apis/git/repositories/{repositoryName}/pullRequests?searchCriteria.status=completed&searchCriteria.creationDate={startDate}&api-version=7.1-preview.1";

        // Fetch PR IDs dynamically
        List<int> pullRequestIds = await FetchPullRequestIdsAsync(pullRequestsUrl, personalAccessToken);

        if (pullRequestIds.Count == 0)
        {
            Console.WriteLine("No active pull requests found.");
            return;
        }
        #endregion

        #region Process Pull Requests
        // Open the markdown file for writing (append mode)
        string importantCommentsMarkdownPath = Path.Combine(AppContext.BaseDirectory, $"important_comments.md");

        // Write the header only once at the beginning of the file
        if (!File.Exists(importantCommentsMarkdownPath) || new FileInfo(importantCommentsMarkdownPath).Length == 0) // Check if the file is empty or doesn't exist
        {
            using (StreamWriter markdownWriter = new StreamWriter(importantCommentsMarkdownPath, append: true))
            {
                markdownWriter.WriteLine("# Important Comments from PRs from the last 30 Days");
                markdownWriter.WriteLine();
            }
        }

        // Process pull requests
        foreach (var batch in pullRequestIds.Chunk(5)) // Process in batches of 5
        {
            foreach (var pullRequestId in batch)
            {
                string prLink = $"https://msazure.visualstudio.com/{organization}/_git/{repositoryName}/pullrequest/{pullRequestId}";
                string threadsUrl = $"https://msazure.visualstudio.com/DefaultCollection/{organization}/_apis/git/repositories/{repositoryName}/pullRequests/{pullRequestId}/threads?api-version=7.1-preview.1";
                string commentsLogPath = Path.Combine(AppContext.BaseDirectory, $"comments_log_pr_{pullRequestId}.json");

                try
                {
                    using (HttpClient httpClient = new HttpClient())
                    {
                        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{personalAccessToken}"));
                        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

                        Console.WriteLine($"Fetching threads for PR #{pullRequestId}");

                        HttpResponseMessage threadsResponse = await httpClient.GetAsync(threadsUrl);
                        if (!threadsResponse.IsSuccessStatusCode)
                        {
                            Console.WriteLine($"Error fetching threads for PR #{pullRequestId}: {threadsResponse.StatusCode} - {threadsResponse.ReasonPhrase}");
                            continue;
                        }

                        string threadsResponseBody = await threadsResponse.Content.ReadAsStringAsync();

                        // Log the JSON of all comments to a file
                        await File.WriteAllTextAsync(commentsLogPath, threadsResponseBody);

                        var threads = JsonSerializer.Deserialize<ThreadResponse>(threadsResponseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        // Track if any content is written for this PR
                        bool hasContentForPR = false;

                        using (StreamWriter markdownWriter = new StreamWriter(importantCommentsMarkdownPath, append: true)) // Enable append mode
                        {
                            foreach (var thread in threads.Value)
                            {
                                foreach (var comment in thread.Comments)
                                {
                                    ProcessComment(comment, thread, chatClient, markdownWriter, prLink, ref hasContentForPR, pullRequestId);
                                }
                            }

                            // Close the <details> section after processing all comments for the PR
                            CloseMarkdownSection(markdownWriter, hasContentForPR);

                            if (!hasContentForPR)
                            {
                                Console.WriteLine($"No important comments found for PR #{pullRequestId}. Skipping markdown output.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to fetch comments for PR #{pullRequestId}: {ex.Message}");
                }
            }

            // Wait for 10 minutes before processing the next batch
            Console.WriteLine("Waiting for 10 minutes before processing the next batch...");
            await Task.Delay(TimeSpan.FromMinutes(10));
        }
        #endregion
    }

    static async Task HandleHttpRequests(HttpListener server)
    {
        while (server.IsListening)
        {
            try
            {
                HttpListenerContext context = await server.GetContextAsync();
                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;

                if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/api/fetch-comments")
                {
                    // Run the application
                    try
                    {
                        await RunApplication();
                        response.StatusCode = 200;
                        byte[] buffer = Encoding.UTF8.GetBytes("Comments fetched successfully");
                        response.ContentLength64 = buffer.Length;
                        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    }
                    catch (Exception ex)
                    {
                        response.StatusCode = 500;
                        byte[] buffer = Encoding.UTF8.GetBytes($"Error: {ex.Message}");
                        response.ContentLength64 = buffer.Length;
                        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    }
                }
                else if (request.Url.AbsolutePath == "/")
                {
                    // Serve index.html - look in project root (3 levels up from bin/Debug/net8.0)
                    string indexPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "index.html");
                    indexPath = Path.GetFullPath(indexPath);
                    if (File.Exists(indexPath))
                    {
                        string html = File.ReadAllText(indexPath);
                        response.ContentType = "text/html";
                        response.StatusCode = 200;
                        byte[] buffer = Encoding.UTF8.GetBytes(html);
                        response.ContentLength64 = buffer.Length;
                        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    }
                    else
                    {
                        response.StatusCode = 404;
                        byte[] buffer = Encoding.UTF8.GetBytes($"index.html not found at {indexPath}");
                        response.ContentLength64 = buffer.Length;
                        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    }
                }
                else
                {
                    response.StatusCode = 404;
                    byte[] buffer = Encoding.UTF8.GetBytes("404 Not Found");
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                }

                response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling request: {ex.Message}");
            }
        }
    }

    #region Helper Methods
    static async Task<List<int>> FetchPullRequestIdsAsync(string pullRequestsUrl, string personalAccessToken)
    {
        using (HttpClient httpClient = new HttpClient())
        {
            var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{personalAccessToken}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

            HttpResponseMessage response = await httpClient.GetAsync(pullRequestsUrl);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error fetching pull requests: {response.StatusCode} - {response.ReasonPhrase}");
                return new List<int>();
            }

            string responseBody = await response.Content.ReadAsStringAsync();
            var pullRequests = JsonSerializer.Deserialize<PullRequestResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return pullRequests?.Value?.Select(pr => pr.PullRequestId).ToList() ?? new List<int>();
        }
    }

    static void ProcessComment(Comment comment, Thread thread, ChatClient chatClient, StreamWriter markdownWriter, string prLink, ref bool hasContentForPR, int pullRequestId)
    {
        string content = comment.Content;

        // Skip comments containing specific phrases
        if (string.IsNullOrEmpty(content) ||
            content.Contains("Ownership Enforcer PME", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Diff coverage check", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("AI feedback", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("PR description", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Coverage", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("AI description", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Split the content into comment and reply
        var contentLines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string mainComment = contentLines.FirstOrDefault() ?? string.Empty;
        string reply = string.Join(" ", contentLines.Skip(1));

        // Prepare the input for the AI model
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are an AI assistant that extracts terms and definitions from comments and replies."),
            new UserChatMessage($@"
                Given the following comment and reply:
                Comment: {mainComment}
                Reply: {reply}

                Determine if the pairing is a troubleshooting step, interesting developer trick, or a definition of a term/concept.

                Then extract and classify the following into 3 different categories:
                1. Troubleshooting Step: A concise action or series of actions to resolve a specific issue (e.g., steps to debug a problem, fix a bug, or optimize performance).
                2. Term/Definition Defined: A specific term or definition mentioned in the comment or reply. Whether that be a system, tool, framework, library, design pattern, architecture, or any other technical term (e.g., ARM, GIG, MDM, GA).
                3. Interesting Developer Trick: A unique or clever technique used by developers to solve common problems (e.g., using a specific design pattern, leveraging a particular library, or employing a novel approach to coding challenges).

                For each category, provide the following output format:
                - If Term/Definition: Extract the term and provide its definition.
                - If Troubleshooting Step: Summarize the troubleshooting step.
                - If Interesting Developer Trick: Summarize the developer trick.
                If the comment and reply do not fit into any of these categories, respond with 'No definition found' or 'No content to extract'.

                Provide the output in the following format:
                Term: [Extracted Term]
                Definition: [Extracted Definition or Summary]
            ")
        };

        // Call the AI client to analyze the input
        var response = chatClient.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            Temperature = 1f,
            FrequencyPenalty = 0,
            PresencePenalty = 0
        }).Result;

        // Parse the AI response
        var aiResponse = response.Value.Content.Last().Text;

        // Extract term, definition, troubleshooting step, and developer trick
        string term = ExtractTermFromAIResponse(aiResponse);
        string definition = ExtractDefinitionFromAIResponse(aiResponse);
        string troubleshootingStep = ExtractTroubleshootingStep(aiResponse);
        string developerTrick = ExtractDeveloperTrick(aiResponse);

        // Skip entries with specific terms or definitions
        if (term.Contains("vote", StringComparison.OrdinalIgnoreCase) ||
            term.Contains("Branch", StringComparison.OrdinalIgnoreCase) ||
            term.Contains("Git", StringComparison.OrdinalIgnoreCase) ||
            term.Contains("PR description", StringComparison.OrdinalIgnoreCase) ||
            term.Contains("PR Assistant", StringComparison.OrdinalIgnoreCase) ||
            term.Contains("refs/", StringComparison.OrdinalIgnoreCase) ||
            term.Contains("No content to extract", StringComparison.OrdinalIgnoreCase) ||
            term.Contains("PRAssistant", StringComparison.OrdinalIgnoreCase)||
            definition.Contains("No definition", StringComparison.OrdinalIgnoreCase) ||
            definition.Contains("No content", StringComparison.OrdinalIgnoreCase) ||
            definition.Contains("No additional information", StringComparison.OrdinalIgnoreCase) ||
            developerTrick.Contains("No content to extract", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Skip entries with "Unknown..." or empty values
        if (term.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
            definition.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
            troubleshootingStep.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
            developerTrick.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Write each category to markdown if applicable
        if (!string.IsNullOrEmpty(troubleshootingStep))
        {
            WriteMarkdownHeaderIfNeeded(markdownWriter, ref hasContentForPR, pullRequestId, prLink);
            markdownWriter.WriteLine($"### Thread {thread.Id}, Comment {comment.Id}");
            markdownWriter.WriteLine();
            markdownWriter.WriteLine($"**Troubleshooting Step:** {troubleshootingStep}");
            markdownWriter.WriteLine();
        }

        if (!string.IsNullOrEmpty(term) && !string.IsNullOrEmpty(definition))
        {
            WriteMarkdownHeaderIfNeeded(markdownWriter, ref hasContentForPR, pullRequestId, prLink);
            markdownWriter.WriteLine($"### Thread {thread.Id}, Comment {comment.Id}");
            markdownWriter.WriteLine();
            markdownWriter.WriteLine($"**Term/Concept Defined:** {term}");
            markdownWriter.WriteLine($"**Definition:** {definition}");
            markdownWriter.WriteLine();
        }

        if (!string.IsNullOrEmpty(developerTrick))
        {
            WriteMarkdownHeaderIfNeeded(markdownWriter, ref hasContentForPR, pullRequestId, prLink);
            markdownWriter.WriteLine($"### Thread {thread.Id}, Comment {comment.Id}");
            markdownWriter.WriteLine();
            markdownWriter.WriteLine($"**Developer Trick:** {developerTrick}");
            markdownWriter.WriteLine();
        }
    }

   static void WriteMarkdownHeaderIfNeeded(StreamWriter markdownWriter, ref bool hasContentForPR, int pullRequestId, string prLink)
    {
        if (!hasContentForPR)
        {
            // Use HTML <details> and <summary> tags for collapsible sections
            markdownWriter.WriteLine($"<details>");
            markdownWriter.WriteLine($"<summary>PR {pullRequestId} - Link: <a href=\"{prLink}\">{prLink}</a></summary>");
            markdownWriter.WriteLine();
            markdownWriter.WriteLine("### Important Comments");
            markdownWriter.WriteLine();
            hasContentForPR = true;
        }
    }

    static void CloseMarkdownSection(StreamWriter markdownWriter, bool hasContentForPR)
    {
        if (hasContentForPR)
        {
            // Close the <details> tag to prevent nesting
            markdownWriter.WriteLine("</details>");
            markdownWriter.WriteLine();
        }
    }

    static string ExtractTermFromAIResponse(string aiResponse)
    {
        var match = Regex.Match(aiResponse, @"Term:\s*(.*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "Unknown Term";
    }

    static string ExtractDefinitionFromAIResponse(string aiResponse)
    {
        var match = Regex.Match(aiResponse, @"Definition:\s*(.*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "Unknown Definition";
    }

    static string ExtractTroubleshootingStep(string aiResponse)
    {
        var match = Regex.Match(aiResponse, @"Troubleshooting Step:\s*(.*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "Unknown Troubleshooting Step";
    }

    static string ExtractDeveloperTrick(string aiResponse)
    {
        var match = Regex.Match(aiResponse, @"Developer Trick:\s*(.*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "Unknown Developer Trick";
    }
    #endregion

    #region Data Models
    public class ThreadResponse
    {
        public Thread[] Value { get; set; }
    }

    public class Thread
    {
        public int Id { get; set; }
        public Comment[] Comments { get; set; }
    }

    public class Comment
    {
        public int Id { get; set; }
        public string Content { get; set; }
        public Author Author { get; set; }
        public DateTime PublishedDate { get; set; }
    }

    public class Author
    {
        public string Email { get; set; }
    }

    public class PullRequestResponse
    {
        public PullRequest[] Value { get; set; }
    }

    public class PullRequest
    {
        public int PullRequestId { get; set; }
    }
    #endregion
}