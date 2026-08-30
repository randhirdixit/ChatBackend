using ChatBackend.Core.Interfaces;
using ChatBackend.Infrastructure.Data;
using ChatBackend.Infrastructure.LLM;
using ChatBackend.Infrastructure.Meta;
using ChatBackend.Infrastructure.Orchestrator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBackend.Infrastructure.Extensions;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration config)
    {
        // ── SQLite / EF Core ──────────────────────────────────────────
        var connString = config.GetConnectionString("SQLite") ?? "Data Source=chat.db";
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseSqlite(connString));

        // ── Groq HttpClient ───────────────────────────────────────────
        var groqApiKey = config["LLM:Groq:ApiKey"]
            ?? throw new InvalidOperationException(
                "LLM:Groq:ApiKey is not configured. " +
                "Run: dotnet user-secrets set \"LLM:Groq:ApiKey\" \"gsk_xxx\"");

        var groqBaseUrl = config["LLM:Groq:BaseUrl"] ?? "https://api.groq.com/openai/v1/";

        // AddHttpClient<GroqAdapter> registers GroqAdapter as a typed client:
        // when GroqAdapter is resolved from DI, it receives an HttpClient that
        // has BaseAddress and Authorization pre-configured by this factory.
        services.AddHttpClient<GroqAdapter>(client =>
        {
            client.BaseAddress = new Uri(groqBaseUrl);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {groqApiKey}");
            client.Timeout = TimeSpan.FromSeconds(60);
        })
        .AddStandardResilienceHandler(opts =>
        {
            opts.Retry.MaxRetryAttempts = 3;
            opts.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
        });

        // IMPORTANT: Use a factory delegate, NOT AddScoped<ILLMClient, GroqAdapter>().
        //
        // AddScoped<ILLMClient, GroqAdapter>() resolves GroqAdapter through standard
        // constructor injection, which injects a plain HttpClient with NO BaseAddress —
        // bypassing the typed client configuration above.
        //
        // sp.GetRequiredService<GroqAdapter>() resolves through the typed client
        // factory, so GroqAdapter receives the correctly configured HttpClient.
        services.AddScoped<ILLMClient>(sp => sp.GetRequiredService<GroqAdapter>());

        // ── Orchestrator ──────────────────────────────────────────────
        services.AddScoped<IChatOrchestrator, ChatOrchestrator>();

        // ── Meta WhatsApp handler (Singleton + own scope per message) ─
        // HttpClient registration provides a client instance for outbound calls.
        // Singleton lifetime is safe because the handler creates its own DI scope
        // per inbound message via IServiceScopeFactory.
        services.AddHttpClient<MetaWhatsAppHandler>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddStandardResilienceHandler(opts =>
        {
            opts.Retry.MaxRetryAttempts = 3;
            opts.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
        });
        services.AddSingleton<MetaWhatsAppHandler>();

        // ── Background Channel & TimeProvider ─────────────────────────
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<WhatsAppMessageChannel>();
        services.AddHostedService<WhatsAppMessageWorker>();

        return services;
    }
}
