namespace ChatBackend.Core.DTOs;

/// <summary>
/// Represents one turn in an LLM conversation context.
/// Normalized representation: any adapter translates this into its own wire format.
/// </summary>
public record LLMMessage(string Role, string Content)
{
    // ── Factory helpers ──────────────────────────────────────────────
    public static LLMMessage System(string content)    => new("system",    content);
    public static LLMMessage User(string content)      => new("user",      content);
    public static LLMMessage Assistant(string content) => new("assistant", content);
}
