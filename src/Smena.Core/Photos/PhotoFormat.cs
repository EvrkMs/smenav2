namespace Smena.Core.Photos;

/// <summary>Форматы, которые сервер принимает с телефона.</summary>
public enum PhotoFormat
{
    Unknown = 0,
    Jpeg,
    Png,
    WebP,

    /// <summary>Снимки айфонов (HEIC/HEIF).</summary>
    Heic,
}

public static class PhotoFormats
{
    /// <summary>
    /// Определяет формат по первым байтам файла. Имени файла и заявленному типу
    /// верить нельзя, а библиотека обработки изображений умеет читать десятки
    /// форматов, в том числе небезопасных, — поэтому ей передаётся только файл,
    /// формат которого мы узнали сами и разрешили.
    /// </summary>
    public static PhotoFormat Detect(ReadOnlySpan<byte> file)
    {
        if (file.StartsWith<byte>([0xFF, 0xD8, 0xFF]))
        {
            return PhotoFormat.Jpeg;
        }

        if (file.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return PhotoFormat.Png;
        }

        // RIFF....WEBP
        if (file.Length >= 12 && file.StartsWith("RIFF"u8) && file.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return PhotoFormat.WebP;
        }

        // Контейнер ISO: [размер][ftyp][марка]. Марки неподвижных снимков HEIF.
        if (file.Length >= 12 && file.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = file.Slice(8, 4);
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8)
                || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8)
                || brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8))
            {
                return PhotoFormat.Heic;
            }
        }

        return PhotoFormat.Unknown;
    }
}
