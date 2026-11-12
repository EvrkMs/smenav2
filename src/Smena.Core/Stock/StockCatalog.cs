namespace Smena.Core.Stock;

/// <summary>Строка отчёта «Остатки» из «МойСклад»: есть только у товаров, по которым были движения.</summary>
/// <param name="ProductId">Идентификатор товара в «МойСклад», если его удалось узнать.</param>
/// <param name="Name">Название.</param>
/// <param name="Article">Артикул.</param>
/// <param name="Code">Код.</param>
/// <param name="Folder">Группа товаров.</param>
/// <param name="Stock">Физический остаток.</param>
public readonly record struct StockReportRow(string? ProductId, string Name, string? Article, string? Code, string? Folder, decimal Stock);

/// <summary>Строка ассортимента: полный каталог, включая товары без движений.</summary>
/// <param name="Type">Вид позиции: product, variant, service, bundle…</param>
/// <param name="ProductId">Идентификатор в «МойСклад».</param>
/// <param name="Name">Название.</param>
/// <param name="Article">Артикул.</param>
/// <param name="Code">Код.</param>
/// <param name="Folder">Группа товаров.</param>
public readonly record struct AssortmentRow(string? Type, string? ProductId, string Name, string? Article, string? Code, string? Folder);

/// <summary>Товар в списке для пересчёта.</summary>
/// <param name="Id">Устойчивый ключ позиции: по нему касса узнаёт товар после обновления остатков.</param>
/// <param name="Name">Название.</param>
/// <param name="Article">Артикул.</param>
/// <param name="Code">Код.</param>
/// <param name="Folder">Группа товаров.</param>
/// <param name="Stock">Остаток по «МойСклад».</param>
public sealed record StockItem(string Id, string Name, string? Article, string? Code, string? Folder, decimal Stock);

/// <summary>
/// Собирает список товаров для пересчёта из двух ответов «МойСклад»: каталог
/// берётся из ассортимента (в нём есть и товары, которых ещё не привозили),
/// остатки — из отчёта.
/// </summary>
public static class StockCatalog
{
    public static List<StockItem> Build(IReadOnlyList<StockReportRow> report, IReadOnlyList<AssortmentRow> assortment)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(assortment);

        // Остаток ищем сначала по идентификатору товара, а если его нет — по названию
        // с артикулом, как делала старая версия.
        var byId = new Dictionary<string, decimal>(report.Count, StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, decimal>(report.Count, StringComparer.OrdinalIgnoreCase);
        foreach (ref readonly var row in report.AsSpanOrCopy())
        {
            if (row.ProductId is { Length: > 0 } id)
            {
                byId[id] = row.Stock;
            }

            byName[NameKey(row.Name, row.Article)] = row.Stock;
        }

        var items = new List<StockItem>(Math.Max(report.Count, assortment.Count));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ref readonly var row in assortment.AsSpanOrCopy())
        {
            // Только то, что лежит на полке: услуги и комплекты пересчитывать нечего.
            if (row.Type is not ("product" or "variant"))
            {
                continue;
            }

            var nameKey = NameKey(row.Name, row.Article);
            var id = row.ProductId is { Length: > 0 } productId ? productId : nameKey;
            if (!seen.Add(id))
            {
                continue;
            }

            var stock = row.ProductId is { Length: > 0 } known && byId.TryGetValue(known, out var found)
                ? found
                : byName.GetValueOrDefault(nameKey);

            items.Add(new StockItem(id, row.Name, row.Article, row.Code, row.Folder, stock));
        }

        // Ассортимент недоступен (нет прав, сбой) — показываем хотя бы то, что есть в отчёте.
        if (items.Count == 0)
        {
            foreach (ref readonly var row in report.AsSpanOrCopy())
            {
                var id = row.ProductId is { Length: > 0 } productId ? productId : NameKey(row.Name, row.Article);
                if (seen.Add(id))
                {
                    items.Add(new StockItem(id, row.Name, row.Article, row.Code, row.Folder, row.Stock));
                }
            }
        }

        items.Sort(static (left, right) =>
        {
            var byFolder = string.Compare(left.Folder, right.Folder, StringComparison.OrdinalIgnoreCase);
            if (byFolder != 0)
            {
                return byFolder;
            }

            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            if (byName != 0)
            {
                return byName;
            }

            // Одинаковые названия: порядок всё равно должен быть одним и тем же при каждом
            // обновлении, иначе строки в списке на кассе менялись бы местами.
            var byArticle = string.Compare(left.Article, right.Article, StringComparison.OrdinalIgnoreCase);
            return byArticle != 0 ? byArticle : string.CompareOrdinal(left.Id, right.Id);
        });

        return items;
    }

    private static string NameKey(string name, string? article) =>
        string.IsNullOrEmpty(article) ? name : name + "|" + article;

    /// <summary>Обход по ссылке без копирования структур, когда список — массив или List.</summary>
    private static ReadOnlySpan<T> AsSpanOrCopy<T>(this IReadOnlyList<T> list) => list switch
    {
        T[] array => array,
        List<T> typed => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(typed),
        _ => list.ToArray(),
    };
}
