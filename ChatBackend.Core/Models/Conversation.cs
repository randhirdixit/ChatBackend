namespace ChatBackend.Core.Models;

public class Conversation
{
    public string Id     { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public User   User   { get; set; } = null!;

    /// <summary>Auto-populated from the first user message (truncated to 60 chars).</summary>
    public string Title  { get; set; } = "New Conversation";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Message> Messages { get; set; } = new List<Message>();
}
