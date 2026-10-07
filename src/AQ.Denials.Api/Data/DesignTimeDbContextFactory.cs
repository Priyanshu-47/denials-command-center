using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AQ.Denials.Api.Data;

/// <summary>
/// Used only by the <c>dotnet ef</c> command line, so creating a migration does not need a
/// running Postgres or a real connection string.
/// </summary>
/// <remarks>
/// The placeholder connection string is never used at runtime — the host reads
/// <c>ConnectionStrings__Denials</c> from the environment and refuses to start without it.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=denials;Username=design;Password=design-only")
            .Options);
}
