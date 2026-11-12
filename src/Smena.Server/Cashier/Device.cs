using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Smena.Server.Cashier;

/// <summary>
/// Устройство кассы. Клиент на кассе предъявляет токен; в базе хранится только
/// его хеш, поэтому из базы токен не восстановить.
/// </summary>
public sealed class Device
{
    public const int NameLength = 100;
    public const int MinTokenLength = 32;
    public const int MaxTokenLength = 128;

    private const int HashLength = SHA256.HashSizeInBytes;

    private Device()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 от токена. Токен случайный и длинный, поэтому медленный хеш с солью не нужен.</summary>
    public byte[] TokenHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Отозванное устройство больше не проходит авторизацию.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Создаёт новый случайный токен. Показывается один раз — при выдаче.</summary>
    public static string GenerateToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <exception cref="ArgumentException">Токен короче или длиннее допустимого.</exception>
    public static Device Create(string name, ReadOnlySpan<char> token, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!TryHashToken(token, out var hash))
        {
            throw new ArgumentException(
                $"Токен устройства должен быть длиной от {MinTokenLength} до {MaxTokenLength} символов.", nameof(token));
        }

        return new Device
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            TokenHash = hash,
            CreatedAt = now.ToUniversalTime(),
        };
    }

    /// <summary>Хеш токена. False — токен заведомо не может быть настоящим (не та длина).</summary>
    public static bool TryHashToken(ReadOnlySpan<char> token, out byte[] hash)
    {
        if (token.Length is < MinTokenLength or > MaxTokenLength)
        {
            hash = [];
            return false;
        }

        // Токен переводится в байты на стеке: запрос приходит на каждое обращение
        // кассы, и временный массив в куче тут не нужен.
        Span<byte> utf8 = stackalloc byte[MaxTokenLength * 3];
        var written = Encoding.UTF8.GetBytes(token, utf8);

        hash = new byte[HashLength];
        SHA256.HashData(utf8[..written], hash);
        return true;
    }
}

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("devices", static table =>
            table.HasCheckConstraint("ck_devices_token_hash", "octet_length(token_hash) = 32"));

        builder.HasKey(static device => device.Id);
        builder.Property(static device => device.Id).ValueGeneratedNever();
        builder.Property(static device => device.Name).HasMaxLength(Device.NameLength);
        builder.HasIndex(static device => device.TokenHash).IsUnique();
    }
}
