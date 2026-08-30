using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChatBackend.Core.DTOs;
using ChatBackend.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatBackend.Infrastructure.LLM;

/// <summary>
/// ILLMClient implementation for Groq Cloud using the OpenAI-compatible
/// /v1/chat/completions endpoint. HttpClient is injected via typed client
/// registration in DI (BaseAddress + auth header pre-configured).
/// </summary>
public sealed class GroqAdapter(
    HttpClient http,
    IConfiguration config,
    ILogger<GroqAdapter> logger) : ILLMClient
{
    private readonly string _model = config["LLM:Groq:Model"] ?? "openai/gpt-oss-120b";

    public string ProviderName => "Groq";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<string> CompleteAsync(
        IReadOnlyList<LLMMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var body = BuildRequest(messages, stream: false);

        logger.LogDebug(
            "Groq request → model={Model} messages={Count}", _model, messages.Count);

        var httpResponse = await http.PostAsJsonAsync(
            "chat/completions", body, cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var error = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Groq error. Status={Status} Body={Body}", httpResponse.StatusCode, error);
            throw new HttpRequestException(
                $"Groq returned {(int)httpResponse.StatusCode}: {error}");
        }

        var result = await httpResponse.Content
            .ReadFromJsonAsync<GroqChatResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Groq returned null body.");

        var content = result.Choices.FirstOrDefault()?.Message.Content
            ?? throw new InvalidOperationException("Groq returned no choices.");

        logger.LogDebug(
            "Groq response ← tokens={Tokens}", result.Usage?.TotalTokens ?? 0);

        return content;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LLMMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = BuildRequest(messages, stream: true);

        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(body)
        };

        using var response = await http.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var json = line["data: ".Length..];
            if (json == "[DONE]") break;

            GroqStreamChunk? chunk;
            try   { chunk = JsonSerializer.Deserialize<GroqStreamChunk>(json, _jsonOptions); }
            catch { continue; }

            var token = chunk?.Choices.FirstOrDefault()?.Delta.Content;
            if (token is not null) yield return token;
        }
    }

    private GroqChatRequest BuildRequest(IReadOnlyList<LLMMessage> messages, bool stream) =>
        new(Model:       _model,
            Messages:    messages.Select(m => new GroqMessage(m.Role, m.Content)).ToList(),
            MaxTokens:   1024,
            Temperature: 0.7,
            Stream:      stream);
}
