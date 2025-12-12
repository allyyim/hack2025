using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ADOPrism.Models;

namespace ADOPrism.Services;

public class AzureDevOpsService
{
    private readonly string _personalAccessToken;
    private readonly string _organization;
    private readonly string _repositoryName;

    public AzureDevOpsService(string personalAccessToken, string organization, string repositoryName)
    {
        _personalAccessToken = personalAccessToken;
        _organization = organization;
        _repositoryName = repositoryName;
    }

    public async Task<List<int>> FetchPullRequestIdsAsync(int daysBack = 7, int maxResults = 15)
    {
        // Use minTime instead of creationDate per Azure DevOps API docs
        string startDate = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-ddTHH:mm:ssZ");
        string pullRequestsUrl = $"https://msazure.visualstudio.com/DefaultCollection/{_organization}/_apis/git/repositories/{_repositoryName}/pullRequests?searchCriteria.status=completed&searchCriteria.minTime={startDate}&api-version=7.1&$top={maxResults}";

        Console.WriteLine($"[API] Fetching PRs from URL: {pullRequestsUrl}");

        using var httpClient = new HttpClient();
        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_personalAccessToken}"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

        HttpResponseMessage response = await httpClient.GetAsync(pullRequestsUrl);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[API ERROR] Error fetching pull requests: {response.StatusCode} - {response.ReasonPhrase}");
            string errorBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[API ERROR] Response body: {errorBody}");
            return new List<int>();
        }

        string responseBody = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[API] Response length: {responseBody.Length} characters");
        
        var pullRequests = JsonSerializer.Deserialize<PullRequestResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        var prIds = pullRequests?.Value?.Select(pr => pr.PullRequestId).ToList() ?? new List<int>();
        Console.WriteLine($"[API] Found {prIds.Count} PRs: {string.Join(", ", prIds.Take(10))}{(prIds.Count > 10 ? "..." : "")}");
        
        return prIds;
    }

    public async Task<ThreadResponse?> FetchPRThreadsAsync(int pullRequestId)
    {
        string threadsUrl = $"https://msazure.visualstudio.com/DefaultCollection/{_organization}/_apis/git/repositories/{_repositoryName}/pullRequests/{pullRequestId}/threads?api-version=7.1-preview.1";

        using var httpClient = new HttpClient();
        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_personalAccessToken}"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

        HttpResponseMessage threadsResponse = await httpClient.GetAsync(threadsUrl);
        if (!threadsResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error fetching threads for PR #{pullRequestId}: {threadsResponse.StatusCode} - {threadsResponse.ReasonPhrase}");
            return null;
        }

        string threadsResponseBody = await threadsResponse.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ThreadResponse>(threadsResponseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public string GetPRLink(int pullRequestId)
    {
        return $"https://msazure.visualstudio.com/{_organization}/_git/{_repositoryName}/pullrequest/{pullRequestId}";
    }
}
