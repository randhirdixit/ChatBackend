using System.ComponentModel.DataAnnotations;
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

        // ── GET /api/conversations/{conversationId} ────────────────────
        // Lightweight read endpoint — returns conversation history
        group.MapGet("/conversations/{conversationId}", async (
            string conversationId,
            ChatBackend.Infrastructure.Data.AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var conversation = await db.Conversations
                .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
                .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

            if (conversation is null)
                return Results.NotFound(new { error = $"Conversation '{conversationId}' not found." });

            return Results.Ok(new
            {
                conversation.Id,
                conversation.Title,
                conversation.CreatedAt,
                conversation.UpdatedAt,
                Messages = conversation.Messages.Select(m => new
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


