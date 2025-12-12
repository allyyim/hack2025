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

    public bool ShouldProcessComment(string content)
    {
        return !string.IsNullOrEmpty(content) &&
               !content.Contains("Ownership Enforcer", StringComparison.OrdinalIgnoreCase) &&
               !content.Contains("Diff coverage", StringComparison.OrdinalIgnoreCase) &&
               !content.Contains("AI feedback", StringComparison.OrdinalIgnoreCase) &&
               !content.Contains("Coverage", StringComparison.OrdinalIgnoreCase) &&
               !content.Contains("PR description", StringComparison.OrdinalIgnoreCase) &&
               !content.Contains("AI description", StringComparison.OrdinalIgnoreCase) &&
               content.Length > 20;
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
        var response = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            Temperature = 1f,
            FrequencyPenalty = 0,
            PresencePenalty = 0
        });

        // Parse the AI response
        var aiResponse = response.Value.Content.Last().Text;

        // Extract information
        string term = ExtractTermFromAIResponse(aiResponse);
        string definition = ExtractDefinitionFromAIResponse(aiResponse);
        string troubleshootingStep = ExtractTroubleshootingStep(aiResponse);
        string developerTrick = ExtractDeveloperTrick(aiResponse);

        // Filter out unwanted results
        if (ShouldFilterResult(term, definition, troubleshootingStep, developerTrick))
        {
            return string.Empty;
        }

        // Build markdown output
        return BuildMarkdownOutput(thread.Id, comment.Id, term, definition, troubleshootingStep, developerTrick);
    }

    private bool ShouldFilterResult(string term, string definition, string troubleshootingStep, string developerTrick)
    {
        var filterTerms = new[] { "vote", "Branch", "Git", "PR description", "PR Assistant", "refs/", 
                                  "No content to extract", "PRAssistant", "Unknown" };
        
        var filterDefinitions = new[] { "No definition", "No content", "No additional information", "Unknown" };

        return filterTerms.Any(f => term.Contains(f, StringComparison.OrdinalIgnoreCase)) ||
               filterDefinitions.Any(f => definition.Contains(f, StringComparison.OrdinalIgnoreCase)) ||
               troubleshootingStep.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
               developerTrick.Contains("No content to extract", StringComparison.OrdinalIgnoreCase) ||
               developerTrick.Contains("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildMarkdownOutput(int threadId, int commentId, string term, string definition, string troubleshootingStep, string developerTrick)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Thread {threadId}, Comment {commentId}");
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

    private string ExtractTermFromAIResponse(string aiResponse)
    {
        var match = Regex.Match(aiResponse, @"Term:\s*(.*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "Unknown Term";
    }

    private string ExtractDefinitionFromAIResponse(string aiResponse)
    {
        var match = Regex.Match(aiResponse, @"Definition:\s*(.*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "Unknown Definition";
    }

    private string ExtractTroubleshootingStep(string aiResponse)
    {
        var match = Regex.Match(aiResponse, @"Troubleshooting Step:\s*(.*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "Unknown Troubleshooting Step";
    }

    private string ExtractDeveloperTrick(string aiResponse)
    {
        var match = Regex.Match(aiResponse, @"Developer Trick:\s*(.*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "Unknown Developer Trick";
    }
}
