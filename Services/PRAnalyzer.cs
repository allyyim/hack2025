using System.Text;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using ADOPrism.Models;

namespace ADOPrism.Services;

public class PRAnalyzer
{
    private readonly string _outputPath;
    private readonly AzureDevOpsService _adoService;
    private readonly CommentProcessor _commentProcessor;

    public PRAnalyzer(string outputPath)
    {
        _outputPath = outputPath;

        // Load configuration
        var personalAccessToken = LoadPersonalAccessToken();
        
        // Initialize services
        _adoService = new AzureDevOpsService(personalAccessToken, "One", "EngSys-MDA-AMCS");
        
        var client = new AzureOpenAIClient(
            new Uri("https://yimal-mfssuu7z-swedencentral.openai.azure.com/"),
            new DefaultAzureCredential()
        );
        var chatClient = client.GetChatClient("gpt-5-nano");
        _commentProcessor = new CommentProcessor(chatClient);
    }

    private string LoadPersonalAccessToken()
    {
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

        return personalAccessToken;
    }

    public async Task AnalyzePullRequestsAsync(int daysBack = 7, int maxPRs = 15)
    {
        Console.WriteLine("Fetching Pull Request Comments...");

        // Reset progress and indicate fetching phase
        ProgressTracker.Reset();
        Console.WriteLine($"[PROGRESS] Reset complete. Starting fetch phase...");
        
        // Small delay to ensure client catches 0/0 state
        await Task.Delay(500);
        
        ProgressTracker.TotalPRs = -1; // -1 indicates fetching in progress
        Console.WriteLine($"[PROGRESS] TotalPRs set to -1 (fetching)...");

        // Fetch PR IDs
        var pullRequestIds = await _adoService.FetchPullRequestIdsAsync(daysBack, maxPRs);

        if (pullRequestIds.Count == 0)
        {
            Console.WriteLine("No pull requests found.");
            return;
        }

        // Initialize output file
        InitializeOutputFile(daysBack);

        Console.WriteLine($"===== Found {pullRequestIds.Count} PRs to analyze =====");
        Console.WriteLine($"PR IDs: {string.Join(", ", pullRequestIds.Select(id => $"#{id}"))}");
        Console.WriteLine("=====================================\n");

        // Initialize progress tracking with actual count
        ProgressTracker.TotalPRs = pullRequestIds.Count;
        ProgressTracker.ProcessedPRs = 0;
        Console.WriteLine($"[PROGRESS] Initialized: Total={ProgressTracker.TotalPRs}, Processed=0");
        
        // Wait 2 seconds to ensure UI polling catches the initial 0/50 state
        await Task.Delay(2000);
        Console.WriteLine($"[PROGRESS] Starting PR processing...");

        // Process pull requests in batches
        int processedCount = 0;
        int foundCount = 0;

        foreach (var batch in pullRequestIds.Chunk(10))
        {
            Console.WriteLine($"\n[BATCH] Processing PRs: {string.Join(", ", batch.Select(id => $"#{id}"))}");
            
            var batchTasks = batch.Select(prId => ProcessPRAsync(prId)).ToList();
            var results = await Task.WhenAll(batchTasks);

            // Write results and update progress after each PR
            using (StreamWriter markdownWriter = new StreamWriter(_outputPath, append: true))
            {
                foreach (var prResult in results)
                {
                    processedCount++;
                    ProgressTracker.ProcessedPRs = processedCount;
                    ProgressTracker.CurrentPR = prResult.PullRequestId;
                    
                    if (prResult.HasContent)
                    {
                        foundCount++;
                        ProgressTracker.FoundPRs = foundCount;
                        markdownWriter.WriteLine(prResult.Content);
                        Console.WriteLine($"✓ [PR #{prResult.PullRequestId}] Found important comments ({foundCount} found / {processedCount} analyzed) [PROGRESS: {processedCount}/{ProgressTracker.TotalPRs}]");
                    }
                    else if (prResult.PullRequestId > 0)
                    {
                        Console.WriteLine($"⊘ [PR #{prResult.PullRequestId}] No important comments ({foundCount} found / {processedCount} analyzed) [PROGRESS: {processedCount}/{ProgressTracker.TotalPRs}]");
                    }
                    
                    // Log progress update explicitly
                    Console.WriteLine($"[PROGRESS UPDATE] Backend now at: {processedCount}/{ProgressTracker.TotalPRs} PRs (Found: {foundCount})");
                }
            }

            // Small delay between batches
            if (processedCount < pullRequestIds.Count)
            {
                Console.WriteLine($"\n[PROGRESS] {processedCount}/{pullRequestIds.Count} PRs analyzed, waiting 5 seconds before next batch...\n");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        Console.WriteLine($"\n=== Processing complete: Found important comments in {foundCount}/{processedCount} PRs ===");
    }

    private void InitializeOutputFile(int daysBack)
    {
        if (!File.Exists(_outputPath))
        {
            using var writer = new StreamWriter(_outputPath, append: false);
            writer.WriteLine($"# Important Comments from PRs from the last {daysBack} Days");
            writer.WriteLine();
        }
    }

    private async Task<PRProcessResult> ProcessPRAsync(int pullRequestId)
    {
        string prLink = _adoService.GetPRLink(pullRequestId);

        try
        {
            Console.WriteLine($"  → [PR #{pullRequestId}] Fetching threads...");

            var threads = await _adoService.FetchPRThreadsAsync(pullRequestId);

            if (threads?.Value == null || threads.Value.Length == 0)
            {
                Console.WriteLine($"  → [PR #{pullRequestId}] No threads found");
                Console.WriteLine($"No threads in PR #{pullRequestId}");
                return new PRProcessResult { PullRequestId = pullRequestId, HasContent = false };
            }

            // Process all comments in parallel
            var commentTasks = new List<Task<string>>();
            int totalComments = 0;
            foreach (CommentThread thread in threads.Value)
            {
                foreach (var comment in thread.Comments)
                {
                    totalComments++;
                    if (CommentProcessor.ShouldProcessComment(comment.Content ?? ""))
                    {
                        commentTasks.Add(_commentProcessor.ProcessCommentAsync(comment, thread, prLink));
                    }
                }
            }

            Console.WriteLine($"  → [PR #{pullRequestId}] Analyzing {commentTasks.Count} comments (out of {totalComments} total)...");
            
            var commentResults = await Task.WhenAll(commentTasks);
            var markdownContent = string.Concat(commentResults.Where(c => !string.IsNullOrEmpty(c)));

            bool hasContent = !string.IsNullOrEmpty(markdownContent);

            string output = hasContent 
                ? $"<details>\n<summary>PR {pullRequestId} - Link: <a href=\"{prLink}\">{prLink}</a></summary>\n\n### Important Comments\n\n{markdownContent}</details>\n\n"
                : string.Empty;

            return new PRProcessResult
            {
                PullRequestId = pullRequestId,
                HasContent = hasContent,
                Content = output
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch comments for PR #{pullRequestId}: {ex.Message}");
            return new PRProcessResult { PullRequestId = pullRequestId, HasContent = false };
        }
    }
}
