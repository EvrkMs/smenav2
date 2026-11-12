using Smena.Core.Stock;
using Smena.Server.Stock;

namespace Smena.Server.Tests.Stock;

/// <summary>«МойСклад» для тестов: отдаёт заданный список или заданную ошибку.</summary>
public sealed class FakeStockSource : IStockSource
{
    public static readonly StockItem[] DefaultItems =
    [
        new("id-cola", "Кола 0.5", "A-1", Code: null, "Напитки", 12),
        new("id-water", "Вода 0.5", null, Code: null, "Напитки", 0),
        new("id-chips", "Чипсы", "S-7", Code: null, "Снеки", 3.5m),
    ];

    public List<StockItem> Items { get; set; } = [.. DefaultItems];

    /// <summary>Если задано, загрузка заканчивается этой ошибкой.</summary>
    public string? Error { get; set; }

    public int Calls { get; private set; }

    public Task<List<StockItem>> LoadAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Error is null
            ? Task.FromResult(new List<StockItem>(Items))
            : Task.FromException<List<StockItem>>(new StockSourceException(Error));
    }
}
