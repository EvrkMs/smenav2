using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Smena.Server.Database;

namespace Smena.Server.Root;

public sealed class RootOptions
{
    public const string SectionName = "Root";

    /// <summary>
    /// Пароль первого пользователя панели. Используется один раз: если в базе нет
    /// ни одного пользователя, при старте создаётся «root» с этим паролем.
    /// </summary>
    public string? BootstrapPassword { get; set; }
}

public enum RootLoginOutcome
{
    Success,
    WrongCredentials,
    Locked,
}

/// <summary>Вход в панель: проверка пароля, блокировка после серии ошибок, смена пароля.</summary>
public sealed class RootAuthService(SmenaDbContext db, IPasswordHasher<RootUser> hasher, TimeProvider time)
{
    public async Task<(RootLoginOutcome Outcome, RootUser? User)> LoginAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return (RootLoginOutcome.WrongCredentials, null);
        }

        var normalized = RootUser.Normalize(username);
        var user = await db.RootUsers.FirstOrDefaultAsync(u => u.Username == normalized, cancellationToken);
        if (user is null)
        {
            return (RootLoginOutcome.WrongCredentials, null);
        }

        var now = time.GetUtcNow();
        if (user.IsLocked(now))
        {
            return (RootLoginOutcome.Locked, null);
        }

        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.RegisterFailedLogin(now);
            await db.SaveChangesAsync(cancellationToken);
            return (user.IsLocked(now) ? RootLoginOutcome.Locked : RootLoginOutcome.WrongCredentials, null);
        }

        user.RegisterSuccessfulLogin();
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // Параметры хеширования в новой версии .NET стали строже — обновляем хеш на лету.
            user.SetPasswordHash(hasher.HashPassword(user, password), user.PasswordChangedAt);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (RootLoginOutcome.Success, user);
    }

    /// <returns>Текст отказа либо null.</returns>
    public async Task<string?> ChangePasswordAsync(
        Guid userId,
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken)
    {
        var user = await db.RootUsers.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null
            || string.IsNullOrEmpty(currentPassword)
            || hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
        {
            return "Текущий пароль неверен.";
        }

        if (CheckNewPassword(newPassword) is { } error)
        {
            return error;
        }

        user.SetPasswordHash(hasher.HashPassword(user, newPassword!), time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public static string? CheckNewPassword(string? password) =>
        password is { Length: >= RootUser.MinPasswordLength }
            ? null
            : $"Пароль должен быть не короче {RootUser.MinPasswordLength} символов.";
}

internal static partial class RootSetup
{
    public const string SchemeName = "Root";
    public const string RootPolicy = "Root";

    private const string BootstrapUsername = "root";

    public static IServiceCollection AddRootAuthentication(this IServiceCollection services)
    {
        services.AddOptions<RootOptions>().BindConfiguration(RootOptions.SectionName);
        services.AddSingleton<IPasswordHasher<RootUser>, PasswordHasher<RootUser>>();
        services.AddScoped<RootAuthService>();

        services.AddAuthentication()
            .AddCookie(SchemeName, static options =>
            {
                options.Cookie.Name = "smena_root";
                options.Cookie.HttpOnly = true;

                // Strict: браузер не приложит cookie к запросу с чужого сайта —
                // это и есть защита панели от подделки запросов.
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = true;

                // Это API, а не сайт со страницей входа: вместо перенаправлений — коды ответа.
                options.Events.OnRedirectToLogin = static context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = static context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(RootPolicy, static policy => policy
                .AddAuthenticationSchemes(SchemeName)
                .RequireAuthenticatedUser());

        return services;
    }

    public static ClaimsPrincipal CreatePrincipal(RootUser user) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
        ],
        SchemeName));

    public static async Task EnsureBootstrapRootAsync(this WebApplication app)
    {
        var password = app.Services.GetRequiredService<IOptions<RootOptions>>().Value.BootstrapPassword;
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SmenaDbContext>();

        if (await db.RootUsers.AnyAsync())
        {
            return;
        }

        if (RootAuthService.CheckNewPassword(password) is { } error)
        {
            throw new InvalidOperationException($"Root:BootstrapPassword: {error}");
        }

        var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var user = RootUser.Create(BootstrapUsername, now);
        user.SetPasswordHash(scope.ServiceProvider.GetRequiredService<IPasswordHasher<RootUser>>().HashPassword(user, password), now);
        db.RootUsers.Add(user);
        await db.SaveChangesAsync();

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Smena.Root");
        LogBootstrapRoot(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Создан пользователь панели «root» из Root:BootstrapPassword")]
    private static partial void LogBootstrapRoot(ILogger logger);
}
