using ChatBackend.Core.DTOs;
using ChatBackend.Core.Interfaces;
using ChatBackend.Core.Models;
using ChatBackend.Infrastructure.Data;
using ChatBackend.Infrastructure.Orchestrator;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ChatBackend.Tests;

/// <summary>
/// Unit tests for ChatOrchestrator.
/// Uses EF Core InMemory provider (no SQLite I/O) and Moq for ILLMClient.
/// </summary>
public class ChatOrchestratorTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Mock<ILLMClient> _llmMock;
    private readonly ChatOrchestrator _sut;

    public ChatOrchestratorTests()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(dbOptions);

        _llmMock = new Mock<ILLMClient>();
        _llmMock.Setup(l => l.ProviderName).Returns("MockLLM");

        _sut = new ChatOrchestrator(
            _db,
            _llmMock.Object,
            NullLogger<ChatOrchestrator>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ProcessAsync_HappyPath_ReturnsResponse()
    {
        _llmMock
            .Setup(l => l.CompleteAsync(It.IsAny<IReadOnlyList<LLMMessage>>(), default))
            .ReturnsAsync("Hello! How can I help?");

        var response = await _sut.ProcessAsync(new ChatRequest
        {
            UserMessage = "Hi",
            UserId      = "user-1",
            Source      = "api"
        });

        response.AssistantMessage.Should().Be("Hello! How can I help?");
        response.Provider.Should().Be("MockLLM");
        response.ConversationId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ProcessAsync_PersistsUserAndConversationAndMessages()
    {
        _llmMock
            .Setup(l => l.CompleteAsync(It.IsAny<IReadOnlyList<LLMMessage>>(), default))
            .ReturnsAsync("Reply");

        await _sut.ProcessAsync(new ChatRequest
        {
            UserMessage = "Hello",
            UserId      = "user-persist",
            Source      = "api"
        });

        var user = await _db.Users
            .SingleOrDefaultAsync(u => u.ExternalId == "user-persist");
        user.Should().NotBeNull();

        var conversations = await _db.Conversations
            .Where(c => c.UserId == user!.Id).ToListAsync();
        conversations.Should().HaveCount(1);

        var messages = await _db.Messages
            .Where(m => m.ConversationId == conversations[0].Id).ToListAsync();
        messages.Should().HaveCount(2);
        messages.Should().ContainSingle(m => m.Role == "user");
        messages.Should().ContainSingle(m => m.Role == "assistant");
    }

    [Fact]
    public async Task ProcessAsync_WithConversationId_ContinuesExistingConversation()
    {
        _llmMock
            .Setup(l => l.CompleteAsync(It.IsAny<IReadOnlyList<LLMMessage>>(), default))
            .ReturnsAsync("Reply");

        // First message — creates conversation
        var first = await _sut.ProcessAsync(new ChatRequest
        {
            UserMessage = "First",
            UserId      = "user-continue",
            Source      = "api"
        });

        // Second message with explicit ConversationId
        var second = await _sut.ProcessAsync(new ChatRequest
        {
            UserMessage    = "Second",
            UserId         = "user-continue",
            Source         = "api",
            ConversationId = first.ConversationId
        });

        second.ConversationId.Should().Be(first.ConversationId);

        var messages = await _db.Messages
            .Where(m => m.ConversationId == first.ConversationId).ToListAsync();
        messages.Should().HaveCount(4); // 2 turns × 2 messages
    }

    [Fact]
    public async Task ProcessAsync_WhatsApp_ResumesExistingConversationAutomatically()
    {
        _llmMock
            .Setup(l => l.CompleteAsync(It.IsAny<IReadOnlyList<LLMMessage>>(), default))
            .ReturnsAsync("Reply");

        var request = new ChatRequest
        {
            UserId = "+911234567890",
            Source = "whatsapp"
        };

        // First WhatsApp message — no ConversationId
        var r1 = await _sut.ProcessAsync(request with { UserMessage = "Hi" });

        // Second WhatsApp message — still no ConversationId
        var r2 = await _sut.ProcessAsync(request with { UserMessage = "How are you?" });

        // Must continue same conversation, not create a new one
        r2.ConversationId.Should().Be(r1.ConversationId);

        var messages = await _db.Messages
            .Where(m => m.ConversationId == r1.ConversationId).ToListAsync();
        messages.Should().HaveCount(4); // Both turns persisted in one conversation
    }

    [Fact]
    public async Task ProcessAsync_WhatsApp_FirstMessageCreatesConversation()
    {
        _llmMock
            .Setup(l => l.CompleteAsync(It.IsAny<IReadOnlyList<LLMMessage>>(), default))
            .ReturnsAsync("Reply");

        var response = await _sut.ProcessAsync(new ChatRequest
        {
            UserMessage = "Hello",
            UserId      = "+911111111111",
            Source      = "whatsapp"
        });

        var conversations = await _db.Conversations.ToListAsync();
        conversations.Should().HaveCount(1);
        conversations[0].Id.Should().Be(response.ConversationId);
    }

    [Fact]
    public async Task ProcessAsync_SystemPromptAlwaysFirstInContext()
    {
        IReadOnlyList<LLMMessage>? captured = null;

        _llmMock
            .Setup(l => l.CompleteAsync(It.IsAny<IReadOnlyList<LLMMessage>>(), default))
            .Callback<IReadOnlyList<LLMMessage>, CancellationToken>((msgs, _) => captured = msgs)
            .ReturnsAsync("OK");

        await _sut.ProcessAsync(new ChatRequest { UserMessage = "Test" });

        captured.Should().NotBeNullOrEmpty();
        captured![0].Role.Should().Be("system");
        captured[0].Content.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ProcessAsync_LLMError_BubblesException()
    {
        _llmMock
            .Setup(l => l.CompleteAsync(It.IsAny<IReadOnlyList<LLMMessage>>(), default))
            .ThrowsAsync(new HttpRequestException("Groq is unavailable"));

        var act = async () => await _sut.ProcessAsync(
            new ChatRequest { UserMessage = "Hi" });

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*Groq is unavailable*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ProcessAsync_EmptyOrWhitespaceMessage_ThrowsArgumentException(string? invalidMessage)
    {
        var act = async () => await _sut.ProcessAsync(
            new ChatRequest { UserMessage = invalidMessage! });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ProcessAsync_SlidingWindow_LimitsContextHistory()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["Chat:MaxHistoryMessages"] = "4"
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var sutWithConfig = new ChatOrchestrator(
            _db,
            _llmMock.Object,
            NullLogger<ChatOrchestrator>.Instance,
            config);

        IReadOnlyList<LLMMessage>? lastCapturedContext = null;
        _llmMock
            .Setup(l => l.CompleteAsync(It.IsAny<IReadOnlyList<LLMMessage>>(), default))
            .Callback<IReadOnlyList<LLMMessage>, CancellationToken>((msgs, _) => lastCapturedContext = msgs)
            .ReturnsAsync("Reply");

        // Seed 4 turns (8 messages) into one conversation
        var user = new User { ExternalId = "user-sliding", Source = "api" };
        _db.Users.Add(user);

        var conv = new Conversation { UserId = user.Id, Title = "Sliding Test" };
        for (int i = 1; i <= 4; i++)
        {
            conv.Messages.Add(new Message { ConversationId = conv.Id, Role = "user", Content = $"Turn {i} user", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i) });
            conv.Messages.Add(new Message { ConversationId = conv.Id, Role = "assistant", Content = $"Turn {i} assistant", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i).AddSeconds(1) });
        }
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();

        await sutWithConfig.ProcessAsync(new ChatRequest
        {
            UserMessage    = "New user message",
            UserId         = "user-sliding",
            ConversationId = conv.Id,
            Source         = "api"
        });

        lastCapturedContext.Should().NotBeNull();
        // Context should be: 1 System prompt + 4 history messages (sliding window) + 1 current user turn = 6 messages
        lastCapturedContext!.Count.Should().Be(6);
        lastCapturedContext[0].Role.Should().Be("system");
        lastCapturedContext[1].Content.Should().Be("Turn 3 user");
        lastCapturedContext[2].Content.Should().Be("Turn 3 assistant");
        lastCapturedContext[3].Content.Should().Be("Turn 4 user");
        lastCapturedContext[4].Content.Should().Be("Turn 4 assistant");
        lastCapturedContext[5].Content.Should().Be("New user message");
    }
}
