namespace ADOPrism.Models;

public record StreamDescriptor(string Id, string Title, string Description, string SchemaUrl, string SampleUrl, string ConnectUrl, int FreshnessSeconds);

public class ThreadResponse
{
    public CommentThread[] Value { get; set; } = Array.Empty<CommentThread>();
}

public class CommentThread
{
    public int Id { get; set; }
    public Comment[] Comments { get; set; } = Array.Empty<Comment>();
}

public class Comment
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public Author Author { get; set; } = new Author();
    public DateTime PublishedDate { get; set; }
}

public class Author
{
    public string Email { get; set; } = string.Empty;
}

public class PullRequestResponse
{
    public PullRequest[] Value { get; set; } = Array.Empty<PullRequest>();
}

public class PullRequest
{
    public int PullRequestId { get; set; }
}

public class PRProcessResult
{
    public int PullRequestId { get; set; }
    public bool HasContent { get; set; }
    public string Content { get; set; } = string.Empty;
}
