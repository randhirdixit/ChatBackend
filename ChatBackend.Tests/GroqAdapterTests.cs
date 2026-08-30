using System.Net;
using System.Text;
using ChatBackend.Core.DTOs;
using ChatBackend.Infrastructure.LLM;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace ChatBackend.Tests;

public class GroqAdapterTests
{
    private readonly IConfiguration _config;

    public GroqAdapterTests()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LLM:Groq:Model"] = "openai/gpt-oss-120b"
            })
            .Build();
    }

    [Fact]
    public async Task CompleteAsync_ValidResponse_ReturnsAssistantContent()
    {
        var responseJson = """
        {
            "id": "chatcmpl-123",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "Hello from Groq LLM!"
                    },
                    "finish_reason": "stop"
                }
            ],
            "usage": {
                "prompt_tokens": 10,
                "completion_tokens": 5,
                "total_tokens": 15
            }
        }
        """;

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
                Content    = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("https://api.groq.com/openai/v1/")
        };

        var adapter = new GroqAdapter(httpClient, _config, NullLogger<GroqAdapter>.Instance);

        var messages = new List<LLMMessage>
        {
            LLMMessage.System("Be helpful."),
            LLMMessage.User("Hi!")
        };

        var result = await adapter.CompleteAsync(messages);

        result.Should().Be("Hello from Groq LLM!");
    }

    [Fact]
    public async Task CompleteAsync_ErrorStatus_ThrowsHttpRequestException()
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
                StatusCode = HttpStatusCode.BadRequest,
                Content    = new StringContent("{\"error\":\"Model not supported\"}")
            });

        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("https://api.groq.com/openai/v1/")
        };

        var adapter = new GroqAdapter(httpClient, _config, NullLogger<GroqAdapter>.Instance);

        var act = async () => await adapter.CompleteAsync(new List<LLMMessage> { LLMMessage.User("Hi") });

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*Groq returned 400*");
    }

    [Fact]
    public async Task StreamAsync_ValidSSEChunks_YieldsTokens()
    {
        var sseData = """
        data: {"choices":[{"delta":{"content":"Hello"},"finish_reason":null}]}

        data: {"choices":[{"delta":{"content":" world"},"finish_reason":null}]}

        data: [DONE]

        """;

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
                Content    = new StringContent(sseData, Encoding.UTF8, "text/event-stream")
            });

        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("https://api.groq.com/openai/v1/")
        };

        var adapter = new GroqAdapter(httpClient, _config, NullLogger<GroqAdapter>.Instance);

        var tokens = new List<string>();
        await foreach (var token in adapter.StreamAsync(new List<LLMMessage> { LLMMessage.User("Hi") }))
        {
            tokens.Add(token);
        }

        tokens.Should().ContainInOrder("Hello", " world");
    }
}
