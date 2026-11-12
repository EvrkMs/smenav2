using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Smena.Core.Telegram;
using Smena.Server.Database;
using Smena.Server.Notifications;

namespace Smena.Server.MiniApp;

/// <summary>
/// Ключ, которым проверяется подпись Telegram. Выводится из токена бота один раз
/// при старте. Без токена бота MiniApp выключен: проверить подпись нечем.
/// </summary>
internal sealed class MiniAppKey(IOptions<TelegramOptions> options)
{
    private readonly byte[]? _key = options.Value.IsConfigured
        ? MiniAppInitData.DeriveSecretKey(options.Value.BotToken!)
        : null;

    public bool IsConfigured => _key is not null;

    public ReadOnlySpan<byte> Value => _key;
}

/// <summary>
/// Вход сотрудника: заголовок <c>Authorization: tma &lt;initData&gt;</c>, где initData —
/// строка, которую Telegram выдал MiniApp при запуске. Сессий сервер не хранит:
/// подпись проверяется на каждом запросе, это один HMAC.
/// </summary>
internal sealed class MiniAppAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    MiniAppKey key,
    TimeProvider time,
    SmenaDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "MiniApp";
    public const string EmployeePolicy = "Employee";

    /// <summary>Счёт сотрудника в учёте — чтобы обработчикам не искать его заново.</summary>
    public const string AccountClaim = "smena:account";

    /// <summary>Имя пользователя в Telegram на момент входа.</summary>
    public const string UsernameClaim = "smena:tg-username";

    private const string Prefix = "tma ";

    /// <summary>
    /// Сколько действует строка после запуска MiniApp. Telegram выдаёт её один раз
    /// на запуск, поэтому срок — с запасом на долго открытое приложение; после
    /// него достаточно открыть MiniApp заново.
    /// </summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private static readonly Func<SmenaDbContext, long, CancellationToken, Task<EmployeeIdentity?>> FindEmployeeQuery =
        EF.CompileAsyncQuery((SmenaDbContext context, long telegramId, CancellationToken _) =>
            context.Employees
                .AsNoTracking()
                .Where(employee => employee.TelegramId == telegramId && employee.IsActive)
                .Select(employee => new EmployeeIdentity(employee.Id, employee.Name, employee.Account.Id))
                .FirstOrDefault());

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization;
        if (header.Count != 1 || header[0] is not { } value || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        if (!key.IsConfigured)
        {
            return AuthenticateResult.Fail("MiniApp выключен: не задан токен бота.");
        }

        if (!MiniAppInitData.TryValidate(value[Prefix.Length..].Trim(), key.Value, time.GetUtcNow(), MaxAge, out var user))
        {
            return AuthenticateResult.Fail("Подпись Telegram не сошлась или устарела.");
        }

        // Отключённый сотрудник теряет доступ сразу, а не когда истечёт подпись.
        var employee = await FindEmployeeQuery(db, user.Id, Context.RequestAborted);
        if (employee is null)
        {
            return AuthenticateResult.Fail("Пользователя Telegram нет среди активных сотрудников.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, employee.Id.ToString()),
                new Claim(ClaimTypes.Name, employee.Name),
                new Claim(AccountClaim, employee.AccountId.ToString(CultureInfo.InvariantCulture)),
                new Claim(UsernameClaim, user.Username ?? string.Empty),
            ],
            SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private sealed record EmployeeIdentity(Guid Id, string Name, int AccountId);
}

internal static class MiniAppSetup
{
    public static IServiceCollection AddMiniAppAuthentication(this IServiceCollection services)
    {
        services.AddSingleton<MiniAppKey>();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, MiniAppAuthenticationHandler>(
                MiniAppAuthenticationHandler.SchemeName, configureOptions: null);

        // Только эта схема: ни токен кассы, ни cookie панели в кабинет сотрудника не пускают.
        services.AddAuthorizationBuilder()
            .AddPolicy(MiniAppAuthenticationHandler.EmployeePolicy, static policy => policy
                .AddAuthenticationSchemes(MiniAppAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());

        return services;
    }
}
