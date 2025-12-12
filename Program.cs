using System.ClientModel;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using System.Collections.Concurrent;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

class Program
{
    // --- Phase-0 stream catalog and subscriber storage ---
    public record StreamDescriptor(string Id, string Title, string Description, string SchemaUrl, string SampleUrl, string ConnectUrl, int FreshnessSeconds);

    static readonly List<StreamDescriptor> StreamCatalog = new()
    {
        new StreamDescriptor(
            "pr-comments-analysis",
            "PR Comments Analysis",
            "AI-extracted terms, definitions, troubleshooting steps and developer tricks from PR comments",
            "/api/streams/pr-comments-analysis/schema",
            "/api/streams/pr-comments-analysis/sample",
            "/streams/pr-comments-analysis/connect",
            60
        )
    };

    static readonly ConcurrentDictionary<string, List<HttpListenerResponse>> Subscribers = new();
    static DateTime LastFetchTime = DateTime.MinValue;
    static readonly object FetchLock = new object();

    static async Task Main(string[] args)
    {
        // Start HTTP server in background
        var server = new HttpListener();
        
        // Azure App Service requires binding to port from PORT env var (default 8080)
        // Local dev uses 5000
        string port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
        bool isAzure = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
        
        if (isAzure)
        {
            server.Prefixes.Add($"http://+:{port}/");
        }
        else
        {
            server.Prefixes.Add($"http://localhost:{port}/");
        }
        
        server.Start();
        Console.WriteLine($"HTTP Server started on port {port}");

        // Handle requests in a background task
        _ = Task.Run(async () => await HandleHttpRequests(server));

        // Keep the application running (Azure manages lifecycle, local waits for key)
        if (isAzure)
        {
            await Task.Delay(Timeout.Infinite);
        }
        else
        {
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }

    static async Task RunApplication()
    {
        #region Azure OpenAI Setup
        // Azure OpenAI Client Setup
        var deploymentName = "gpt-5-nano";
        var endpointUrl = "https://yimal-mfssuu7z-swedencentral.openai.azure.com/";

        var client = new AzureOpenAIClient(
            new Uri(endpointUrl), 
            new DefaultAzureCredential()
        );      

        var chatClient = client.GetChatClient(deploymentName);
        #endregion

        #region Azure DevOps Setup
        // Azure DevOps API Setup
        Console.WriteLine("Fetching Pull Request Comments...");
        Console.Out.Flush();
        string organization = "One";
        string repositoryName = "EngSys-MDA-AMCS";
        
        // Try environment variable first, then appsettings.json
        string? personalAccessToken = Environment.GetEnvironmentVariable("ADO_PAT");
        if (string.IsNullOrEmpty(personalAccessToken))
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .Build();
            personalAccessToken = config["AdoPat"];
        }
        if (string.IsNullOrEmpty(personalAccessToken))
        {
            throw new InvalidOperationException("ADO_PAT environment variable or appsettings.json AdoPat not set");
        }
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
        if (!File.Exists(importantCommentsMarkdownPath))
        {
            using (StreamWriter markdownWriter = new StreamWriter(importantCommentsMarkdownPath, append: false))
            {
                markdownWriter.WriteLine("# Important Comments from PRs from the last 30 Days");
                markdownWriter.WriteLine();
            }
        }

        // Process pull requests in parallel batches
        foreach (var batch in pullRequestIds.Chunk(5)) // Process in batches of 5
        {
            // Process all PRs in batch concurrently
            var batchTasks = batch.Select(prId => ProcessPRAsync(prId, organization, repositoryName, personalAccessToken, chatClient)).ToList();
            var results = await Task.WhenAll(batchTasks);

            // Write results sequentially (thread-safe)
            using (StreamWriter markdownWriter = new StreamWriter(importantCommentsMarkdownPath, append: true))
            {
                foreach (var prResult in results)
                {
                    if (prResult.HasContent)
                    {
                        markdownWriter.WriteLine(prResult.Content);
                    }
                    else if (prResult.PullRequestId > 0)
                    {
                        Console.WriteLine($"No important comments found for PR #{prResult.PullRequestId}. Skipping markdown output.");
                    }
                }
            }

            // Wait for 10 minutes before processing the next batch
            Console.WriteLine("Waiting for 10 minutes before processing the next batch...");
            await Task.Delay(TimeSpan.FromMinutes(10));
        }
        #endregion
    }

    // A short, one-time fetch + process run (no long delays). Used by /important-comments.
    static async Task FetchOnce()
    {
        // Delegate to RunApplication so there's a single implementation and console output.
        try
        {
            await RunApplication();
        }
        catch (Exception ex)
        {
            // Log errors for visibility
            Console.WriteLine($"FetchOnce error: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"  Inner: {ex.InnerException.Message}");
            }
        }
    }

    // Process a single PR: fetch threads and analyze comments in parallel
    static async Task<PRProcessResult> ProcessPRAsync(int pullRequestId, string organization, string repositoryName, string personalAccessToken, ChatClient chatClient)
    {
        string prLink = $"https://msazure.visualstudio.com/{organization}/_git/{repositoryName}/pullrequest/{pullRequestId}";
        string threadsUrl = $"https://msazure.visualstudio.com/DefaultCollection/{organization}/_apis/git/repositories/{repositoryName}/pullRequests/{pullRequestId}/threads?api-version=7.1-preview.1";

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
                    return new PRProcessResult { PullRequestId = pullRequestId, HasContent = false };
                }

                string threadsResponseBody = await threadsResponse.Content.ReadAsStringAsync();
                var threads = JsonSerializer.Deserialize<ThreadResponse>(threadsResponseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                // Process all comments in parallel and collect results
                var commentTasks = new List<Task<string>>();
                foreach (var thread in threads.Value)
                {
                    foreach (var comment in thread.Comments)
                    {
                        commentTasks.Add(ProcessCommentAsync(comment, thread, chatClient, prLink));
                    }
                }

                var commentResults = await Task.WhenAll(commentTasks);
                var markdownContent = string.Concat(commentResults.Where(c => !string.IsNullOrEmpty(c)));

                bool hasContent = !string.IsNullOrEmpty(markdownContent);

                string output = "";
                if (hasContent)
                {
                    output = $"<details>\n<summary>PR {pullRequestId} - Link: <a href=\"{prLink}\">{prLink}</a></summary>\n\n### Important Comments\n\n{markdownContent}</details>\n\n";
                }

                return new PRProcessResult 
                { 
                    PullRequestId = pullRequestId, 
                    HasContent = hasContent, 
                    Content = output 
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch comments for PR #{pullRequestId}: {ex.Message}");
            return new PRProcessResult { PullRequestId = pullRequestId, HasContent = false };
        }
    }

    // Process a single comment asynchronously
    static async Task<string> ProcessCommentAsync(Comment comment, Thread thread, ChatClient chatClient, string prLink)
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
            return "";
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

        // Call the AI client to analyze the input (non-blocking)
        var response = await chatClient.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            Temperature = 1f,
            FrequencyPenalty = 0,
            PresencePenalty = 0
        });

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
            return "";
        }

        // Skip entries with "Unknown..." or empty values
        if (term.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
            definition.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
            troubleshootingStep.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
            developerTrick.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        // Build markdown output
        var sb = new StringBuilder();
        sb.AppendLine($"### Thread {thread.Id}, Comment {comment.Id}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(troubleshootingStep))
        {
            sb.AppendLine($"**Troubleshooting Step:** {troubleshootingStep}");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(term) && !string.IsNullOrEmpty(definition))
        {
            sb.AppendLine($"**Term/Concept Defined:** {term}");
            sb.AppendLine($"**Definition:** {definition}");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(developerTrick))
        {
            sb.AppendLine($"**Developer Trick:** {developerTrick}");
            sb.AppendLine();
        }

        return sb.ToString();
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
                    // Serve index.html - in Azure it's in the same directory as the DLL
                    string indexPath = Path.Combine(AppContext.BaseDirectory, "index.html");
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
                    else if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/important-comments")
                    {
                        // Serve the important_comments.md from the same directory
                        string mdPath = Path.Combine(AppContext.BaseDirectory, "important_comments.md");
                        response.ContentType = "text/html";

                        // Only fetch if we haven't fetched in the last 5 minutes (debounce)
                        lock (FetchLock)
                        {
                            if (DateTime.UtcNow.Subtract(LastFetchTime).TotalMinutes >= 5)
                            {
                                LastFetchTime = DateTime.UtcNow;
                                // Kick off a short, one-time fetch in the background to populate the markdown
                                var fetchTask = Task.Run(() => FetchOnce());
                                // Wait up to 60 seconds for the fetch to complete
                                var completed = Task.WaitAny(new[] { fetchTask }, TimeSpan.FromSeconds(60));
                            }
                        }

                        if (File.Exists(mdPath))
                        {
                            string md = File.ReadAllText(mdPath);
                            // Simple rendering: wrap markdown in <pre> to preserve formatting
                            string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Important Comments</title></head><body style=\"font-family:Segoe UI, Tahoma, Geneva, Verdana, sans-serif;padding:20px;\"><pre style=\"white-space:pre-wrap;font-size:14px;\">{System.Net.WebUtility.HtmlEncode(md)}</pre></body></html>";
                            response.StatusCode = 200;
                            byte[] buffer = Encoding.UTF8.GetBytes(html);
                            response.ContentLength64 = buffer.Length;
                            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                        }
                        else
                        {
                            string body = "No important comments found yet. Try again in a few seconds if a fetch is in progress.";
                            string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Important Comments</title></head><body style=\"font-family:Segoe UI, Tahoma, Geneva, Verdana, sans-serif;padding:20px;\">{System.Net.WebUtility.HtmlEncode(body)}</body></html>";
                            response.StatusCode = 200;
                            byte[] buffer = Encoding.UTF8.GetBytes(html);
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

    public class PRProcessResult
    {
        public int PullRequestId { get; set; }
        public bool HasContent { get; set; }
        public string Content { get; set; } = "";
    }
    #endregion
}