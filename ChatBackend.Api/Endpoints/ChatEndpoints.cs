using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ChatBackend.Core.DTOs;
using ChatBackend.Core.Interfaces;
using ChatBackend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatBackend.Api.Endpoints;

public static class ChatEndpoints
{
    public static WebApplication MapChatEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api")
            .WithTags("Chat")
            .RequireRateLimiting("api");

        // ── POST /api/chat ─────────────────────────────────────────────
        group.MapPost("/chat", async (
            ChatRequest request,
            IChatOrchestrator orchestrator,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            // Manual validation (minimal APIs don't auto-validate data annotations)
            var validationResults = new List<ValidationResult>();
            var validationCtx    = new ValidationContext(request);

            if (!Validator.TryValidateObject(request, validationCtx, validationResults, true))
            {
                var errors = validationResults
                    .GroupBy(v => v.MemberNames.FirstOrDefault() ?? "General")
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(v => v.ErrorMessage ?? "Invalid").ToArray());

                return Results.ValidationProblem(errors);
            }

            logger.LogInformation(
                "Chat request received. UserId={UserId} ConvId={ConvId} Source={Source}",
                request.UserId ?? "anonymous",
                request.ConversationId ?? "new",
                request.Source);

            var response = await orchestrator.ProcessAsync(request, cancellationToken);
            return Results.Ok(response);
        })
        .WithName("PostChat")
        .WithSummary("Send a chat message and receive an AI response")
        .WithDescription("""
            Sends a user message to Groq Llama 3,
            persists the conversation in SQLite, and returns the assistant's reply.

            - Omit **conversationId** to start a new conversation.
            - Provide **conversationId** from a prior response to continue a thread.
            - **userId** defaults to 'anonymous'; supply your own identifier to link
              conversations to a specific user.
            """)
        .Produces<ChatResponse>(StatusCodes.Status200OK)
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        // ── POST /api/chat/stream (Server-Sent Events) ─────────────────
        group.MapPost("/chat/stream", async (
            ChatRequest request,
            ILLMClient llm,
            AppDbContext db,
            HttpContext httpContext,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var validationResults = new List<ValidationResult>();
            var validationCtx    = new ValidationContext(request);

            if (!Validator.TryValidateObject(request, validationCtx, validationResults, true))
            {
                var errors = validationResults
                    .GroupBy(v => v.MemberNames.FirstOrDefault() ?? "General")
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(v => v.ErrorMessage ?? "Invalid").ToArray());

                return Results.ValidationProblem(errors);
            }

            var sanitizedMessage = request.UserMessage.Trim();

            // Resolve or create user
            var externalId = string.IsNullOrWhiteSpace(request.UserId) ? "anonymous" : request.UserId;
            var user = await db.Users.FirstOrDefaultAsync(
                u => u.ExternalId == externalId && u.Source == request.Source,
                cancellationToken);

            if (user is null)
            {
                user = new ChatBackend.Core.Models.User { ExternalId = externalId, Source = request.Source };
                db.Users.Add(user);
            }

            // Resolve conversation
            ChatBackend.Core.Models.Conversation? conversation = null;
            if (!string.IsNullOrWhiteSpace(request.ConversationId))
            {
                conversation = await db.Conversations
                    .Include(c => c.Messages)
                    .FirstOrDefaultAsync(
                        c => c.Id == request.ConversationId && c.UserId == user.Id,
                        cancellationToken);
            }

            if (conversation is null)
            {
                conversation = new ChatBackend.Core.Models.Conversation
                {
                    UserId = user.Id,
                    Title  = sanitizedMessage.Length > 60
                        ? sanitizedMessage[..60] + "…"
                        : sanitizedMessage
                };
                db.Conversations.Add(conversation);
            }

            // Build context messages with sliding window
            var history = conversation.Messages
                .OrderBy(m => m.CreatedAt)
                .Select(m => new LLMMessage(m.Role, m.Content))
                .TakeLast(20)
                .ToList();

            IReadOnlyList<LLMMessage> contextMessages = [
                LLMMessage.System(
                    "You are a helpful, concise, and friendly AI assistant. " +
                    "Answer in the language the user writes in. " +
                    "Be direct and avoid unnecessary filler phrases."),
                .. history,
                LLMMessage.User(sanitizedMessage)
            ];

            // Persist inbound user turn
            var userMessage = new ChatBackend.Core.Models.Message
            {
                ConversationId = conversation.Id,
                Role           = "user",
                Content        = sanitizedMessage
            };
            db.Messages.Add(userMessage);

            // Configure SSE response
            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Connection = "keep-alive";

            var assistantContent = new System.Text.StringBuilder();

            await foreach (var token in llm.StreamAsync(contextMessages, cancellationToken))
            {
                assistantContent.Append(token);
                var ssePayload = $"data: {JsonSerializer.Serialize(new { token, conversationId = conversation.Id })}\n\n";
                await httpContext.Response.WriteAsync(ssePayload, cancellationToken);
                await httpContext.Response.Body.FlushAsync(cancellationToken);
            }

            // End SSE stream
            await httpContext.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);

            // Persist assistant reply
            var assistantMessage = new ChatBackend.Core.Models.Message
            {
                ConversationId = conversation.Id,
                Role           = "assistant",
                Content        = assistantContent.ToString(),
                Provider       = llm.ProviderName
            };
            db.Messages.Add(assistantMessage);
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            return Results.Empty;
        })
        .WithName("PostChatStream")
        .WithSummary("Stream chat response token-by-token using Server-Sent Events (SSE)")
        .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
        .ProducesValidationProblem();

        // ── GET /api/conversations/{conversationId} ────────────────────
        // Lightweight read endpoint — returns conversation history
        group.MapGet("/conversations/{conversationId}", async (
            string conversationId,
            ChatBackend.Infrastructure.Data.AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var conversation = await db.Conversations
                .Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

            if (conversation is null)
                return Results.NotFound(new { error = $"Conversation '{conversationId}' not found." });

            return Results.Ok(new
            {
                conversation.Id,
                conversation.Title,
                conversation.CreatedAt,
                conversation.UpdatedAt,
                Messages = conversation.Messages
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => new
                    {
                        m.Id,
                        m.Role,
                        m.Content,
                        m.Provider,
                        m.CreatedAt
                    })
            });
        })
        .WithName("GetConversation")
        .WithSummary("Retrieve full conversation history by ID")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}


