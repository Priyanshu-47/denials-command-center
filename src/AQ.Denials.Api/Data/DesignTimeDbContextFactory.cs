using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AQ.Denials.Api.Data;

/// <summary>
/// Used by the <c>dotnet ef</c> command line, so creating a migration does not need a running
/// Postgres or a real connection string.
/// </summary>
/// <remarks>
/// <para>
/// The default is a placeholder that connects nowhere — <c>creating</c> a migration only needs
/// the model, not a database, and a developer must not need credentials to add one.
/// </para>
/// <para>
/// <c>database update</c> is the exception: it has to reach a real server. When
/// <c>ConnectionStrings__Denials</c> is set in the environment it wins over the placeholder, so
/// `dotnet ef database update` applies the same connection the running API would use instead of
/// silently migrating a different database than the one in service. At runtime the host reads
/// that variable and refuses to start without it; nothing here is a hard-coded connection.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string Placeholder =
        "Host=localhost;Database=denials;Username=design;Password=design-only";

    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Denials")
                       ?? Placeholder)
            .Options);
}
