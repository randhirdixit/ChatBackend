using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatBackend.Infrastructure.Meta;

/// <summary>
/// Hosted background worker that drains the WhatsApp message channel queue and
/// processes inbound messages with graceful cancellation and concurrency control.
/// </summary>
public sealed class WhatsAppMessageWorker(
    WhatsAppMessageChannel channel,
    MetaWhatsAppHandler handler,
    ILogger<WhatsAppMessageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WhatsApp message background worker started.");

        try
        {
            await foreach (var payload in channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await handler.ProcessInboundAsync(payload, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Error processing WhatsApp background payload.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("WhatsApp message background worker stopped gracefully.");
        }
    }
}
