using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Smena.Server.Database;
using Smena.Server.Photos;

namespace Smena.Server.Notifications;

/// <summary>Один проход по очереди: отправляет ожидающие сообщения по порядку.</summary>
public sealed partial class OutboxDispatcher(
    SmenaDbContext db,
    ITelegramClient telegram,
    PhotoStorage storage,
    TimeProvider time,
    ILogger<OutboxDispatcher> logger)
{
    private const int BatchSize = 20;

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    /// <returns>Сколько сообщений отправлено за проход.</returns>
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var batch = await db.OutboxMessages
            .Where(static message => message.Status == OutboxStatus.Pending)
            .OrderBy(static message => message.Sequence)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var sent = 0;

        foreach (var message in batch)
        {
            var now = time.GetUtcNow();

            // Порядок важнее скорости: пока самое старое сообщение ждёт повтора,
            // более новые не обгоняют его.
            if (message.NextAttemptAt > now)
            {
                break;
            }

            var result = message.PhotosShiftId is { } shiftId
                ? await SendPhotosAsync(message, shiftId, cancellationToken)
                : await telegram.SendMessageAsync(message.ChatId, message.ThreadId, message.Text, cancellationToken);

            switch (result.Outcome)
            {
                case TelegramOutcome.Sent:
                    message.MarkSent(result.MessageId, now);
                    sent++;
                    break;

                case TelegramOutcome.Permanent:
                    message.MarkFailed(result.Error ?? "Telegram отказал.");
                    LogFailed(logger, message.Sequence, message.LastError);
                    break;

                default:
                    var delay = result.RetryAfter ?? RetryDelay(message.Attempts);
                    message.MarkRetry(result.Error ?? "Временный сбой.", now + delay);
                    LogRetry(logger, message.Sequence, message.Attempts, delay.TotalSeconds, message.LastError);
                    break;
            }

            // Сохраняем после каждого сообщения: если процесс упадёт посреди прохода,
            // уже отправленное не уйдёт повторно.
            await db.SaveChangesAsync(cancellationToken);

            if (result.Outcome == TelegramOutcome.Transient)
            {
                // Сеть или прокси лежат — остальные сообщения постигнет то же самое.
                break;
            }
        }

        return sent;
    }

    private async Task<TelegramSendResult> SendPhotosAsync(OutboxMessage message, Guid shiftId, CancellationToken cancellationToken)
    {
        var paths = await db.Photos
            .AsNoTracking()
            .Where(photo => photo.ShiftId == shiftId)
            .OrderBy(static photo => photo.Id)
            .Select(static photo => photo.Path)
            .ToListAsync(cancellationToken);

        if (paths.Count == 0)
        {
            return new TelegramSendResult(TelegramOutcome.Permanent, Error: "У смены нет фото.");
        }

        return await telegram.SendPhotosAsync(
            message.ChatId,
            message.ThreadId,
            paths.ConvertAll(storage.FullPath),
            message.Text,
            cancellationToken);
    }

    /// <summary>10 с, 20 с, 40 с… но не дольше 15 минут. Повторы не прекращаются: отчёт о смене должен дойти.</summary>
    private static TimeSpan RetryDelay(int attemptsSoFar)
    {
        var factor = Math.Pow(2, Math.Min(attemptsSoFar, 10));
        var delay = TimeSpan.FromTicks((long)(FirstRetryDelay.Ticks * factor));
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Сообщение {Sequence} не ушло (попытка {Attempt}), повтор через {DelaySeconds} с: {Error}")]
    private static partial void LogRetry(ILogger logger, long sequence, int attempt, double delaySeconds, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Сообщение {Sequence} отклонено Telegram и больше не отправляется: {Error}")]
    private static partial void LogFailed(ILogger logger, long sequence, string? error);
}

/// <summary>Фоновая отправка очереди. Работает только если задан токен бота.</summary>
internal sealed partial class OutboxSenderService(
    IServiceScopeFactory scopes,
    IOptions<TelegramOptions> options,
    ILogger<OutboxSenderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            LogDisabled(logger);
            return;
        }

        LogEnabled(logger, string.IsNullOrWhiteSpace(settings.ProxyUri) ? "напрямую" : "через прокси");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, settings.PollIntervalSeconds)));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Например, база недоступна. Отправитель не должен умирать: попробует на следующем шаге.
                LogPassFailed(logger, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Telegram не настроен (Telegram:BotToken пуст): сообщения копятся в очереди и не отправляются")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Отправка в Telegram включена, соединение {Mode}")]
    private static partial void LogEnabled(ILogger logger, string mode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Проход по очереди сообщений завершился ошибкой")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}

internal static class NotificationSetup
{
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        services.AddOptions<TelegramOptions>().BindConfiguration(TelegramOptions.SectionName);
        services.AddSingleton<ITelegramClient, TelegramClient>();
        services.AddScoped<NotificationService>();
        services.AddScoped<OutboxDispatcher>();
        services.AddHostedService<OutboxSenderService>();
        return services;
    }
}
