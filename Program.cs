using System.Net;
using System.Text;
using System.Collections.Concurrent;
using ADOPrism.Models;
using ADOPrism.Services;

// Progress tracking - must be public and outside Program class so PRAnalyzer can access it
public static class ProgressTracker
{
    public static int TotalPRs { get; set; }
    public static int ProcessedPRs { get; set; }
    public static int FoundPRs { get; set; }
    public static int CurrentPR { get; set; }
    public static void Reset() { TotalPRs = 0; ProcessedPRs = 0; FoundPRs = 0; CurrentPR = 0; }
}

class Program
{
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
        string importantCommentsMarkdownPath = Path.Combine(AppContext.BaseDirectory, "important_comments.md");
        var analyzer = new PRAnalyzer(importantCommentsMarkdownPath);
        await analyzer.AnalyzePullRequestsAsync(daysBack: 30, maxPRs: 50);
    }

    static async Task FetchOnce()
    {
        try
        {
            await RunApplication();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FetchOnce error: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"  Inner: {ex.InnerException.Message}");
            }
        }
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

                if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/api/progress")
                {
                    // Return progress status as JSON
                    var progressJson = $"{{\"total\":{ProgressTracker.TotalPRs},\"processed\":{ProgressTracker.ProcessedPRs},\"found\":{ProgressTracker.FoundPRs},\"currentPR\":{ProgressTracker.CurrentPR}}}";
                    Console.WriteLine($"[PROGRESS API] Returning: {progressJson}");
                    response.ContentType = "application/json";
                    response.StatusCode = 200;
                    byte[] buffer = Encoding.UTF8.GetBytes(progressJson);
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                }
                else if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/api/fetch-comments")
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
                        bool shouldFetch = false;
                        lock (FetchLock)
                        {
                            if (DateTime.UtcNow.Subtract(LastFetchTime).TotalMinutes >= 5)
                            {
                                LastFetchTime = DateTime.UtcNow;
                                shouldFetch = true;
                            }
                        }

                        if (shouldFetch)
                        {
                            // Delete old file to ensure fresh data
                            if (File.Exists(mdPath))
                            {
                                File.Delete(mdPath);
                            }
                            
                            // Start fetch and wait for completion
                            try
                            {
                                await FetchOnce();
                            }
                            catch (Exception ex)
                            {
                                string errorHtml = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Error</title></head><body style=\"font-family:Segoe UI, Tahoma, Geneva, Verdana, sans-serif;padding:20px;\"><h2>Error fetching comments</h2><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p></body></html>";
                                response.StatusCode = 500;
                                byte[] errorBuffer = Encoding.UTF8.GetBytes(errorHtml);
                                response.ContentLength64 = errorBuffer.Length;
                                await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                                response.Close();
                                continue;
                            }
                        }

                        if (File.Exists(mdPath))
                        {
                            string md = File.ReadAllText(mdPath);
                            
                            // Check if file only has header (no actual comments)
                            bool hasOnlyHeader = md.Trim().StartsWith("# Important Comments") && 
                                                 !md.Contains("<details>") &&
                                                 md.Trim().Split('\n').Length <= 3;
                            
                            if (string.IsNullOrWhiteSpace(md) || hasOnlyHeader)
                            {
                                string body = "No important comments were found in the last 30 days after analyzing 50 PRs. This could mean: PRs contain mostly automated/bot comments, general discussion without technical insights, or the filtering criteria may be too strict.";
                                string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Important Comments</title></head><body style=\"font-family:Segoe UI, Tahoma, Geneva, Verdana, sans-serif;padding:20px;\"><h2>No Important Comments Found</h2><p>{body}</p></body></html>";
                                response.StatusCode = 200;
                                byte[] buffer = Encoding.UTF8.GetBytes(html);
                                response.ContentLength64 = buffer.Length;
                                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                            }
                            else
                            {
                                // Render markdown as HTML (it already contains HTML tags like <details>)
                                string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Important Comments</title><style>details{{margin-bottom:20px;border:1px solid #ddd;padding:10px;border-radius:5px;}}summary{{cursor:pointer;font-weight:bold;color:#667eea;}}summary:hover{{color:#4c51bf;}}</style></head><body style=\"font-family:Segoe UI, Tahoma, Geneva, Verdana, sans-serif;padding:20px;\">{md}</body></html>";
                                response.StatusCode = 200;
                                byte[] buffer = Encoding.UTF8.GetBytes(html);
                                response.ContentLength64 = buffer.Length;
                                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                            }
                        }
                        else
                        {
                            string body = "Could not load comments. Please try again.";
                            string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Important Comments</title></head><body style=\"font-family:Segoe UI, Tahoma, Geneva, Verdana, sans-serif;padding:20px;\">{body}</body></html>";
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

}