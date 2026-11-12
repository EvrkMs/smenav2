using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Smena.Server.Database;

/// <summary>
/// Для <c>dotnet ef migrations add</c>: позволяет строить миграции, не запуская
/// сервер и не имея доступной базы. Строка подключения здесь не используется.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SmenaDbContext>
{
    public SmenaDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SmenaDbContext>();
        SmenaDbContext.Configure(builder, "Host=design-time-only");
        return new SmenaDbContext(builder.Options);
    }
}
