using System.ComponentModel.DataAnnotations;

namespace ChatBackend.Core.DTOs;

/// <summary>Inbound chat request from any channel (API, WhatsApp, etc.).</summary>
public record ChatRequest
{
    /// <summary>The user's message text. Required, max 4000 characters.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "UserMessage is required.")]
    [MinLength(1, ErrorMessage = "UserMessage cannot be empty.")]
    [MaxLength(4000, ErrorMessage = "UserMessage cannot exceed 4000 characters.")]
    public string UserMessage { get; init; } = string.Empty;

    /// <summary>
    /// Provide to continue an existing conversation.
    /// Omit to start a new one.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Caller-supplied external user identifier (e.g. auth sub, phone number).
    /// Defaults to "anonymous" when omitted.
    /// </summary>
    [MaxLength(256)]
    public string? UserId { get; init; }

    /// <summary>
    /// Originating channel. Allowed values: "api", "whatsapp".
    /// Controls how the user record is bucketed.
    /// </summary>
    [MaxLength(50)]
    public string Source { get; init; } = "api";
}
