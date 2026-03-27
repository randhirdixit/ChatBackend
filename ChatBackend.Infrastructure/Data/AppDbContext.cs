using ChatBackend.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatBackend.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User>         Users         => Set<User>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message>      Messages      => Set<Message>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── User ─────────────────────────────────────────────────────
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            // Composite unique index: same phone on different channels = different users
            e.HasIndex(u => new { u.ExternalId, u.Source }).IsUnique();
            e.Property(u => u.ExternalId).IsRequired().HasMaxLength(256);
            e.Property(u => u.Source).IsRequired().HasMaxLength(50);
        });

        // ── Conversation ──────────────────────────────────────────────
        modelBuilder.Entity<Conversation>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasOne(c => c.User)
             .WithMany(u => u.Conversations)
             .HasForeignKey(c => c.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Property(c => c.Title).HasMaxLength(256);
        });

        // ── Message ───────────────────────────────────────────────────
        modelBuilder.Entity<Message>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasOne(m => m.Conversation)
             .WithMany(c => c.Messages)
             .HasForeignKey(m => m.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Property(m => m.Role).IsRequired().HasMaxLength(20);
            e.Property(m => m.Provider).HasMaxLength(50);
        });
    }
}
