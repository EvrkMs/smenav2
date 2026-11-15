using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Smena.Server.Notifications;

/// <summary>
/// Подключение к Telegram. Секреты и адрес прокси — параметры развёртывания,
/// поэтому задаются конфигурацией и переменными среды (<c>Telegram__BotToken</c>,
/// <c>Telegram__ProxyUri</c>), а не в панели. Чаты и темы — в настройках клуба.
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// <summary>Токен бота. Пусто — отправка выключена, сообщения копятся в очереди.</summary>
    public string? BotToken { get; set; }

    /// <summary>
    /// Прокси для выхода к Telegram, например <c>socks5://192.168.88.92:10801</c>.
    /// Нужен там, где Telegram недоступен напрямую.
    /// </summary>
    public string? ProxyUri { get; set; }

    /// <summary>Как часто проверять очередь, секунд.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken);
}

public enum TelegramOutcome
{
    Sent,

    /// <summary>Сеть, прокси, перегрузка или неверный токен: повторить позже.</summary>
    Transient,

    /// <summary>Telegram отказал по существу (нет чата, темы, бот исключён): повтор не поможет.</summary>
    Permanent,
}

public readonly record struct TelegramSendResult(TelegramOutcome Outcome, long MessageId = 0, string? Error = null, TimeSpan? RetryAfter = null);

public interface ITelegramClient
{
    /// <param name="chatId">Чат.</param>
    /// <param name="threadId">Тема в чате, если это форум.</param>
    /// <param name="text">Текст.</param>
    /// <param name="replyToMessageId">Номер сообщения Telegram, на которое это ответ.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<TelegramSendResult> SendMessageAsync(
        long chatId,
        int? threadId,
        string text,
        long? replyToMessageId,
        CancellationToken cancellationToken);

    /// <summary>Отправляет фото одним сообщением: один снимок — как фото, несколько — альбомом.</summary>
    /// <param name="chatId">Чат.</param>
    /// <param name="threadId">Тема в чате, если это форум.</param>
    /// <param name="filePaths">Полные пути к файлам JPEG, от 1 до 10.</param>
    /// <param name="caption">Подпись; пустая — без подписи.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<TelegramSendResult> SendPhotosAsync(
        long chatId,
        int? threadId,
        IReadOnlyList<string> filePaths,
        string caption,
        CancellationToken cancellationToken);
}

/// <summary>
/// Отправка сообщений через Bot API. Один метод — один HTTP-запрос, поэтому без
/// сторонней библиотеки. Никогда не бросает на сетевых сбоях: результат всегда
/// говорит, что делать с сообщением дальше.
/// </summary>
public sealed class TelegramClient : ITelegramClient, IDisposable
{
    private static readonly TimeSpan TextTimeout = TimeSpan.FromSeconds(20);

    // Альбом — это несколько сотен килобайт на каждый снимок, да ещё через прокси.
    private static readonly TimeSpan PhotosTimeout = TimeSpan.FromSeconds(90);

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public TelegramClient(IOptions<TelegramOptions> options)
    {
        var settings = options.Value;

        // Собственный HttpClient, а не IHttpClientFactory: фабрика пишет в лог адрес
        // каждого запроса, а в адресе Bot API стоит токен бота.
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        if (!string.IsNullOrWhiteSpace(settings.ProxyUri))
        {
            handler.Proxy = new WebProxy(settings.ProxyUri);
            handler.UseProxy = true;
        }

        // Общий таймаут выключен: у текста и у альбома он разный и задаётся на каждый запрос.
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _baseUrl = $"https://api.telegram.org/bot{settings.BotToken}/";
    }

    public async Task<TelegramSendResult> SendMessageAsync(
        long chatId,
        int? threadId,
        string text,
        long? replyToMessageId,
        CancellationToken cancellationToken)
    {
        // Если исходное сообщение удалили руками, ответ всё равно уходит — просто без цитаты.
        var reply = replyToMessageId is { } id ? new ReplyParameters(id, AllowSendingWithoutReply: true) : null;

        using var content = JsonContent.Create(
            new SendMessageRequest(chatId, threadId, text, reply),
            TelegramJsonContext.Default.SendMessageRequest);

        return await PostAsync("sendMessage", content, TextTimeout, cancellationToken);
    }

    public async Task<TelegramSendResult> SendPhotosAsync(
        long chatId,
        int? threadId,
        IReadOnlyList<string> filePaths,
        string caption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentOutOfRangeException.ThrowIfZero(filePaths.Count);

        // Форма владеет своими частями, а части — потоками файлов: всё закрывается вместе с ней.
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId.ToString(CultureInfo.InvariantCulture)), "chat_id");
        if (threadId is { } thread)
        {
            form.Add(new StringContent(thread.ToString(CultureInfo.InvariantCulture)), "message_thread_id");
        }

        try
        {
            if (filePaths.Count == 1)
            {
                // Альбом из одного снимка Telegram не принимает.
                if (caption.Length > 0)
                {
                    form.Add(new StringContent(caption), "caption");
                }

                form.Add(FileContent(filePaths[0]), "photo", "photo.jpg");
                return await PostAsync("sendPhoto", form, PhotosTimeout, cancellationToken);
            }

            var media = new InputMediaPhoto[filePaths.Count];
            for (var index = 0; index < media.Length; index++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"photo{index}");
                form.Add(FileContent(filePaths[index]), name, name + ".jpg");

                // Подпись альбома — это подпись его первого снимка.
                media[index] = new InputMediaPhoto("photo", "attach://" + name, index == 0 && caption.Length > 0 ? caption : null);
            }

            form.Add(new StringContent(JsonSerializer.Serialize(media, TelegramJsonContext.Default.InputMediaPhotoArray)), "media");
            return await PostAsync("sendMediaGroup", form, PhotosTimeout, cancellationToken);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // Файл удалён с диска: повторять бессмысленно.
            return new TelegramSendResult(TelegramOutcome.Permanent, Error: $"Файл фото не найден: {e.Message}");
        }
    }

    public void Dispose() => _http.Dispose();

    private static StreamContent FileContent(string path)
    {
        var content = new StreamContent(File.OpenRead(path));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return content;
    }

    private async Task<TelegramSendResult> PostAsync(string method, HttpContent content, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TelegramResponse? response;
        try
        {
            using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limited.CancelAfter(timeout);

            using var http = await _http.PostAsync(_baseUrl + method, content, limited.Token);

            // Telegram отвечает JSON и при ошибках; код ответа дублируется в error_code.
            response = await http.Content.ReadFromJsonAsync(TelegramJsonContext.Default.TelegramResponse, limited.Token);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException
                                  || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Нет сети, недоступен прокси, истёк таймаут, вместо JSON пришла страница-заглушка.
            return new TelegramSendResult(TelegramOutcome.Transient, Error: $"Нет связи с Telegram: {e.Message}");
        }

        if (response is { Ok: true } && TryReadMessageId(response.Result, out var messageId))
        {
            return new TelegramSendResult(TelegramOutcome.Sent, messageId);
        }

        var code = response?.ErrorCode ?? 0;
        var error = $"Telegram {code}: {response?.Description ?? "пустой ответ"}";

        return code switch
        {
            // Слишком часто: Telegram сам говорит, сколько подождать.
            429 => new TelegramSendResult(
                TelegramOutcome.Transient,
                Error: error,
                RetryAfter: TimeSpan.FromSeconds(response?.Parameters?.RetryAfter ?? 30)),

            // Неверный токен — ошибка настройки сервера, а не сообщения: после
            // исправления токена очередь должна уйти, а не оказаться списанной.
            401 or 404 => new TelegramSendResult(TelegramOutcome.Transient, Error: error),

            // Нет такого чата или темы, бот исключён, текст не принят.
            400 or 403 => new TelegramSendResult(TelegramOutcome.Permanent, Error: error),

            _ => new TelegramSendResult(TelegramOutcome.Transient, Error: error),
        };
    }

    /// <summary>В ответе либо одно сообщение, либо (для альбома) их список — берём первое.</summary>
    private static bool TryReadMessageId(JsonElement result, out long messageId)
    {
        messageId = 0;

        var message = result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0 ? result[0] : result;

        return message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("message_id", out var id)
            && id.TryGetInt64(out messageId);
    }
}

internal sealed record SendMessageRequest(long ChatId, int? MessageThreadId, string Text, ReplyParameters? ReplyParameters);

internal sealed record ReplyParameters(long MessageId, bool AllowSendingWithoutReply);

/// <param name="Type">Всегда «photo».</param>
/// <param name="Media">Ссылка на часть формы с файлом: «attach://имя».</param>
/// <param name="Caption">Подпись.</param>
internal sealed record InputMediaPhoto(string Type, string Media, string? Caption);

internal sealed record TelegramResponse(bool Ok, JsonElement Result, int? ErrorCode, string? Description, TelegramResponseParameters? Parameters);

internal sealed record TelegramResponseParameters(int? RetryAfter);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SendMessageRequest))]
[JsonSerializable(typeof(InputMediaPhoto[]))]
[JsonSerializable(typeof(TelegramResponse))]
internal sealed partial class TelegramJsonContext : JsonSerializerContext;
