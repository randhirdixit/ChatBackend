using System.Text.Json;

namespace ChatBackend.Api.Middleware;

/// <summary>
/// Catches all unhandled exceptions and returns a standardised RFC 7807 ProblemDetails
/// JSON response. Stack traces are logged server-side but never sent to the client.
/// </summary>
public sealed class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger)
    {
        _next   = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected — not an error
            _logger.LogInformation("Request cancelled by client. Path={Path}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("not implemented", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(ex, "Configuration or not-implemented error.");
            await WriteProblemAsync(context, StatusCodes.Status501NotImplemented,
                "Feature not available",
                "This feature is not yet configured or implemented on this server.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Upstream HTTP error calling LLM provider.");
            await WriteProblemAsync(context, StatusCodes.Status502BadGateway,
                "LLM provider error",
                "The AI provider returned an error. Please try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception. Path={Path}", context.Request.Path);
            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError,
                "Internal server error",
                "An unexpected error occurred. Please try again later.");
        }
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail)
    {
        context.Response.StatusCode  = statusCode;
        context.Response.ContentType = "application/problem+json";

        var problem = new
        {
            type     = $"https://httpstatuses.com/{statusCode}",
            title,
            status   = statusCode,
            detail,
            instance = context.Request.Path.Value,
            traceId  = context.TraceIdentifier
        };

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(problem, _jsonOptions));
    }
}
