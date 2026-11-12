using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Smena.Server.Shifts;

namespace Smena.Server.Photos;

/// <summary>
/// Сессия загрузки фото: касса создаёт её и показывает QR со ссылкой, телефон
/// по этой ссылке загружает снимки. Открытая сессия держит непривязанные фото;
/// при закрытии смены фото привязываются к смене, а сессия закрывается.
/// </summary>
public sealed class PhotoSession
{
    private const int TokenBytes = 24;

    private PhotoSession()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// Секрет из ссылки в QR. Хранится открыто: касса должна уметь показать QR
    /// повторно, а даёт он только право догрузить фото в ещё не закрытую смену.
    /// </summary>
    public string Token { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Заполняется, когда фото привязаны к смене. После этого загрузка по ссылке невозможна.</summary>
    public DateTimeOffset? ClosedAt { get; private set; }

    public bool IsOpen => ClosedAt is null;

    internal static PhotoSession Create(DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes)),
        CreatedAt = now.ToUniversalTime(),
    };

    internal void Close(DateTimeOffset now) => ClosedAt = now.ToUniversalTime();
}

/// <summary>
/// Снимок к смене. В базе — только сведения о нём; сам файл (уже пережатый JPEG)
/// лежит на диске по пути <see cref="Path"/>.
/// </summary>
public sealed class Photo
{
    public const int HashLength = 32;

    private Photo()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Удаление сессии удаляет и строки её фото; файлы убирает сервер.</summary>
    public Guid SessionId { get; private set; }

    /// <summary>Пусто, пока отчёт не отправлен.</summary>
    public Guid? ShiftId { get; private set; }

    /// <summary>
    /// SHA-256 исходного файла, каким он пришёл с телефона. Уникален: тот же
    /// файл нельзя приложить ни дважды к одной смене, ни к другой смене позже.
    /// </summary>
    public byte[] Sha256 { get; private set; } = [];

    /// <summary>Путь к файлу относительно каталога хранения, через «/».</summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>Размер сохранённого файла.</summary>
    public int Bytes { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Время съёмки из метаданных исходного файла; пусто, если его там нет.</summary>
    public DateTimeOffset? TakenAt { get; private set; }

    /// <summary>Снимок старше допустимого на момент загрузки или без времени съёмки.</summary>
    public bool IsStale { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    /// <summary>
    /// Снимок вставлен на кассе из буфера обмена (снимок экрана), а не снят телефоном.
    /// Времени съёмки у такого файла нет и быть не может, поэтому давность для него
    /// не оценивается (<see cref="IsStale"/> всегда false), а в отчёте он считается отдельно.
    /// </summary>
    public bool PastedAtCashier { get; private set; }

    internal static Photo Create(
        Guid id,
        Guid sessionId,
        byte[] sha256,
        string path,
        int bytes,
        int width,
        int height,
        DateTimeOffset? takenAt,
        bool isStale,
        DateTimeOffset uploadedAt,
        bool pastedAtCashier) => new()
    {
        Id = id,
        SessionId = sessionId,
        Sha256 = sha256,
        Path = path,
        Bytes = bytes,
        Width = width,
        Height = height,
        TakenAt = takenAt?.ToUniversalTime(),
        IsStale = isStale,
        UploadedAt = uploadedAt.ToUniversalTime(),
        PastedAtCashier = pastedAtCashier,
    };

    internal void AttachTo(Guid shiftId) => ShiftId = shiftId;
}

internal sealed class PhotoSessionConfiguration : IEntityTypeConfiguration<PhotoSession>
{
    public void Configure(EntityTypeBuilder<PhotoSession> builder)
    {
        builder.ToTable("photo_sessions");

        builder.HasKey(static session => session.Id);
        builder.Property(static session => session.Id).ValueGeneratedNever();
        builder.Property(static session => session.Token).HasMaxLength(64);
        builder.HasIndex(static session => session.Token).IsUnique();
    }
}

internal sealed class PhotoConfiguration : IEntityTypeConfiguration<Photo>
{
    public void Configure(EntityTypeBuilder<Photo> builder)
    {
        builder.ToTable("photos", static table =>
        {
            table.HasCheckConstraint("ck_photos_sha256", $"octet_length(sha256) = {Photo.HashLength}");
            table.HasCheckConstraint("ck_photos_size", "bytes > 0 AND width > 0 AND height > 0");
            table.HasCheckConstraint("ck_photos_pasted_not_stale", "NOT (pasted_at_cashier AND is_stale)");
        });

        builder.HasKey(static photo => photo.Id);
        builder.Property(static photo => photo.Id).ValueGeneratedNever();
        builder.Property(static photo => photo.Path).HasMaxLength(128);

        builder.HasIndex(static photo => photo.Sha256).IsUnique();
        builder.HasIndex(static photo => photo.SessionId);
        builder.HasIndex(static photo => photo.ShiftId);

        builder.HasOne<PhotoSession>()
            .WithMany()
            .HasForeignKey(static photo => photo.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Удаление смены (вместе с её операцией) удаляет строки фото.
        // Файлы на диске каскад не трогает — они остаются сиротами.
        builder.HasOne<Shift>()
            .WithMany()
            .HasForeignKey(static photo => photo.ShiftId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
