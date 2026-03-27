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
            ILogger<Program> logger) =>
        {
            string rawBody;
            using (var reader = new StreamReader(ctx.Request.Body))
                rawBody = await reader.ReadToEndAsync();

            logger.LogInformation(
                "POST /webhook/whatsapp. BodyLength={Len}", rawBody.Length);

            if (string.IsNullOrWhiteSpace(rawBody))
                return Results.Ok();

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

            // Fire-and-forget: return 200 to Meta immediately (required within 5s).
            // MetaWhatsAppHandler creates its OWN fresh DI scope per message internally,
            // so AppDbContext is completely independent of this (now-ending) request scope.
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler.ProcessInboundAsync(payload, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "ProcessInboundAsync failed.");
                }
            });

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
