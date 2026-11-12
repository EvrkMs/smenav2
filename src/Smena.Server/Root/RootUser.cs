using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Smena.Server.Root;

/// <summary>
/// Пользователь панели. Пароль хранится только как хеш, сделанный стандартным
/// хешером ASP.NET Core Identity (PBKDF2 с солью) — своей криптографии здесь нет.
/// </summary>
public sealed class RootUser
{
    public const int UsernameLength = 50;
    public const int MinPasswordLength = 10;

    /// <summary>После стольких неверных паролей подряд вход блокируется.</summary>
    public const int MaxFailedLogins = 5;

    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private RootUser()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>В нижнем регистре: вход не зависит от регистра.</summary>
    public string Username { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public int FailedLogins { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset PasswordChangedAt { get; private set; }

    public static string Normalize(string username) => username.Trim().ToLowerInvariant();

    internal static RootUser Create(string username, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Username = Normalize(username),
        CreatedAt = now.ToUniversalTime(),
    };

    internal bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    internal void SetPasswordHash(string hash, DateTimeOffset now)
    {
        PasswordHash = hash;
        PasswordChangedAt = now.ToUniversalTime();
        FailedLogins = 0;
        LockedUntil = null;
    }

    internal void RegisterFailedLogin(DateTimeOffset now)
    {
        FailedLogins++;
        if (FailedLogins >= MaxFailedLogins)
        {
            LockedUntil = now.ToUniversalTime() + LockoutDuration;
            FailedLogins = 0;
        }
    }

    internal void RegisterSuccessfulLogin()
    {
        FailedLogins = 0;
        LockedUntil = null;
    }
}

internal sealed class RootUserConfiguration : IEntityTypeConfiguration<RootUser>
{
    public void Configure(EntityTypeBuilder<RootUser> builder)
    {
        builder.ToTable("root_users");

        builder.HasKey(static user => user.Id);
        builder.Property(static user => user.Id).ValueGeneratedNever();
        builder.Property(static user => user.Username).HasMaxLength(RootUser.UsernameLength);
        builder.Property(static user => user.PasswordHash).HasMaxLength(200);
        builder.HasIndex(static user => user.Username).IsUnique();
    }
}
