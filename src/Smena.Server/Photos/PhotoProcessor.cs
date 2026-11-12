using ImageMagick;
using Smena.Core.Photos;

namespace Smena.Server.Photos;

/// <summary>Снимок после обработки: готовый JPEG и то, что удалось узнать из исходника.</summary>
public sealed record ProcessedPhoto(ReadOnlyMemory<byte> Jpeg, int Width, int Height, DateTimeOffset? TakenAt);

/// <summary>
/// Превращает файл с телефона в то, что хранит сервер: читает время съёмки,
/// поворачивает по метке ориентации, уменьшает и пережимает в JPEG.
/// Метаданные (в том числе координаты съёмки) в сохранённый файл не попадают.
/// </summary>
public sealed class PhotoProcessor : IDisposable
{
    // Защита от «бомбы»: маленький файл, который разворачивается в гигабайты пикселей.
    private const long MaxPixels = 120_000_000;

    // Разбор одного снимка занимает десятки мегабайт памяти. Фото приходят с одного
    // телефона по одному, поэтому очередь из одного обработчика ничего не замедляет,
    // но не даёт нескольким загрузкам сразу занять память сервера.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <returns>null, если файл — не изображение, которое сервер принимает.</returns>
    public async Task<ProcessedPhoto?> ProcessAsync(ReadOnlyMemory<byte> original, TimeZoneInfo clubZone, CancellationToken cancellationToken)
    {
        var format = PhotoFormats.Detect(original.Span);
        if (format == PhotoFormat.Unknown)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return Process(original.Span, format, clubZone);
        }
        catch (MagickException)
        {
            // Повреждённый файл или разновидность формата, которую библиотека не читает.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static ProcessedPhoto? Process(ReadOnlySpan<byte> original, PhotoFormat format, TimeZoneInfo clubZone)
    {
        // Формат задаётся явно: библиотека не угадывает его сама и не может
        // принять файл за один из десятков других форматов, которые умеет читать.
        var settings = new MagickReadSettings { Format = ToMagick(format) };

        using var image = new MagickImage();

        // Сначала только заголовок: размеры известны до того, как выделена память под пиксели.
        image.Ping(original, settings);
        if ((long)image.Width * image.Height > MaxPixels)
        {
            return null;
        }

        // Подсказка декодеру JPEG: уменьшать прямо при чтении (в 2, 4 или 8 раз, но
        // не меньше указанного) — снимок на 50 Мп не разворачивается в память целиком.
        // Только для действительно больших снимков: на маленьких декодер с этой
        // подсказкой, наоборот, растягивает изображение.
        const uint DecodeSide = PhotoRules.LongSide * 2;
        if (format == PhotoFormat.Jpeg && Math.Max(image.Width, image.Height) >= DecodeSide * 2)
        {
            settings.SetDefine(MagickFormat.Jpeg, "size", $"{DecodeSide}x{DecodeSide}");
        }

        image.Read(original, settings);

        var takenAt = ReadTakenAt(image.GetExifProfile(), clubZone);

        image.AutoOrient();

        // Только уменьшаем: маленький снимок не растягивается. Resize вписывает
        // изображение в заданный квадрат с сохранением пропорций.
        if (Math.Max(image.Width, image.Height) > PhotoRules.LongSide)
        {
            image.Resize(PhotoRules.LongSide, PhotoRules.LongSide);
        }

        image.Strip();
        image.Quality = PhotoRules.JpegQuality;

        return new ProcessedPhoto(image.ToByteArray(MagickFormat.Jpeg), (int)image.Width, (int)image.Height, takenAt);
    }

    private static DateTimeOffset? ReadTakenAt(IExifProfile? exif, TimeZoneInfo clubZone) =>
        exif?.GetValue(ExifTag.DateTimeOriginal)?.Value is { } dateTime
            ? PhotoRules.ParseExifTime(dateTime, exif.GetValue(ExifTag.OffsetTimeOriginal)?.Value, clubZone)
            : null;

    private static MagickFormat ToMagick(PhotoFormat format) => format switch
    {
        PhotoFormat.Jpeg => MagickFormat.Jpeg,
        PhotoFormat.Png => MagickFormat.Png,
        PhotoFormat.WebP => MagickFormat.WebP,
        PhotoFormat.Heic => MagickFormat.Heic,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Формат не разрешён."),
    };
}
