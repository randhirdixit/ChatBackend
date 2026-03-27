namespace ChatBackend.Core.Models;

public class Message
{
    public string Id             { get; set; } = Guid.NewGuid().ToString();
    public string ConversationId { get; set; } = string.Empty;
    public Conversation Conversation { get; set; } = null!;

    /// <summary>"user" or "assistant"</summary>
    public string Role    { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    /// <summary>Null for user messages; set to ProviderName for assistant messages.</summary>
    public string? Provider { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
