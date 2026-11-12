using System.Net.Http.Json;
using DotNet.Testcontainers.Configurations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Smena.Server.Database;
using Smena.Server.Notifications;
using Smena.Server.Tests;
using Smena.Server.Tests.Notifications;
using Testcontainers.PostgreSql;

// База одна на все тесты, а балансы сейфа и банка общие, поэтому тесты сервера
// идут по очереди (см. xunit.runner.json). Их немного, и основное время занимает
// запуск контейнера.
[assembly: AssemblyFixture(typeof(SmenaApp))]

namespace Smena.Server.Tests;

/// <summary>
/// Настоящий сервер поверх настоящего PostgreSQL в одноразовом контейнере.
/// Поднимается один раз на весь прогон тестов; миграции применяет сам сервер
/// при старте — так же, как в работе.
/// </summary>
public sealed class SmenaApp : IAsyncLifetime
{
    // Docker Hub недоступен напрямую, поэтому образ берётся с зеркала.
    private const string PostgresImage = "mirror.gcr.io/library/postgres:18-alpine";

    /// <summary>Токен устройства, которое сервер создаёт при старте из настройки Cashier:BootstrapToken.</summary>
    public const string CashierToken = "test-cashier-token-0123456789-abcdefghij";

    static SmenaApp()
    {
        // Ryuk — служебный контейнер Testcontainers, убирающий забытые контейнеры.
        // Его образ есть только на Docker Hub, который отсюда недоступен.
        // Контейнер базы мы останавливаем сами в DisposeAsync; остаться он может,
        // только если тестовый процесс убит принудительно.
        TestcontainersSettings.ResourceReaperEnabled = false;
    }

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage).Build();
    private WebApplicationFactory<Program>? _factory;

    private WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Фикстура ещё не инициализирована.");

    public HttpClient CreateClient() => Factory.CreateClient();

    public HttpClient CreateClient(WebApplicationFactoryClientOptions options) => Factory.CreateClient(options);

    /// <summary>Отдельный контекст для проверки того, что реально лежит в базе.</summary>
    public async Task<T> QueryDatabaseAsync<T>(Func<SmenaDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<SmenaDbContext>());
    }

    /// <summary>Выполняет действие в области запроса — с теми же сервисами, что получает обработчик API.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>
    /// Убирает всё, что тесты записали в учёт. Справочники, системные счета и
    /// настройки остаются. Тесты сервера идут по очереди, поэтому каждый начинает
    /// с пустого учёта и не видит чужих операций.
    /// </summary>
    /// <param name="minShiftPhotos">
    /// Сколько фото требует смена. По умолчанию — как в работе; тесты денег
    /// передают 0, чтобы не загружать снимки перед каждым закрытием смены.
    /// </param>
    public Task ResetLedgerAsync(int minShiftPhotos = 3)
    {
        Telegram.Reset();

        // Строки фото уберёт TRUNCATE ниже, файлы — мы сами.
        if (Directory.Exists(PhotoDirectory))
        {
            Directory.Delete(PhotoDirectory, recursive: true);
        }

        return ResetDatabaseAsync(minShiftPhotos);
    }

    private Task<int> ResetDatabaseAsync(int minShiftPhotos) => QueryDatabaseAsync(async db =>
    {
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE operations CASCADE; TRUNCATE photo_sessions CASCADE; " +
            "DELETE FROM categories WHERE code IS NULL; DELETE FROM employees; " +
            "UPDATE receipt_channels SET delay_days = 1, fee_percent = 0 WHERE id = 1; " +
            "UPDATE receipt_channels SET delay_days = NULL, fee_percent = NULL WHERE id = 2; " +
            "UPDATE club_settings SET telegram_chat_id = NULL, telegram_reports_thread_id = NULL, telegram_safe_thread_id = NULL, " +
            "telegram_expenses_thread_id = NULL, telegram_salary_chat_id = NULL, cash_float = 1000, " +
            "max_employees_per_shift = 3, max_hours_per_shift = 12, min_shift_photos = 3, photo_max_age_hours = 5, " +
            "time_zone_id = 'Europe/Moscow'; " +
            "UPDATE root_users SET failed_logins = 0, locked_until = NULL; " +
            "DELETE FROM devices WHERE name <> 'Касса';");

        if (minShiftPhotos != 3)
        {
            await db.Database.ExecuteSqlAsync($"UPDATE club_settings SET min_shift_photos = {minShiftPhotos}");
        }

        return 0;
    });

    /// <summary>Пароль пользователя «root», которого сервер создаёт при старте из Root:BootstrapPassword.</summary>
    public const string RootPassword = "test-root-password";

    /// <summary>Клиент, вошедший в панель: cookie хранится в самом клиенте.</summary>
    public async Task<HttpClient> CreateRootClientAsync()
    {
        var client = Factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/root/login", new { username = "root", password = RootPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Токен бота: им сервер проверяет подпись Telegram при входе в MiniApp. В сеть с ним никто не ходит.</summary>
    public const string BotToken = "123456:TEST-token-for-server-tests";

    /// <summary>
    /// Строка initData, какую Telegram выдал бы MiniApp, открытому пользователем
    /// <paramref name="telegramId"/>: подписана токеном бота.
    /// </summary>
    public static string SignInitData(long telegramId, string? username = null, DateTimeOffset? issuedAt = null, string? botToken = null)
    {
        var user = username is null
            ? $$"""{"id":{{telegramId}},"first_name":"Тест"}"""
            : $$"""{"id":{{telegramId}},"first_name":"Тест","username":"{{username}}"}""";

        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["query_id"] = "AAHdF6IQAAAAAN0XohDhrOrc",
            ["user"] = user,
        };

        var check = string.Join('\n', fields.Select(static field => field.Key + "=" + field.Value));
        var secret = System.Security.Cryptography.HMACSHA256.HashData("WebAppData"u8, System.Text.Encoding.UTF8.GetBytes(botToken ?? BotToken));
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(secret, System.Text.Encoding.UTF8.GetBytes(check)));

        return string.Join('&', fields.Select(static field => field.Key + "=" + System.Net.WebUtility.UrlEncode(field.Value))) + "&hash=" + hash;
    }

    /// <summary>Клиент MiniApp, открытого пользователем Telegram <paramref name="telegramId"/>.</summary>
    public HttpClient CreateMiniAppClient(long telegramId, string? username = null)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "tma " + SignInitData(telegramId, username));
        return client;
    }

    /// <summary>Поддельный Telegram: сервер шлёт сообщения в него, а не в сеть.</summary>
    public FakeTelegramClient Telegram { get; } = new();

    /// <summary>Клиент с токеном устройства кассы — как настоящий клиент на кассе.</summary>
    public HttpClient CreateCashierClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CashierToken);
        return client;
    }

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Database:ConnectionString", _postgres.GetConnectionString())
            .UseSetting("Cashier:BootstrapToken", CashierToken)
            .UseSetting("Root:BootstrapPassword", RootPassword)
            .UseSetting("Photos:StoragePath", PhotoDirectory)
            .UseSetting("Photos:PublicBaseUrl", PublicBaseUrl)
            .UseSetting("Telegram:BotToken", BotToken)
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<ITelegramClient>(Telegram);

                // С токеном бота сервер запустил бы фоновую отправку очереди. Тесты
                // разбирают очередь сами, по шагам, — фоновый отправитель им мешал бы.
                services.Remove(services.Single(static service => service.ImplementationType?.Name == "OutboxSenderService"));
            }));

        // Первый клиент запускает сервер и тем самым миграции.
        using var client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();

        if (Directory.Exists(PhotoDirectory))
        {
            Directory.Delete(PhotoDirectory, recursive: true);
        }
    }

    /// <summary>Адрес, который сервер подставляет в ссылку для QR.</summary>
    public const string PublicBaseUrl = "http://club.test:5080";

    /// <summary>Каталог с файлами фото — свой на каждый прогон, удаляется в конце.</summary>
    public string PhotoDirectory { get; } = Path.Combine(Path.GetTempPath(), "smena-tests-" + Guid.NewGuid().ToString("N"));
}
