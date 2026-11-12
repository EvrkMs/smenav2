using Smena.Server.Notifications;

namespace Smena.Server.Tests.Notifications;

/// <summary>
/// Telegram для тестов: запоминает отправленное и отвечает по сценарию.
/// Без сценария каждое сообщение «доставлено».
/// </summary>
public sealed class FakeTelegramClient : ITelegramClient
{
    private readonly Queue<TelegramSendResult> _script = new();
    private long _nextMessageId = 1000;

    public List<(long ChatId, int? ThreadId, string Text)> Sent { get; } = [];

    /// <summary>Все попытки, включая неудачные.</summary>
    public int Attempts { get; private set; }

    /// <summary>Ответы на следующие вызовы, по порядку.</summary>
    public void Script(params TelegramSendResult[] results)
    {
        foreach (var result in results)
        {
            _script.Enqueue(result);
        }
    }

    public void Reset()
    {
        _script.Clear();
        Sent.Clear();
        SentPhotos.Clear();
        Attempts = 0;
    }

    /// <summary>Отправленные альбомы: куда, подпись и размеры файлов, которые ушли бы в Telegram.</summary>
    public List<(long ChatId, int? ThreadId, string Caption, long[] FileSizes)> SentPhotos { get; } = [];

    public Task<TelegramSendResult> SendPhotosAsync(
        long chatId,
        int? threadId,
        IReadOnlyList<string> filePaths,
        string caption,
        CancellationToken cancellationToken)
    {
        Attempts++;

        var result = _script.Count > 0
            ? _script.Dequeue()
            : new TelegramSendResult(TelegramOutcome.Sent);

        if (result.Outcome == TelegramOutcome.Sent)
        {
            // Размер читается с диска: тест заодно проверяет, что файлы на месте.
            SentPhotos.Add((chatId, threadId, caption, [.. filePaths.Select(static path => new FileInfo(path).Length)]));
            result = result with { MessageId = _nextMessageId++ };
        }

        return Task.FromResult(result);
    }

    public Task<TelegramSendResult> SendMessageAsync(long chatId, int? threadId, string text, CancellationToken cancellationToken)
    {
        Attempts++;

        var result = _script.Count > 0
            ? _script.Dequeue()
            : new TelegramSendResult(TelegramOutcome.Sent);

        if (result.Outcome == TelegramOutcome.Sent)
        {
            Sent.Add((chatId, threadId, text));
            result = result with { MessageId = _nextMessageId++ };
        }

        return Task.FromResult(result);
    }
}
