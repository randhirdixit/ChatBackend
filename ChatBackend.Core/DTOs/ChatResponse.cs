namespace ChatBackend.Core.DTOs;

/// <summary>Outbound chat response returned to any caller.</summary>
public record ChatResponse(
    string AssistantMessage,
    string ConversationId,
    string MessageId,
    string Provider,
    DateTimeOffset Timestamp
);
