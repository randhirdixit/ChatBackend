using System.Text.Json;
using System.Web;
using ChatBackend.Infrastructure.Meta;

namespace ChatBackend.Api.Endpoints;

public static class WhatsAppEndpoints
{
    public static WebApplication MapWhatsAppEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/webhook").WithTags("WhatsApp");

        // ── GET /webhook/whatsapp — Meta ownership verification ────────
        group.MapGet("/whatsapp", (HttpContext ctx, ILogger<Program> logger) =>
        {
            var qs = HttpUtility.ParseQueryString(
                ctx.Request.QueryString.Value ?? string.Empty);

            var mode      = qs["hub.mode"]          ?? string.Empty;
            var token     = qs["hub.verify_token"]   ?? string.Empty;
            var challenge = qs["hub.challenge"]      ?? string.Empty;

            logger.LogInformation(
                "GET /webhook/whatsapp → mode='{Mode}' token='{Token}'", mode, token);

            var handler = ctx.RequestServices.GetRequiredService<MetaWhatsAppHandler>();
            var (valid, echo) = handler.VerifyWebhook(mode, token, challenge);

            if (!valid) return Results.Forbid();

            return Results.Text(echo); // Must be plain text, not JSON
        })
        .WithName("WhatsAppVerify")
        .WithSummary("Meta webhook ownership verification");

        // ── POST /webhook/whatsapp — inbound messages ──────────────────
        group.MapPost("/whatsapp", async (
            HttpContext ctx,
            MetaWhatsAppHandler handler,
            WhatsAppMessageChannel channel,
            ILogger<Program> logger) =>
        {
            string rawBody;
            using (var reader = new StreamReader(ctx.Request.Body))
                rawBody = await reader.ReadToEndAsync();

            logger.LogInformation(
                "POST /webhook/whatsapp. BodyLength={Len}", rawBody.Length);

            if (string.IsNullOrWhiteSpace(rawBody))
                return Results.Ok();

            var signatureHeader = ctx.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
            if (!handler.ValidateSignature(rawBody, signatureHeader))
            {
                logger.LogWarning("Unauthorized WhatsApp webhook request: invalid signature.");
                return Results.Unauthorized();
            }

            MetaWebhookPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<MetaWebhookPayload>(rawBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Failed to parse Meta payload.");
                return Results.Ok(); // Always 200 — never let Meta think we're down
            }

            if (payload is null || payload.Object != "whatsapp_business_account")
            {
                logger.LogInformation(
                    "Skipping non-message event. Object='{Obj}'",
                    payload?.Object ?? "null");
                return Results.Ok();
            }

            // Enqueue payload into background channel for guaranteed processing
            await channel.Writer.WriteAsync(payload);

            return Results.Ok();
        })
        .WithName("WhatsAppInbound")
        .WithSummary("Receive inbound WhatsApp messages from Meta");

        // ── Debug endpoint — remove after testing ──────────────────────
        group.MapGet("/whatsapp-debug", (HttpContext ctx) =>
        {
            var qs   = HttpUtility.ParseQueryString(ctx.Request.QueryString.Value ?? "");
            var keys = new List<string>();
            foreach (string? k in qs.Keys) if (k != null) keys.Add(k);
            return Results.Ok(new
            {
                rawQueryString = ctx.Request.QueryString.Value,
                allParsedKeys  = keys,
                hubMode        = qs["hub.mode"],
                hubVerifyToken = qs["hub.verify_token"],
                hubChallenge   = qs["hub.challenge"]
            });
        })
        .WithName("WhatsAppDebug");

        return app;
    }
}
