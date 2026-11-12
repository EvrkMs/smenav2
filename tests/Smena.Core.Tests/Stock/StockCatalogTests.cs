using Smena.Core.Stock;

namespace Smena.Core.Tests.Stock;

public sealed class StockCatalogTests
{
    private static StockReportRow Report(string name, decimal stock, string? id = null, string? article = null, string? folder = null) =>
        new(id, name, article, Code: null, folder, stock);

    private static AssortmentRow Product(string name, string? id = null, string? article = null, string? folder = null, string type = "product") =>
        new(type, id, name, article, Code: null, folder);

    [Fact]
    public void Catalog_comes_from_assortment_and_stock_from_the_report()
    {
        var items = StockCatalog.Build(
            [Report("Кола 0.5", 12, id: "a"), Report("Чипсы", 3.5m, id: "b")],
            [Product("Кола 0.5", id: "a"), Product("Чипсы", id: "b"), Product("Новинка без движений", id: "c")]);

        Assert.Equal(
            [("Кола 0.5", 12m), ("Новинка без движений", 0m), ("Чипсы", 3.5m)],
            items.Select(item => (item.Name, item.Stock)));
        Assert.Equal(["a", "c", "b"], items.Select(item => item.Id));
    }

    [Fact]
    public void Product_id_wins_over_name_when_names_repeat()
    {
        // Два разных товара с одинаковым названием: по названию их не различить.
        var items = StockCatalog.Build(
            [Report("Энергетик", 5, id: "a"), Report("Энергетик", 9, id: "b")],
            [Product("Энергетик", id: "a"), Product("Энергетик", id: "b")]);

        Assert.Equal([("a", 5m), ("b", 9m)], items.Select(item => (item.Id, item.Stock)).OrderBy(pair => pair.Id));
    }

    [Fact]
    public void Without_ids_rows_are_matched_by_name_and_article()
    {
        var items = StockCatalog.Build(
            [Report("Сок", 4, article: "A1"), Report("Сок", 7, article: "A2"), Report("вода", 2)],
            [Product("Сок", article: "A1"), Product("Сок", article: "A2"), Product("Вода")]);

        Assert.Equal(
            [("Вода", null, 2m), ("Сок", "A1", 4m), ("Сок", "A2", 7m)],
            items.Select(item => (item.Name, item.Article, item.Stock)));
    }

    [Fact]
    public void Services_and_bundles_are_not_counted()
    {
        var items = StockCatalog.Build(
            [],
            [Product("Час игры", id: "s", type: "service"), Product("Набор", id: "k", type: "bundle"),
             Product("Кола (0.33)", id: "v", type: "variant"), Product("Кола", id: "p")]);

        Assert.Equal(["Кола", "Кола (0.33)"], items.Select(item => item.Name));
    }

    [Fact]
    public void Duplicate_assortment_rows_appear_once()
    {
        var items = StockCatalog.Build(
            [Report("Кола", 5, id: "a")],
            [Product("Кола", id: "a"), Product("Кола", id: "a"), Product("Сок"), Product("сок")]);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void Empty_assortment_falls_back_to_the_report()
    {
        var items = StockCatalog.Build(
            [Report("Чипсы", 3, id: "b", folder: "Снеки"), Report("Кола", 12, id: "a", folder: "Напитки")],
            []);

        Assert.Equal([("Кола", 12m), ("Чипсы", 3m)], items.Select(item => (item.Name, item.Stock)));
    }

    [Fact]
    public void Items_are_sorted_by_folder_then_name()
    {
        var items = StockCatalog.Build(
            [],
            [Product("Яблочный сок", folder: "Напитки"), Product("Чипсы", folder: "Снеки"),
             Product("вода", folder: "Напитки"), Product("Без группы")]);

        Assert.Equal(["Без группы", "вода", "Яблочный сок", "Чипсы"], items.Select(item => item.Name));
    }

    [Fact]
    public void Negative_and_fractional_stock_is_kept_as_is()
    {
        // «МойСклад» допускает отрицательный остаток (продали больше, чем оприходовали).
        var items = StockCatalog.Build([Report("Кофе, кг", -0.25m, id: "a")], [Product("Кофе, кг", id: "a")]);

        Assert.Equal(-0.25m, Assert.Single(items).Stock);
    }
}
