using System.Globalization;

namespace Smena.Core.Photos;

/// <summary>Правила приёма фото к смене. Раздел «Фото» в docs/ARCHITECTURE.md.</summary>
public static class PhotoRules
{
    /// <summary>Больше в одну смену не принимаем: столько вмещает один альбом Telegram.</summary>
    public const int MaxPerSession = 10;

    /// <summary>Предел размера исходного файла. Снимок с телефона — 3–12 МБ.</summary>
    public const int MaxUploadBytes = 25 * 1024 * 1024;

    /// <summary>Длинная сторона сохранённого снимка, пикселей.</summary>
    public const int LongSide = 1600;

    public const int JpegQuality = 80;

    // Формат времени в EXIF фиксирован стандартом; пояс там отдельным полем и не всегда.
    private const string ExifTimeFormat = "yyyy:MM:dd HH:mm:ss";

    /// <summary>
    /// Время съёмки из EXIF. Если пояс в файле не указан, время считается
    /// временем клуба: снимают в клубе, телефон стоит в его поясе.
    /// </summary>
    /// <param name="dateTime">Поле DateTimeOriginal.</param>
    /// <param name="offset">Поле OffsetTimeOriginal вида «+03:00», если есть.</param>
    /// <param name="clubZone">Часовой пояс клуба.</param>
    /// <returns>Время в UTC либо null, если поля нет или оно не разбирается.</returns>
    public static DateTimeOffset? ParseExifTime(string? dateTime, string? offset, TimeZoneInfo clubZone)
    {
        ArgumentNullException.ThrowIfNull(clubZone);

        // Камеры без часов пишут нули или пробелы — это тоже «времени нет».
        if (!DateTime.TryParseExact(
                dateTime?.Trim().TrimEnd('\0'),
                ExifTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            return null;
        }

        var shift = TryParseOffset(offset, out var parsed) ? parsed : clubZone.GetUtcOffset(local);
        return new DateTimeOffset(local, shift).ToUniversalTime();
    }

    /// <summary>
    /// Сомнительный снимок: время съёмки неизвестно или он сделан раньше, чем за
    /// <paramref name="maxAgeHours"/> часов до загрузки. Такой снимок принимается,
    /// но помечается в отчёте.
    /// </summary>
    public static bool IsStale(DateTimeOffset? takenAt, DateTimeOffset uploadedAt, int maxAgeHours) =>
        takenAt is not { } taken || uploadedAt - taken > TimeSpan.FromHours(maxAgeHours);

    private static bool TryParseOffset(string? offset, out TimeSpan result)
    {
        result = default;

        var text = offset.AsSpan().Trim().TrimEnd('\0');
        if (text.Length != 6 || text[0] is not ('+' or '-'))
        {
            return false;
        }

        if (!TimeSpan.TryParseExact(text[1..], @"hh\:mm", CultureInfo.InvariantCulture, out var value)
            || value > TimeSpan.FromHours(14))
        {
            return false;
        }

        result = text[0] == '-' ? -value : value;
        return true;
    }
}
