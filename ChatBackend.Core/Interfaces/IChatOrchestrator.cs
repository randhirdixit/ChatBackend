using ChatBackend.Core.DTOs;

namespace ChatBackend.Core.Interfaces;

/// <summary>
/// Orchestrates the full chat lifecycle: user resolution, conversation persistence,
/// LLM call, and response persistence.
/// </summary>
public interface IChatOrchestrator
{
    Task<ChatResponse> ProcessAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default);
}
