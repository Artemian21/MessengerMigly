using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Messenger.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef migrations add`; no database connection is opened.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MessengerDbContext>
{
    public MessengerDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MessengerDbContext>()
            .UseNpgsql("Host=localhost;Database=messenger;Username=messenger;Password=messenger")
            .UseSnakeCaseNamingConvention()
            .Options);
}
