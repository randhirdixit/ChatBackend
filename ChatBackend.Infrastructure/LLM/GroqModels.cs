using System.Text.Json.Serialization;

namespace ChatBackend.Infrastructure.LLM;

// ── Request ───────────────────────────────────────────────────────────

internal record GroqChatRequest(
    [property: JsonPropertyName("model")]      string Model,
    [property: JsonPropertyName("messages")]   IReadOnlyList<GroqMessage> Messages,
    [property: JsonPropertyName("max_tokens")] int MaxTokens   = 1024,
    [property: JsonPropertyName("temperature")]double Temperature = 0.7,
    [property: JsonPropertyName("stream")]     bool Stream     = false
);

internal record GroqMessage(
    [property: JsonPropertyName("role")]    string Role,
    [property: JsonPropertyName("content")] string Content
);

// ── Non-streaming response ────────────────────────────────────────────

internal record GroqChatResponse(
    [property: JsonPropertyName("id")]      string Id,
    [property: JsonPropertyName("choices")] IReadOnlyList<GroqChoice> Choices,
    [property: JsonPropertyName("usage")]   GroqUsage? Usage
);

internal record GroqChoice(
    [property: JsonPropertyName("message")]       GroqMessage Message,
    [property: JsonPropertyName("finish_reason")] string FinishReason
);

internal record GroqUsage(
    [property: JsonPropertyName("prompt_tokens")]     int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")]      int TotalTokens
);

// ── Streaming (SSE) response ──────────────────────────────────────────

internal record GroqStreamChunk(
    [property: JsonPropertyName("choices")] IReadOnlyList<GroqStreamChoice> Choices
);

internal record GroqStreamChoice(
    [property: JsonPropertyName("delta")]         GroqDelta Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason
);

internal record GroqDelta(
    [property: JsonPropertyName("content")] string? Content
);
