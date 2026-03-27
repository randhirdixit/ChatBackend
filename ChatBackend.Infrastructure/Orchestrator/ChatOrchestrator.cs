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
public sealed class ChatOrchestrator : IChatOrchestrator
{
    private readonly AppDbContext _db;
    private readonly ILLMClient   _llm;
    private readonly ILogger<ChatOrchestrator> _logger;

    private const string SystemPrompt =
        "You are a helpful, concise, and friendly AI assistant. " +
        "Answer in the language the user writes in. " +
        "Be direct and avoid unnecessary filler phrases.";

    public ChatOrchestrator(
        AppDbContext db,
        ILLMClient   llm,
        ILogger<ChatOrchestrator> logger)
    {
        _db     = db;
        _llm    = llm;
        _logger = logger;
    }

    public async Task<ChatResponse> ProcessAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        // ── 1. Resolve or create User ─────────────────────────────────
        var externalId = string.IsNullOrWhiteSpace(request.UserId)
            ? "anonymous"
            : request.UserId;

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.ExternalId == externalId && u.Source == request.Source,
            cancellationToken);

        if (user is null)
        {
            user = new User { ExternalId = externalId, Source = request.Source };
            _db.Users.Add(user);
            _logger.LogInformation(
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
            conversation = await _db.Conversations
                .Include(c => c.Messages)
                .FirstOrDefaultAsync(
                    c => c.Id == request.ConversationId && c.UserId == user.Id,
                    cancellationToken);

            if (conversation is null)
                _logger.LogWarning(
                    "ConversationId={Id} not found for user — starting new.",
                    request.ConversationId);
        }
        else
        {
            // WhatsApp / channel callers: resume the most recent conversation.
            //
            // SQLite cannot sort DateTimeOffset in SQL, so we:
            //   Step 1 — fetch only Id + UpdatedAt for this user (lightweight)
            //   Step 2 — sort in memory to find the latest
            //   Step 3 — load that conversation with its messages by Id
            //
            // This avoids bringing all message content into memory unnecessarily.
            var latestId = (await _db.Conversations
                    .Where(c => c.UserId == user.Id)
                    .Select(c => new { c.Id, c.UpdatedAt })
                    .ToListAsync(cancellationToken))
                .OrderByDescending(c => c.UpdatedAt)   // LINQ to Objects — no SQL ORDER BY
                .FirstOrDefault()?.Id;

            if (latestId is not null)
            {
                conversation = await _db.Conversations
                    .Include(c => c.Messages)           // no OrderBy here — sort below
                    .FirstOrDefaultAsync(c => c.Id == latestId, cancellationToken);
            }
        }

        if (conversation is null)
        {
            conversation = new Conversation
            {
                UserId = user.Id,
                Title  = request.UserMessage.Length > 60
                    ? request.UserMessage[..60] + "…"
                    : request.UserMessage
            };
            _db.Conversations.Add(conversation);
            _logger.LogInformation(
                "New conversation created. Id={ConversationId}", conversation.Id);
        }

        // ── 3. Persist the inbound user message ───────────────────────
        var userMessage = new Message
        {
            ConversationId = conversation.Id,
            Role           = "user",
            Content        = request.UserMessage
        };
        _db.Messages.Add(userMessage);

        // ── 4. Build LLM context window from full history ─────────────
        // Sort messages in memory — avoids SQLite DateTimeOffset ORDER BY error.
        var history = conversation.Messages
            .OrderBy(m => m.CreatedAt)              // LINQ to Objects — safe
            .Select(m => new LLMMessage(m.Role, m.Content))
            .ToList();

        var contextMessages = new List<LLMMessage>(history.Count + 2)
        {
            LLMMessage.System(SystemPrompt)
        };
        contextMessages.AddRange(history);
        contextMessages.Add(LLMMessage.User(request.UserMessage));

        // ── 5. Call LLM ───────────────────────────────────────────────
        _logger.LogInformation(
            "Calling LLM. Provider={Provider} ConversationId={ConvId} " +
            "History={History} TotalContext={Total}",
            _llm.ProviderName, conversation.Id,
            history.Count, contextMessages.Count);

        var assistantText = await _llm.CompleteAsync(contextMessages, cancellationToken);

        // ── 6. Persist the assistant reply ────────────────────────────
        var assistantMessage = new Message
        {
            ConversationId = conversation.Id,
            Role           = "assistant",
            Content        = assistantText,
            Provider       = _llm.ProviderName
        };
        _db.Messages.Add(assistantMessage);
        conversation.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Saved. ConversationId={ConvId} MessageId={MsgId}",
            conversation.Id, assistantMessage.Id);

        // ── 7. Return ─────────────────────────────────────────────────
        return new ChatResponse(
            AssistantMessage: assistantText,
            ConversationId:   conversation.Id,
            MessageId:        assistantMessage.Id,
            Provider:         _llm.ProviderName,
            Timestamp:        assistantMessage.CreatedAt);
    }
}
