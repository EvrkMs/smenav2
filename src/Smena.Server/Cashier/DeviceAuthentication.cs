using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Smena.Server.Database;

namespace Smena.Server.Cashier;

/// <summary>
/// Авторизация кассы: заголовок <c>Authorization: Bearer &lt;токен устройства&gt;</c>.
/// </summary>
internal sealed class DeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SmenaDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Device";
    public const string CashierPolicy = "Cashier";

    private const string Prefix = "Bearer ";

    private static readonly Func<SmenaDbContext, byte[], CancellationToken, Task<DeviceIdentity?>> FindDeviceQuery =
        EF.CompileAsyncQuery((SmenaDbContext context, byte[] hash, CancellationToken _) =>
            context.Devices
                .AsNoTracking()
                .Where(device => device.TokenHash == hash && device.RevokedAt == null)
                .Select(device => new DeviceIdentity(device.Id, device.Name))
                .FirstOrDefault());

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryReadTokenHash(out var hash))
        {
            return AuthenticateResult.NoResult();
        }

        var device = await FindDeviceQuery(db, hash, Context.RequestAborted);
        if (device is null)
        {
            return AuthenticateResult.Fail("Неизвестный или отозванный токен устройства.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, device.Id.ToString()),
                new Claim(ClaimTypes.Name, device.Name),
            ],
            SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    // Разбор заголовка вынесен в синхронный метод: он работает со Span,
    // а Span нельзя держать через await.
    private bool TryReadTokenHash(out byte[] hash)
    {
        hash = [];

        var header = Request.Headers.Authorization;
        if (header.Count != 1 || header[0] is not { } value)
        {
            return false;
        }

        var span = value.AsSpan();
        return span.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && Device.TryHashToken(span[Prefix.Length..].Trim(), out hash);
    }

    private sealed record DeviceIdentity(Guid Id, string Name);
}

public sealed class CashierOptions
{
    public const string SectionName = "Cashier";

    /// <summary>
    /// Токен первого устройства кассы. Пока нет панели, устройство завести больше
    /// негде: если значение задано, при старте создаётся устройство с этим токеном.
    /// </summary>
    public string? BootstrapToken { get; set; }
}

internal static partial class CashierSetup
{
    private const string BootstrapDeviceName = "Касса";

    public static IServiceCollection AddCashierAuthentication(this IServiceCollection services)
    {
        services.AddOptions<CashierOptions>().BindConfiguration(CashierOptions.SectionName);

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>(
                DeviceAuthenticationHandler.SchemeName, configureOptions: null);

        services.AddAuthorizationBuilder()
            .AddPolicy(DeviceAuthenticationHandler.CashierPolicy, static policy => policy
                .AddAuthenticationSchemes(DeviceAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());

        return services;
    }

    public static async Task EnsureBootstrapDeviceAsync(this WebApplication app)
    {
        var token = app.Services.GetRequiredService<IOptions<CashierOptions>>().Value.BootstrapToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        if (!Device.TryHashToken(token, out var hash))
        {
            throw new InvalidOperationException(
                $"Cashier:BootstrapToken должен быть длиной от {Device.MinTokenLength} до {Device.MaxTokenLength} символов.");
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SmenaDbContext>();

        if (await db.Devices.AnyAsync(device => device.TokenHash == hash))
        {
            return;
        }

        var time = app.Services.GetRequiredService<TimeProvider>();
        db.Devices.Add(Device.Create(BootstrapDeviceName, token, time.GetUtcNow()));
        await db.SaveChangesAsync();

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Smena.Cashier");
        LogBootstrapDevice(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Создано устройство кассы из Cashier:BootstrapToken")]
    private static partial void LogBootstrapDevice(ILogger logger);
}
