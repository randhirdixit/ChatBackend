using System.Net;
using System.Security.Cryptography;
using System.Text;
using ChatBackend.Core.DTOs;
using ChatBackend.Core.Interfaces;
using ChatBackend.Infrastructure.Meta;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace ChatBackend.Tests;

public class MetaWhatsAppHandlerTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<IServiceScope> _scopeMock;
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly Mock<IChatOrchestrator> _orchestratorMock;

    public MetaWhatsAppHandlerTests()
    {
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _scopeMock = new Mock<IServiceScope>();
        _serviceProviderMock = new Mock<IServiceProvider>();
        _orchestratorMock = new Mock<IChatOrchestrator>();

        _scopeFactoryMock.Setup(s => s.CreateScope()).Returns(_scopeMock.Object);
        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IChatOrchestrator)))
            .Returns(_orchestratorMock.Object);
    }

    [Fact]
    public void VerifyWebhook_ValidToken_ReturnsSuccess()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Meta:VerifyToken"] = "test_secret_token"
            })
            .Build();

        var handler = new MetaWhatsAppHandler(
            _scopeFactoryMock.Object,
            new HttpClient(),
            config,
            NullLogger<MetaWhatsAppHandler>.Instance);

        var (valid, challenge) = handler.VerifyWebhook("subscribe", "test_secret_token", "challenge_12345");

        valid.Should().BeTrue();
        challenge.Should().Be("challenge_12345");
    }

    [Fact]
    public void VerifyWebhook_InvalidToken_ReturnsFalse()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Meta:VerifyToken"] = "test_secret_token"
            })
            .Build();

        var handler = new MetaWhatsAppHandler(
            _scopeFactoryMock.Object,
            new HttpClient(),
            config,
            NullLogger<MetaWhatsAppHandler>.Instance);

        var (valid, challenge) = handler.VerifyWebhook("subscribe", "wrong_token", "challenge_12345");

        valid.Should().BeFalse();
        challenge.Should().BeEmpty();
    }

    [Fact]
    public void ValidateSignature_ValidHmac_ReturnsTrue()
    {
        var appSecret = "my_meta_app_secret";
        var rawBody = "{\"test\":\"payload\"}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var hash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        var signatureHeader = $"sha256={hash}";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Meta:AppSecret"] = appSecret
            })
            .Build();

        var handler = new MetaWhatsAppHandler(
            _scopeFactoryMock.Object,
            new HttpClient(),
            config,
            NullLogger<MetaWhatsAppHandler>.Instance);

        var result = handler.ValidateSignature(rawBody, signatureHeader);

        result.Should().BeTrue();
    }

    [Fact]
    public void ValidateSignature_TamperedBody_ReturnsFalse()
    {
        var appSecret = "my_meta_app_secret";
        var rawBody = "{\"test\":\"payload\"}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var hash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        var signatureHeader = $"sha256={hash}";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Meta:AppSecret"] = appSecret
            })
            .Build();

        var handler = new MetaWhatsAppHandler(
            _scopeFactoryMock.Object,
            new HttpClient(),
            config,
            NullLogger<MetaWhatsAppHandler>.Instance);

        var result = handler.ValidateSignature("{\"test\":\"tampered_payload\"}", signatureHeader);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessInboundAsync_ValidTextMessage_InvokesOrchestratorAndSendsReply()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content    = new StringContent("{\"messaging_product\":\"whatsapp\"}")
            });

        var httpClient = new HttpClient(handlerMock.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Meta:AccessToken"]   = "test_access_token",
                ["Meta:PhoneNumberId"] = "123456"
            })
            .Build();

        _orchestratorMock
            .Setup(o => o.ProcessAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse("Hello back from AI!", "conv-1", "msg-1", "Groq", DateTimeOffset.UtcNow));

        var handler = new MetaWhatsAppHandler(
            _scopeFactoryMock.Object,
            httpClient,
            config,
            NullLogger<MetaWhatsAppHandler>.Instance);

        var payload = new MetaWebhookPayload(
            Object: "whatsapp_business_account",
            Entry: new List<MetaEntry>
            {
                new(
                    Id: "entry-1",
                    Changes: new List<MetaChange>
                    {
                        new(
                            Field: "messages",
                            Value: new MetaChangeValue(
                                MessagingProduct: "whatsapp",
                                Metadata: new MetaMetadata("+1234567890", "123456"),
                                Contacts: new List<MetaContact> { new(new MetaProfile("John"), "919999999999") },
                                Messages: new List<MetaMessage>
                                {
                                    new(
                                        From: "919999999999",
                                        Id: "wamid.123",
                                        Timestamp: "1700000000",
                                        Type: "text",
                                        Text: new MetaTextBody("Hello AI")
                                    )
                                }
                            )
                        )
                    }
                )
            }
        );

        await handler.ProcessInboundAsync(payload);

        _orchestratorMock.Verify(o => o.ProcessAsync(
            It.Is<ChatRequest>(r => r.UserMessage == "Hello AI" && r.UserId == "919999999999" && r.Source == "whatsapp"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
