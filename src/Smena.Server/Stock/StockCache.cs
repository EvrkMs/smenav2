using Microsoft.Extensions.Options;
using Smena.Core.Stock;

namespace Smena.Server.Stock;

/// <summary>Последние загруженные остатки.</summary>
/// <param name="Items">Товары; пусто, пока не было ни одной удачной загрузки.</param>
/// <param name="RefreshedAt">Когда остатки удачно загружены в последний раз.</param>
/// <param name="Error">Почему не удалась последняя попытка; null, если она удалась.</param>
public sealed record StockSnapshot(IReadOnlyList<StockItem> Items, DateTimeOffset? RefreshedAt, string? Error)
{
    public static StockSnapshot Empty { get; } = new([], RefreshedAt: null, Error: null);
}

/// <summary>
/// Остатки «МойСклад» в памяти сервера. Касса получает их мгновенно и не зависит
/// от того, отвечает ли «МойСклад» прямо сейчас; сервер обновляет их в фоне.
/// </summary>
public sealed partial class StockCache(IStockSource source, TimeProvider time, ILogger<StockCache> logger) : IDisposable
{
    /// <summary>Чаще «МойСклад» не опрашиваем, как бы часто ни нажимали «Обновить».</summary>
    public static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Снимок неизменяем и подменяется целиком: читатели видят либо старый, либо новый.
    private volatile StockSnapshot _snapshot = StockSnapshot.Empty;
    private DateTimeOffset? _lastAttempt;

    public StockSnapshot Snapshot => _snapshot;

    /// <summary>
    /// Перечитывает остатки. Одновременные вызовы не дублируют запросы: второй
    /// дожидается первого и получает его результат. Сбой не стирает прежние остатки.
    /// </summary>
    /// <param name="force">false — пропустить, если предыдущая попытка была только что.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public async Task<StockSnapshot> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            if (!force && _lastAttempt is { } last && now - last < MinRefreshInterval)
            {
                return _snapshot;
            }

            _lastAttempt = now;
            try
            {
                var items = await source.LoadAsync(cancellationToken);
                _snapshot = new StockSnapshot(items, time.GetUtcNow(), Error: null);
                LogRefreshed(logger, items.Count);
            }
            catch (StockSourceException e)
            {
                _snapshot = _snapshot with { Error = e.Message };
                LogFailed(logger, e);
            }

            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Остатки «МойСклад» обновлены: {Count} позиций")]
    private static partial void LogRefreshed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Остатки «МойСклад» не обновлены, остаются прежние")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>Фоновое обновление остатков. Работает, только если задан доступ к «МойСклад».</summary>
internal sealed partial class StockRefreshService(
    StockCache cache,
    IOptions<MoySkladOptions> options,
    ILogger<StockRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            LogDisabled(logger);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.RefreshMinutes));
        do
        {
            try
            {
                await cache.RefreshAsync(force: true, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Неожиданная ошибка не должна останавливать обновление навсегда.
                LogPassFailed(logger, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "«МойСклад» не настроен (MoySklad:Token пуст): пересчёт склада выключен")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Обновление остатков «МойСклад» завершилось ошибкой")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
