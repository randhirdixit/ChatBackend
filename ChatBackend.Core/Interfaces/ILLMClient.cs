using ChatBackend.Core.DTOs;

namespace ChatBackend.Core.Interfaces;

/// <summary>
/// Provider-agnostic contract for any LLM backend.
/// Implement this interface to add new providers (OpenAI, Gemini, etc.)
/// without touching any other code.
/// </summary>
public interface ILLMClient
{
    /// <summary>Unique provider identifier, e.g. "Groq".</summary>
    string ProviderName { get; }

    /// <summary>
    /// Sends a message list to the LLM and returns the complete response as a string.
    /// Suitable for standard REST chat completion calls.
    /// </summary>
    Task<string> CompleteAsync(
        IReadOnlyList<LLMMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the LLM response token-by-token via IAsyncEnumerable.
    /// Pair this with Server-Sent Events (SSE) in the API layer for real-time output.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LLMMessage> messages,
        CancellationToken cancellationToken = default);
}
