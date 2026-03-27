using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChatBackend.Infrastructure.Data;

/// <summary>
/// Allows EF Core tools to instantiate AppDbContext at design time without running
/// the full web host. Enables: dotnet ef migrations add / database update
/// from the Infrastructure project directory.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=chat_dev.db")
            .Options;

        return new AppDbContext(options);
    }
}
