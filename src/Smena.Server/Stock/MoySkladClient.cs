using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Smena.Core.Stock;

namespace Smena.Server.Stock;

/// <summary>
/// Подключение к «МойСклад». Секреты — параметры развёртывания, поэтому задаются
/// конфигурацией и переменными среды (<c>MoySklad__Token</c>), а не в панели.
/// </summary>
public sealed class MoySkladOptions
{
    public const string SectionName = "MoySklad";

    /// <summary>Токен доступа к API. Пусто (и логин не задан) — пересчёт склада выключен.</summary>
    public string? Token { get; set; }

    /// <summary>Вход по логину и паролю — если токена нет.</summary>
    public string? Login { get; set; }

    public string? Password { get; set; }

    public string BaseUrl { get; set; } = "https://api.moysklad.ru/api/remap/1.2";

    /// <summary>Как часто перечитывать остатки, минут.</summary>
    public int RefreshMinutes { get; set; } = 5;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token) || !string.IsNullOrWhiteSpace(Login);
}

internal sealed class MoySkladOptionsValidator : IValidateOptions<MoySkladOptions>
{
    public ValidateOptionsResult Validate(string? name, MoySkladOptions options)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return ValidateOptionsResult.Fail("MoySklad:BaseUrl должен быть адресом вида https://….");
        }

        return options.RefreshMinutes < 1
            ? ValidateOptionsResult.Fail("MoySklad:RefreshMinutes должен быть не меньше 1.")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>Откуда берутся остатки. За интерфейсом — «МойСклад»; в тестах — подставной источник.</summary>
public interface IStockSource
{
    /// <exception cref="StockSourceException">Остатки получить не удалось.</exception>
    Task<List<StockItem>> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>«МойСклад» недоступен или отказал. Текст можно показывать администратору.</summary>
public sealed class StockSourceException : Exception
{
    public StockSourceException()
    {
    }

    public StockSourceException(string message)
        : base(message)
    {
    }

    public StockSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Читает из «МойСклад» отчёт «Остатки» и ассортимент и собирает из них список
/// товаров. Только чтение: сервер в «МойСклад» ничего не записывает.
/// </summary>
public sealed class MoySkladClient : IStockSource
{
    private const int PageSize = 1000;

    // Защита от бесконечного листания, если API вдруг перестанет уменьшать страницы.
    private const int MaxPages = 100;

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public MoySkladClient(HttpClient http, IOptions<MoySkladOptions> options)
    {
        var settings = options.Value;
        _http = http;
        _baseUrl = settings.BaseUrl.TrimEnd('/');

        // «МойСклад» требует Accept с указанием кодировки; без него отвечает ошибкой.
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        http.Timeout = TimeSpan.FromSeconds(60);

        if (!string.IsNullOrWhiteSpace(settings.Token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(settings.Login))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Login.Trim()}:{settings.Password?.Trim()}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    public async Task<List<StockItem>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Без отчёта остатков пересчитывать нечего — его сбой прерывает загрузку.
            var report = new List<StockReportRow>();
            await foreach (var page in PagesAsync("report/stock/all?stockMode=all", MoySkladJsonContext.Default.MoySkladReportPage, required: true, cancellationToken))
            {
                foreach (var row in page.Rows ?? [])
                {
                    report.Add(new StockReportRow(
                        IdFromHref(row.Meta?.Href), row.Name ?? string.Empty, row.Article, row.Code, FolderName(row.Folder), row.Stock));
                }
            }

            // Ассортимент может быть недоступен по правам токена — тогда хватит отчёта.
            var assortment = new List<AssortmentRow>();
            await foreach (var page in PagesAsync("entity/assortment", MoySkladJsonContext.Default.MoySkladAssortmentPage, required: false, cancellationToken))
            {
                foreach (var row in page.Rows ?? [])
                {
                    assortment.Add(new AssortmentRow(
                        row.Meta?.Type,
                        row.Id,
                        row.Name ?? string.Empty,
                        row.Article,
                        row.Code,
                        FolderName(row.ProductFolder) ?? FolderName(row.Product?.ProductFolder)));
                }
            }

            return StockCatalog.Build(report, assortment);
        }
        catch (HttpRequestException e)
        {
            throw new StockSourceException("Нет связи с «МойСклад».", e);
        }
        catch (JsonException e)
        {
            throw new StockSourceException("«МойСклад» ответил не так, как ожидалось.", e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StockSourceException("«МойСклад» не ответил вовремя.", e);
        }
    }

    private async IAsyncEnumerable<TPage> PagesAsync<TPage>(
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TPage> typeInfo,
        bool required,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        where TPage : class, IMoySkladPage
    {
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        for (var page = 0; page < MaxPages; page++)
        {
            var url = string.Create(
                CultureInfo.InvariantCulture, $"{_baseUrl}/{path}{separator}limit={PageSize}&offset={page * PageSize}");

            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (!required)
                {
                    yield break;
                }

                throw new StockSourceException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "«МойСклад» не принял токен или у него нет прав на остатки.",
                    HttpStatusCode.TooManyRequests => "«МойСклад» просит подождать: слишком много запросов.",
                    _ => $"«МойСклад» ответил ошибкой {(int)response.StatusCode}.",
                });
            }

            var data = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken);
            if (data is null || data.RowCount == 0)
            {
                yield break;
            }

            yield return data;

            if (data.RowCount < PageSize)
            {
                yield break;
            }
        }
    }

    /// <summary>Ссылка вида «…/entity/product/{id}?expand=…» → {id}.</summary>
    private static string? IdFromHref(string? href)
    {
        if (string.IsNullOrEmpty(href))
        {
            return null;
        }

        var path = href.AsSpan();
        var query = path.IndexOf('?');
        if (query >= 0)
        {
            path = path[..query];
        }

        var id = path[(path.LastIndexOf('/') + 1)..];
        return id.IsEmpty ? null : id.ToString();
    }

    private static string? FolderName(MoySkladFolder? folder) =>
        string.IsNullOrEmpty(folder?.PathName) ? folder?.Name : folder.PathName + "/" + folder.Name;
}

internal interface IMoySkladPage
{
    int RowCount { get; }
}

internal sealed record MoySkladMeta(string? Href, string? Type);

/// <param name="Name">Название группы.</param>
/// <param name="PathName">Путь к ней от корня; пусто для группы верхнего уровня.</param>
internal sealed record MoySkladFolder(string? Name, string? PathName);

internal sealed record MoySkladReportRow(MoySkladMeta? Meta, string? Name, string? Article, string? Code, decimal Stock, MoySkladFolder? Folder);

internal sealed record MoySkladReportPage(List<MoySkladReportRow>? Rows) : IMoySkladPage
{
    [JsonIgnore]
    public int RowCount => Rows?.Count ?? 0;
}

internal sealed record MoySkladParent(MoySkladFolder? ProductFolder);

internal sealed record MoySkladAssortmentRow(
    MoySkladMeta? Meta, string? Id, string? Name, string? Article, string? Code, MoySkladFolder? ProductFolder, MoySkladParent? Product);

internal sealed record MoySkladAssortmentPage(List<MoySkladAssortmentRow>? Rows) : IMoySkladPage
{
    [JsonIgnore]
    public int RowCount => Rows?.Count ?? 0;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(MoySkladReportPage))]
[JsonSerializable(typeof(MoySkladAssortmentPage))]
internal sealed partial class MoySkladJsonContext : JsonSerializerContext;
