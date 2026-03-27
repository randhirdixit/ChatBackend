using ChatBackend.Api.Endpoints;
using ChatBackend.Api.Middleware;
using ChatBackend.Infrastructure.Data;
using ChatBackend.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json",
        optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>(optional: true);

builder.Logging
    .ClearProviders()
    .AddConsole()
    .AddDebug();

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opts =>
    opts.SwaggerDoc("v1", new()
    {
        Title       = "ChatBackend API",
        Version     = "v1",
        Description = "WhatsApp AI chat backend — Groq Llama 3 + Meta Cloud API + SQLite"
    }));

// Fixed-window rate limit: 30 req/min per IP on /api/* routes
builder.Services.AddRateLimiter(opts =>
{
    opts.AddPolicy("api", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 30,
                Window               = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 5
            }));

    opts.OnRejected = async (ctx, token) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsync(
            "Rate limit exceeded. Please wait before retrying.", token);
    };
});

builder.Services.AddCors(opts =>
    opts.AddDefaultPolicy(p =>
        p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// Auto-migrate SQLite on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    app.Logger.LogInformation("Database ready. Provider={Provider}",
        db.Database.ProviderName);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseHttpsRedirection();

app.MapChatEndpoints();
app.MapWhatsAppEndpoints();

app.MapGet("/health", () => Results.Ok(new
{
    status    = "healthy",
    timestamp = DateTimeOffset.UtcNow,
    env       = app.Environment.EnvironmentName
}))
.WithName("HealthCheck")
.WithTags("Health");

app.Run();

public partial class Program { }
