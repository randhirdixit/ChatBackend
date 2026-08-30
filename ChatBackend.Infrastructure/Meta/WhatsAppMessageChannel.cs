using System.Threading.Channels;

namespace ChatBackend.Infrastructure.Meta;

/// <summary>
/// Thread-safe in-memory channel queue for asynchronous WhatsApp webhook processing.
/// Decouples immediate HTTP 200 responses from background LLM and database orchestration.
/// </summary>
public sealed class WhatsAppMessageChannel
{
    private readonly Channel<MetaWebhookPayload> _channel = Channel.CreateBounded<MetaWebhookPayload>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

    public ChannelWriter<MetaWebhookPayload> Writer => _channel.Writer;
    public ChannelReader<MetaWebhookPayload> Reader => _channel.Reader;
}
