using ChatBackend.Core.DTOs;
using ChatBackend.Core.Interfaces;
using ChatBackend.Core.Models;
using ChatBackend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChatBackend.Infrastructure.Orchestrator;

/// <summary>
/// Orchestrates the full chat lifecycle:
///   1. Resolve or create User
///   2. Resolve or create Conversation
///   3. Persist the inbound user message
///   4. Build full context window from history
///   5. Call Groq via ILLMClient
///   6. Persist the assistant reply
///   7. Return ChatResponse
///
/// Conversation continuity strategy:
///   - API callers pass ConversationId explicitly to resume a specific thread.
///   - WhatsApp callers omit ConversationId. The orchestrator automatically resumes
///     the user's most recent conversation, so every WhatsApp exchange feels like
///     one continuous thread rather than isolated Q&A sessions.
///
/// SQLite note: DateTimeOffset columns cannot be used in EF ORDER BY translations.
///   Sorting on DateTimeOffset is done in memory (LINQ to Objects) after fetching,
///   which is safe here because a user has few conversations and messages.
/// </summary>
public sealed class ChatOrchestrator(
    AppDbContext db,
    ILLMClient llm,
    ILogger<ChatOrchestrator> logger,
    Microsoft.Extensions.Configuration.IConfiguration? config = null,
    TimeProvider? timeProvider = null) : IChatOrchestrator
{
    private readonly int _maxHistoryMessages =
        int.TryParse(config?["Chat:MaxHistoryMessages"], out var max) && max > 0 ? max : 20;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private const string SystemPrompt =
        "You are a helpful, concise, and friendly AI assistant. " +
        "Answer in the language the user writes in. " +
        "Be direct and avoid unnecessary filler phrases.";

    public async Task<ChatResponse> ProcessAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var sanitizedMessage = request.UserMessage?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sanitizedMessage))
            throw new ArgumentException("UserMessage cannot be empty or whitespace.", nameof(request));

        var now = _timeProvider.GetUtcNow();

        // ── 1. Resolve or create User ─────────────────────────────────
        var externalId = string.IsNullOrWhiteSpace(request.UserId)
            ? "anonymous"
            : request.UserId;

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.ExternalId == externalId && u.Source == request.Source,
            cancellationToken);

        if (user is null)
        {
            user = new User { ExternalId = externalId, Source = request.Source, CreatedAt = now };
            db.Users.Add(user);
            logger.LogInformation(
                "New user created. ExternalId={ExternalId} Source={Source}",
                externalId, request.Source);
        }

        // ── 2. Resolve or create Conversation ─────────────────────────
        Conversation? conversation = null;

        if (!string.IsNullOrWhiteSpace(request.ConversationId))
        {
            // API callers supply a ConversationId to resume a specific thread.
            // Load messages without EF ORDER BY — sort in memory to avoid
            // SQLite's DateTimeOffset ORDER BY limitation.
            conversation = await db.Conversations
                .Include(c => c.Messages)
                .FirstOrDefaultAsync(
                    c => c.Id == request.ConversationId && c.UserId == user.Id,
                    cancellationToken);

            if (conversation is null)
                logger.LogWarning(
                    "ConversationId={Id} not found for user — starting new.",
                    request.ConversationId);
        }
        else
        {
            // WhatsApp / channel callers: resume the most recent conversation.
            var latestId = (await db.Conversations
                    .Where(c => c.UserId == user.Id)
                    .Select(c => new { c.Id, c.UpdatedAt })
                    .ToListAsync(cancellationToken))
                .OrderByDescending(c => c.UpdatedAt)   // LINQ to Objects — no SQL ORDER BY
                .FirstOrDefault()?.Id;

            if (latestId is not null)
            {
                conversation = await db.Conversations
                    .Include(c => c.Messages)           // no OrderBy here — sort below
                    .FirstOrDefaultAsync(c => c.Id == latestId, cancellationToken);
            }
        }

        if (conversation is null)
        {
            conversation = new Conversation
            {
                UserId    = user.Id,
                Title     = sanitizedMessage.Length > 60 ? string.Concat(sanitizedMessage.AsSpan(0, 60), "…") : sanitizedMessage,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Conversations.Add(conversation);
            logger.LogInformation(
                "New conversation created. Id={ConversationId}", conversation.Id);
        }

        // ── 3. Build LLM context window from existing history ─────────
        // Sort messages in memory and apply sliding context window budgeting.
        var history = conversation.Messages
            .OrderBy(m => m.CreatedAt)              // LINQ to Objects — safe
            .Select(m => new LLMMessage(m.Role, m.Content))
            .TakeLast(_maxHistoryMessages)
            .ToList();

        // Modern C# collection expression with spread operator
        IReadOnlyList<LLMMessage> contextMessages = [
            LLMMessage.System(SystemPrompt),
            .. history,
            LLMMessage.User(sanitizedMessage)
        ];

        // ── 4. Persist the inbound user message ───────────────────────
        var userMessage = new Message
        {
            ConversationId = conversation.Id,
            Role           = "user",
            Content        = sanitizedMessage,
            CreatedAt      = now
        };
        db.Messages.Add(userMessage);

        // ── 5. Call LLM ───────────────────────────────────────────────
        logger.LogInformation(
            "Calling LLM. Provider={Provider} ConversationId={ConvId} " +
            "History={History} TotalContext={Total}",
            llm.ProviderName, conversation.Id,
            history.Count, contextMessages.Count);

        var assistantText = await llm.CompleteAsync(contextMessages, cancellationToken);

        // ── 6. Persist the assistant reply ────────────────────────────
        var assistantMessage = new Message
        {
            ConversationId = conversation.Id,
            Role           = "assistant",
            Content        = assistantText,
            Provider       = llm.ProviderName,
            CreatedAt      = _timeProvider.GetUtcNow()
        };
        db.Messages.Add(assistantMessage);
        conversation.UpdatedAt = assistantMessage.CreatedAt;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Saved. ConversationId={ConvId} MessageId={MsgId}",
            conversation.Id, assistantMessage.Id);

        // ── 7. Return ─────────────────────────────────────────────────
        return new ChatResponse(
            AssistantMessage: assistantText,
            ConversationId:   conversation.Id,
            MessageId:        assistantMessage.Id,
            Provider:         llm.ProviderName,
            Timestamp:        assistantMessage.CreatedAt);
    }
}
