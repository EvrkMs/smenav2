using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Smena.Server.Database;

internal static partial class DatabaseSetup
{
    public static IServiceCollection AddSmenaDatabase(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();
        services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .ValidateOnStart();

        // Пул контекстов: экземпляры переиспользуются между запросами,
        // а не создаются и настраиваются заново на каждый.
        services.AddDbContextPool<SmenaDbContext>(static (provider, builder) =>
        {
            var options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            SmenaDbContext.Configure(builder, options.ConnectionString);
        });

        return services;
    }

    public static async Task MigrateDatabaseAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.MigrateOnStartup)
        {
            return;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Smena.Database");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SmenaDbContext>();

        var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        if (pending.Length == 0)
        {
            LogUpToDate(logger);
            return;
        }

        LogApplying(logger, pending.Length, pending[^1]);
        await db.Database.MigrateAsync();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "База данных актуальна, миграций к применению нет")]
    private static partial void LogUpToDate(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Применяю миграции: {Count}, последняя — {Last}")]
    private static partial void LogApplying(ILogger logger, int count, string last);
}
