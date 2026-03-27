using System.Net.Http.Json;
using System.Text.Json;
using ChatBackend.Core.DTOs;
using ChatBackend.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatBackend.Infrastructure.Meta;

/// <summary>
/// Handles the Meta WhatsApp Cloud API webhook lifecycle.
///
/// Registered as Singleton. Uses IServiceScopeFactory to create a fresh DI scope
/// per inbound message so scoped services (AppDbContext, ChatOrchestrator) are
/// never tied to — or disposed by — the HTTP request lifetime.
/// </summary>
public sealed class MetaWhatsAppHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HttpClient           _http;
    private readonly IConfiguration       _config;
    private readonly ILogger<MetaWhatsAppHandler> _logger;

    public MetaWhatsAppHandler(
        IServiceScopeFactory scopeFactory,
        HttpClient           http,
        IConfiguration       config,
        ILogger<MetaWhatsAppHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _http         = http;
        _config       = config;
        _logger       = logger;
    }

    // ── Webhook verification (GET) ────────────────────────────────────

    public (bool valid, string challenge) VerifyWebhook(
        string mode, string token, string challenge)
    {
        var expected = _config["Meta:VerifyToken"];

        if (string.IsNullOrWhiteSpace(expected))
        {
            _logger.LogError(
                "Meta:VerifyToken is not configured. " +
                "Run: dotnet user-secrets set \"Meta:VerifyToken\" \"your_token\"");
            return (false, string.Empty);
        }

        if (mode == "subscribe" &&
            string.Equals(token, expected, StringComparison.Ordinal))
        {
            _logger.LogInformation("Meta webhook verified successfully.");
            return (true, challenge);
        }

        _logger.LogWarning(
            "Webhook verification failed. " +
            "ModeOk={ModeOk} TokenMatch={TokenMatch}",
            mode == "subscribe", token == expected);

        return (false, string.Empty);
    }

    // ── Process inbound message (POST) ───────────────────────────────

    public async Task ProcessInboundAsync(
        MetaWebhookPayload payload,
        CancellationToken cancellationToken = default)
    {
        foreach (var entry in payload.Entry)
        {
            foreach (var change in entry.Changes)
            {
                var messages = change.Value.Messages;
                if (messages is null || messages.Count == 0) continue;

                var phoneNumberId = change.Value.Metadata.PhoneNumberId;

                foreach (var msg in messages)
                {
                    if (msg.Type != "text" || msg.Text is null)
                    {
                        _logger.LogInformation(
                            "Skipping non-text message. Type={Type} From={From}",
                            msg.Type, msg.From);
                        continue;
                    }

                    _logger.LogInformation(
                        "WhatsApp inbound. From={From} Body='{Body}'",
                        msg.From,
                        msg.Text.Body.Length > 80
                            ? msg.Text.Body[..80] + "…"
                            : msg.Text.Body);

                    _ = MarkAsReadAsync(phoneNumberId, msg.Id);

                    // Fresh scope per message — AppDbContext is independent of
                    // the HTTP request that has already returned 200 to Meta.
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var orchestrator = scope.ServiceProvider
                        .GetRequiredService<IChatOrchestrator>();

                    var chatRequest = new ChatRequest
                    {
                        UserMessage = msg.Text.Body,
                        UserId      = msg.From,
                        Source      = "whatsapp"
                        // ConversationId intentionally omitted — orchestrator
                        // resumes the user's most recent conversation automatically.
                    };

                    ChatResponse chatResponse;
                    try
                    {
                        chatResponse = await orchestrator.ProcessAsync(
                            chatRequest, cancellationToken);

                        _logger.LogInformation(
                            "Orchestrator OK. ConversationId={ConvId} " +
                            "Provider={Provider} ReplyLength={Len}",
                            chatResponse.ConversationId,
                            chatResponse.Provider,
                            chatResponse.AssistantMessage.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Orchestrator failed for message {MsgId}.", msg.Id);

                        await SendReplyAsync(phoneNumberId, msg.From,
                            "Sorry, I encountered an error. Please try again.");
                        continue;
                    }

                    await SendReplyAsync(
                        phoneNumberId, msg.From, chatResponse.AssistantMessage);
                }
            }
        }
    }

    // ── Send outbound message ─────────────────────────────────────────

    private async Task SendReplyAsync(string phoneNumberId, string to, string body)
    {
        var accessToken = _config["Meta:AccessToken"];

        if (string.IsNullOrWhiteSpace(phoneNumberId) ||
            string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.LogError(
                "Cannot send reply — " +
                "PhoneNumberId='{PNI}' AccessToken='{AT}'",
                string.IsNullOrWhiteSpace(phoneNumberId) ? "MISSING" : "OK",
                string.IsNullOrWhiteSpace(accessToken)   ? "MISSING" : "OK");
            return;
        }

        var requestPayload = new MetaSendMessageRequest(
            MessagingProduct: "whatsapp",
            To:   to,
            Type: "text",
            Text: new MetaTextContent(Body: body));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.facebook.com/v19.0/{phoneNumberId}/messages");

        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(requestPayload);

        try
        {
            var response = await _http.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                _logger.LogError(
                    "Meta API error. Status={Status} Body={Body}",
                    (int)response.StatusCode, responseBody);
            else
                _logger.LogInformation(
                    "WhatsApp reply sent. To={To} Length={Len}", to, body.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP call to Meta API failed.");
        }
    }

    // ── Mark as read (double blue ticks) ─────────────────────────────

    private async Task MarkAsReadAsync(string phoneNumberId, string messageId)
    {
        var accessToken = _config["Meta:AccessToken"];
        if (string.IsNullOrWhiteSpace(accessToken)) return;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://graph.facebook.com/v19.0/{phoneNumberId}/messages");

            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = JsonContent.Create(new
            {
                messaging_product = "whatsapp",
                status            = "read",
                message_id        = messageId
            });

            await _http.SendAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not mark message {MsgId} as read.", messageId);
        }
    }
}
