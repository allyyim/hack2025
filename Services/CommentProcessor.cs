using System.Text;
using System.Text.RegularExpressions;
using OpenAI.Chat;
using ADOPrism.Models;

namespace ADOPrism.Services;

public class CommentProcessor
{
    private readonly ChatClient _chatClient;

    public CommentProcessor(ChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public static bool ShouldProcessComment(string content)
    {
        return !string.IsNullOrEmpty(content) &&
               !content.Contains("Ownership Enforcer", StringComparison.OrdinalIgnoreCase) &&
               !content.Contains("Diff coverage", StringComparison.OrdinalIgnoreCase) &&
               !content.Contains("AI feedback", StringComparison.OrdinalIgnoreCase) &&
               !content.Contains("Coverage", StringComparison.OrdinalIgnoreCase) &&
               !content.Contains("PR description", StringComparison.OrdinalIgnoreCase) &&
               !content.Contains("AI description", StringComparison.OrdinalIgnoreCase) &&
               !content.Contains("PRAssistant", StringComparison.OrdinalIgnoreCase) &&
               content.Length > 15; // Reduced from 20 to catch more content
    }

    public async Task<string> ProcessCommentAsync(Comment comment, CommentThread thread, string prLink)
    {
        string content = comment.Content;

        if (!ShouldProcessComment(content))
        {
            return string.Empty;
        }

        // Split the content into comment and reply
        var contentLines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string mainComment = contentLines.FirstOrDefault() ?? string.Empty;
        string reply = string.Join(" ", contentLines.Skip(1));

        // Prepare the input for the AI model
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are an AI assistant that extracts important technical insights from PR comments."),
            new UserChatMessage($@"
                Analyze this PR comment and extract any important technical information:
                Comment: {mainComment}
                Reply: {reply}

                Extract and categorize insights into these categories:
                1. **Technical Concept/Term**: Any technical term, acronym, system name, or concept being explained (e.g., BCDR, VDCR, ARM, MDM)
                2. **Business Logic**: Conditional logic, rules, or decision-making explained (e.g., ""if workspace has X, then Y should happen"")
                3. **Troubleshooting**: Steps to debug, fix bugs, or resolve issues
                4. **Code Pattern/Trick**: Clever techniques, design patterns, or coding approaches
                5. **Configuration/Setup**: How to configure systems, settings, or deployment instructions

                Format your response as:
                Category: [One of the above categories]
                Summary: [Brief summary of the insight]
                Details: [More detailed explanation if needed]

                Only respond with 'No important content' if this is truly just casual discussion, automated messages, or has no technical value.
            ")
        };

        // Call the AI client to analyze the input
        var response = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            Temperature = 1f,
            FrequencyPenalty = 0,
            PresencePenalty = 0
        });

        // Parse the AI response
        var aiResponse = response.Value.Content.Last().Text;

        // Filter out non-important content
        if (aiResponse.Contains("No important content", StringComparison.OrdinalIgnoreCase) ||
            aiResponse.Contains("No definition found", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        // Extract information using new format
        string category = ExtractField(aiResponse, "Category");
        string summary = ExtractField(aiResponse, "Summary");
        string details = ExtractField(aiResponse, "Details");

        // Skip if no meaningful content
        if (string.IsNullOrEmpty(summary) || summary.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        // Filter out git/PR meta comments
        var filterTerms = new[] { "vote", "Branch", "Git", "PR description", "PR Assistant", "refs/", "PRAssistant" };
        if (filterTerms.Any(f => summary.Contains(f, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        // Build markdown output
        return BuildMarkdownOutput(thread.Id, comment.Id, category, summary, details);
    }

    private static string BuildMarkdownOutput(int threadId, int commentId, string category, string summary, string details)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Thread {threadId}, Comment {commentId}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(category))
        {
            sb.AppendLine($"**Category:** {category}");
        }

        if (!string.IsNullOrEmpty(summary))
        {
            sb.AppendLine($"**Summary:** {summary}");
        }

        if (!string.IsNullOrEmpty(details) && !details.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"**Details:** {details}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string ExtractField(string aiResponse, string fieldName)
    {
        var match = Regex.Match(aiResponse, $@"{fieldName}:\s*(.*?)(?=\n[A-Z][a-z]+:|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }
}
