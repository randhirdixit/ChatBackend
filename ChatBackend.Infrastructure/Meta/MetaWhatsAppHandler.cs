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
public sealed class MetaWhatsAppHandler(
    IServiceScopeFactory scopeFactory,
    HttpClient http,
    IConfiguration config,
    ILogger<MetaWhatsAppHandler> logger)
{
    // ── Webhook verification (GET) ────────────────────────────────────

    public (bool valid, string challenge) VerifyWebhook(
        string mode, string token, string challenge)
    {
        var expected = config["Meta:VerifyToken"];

        if (string.IsNullOrWhiteSpace(expected))
        {
            logger.LogError(
                "Meta:VerifyToken is not configured. " +
                "Run: dotnet user-secrets set \"Meta:VerifyToken\" \"your_token\"");
            return (false, string.Empty);
        }

        if (mode == "subscribe" &&
            string.Equals(token, expected, StringComparison.Ordinal))
        {
            logger.LogInformation("Meta webhook verified successfully.");
            return (true, challenge);
        }

        logger.LogWarning(
            "Webhook verification failed. " +
            "ModeOk={ModeOk} TokenMatch={TokenMatch}",
            mode == "subscribe", token == expected);

        return (false, string.Empty);
    }

    // ── Webhook signature validation (POST) ───────────────────────────

    public bool ValidateSignature(string rawBody, string? signatureHeader)
    {
        var appSecret = config["Meta:AppSecret"];

        if (string.IsNullOrWhiteSpace(appSecret))
        {
            logger.LogWarning(
                "Meta:AppSecret is not configured. Webhook signature validation skipped.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader) ||
            !signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Invalid or missing X-Hub-Signature-256 header.");
            return false;
        }

        var expectedSignatureHex = signatureHeader["sha256=".Length..];

        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(appSecret));
        var computedHashBytes = hmac.ComputeHash(
            System.Text.Encoding.UTF8.GetBytes(rawBody));
        var computedSignatureHex = Convert.ToHexString(computedHashBytes).ToLowerInvariant();

        var isValid = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(computedSignatureHex),
            System.Text.Encoding.UTF8.GetBytes(expectedSignatureHex.ToLowerInvariant()));

        if (!isValid)
        {
            logger.LogWarning("Meta webhook HMAC signature verification failed.");
        }

        return isValid;
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
                        logger.LogInformation(
                            "Skipping non-text message. Type={Type} From={From}",
                            msg.Type, msg.From);
                        continue;
                    }

                    logger.LogInformation(
                        "WhatsApp inbound. From={From} Body='{Body}'",
                        msg.From,
                        msg.Text.Body.Length > 80
                            ? msg.Text.Body[..80] + "…"
                            : msg.Text.Body);

                    _ = MarkAsReadAsync(phoneNumberId, msg.Id);

                    // Fresh scope per message — AppDbContext is independent of
                    // the HTTP request that has already returned 200 to Meta.
                    await using var scope = scopeFactory.CreateAsyncScope();
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

                        logger.LogInformation(
                            "Orchestrator OK. ConversationId={ConvId} " +
                            "Provider={Provider} ReplyLength={Len}",
                            chatResponse.ConversationId,
                            chatResponse.Provider,
                            chatResponse.AssistantMessage.Length);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
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
        var accessToken = config["Meta:AccessToken"];

        if (string.IsNullOrWhiteSpace(phoneNumberId) ||
            string.IsNullOrWhiteSpace(accessToken))
        {
            logger.LogError(
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
            var response = await http.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                logger.LogError(
                    "Meta API error. Status={Status} Body={Body}",
                    (int)response.StatusCode, responseBody);
            else
                logger.LogInformation(
                    "WhatsApp reply sent. To={To} Length={Len}", to, body.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HTTP call to Meta API failed.");
        }
    }

    // ── Mark as read (double blue ticks) ─────────────────────────────

    private async Task MarkAsReadAsync(string phoneNumberId, string messageId)
    {
        var accessToken = config["Meta:AccessToken"];
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

            await http.SendAsync(request);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not mark message {MsgId} as read.", messageId);
        }
    }
}
