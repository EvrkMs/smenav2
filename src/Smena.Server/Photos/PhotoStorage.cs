using System.Globalization;
using Microsoft.Extensions.Options;

namespace Smena.Server.Photos;

/// <summary>
/// Где лежат файлы и по какому адресу телефон открывает страницу загрузки. Это
/// свойства развёртывания, поэтому задаются конфигурацией и переменными среды
/// (<c>Photos__StoragePath</c>, <c>Photos__PublicBaseUrl</c>), а не в панели.
/// </summary>
public sealed class PhotoOptions
{
    public const string SectionName = "Photos";

    /// <summary>Каталог с файлами. Относительный путь считается от каталога сервера.</summary>
    public string StoragePath { get; set; } = "data/photos";

    /// <summary>
    /// Адрес сервера, каким его видит телефон, например <c>http://192.168.1.10:5080</c>.
    /// Пусто — берётся адрес, по которому к серверу обратилась касса.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}

internal sealed class PhotoOptionsValidator : IValidateOptions<PhotoOptions>
{
    public ValidateOptionsResult Validate(string? name, PhotoOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.StoragePath))
        {
            return ValidateOptionsResult.Fail("Photos:StoragePath не задан.");
        }

        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            && !(Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            return ValidateOptionsResult.Fail("Photos:PublicBaseUrl должен быть адресом вида http://хост:порт.");
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>Файлы фото на диске. В базе хранится только относительный путь.</summary>
public sealed partial class PhotoStorage
{
    private readonly string _root;
    private readonly ILogger<PhotoStorage> _logger;

    public PhotoStorage(IOptions<PhotoOptions> options, IHostEnvironment environment, ILogger<PhotoStorage> logger)
    {
        _root = Path.GetFullPath(options.Value.StoragePath, environment.ContentRootPath);
        _logger = logger;
    }

    public string FullPath(string relativePath) => Path.Combine(_root, relativePath);

    /// <summary>Сохраняет файл и возвращает путь для записи в базу.</summary>
    public async Task<string> SaveAsync(Guid photoId, DateTimeOffset now, ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken)
    {
        // По каталогу на месяц: в одном каталоге не копятся тысячи файлов,
        // и старое легко найти или убрать руками.
        var relativePath = string.Create(
            CultureInfo.InvariantCulture,
            $"{now.UtcDateTime:yyyy}/{now.UtcDateTime:MM}/{photoId:N}.jpg");

        var path = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Сначала во временный файл: при сбое посреди записи на диске не остаётся
        // обрезанного снимка под настоящим именем.
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, jpeg, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }

        return relativePath;
    }

    /// <summary>
    /// Удаляет файлы. Не бросает: строки в базе к этому моменту уже удалены,
    /// и неудаляемый файл не должен превращать успешную операцию в ошибку.
    /// </summary>
    public void Delete(IEnumerable<string> relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            try
            {
                File.Delete(FullPath(relativePath));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogDeleteFailed(_logger, relativePath, e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Файл фото {Path} не удалён и остался на диске")]
    private static partial void LogDeleteFailed(ILogger logger, string path, Exception exception);
}
