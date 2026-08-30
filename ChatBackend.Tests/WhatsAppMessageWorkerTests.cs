using ChatBackend.Core.Interfaces;
using ChatBackend.Infrastructure.Meta;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ChatBackend.Tests;

public class WhatsAppMessageWorkerTests
{
    [Fact]
    public async Task Channel_WriteAndReadAsync_TransfersPayload()
    {
        var channel = new WhatsAppMessageChannel();
        var payload = new MetaWebhookPayload(
            Object: "whatsapp_business_account",
            Entry: []
        );

        await channel.Writer.WriteAsync(payload);
        var read = await channel.Reader.ReadAsync();

        read.Should().Be(payload);
    }

    [Fact]
    public async Task Worker_ExecuteAsync_DrainsChannelAndProcessesPayload()
    {
        var channel = new WhatsAppMessageChannel();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var scopeMock = new Mock<IServiceScope>();
        var serviceProviderMock = new Mock<IServiceProvider>();
        var orchestratorMock = new Mock<IChatOrchestrator>();

        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProviderMock.Object);
        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IChatOrchestrator)))
            .Returns(orchestratorMock.Object);

        var config = new ConfigurationBuilder().Build();
        var handler = new MetaWhatsAppHandler(
            scopeFactoryMock.Object,
            new HttpClient(),
            config,
            NullLogger<MetaWhatsAppHandler>.Instance);

        var worker = new WhatsAppMessageWorker(
            channel,
            handler,
            NullLogger<WhatsAppMessageWorker>.Instance);

        using var cts = new CancellationTokenSource();

        var payload = new MetaWebhookPayload(
            Object: "whatsapp_business_account",
            Entry: []
        );

        await channel.Writer.WriteAsync(payload, cts.Token);

        var workerTask = worker.StartAsync(cts.Token);

        // Allow worker to drain queue
        await Task.Delay(200);

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        // Queue was drained
        channel.Reader.TryRead(out _).Should().BeFalse();
    }
}
