using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Smena.Server.Cashier;
using Smena.Server.Network;

namespace Smena.Server.Stock;

public sealed record StockItemResponse(string Id, string Name, string? Article, string? Folder, decimal Stock);

/// <param name="Enabled">false — доступ к «МойСклад» не настроен, пересчёт недоступен.</param>
/// <param name="RefreshedAt">Когда остатки удачно загружены в последний раз; null — ещё ни разу.</param>
/// <param name="Error">Почему не удалась последняя попытка обновления; остатки при этом прежние.</param>
/// <param name="Items">Товары с остатками, по группам и названиям.</param>
public sealed record StockResponse(bool Enabled, DateTimeOffset? RefreshedAt, string? Error, IReadOnlyList<StockItemResponse> Items);

/// <summary>
/// Остатки «МойСклад» для пересчёта склада на кассе. Сам пересчёт (сравнение
/// с тем, что насчитал администратор) пока делает касса и никуда не отправляет.
/// </summary>
internal static class StockEndpoints
{
    public static IServiceCollection AddStock(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<MoySkladOptions>, MoySkladOptionsValidator>();
        services.AddOptions<MoySkladOptions>().BindConfiguration(MoySkladOptions.SectionName).ValidateOnStart();

        // Через общий выход наружу: тот же прокси, что у Telegram. Распаковку сжатых
        // ответов (а «МойСклад» отдаёт только сжатые) обработчик включает сам.
        services.AddHttpClient<IStockSource, MoySkladClient>()
            .ConfigurePrimaryHttpMessageHandler(static provider =>
                OutboundHttp.CreateHandler(provider.GetRequiredService<IOptions<NetworkOptions>>().Value));

        services.AddSingleton<StockCache>(static provider => new StockCache(
            // Источник живёт столько же, сколько кэш; соединения обновляет сам обработчик.
            provider.GetRequiredService<IStockSource>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<StockCache>>()));
        services.AddHostedService<StockRefreshService>();
        return services;
    }

    public static IEndpointRouteBuilder MapStockApi(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/stock").RequireAuthorization(DeviceAuthenticationHandler.CashierPolicy);

        api.MapGet("/", static (StockCache cache, IOptions<MoySkladOptions> options) =>
            TypedResults.Ok(ToResponse(cache.Snapshot, options.Value)));

        // Перед пересчётом администратору нужны свежие цифры, а не пятиминутной давности.
        api.MapPost("/refresh", RefreshAsync);

        return routes;
    }

    private static async Task<Ok<StockResponse>> RefreshAsync(
        StockCache cache,
        IOptions<MoySkladOptions> options,
        CancellationToken cancellationToken)
    {
        var snapshot = options.Value.IsConfigured
            ? await cache.RefreshAsync(force: false, cancellationToken)
            : cache.Snapshot;

        return TypedResults.Ok(ToResponse(snapshot, options.Value));
    }

    private static StockResponse ToResponse(StockSnapshot snapshot, MoySkladOptions options)
    {
        var items = new StockItemResponse[snapshot.Items.Count];
        for (var index = 0; index < items.Length; index++)
        {
            var item = snapshot.Items[index];
            items[index] = new StockItemResponse(item.Id, item.Name, item.Article, item.Folder, item.Stock);
        }

        return new StockResponse(options.IsConfigured, snapshot.RefreshedAt, snapshot.Error, items);
    }
}
