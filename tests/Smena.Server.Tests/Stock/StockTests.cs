using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Smena.Core.Stock;
using Smena.Server.Stock;

namespace Smena.Server.Tests.Stock;

/// <summary>Остатки «МойСклад»: разбор ответов API, кэш на сервере и адрес для кассы.</summary>
public sealed class StockTests(SmenaApp app)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Адрес для кассы ----

    [Fact]
    public async Task Stock_requires_the_cashier_token()
    {
        using var anonymous = app.CreateClient();
        using var root = await app.CreateRootClientAsync();
        using var employee = app.CreateMiniAppClient(telegramId: 1);

        foreach (var client in new[] { anonymous, root, employee })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri("/api/stock", UriKind.Relative), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(new Uri("/api/stock/refresh", UriKind.Relative), null, Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task Cashier_gets_the_items_loaded_in_the_background()
    {
        using var cashier = app.CreateCashierClient();

        // Первая загрузка идёт в фоне при старте сервера — дожидаемся её.
        StockResponse? stock = null;
        for (var attempt = 0; attempt < 50 && stock?.RefreshedAt is null; attempt++)
        {
            stock = await cashier.GetFromJsonAsync<StockResponse>(new Uri("/api/stock", UriKind.Relative), Json, Ct);
            if (stock?.RefreshedAt is null)
            {
                await Task.Delay(100, Ct);
            }
        }

        Assert.NotNull(stock);
        Assert.True(stock.Enabled);
        Assert.NotNull(stock.RefreshedAt);
        Assert.Null(stock.Error);
        Assert.Equal(
            FakeStockSource.DefaultItems.Select(item => new StockItemResponse(item.Id, item.Name, item.Article, item.Folder, item.Stock)),
            stock.Items);
    }

    // ---- Кэш ----

    [Fact]
    public async Task Refresh_replaces_items_and_failure_keeps_the_old_ones()
    {
        var source = new FakeStockSource();
        var clock = new ManualClock();
        using var cache = new StockCache(source, clock, NullLogger<StockCache>.Instance);

        Assert.Empty(cache.Snapshot.Items);
        Assert.Null(cache.Snapshot.RefreshedAt);

        var loaded = await cache.RefreshAsync(force: true, Ct);
        Assert.Equal(3, loaded.Items.Count);
        Assert.Equal(clock.Now, loaded.RefreshedAt);

        // «МойСклад» упал: остатки прежние, но видно, что обновить не удалось.
        clock.Now += TimeSpan.FromMinutes(5);
        source.Error = "Нет связи с «МойСклад».";
        var failed = await cache.RefreshAsync(force: true, Ct);

        Assert.Equal(3, failed.Items.Count);
        Assert.Equal(loaded.RefreshedAt, failed.RefreshedAt);
        Assert.Equal("Нет связи с «МойСклад».", failed.Error);

        // Заработал: ошибка снята, список новый.
        clock.Now += TimeSpan.FromMinutes(5);
        source.Error = null;
        source.Items = [new StockItem("x", "Новый товар", null, null, null, 1)];
        var recovered = await cache.RefreshAsync(force: true, Ct);

        Assert.Equal("Новый товар", Assert.Single(recovered.Items).Name);
        Assert.Null(recovered.Error);
        Assert.Equal(clock.Now, recovered.RefreshedAt);
    }

    [Fact]
    public async Task Manual_refresh_is_throttled()
    {
        var source = new FakeStockSource();
        var clock = new ManualClock();
        using var cache = new StockCache(source, clock, NullLogger<StockCache>.Instance);

        await cache.RefreshAsync(force: false, Ct);
        await cache.RefreshAsync(force: false, Ct);
        clock.Now += StockCache.MinRefreshInterval - TimeSpan.FromSeconds(1);
        await cache.RefreshAsync(force: false, Ct);
        Assert.Equal(1, source.Calls);

        clock.Now += TimeSpan.FromSeconds(1);
        await cache.RefreshAsync(force: false, Ct);
        Assert.Equal(2, source.Calls);

        // Фоновое обновление по расписанию ограничение не касается.
        await cache.RefreshAsync(force: true, Ct);
        Assert.Equal(3, source.Calls);
    }

    // ---- Разбор ответов «МойСклад» ----

    [Fact]
    public async Task Client_joins_report_and_assortment_by_product_id()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/report/stock/all", StringComparison.Ordinal) => Respond("""
                {"rows":[
                  {"meta":{"href":"https://api.moysklad.ru/api/remap/1.2/entity/product/11111111-aaaa?expand=supplier","type":"product"},
                   "name":"Кола 0.5","article":"A-1","stock":12,"folder":{"name":"Газировка","pathName":"Напитки"}},
                  {"meta":{"href":"https://api.moysklad.ru/api/remap/1.2/entity/product/22222222-bbbb","type":"product"},
                   "name":"Кофе, кг","stock":0.75}
                ]}
                """),
            _ => Respond("""
                {"rows":[
                  {"meta":{"type":"product"},"id":"11111111-aaaa","name":"Кола 0.5 (новое название)","article":"A-1",
                   "productFolder":{"name":"Газировка","pathName":"Напитки"}},
                  {"meta":{"type":"product"},"id":"22222222-bbbb","name":"Кофе, кг"},
                  {"meta":{"type":"product"},"id":"33333333-cccc","name":"Ещё не привозили","productFolder":{"name":"Снеки","pathName":""}},
                  {"meta":{"type":"service"},"id":"44444444-dddd","name":"Час игры"},
                  {"meta":{"type":"variant"},"id":"55555555-eeee","name":"Футболка (L)","product":{"productFolder":{"name":"Мерч"}}}
                ]}
                """),
        });

        var items = await Client(handler).LoadAsync(Ct);

        Assert.Equal(
            [
                new StockItem("22222222-bbbb", "Кофе, кг", null, null, null, 0.75m),
                new StockItem("55555555-eeee", "Футболка (L)", null, null, "Мерч", 0),
                new StockItem("11111111-aaaa", "Кола 0.5 (новое название)", "A-1", null, "Напитки/Газировка", 12),
                new StockItem("33333333-cccc", "Ещё не привозили", null, null, "Снеки", 0),
            ],
            items);

        // Запросы уходят с токеном и обязательным для «МойСклад» заголовком Accept.
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer", request.Authorization?.Scheme);
            Assert.Equal("test-token", request.Authorization?.Parameter);
            Assert.Equal("application/json; charset=utf-8", request.Accept);
        });
        Assert.Contains("stockMode=all", handler.Requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("limit=1000&offset=0", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_reads_all_pages()
    {
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (!url.Contains("/report/stock/all", StringComparison.Ordinal))
            {
                return Respond("""{"rows":[]}""");
            }

            // Первая страница полная (1000 строк), вторая — последняя.
            var offset = url.Contains("offset=1000", StringComparison.Ordinal) ? 1000 : 0;
            var count = offset == 0 ? 1000 : 3;
            var rows = Enumerable.Range(offset, count).Select(i => $$"""{"name":"Товар {{i:D4}}","stock":{{i}}}""");
            return Respond($$"""{"rows":[{{string.Join(',', rows)}}]}""");
        });

        var items = await Client(handler).LoadAsync(Ct);

        Assert.Equal(1003, items.Count);
        Assert.Equal(1002, items[^1].Stock);
        Assert.Equal(2, handler.Requests.Count(request => request.Url.Contains("/report/stock/all", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Unavailable_assortment_falls_back_to_the_report()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/report/stock/all", StringComparison.Ordinal)
            ? Respond("""{"rows":[{"name":"Кола","stock":5}]}""")
            : new HttpResponseMessage(HttpStatusCode.Forbidden));

        var items = await Client(handler).LoadAsync(Ct);

        Assert.Equal(("Кола", 5m), (Assert.Single(items).Name, items[0].Stock));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "токен")]
    [InlineData(HttpStatusCode.TooManyRequests, "подождать")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Failed_report_is_a_readable_error(HttpStatusCode status, string expectedPart)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        var error = await Assert.ThrowsAsync<StockSourceException>(() => Client(handler).LoadAsync(Ct));

        Assert.Contains(expectedPart, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_failure_and_garbage_are_readable_errors()
    {
        var offline = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var garbage = new StubHandler(_ => Respond("<html>502 Bad Gateway</html>"));

        Assert.Contains("Нет связи", (await Assert.ThrowsAsync<StockSourceException>(() => Client(offline).LoadAsync(Ct))).Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<StockSourceException>(() => Client(garbage).LoadAsync(Ct));
    }

    [Fact]
    public async Task Login_and_password_are_used_when_there_is_no_token()
    {
        var handler = new StubHandler(_ => Respond("""{"rows":[]}"""));
        var client = new MoySkladClient(
            new HttpClient(handler),
            Options.Create(new MoySkladOptions { Login = "admin@club", Password = "secret" }));

        await client.LoadAsync(Ct);

        Assert.Equal("Basic", handler.Requests[0].Authorization?.Scheme);
        Assert.Equal("admin@club:secret", Encoding.UTF8.GetString(Convert.FromBase64String(handler.Requests[0].Authorization!.Parameter!)));
    }

    private static MoySkladClient Client(StubHandler handler) =>
        new(new HttpClient(handler), Options.Create(new MoySkladOptions { Token = "test-token" }));

    private static HttpResponseMessage Respond(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record SeenRequest(string Url, System.Net.Http.Headers.AuthenticationHeaderValue? Authorization, string? Accept);

    /// <summary>Вместо сети: отвечает заданной функцией и запоминает запросы.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SeenRequest(
                request.RequestUri!.ToString(),
                request.Headers.Authorization,
                request.Headers.TryGetValues("Accept", out var accept) ? string.Join(",", accept) : null));

            return Task.FromResult(respond(request));
        }
    }
}
