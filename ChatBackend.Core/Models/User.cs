namespace ChatBackend.Core.Models;

/// <summary>
/// Represents a user identified by an external ID and a source channel.
/// The same phone number via WhatsApp and via API are separate users intentionally.
/// </summary>
public class User
{
    public string Id         { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Phone number, auth sub, or any external identifier.</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>"api" or "whatsapp"</summary>
    public string Source     { get; set; } = "api";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
}
