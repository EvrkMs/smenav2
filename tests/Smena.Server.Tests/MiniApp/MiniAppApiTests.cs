using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Smena.Core.Shifts;
using Smena.Server.Cashier;
using Smena.Server.Employees;
using Smena.Server.MiniApp;
using Smena.Server.Root;

namespace Smena.Server.Tests.MiniApp;

/// <summary>Кабинет сотрудника: вход по подписи Telegram, свой баланс и своя история.</summary>
public sealed class MiniAppApiTests(SmenaApp app) : IAsyncLifetime
{
    private const long IvanTelegram = 1001;
    private const long MariaTelegram = 1002;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private Guid _ivan;
    private Guid _maria;

    public async ValueTask InitializeAsync()
    {
        await app.ResetLedgerAsync(minShiftPhotos: 0);

        _ivan = await CreateEmployeeAsync(IvanTelegram, "Иван", 200);
        _maria = await CreateEmployeeAsync(MariaTelegram, "Мария", 180);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- Вход ----

    [Fact]
    public async Task Employee_sees_own_profile()
    {
        using var ivan = app.CreateMiniAppClient(IvanTelegram);

        var profile = await GetAsync<EmployeeProfileResponse>(ivan, "/api/me");

        Assert.Equal(new EmployeeProfileResponse("Иван", 200, 0), profile);
    }

    [Fact]
    public async Task Without_telegram_signature_there_is_no_access()
    {
        using var anonymous = app.CreateClient();
        using var cashier = app.CreateCashierClient();
        using var root = await app.CreateRootClientAsync();

        foreach (var client in new[] { anonymous, cashier, root })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Relative("/api/me"), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Relative("/api/me/operations"), Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task Forged_or_stale_signature_is_rejected()
    {
        var genuine = SmenaApp.SignInitData(IvanTelegram);
        var otherBot = SmenaApp.SignInitData(IvanTelegram, botToken: "999999:another-bot");
        var stale = SmenaApp.SignInitData(IvanTelegram, issuedAt: DateTimeOffset.UtcNow.AddHours(-25));
        var swapped = genuine.Replace("%22id%22%3A1001", "%22id%22%3A1002", StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(genuine));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(otherBot));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(stale));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(swapped));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync("мусор"));
    }

    [Fact]
    public async Task Stranger_and_disabled_employee_are_rejected()
    {
        using var stranger = app.CreateMiniAppClient(telegramId: 555);
        using var ivan = app.CreateMiniAppClient(IvanTelegram);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync(Relative("/api/me"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ivan.GetAsync(Relative("/api/me"), Ct)).StatusCode);

        await app.QueryDatabaseAsync(db => db.Employees.Where(e => e.Id == _ivan).ExecuteUpdateAsync(
            update => update.SetProperty(e => e.IsActive, false), Ct));

        // Отключили в панели — доступ пропал сразу, хотя подпись ещё действует.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ivan.GetAsync(Relative("/api/me"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Telegram_signature_does_not_open_cashier_or_panel()
    {
        using var ivan = app.CreateMiniAppClient(IvanTelegram);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ivan.GetAsync(Relative("/api/safe"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ivan.GetAsync(Relative("/api/root/overview"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ivan.PostAsync(Relative("/api/photo-sessions"), null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Telegram_username_is_remembered_at_login()
    {
        using var named = app.CreateMiniAppClient(IvanTelegram, username: "ivan_admin");
        using var renamed = app.CreateMiniAppClient(IvanTelegram, username: "ivan_new");
        using var nameless = app.CreateMiniAppClient(IvanTelegram);

        await GetAsync<EmployeeProfileResponse>(named, "/api/me");
        Assert.Equal("ivan_admin", await UsernameAsync());

        await GetAsync<EmployeeProfileResponse>(renamed, "/api/me");
        Assert.Equal("ivan_new", await UsernameAsync());

        await GetAsync<EmployeeProfileResponse>(nameless, "/api/me");
        Assert.Null(await UsernameAsync());
    }

    // ---- Баланс и история ----

    [Fact]
    public async Task History_shows_own_movements_newest_first_and_sums_to_balance()
    {
        await EarnAsync();
        using var ivan = app.CreateMiniAppClient(IvanTelegram);

        var profile = await GetAsync<EmployeeProfileResponse>(ivan, "/api/me");
        var history = await GetAsync<List<EmployeeMovementResponse>>(ivan, "/api/me/operations");

        // Смена: 8 ч × 200 = 1600; аванс 500; штраф 300.
        Assert.Equal(800, profile.Balance);
        Assert.Equal([-300, -500, 1600], history.Select(movement => movement.Amount));
        Assert.Equal(profile.Balance, history.Sum(movement => movement.Amount));

        Assert.All(history, movement => Assert.False(string.IsNullOrWhiteSpace(movement.Title)));
        Assert.Equal("опоздание", history[0].Comment);
        Assert.Equal("до зарплаты", history[1].Comment);
        Assert.Null(history[2].Comment);
        Assert.True(history[0].Id > history[1].Id && history[1].Id > history[2].Id);
    }

    [Fact]
    public async Task Employees_do_not_see_each_other()
    {
        await EarnAsync();
        using var maria = app.CreateMiniAppClient(MariaTelegram);

        var profile = await GetAsync<EmployeeProfileResponse>(maria, "/api/me");
        var history = await GetAsync<List<EmployeeMovementResponse>>(maria, "/api/me/operations");

        // У Марии только её начисление за ту же смену: 4 ч × 180.
        Assert.Equal(new EmployeeProfileResponse("Мария", 180, 720), profile);
        Assert.Equal([720], history.Select(movement => movement.Amount));
    }

    [Fact]
    public async Task History_is_paged_from_new_to_old()
    {
        await EarnAsync();
        using var ivan = app.CreateMiniAppClient(IvanTelegram);

        var first = await GetAsync<List<EmployeeMovementResponse>>(ivan, "/api/me/operations?take=2");
        var second = await GetAsync<List<EmployeeMovementResponse>>(ivan, $"/api/me/operations?take=2&before={first[^1].Id}");
        var third = await GetAsync<List<EmployeeMovementResponse>>(ivan, $"/api/me/operations?take=2&before={second[^1].Id}");

        Assert.Equal([-300, -500], first.Select(movement => movement.Amount));
        Assert.Equal([1600], second.Select(movement => movement.Amount));
        Assert.Empty(third);
    }

    [Fact]
    public async Task Cancelled_operation_leaves_both_balance_and_history()
    {
        await EarnAsync();
        using var root = await app.CreateRootClientAsync();
        using var ivan = app.CreateMiniAppClient(IvanTelegram);
        var fine = await app.QueryDatabaseAsync(db => db.Operations
            .Where(operation => operation.Comment == "опоздание").Select(operation => operation.Id).SingleAsync(Ct));

        (await root.PostAsJsonAsync($"/api/root/operations/{fine}/cancel", new { reason = "ошибка" }, Ct)).EnsureSuccessStatusCode();

        var profile = await GetAsync<EmployeeProfileResponse>(ivan, "/api/me");
        var history = await GetAsync<List<EmployeeMovementResponse>>(ivan, "/api/me/operations");

        Assert.Equal(1100, profile.Balance);
        Assert.Equal([-500, 1600], history.Select(movement => movement.Amount));
    }

    [Fact]
    public async Task Miniapp_page_is_served()
    {
        using var client = app.CreateClient();

        // Сборки интерфейса в тестах может не быть — тогда сервер честно отвечает 404, а не 500.
        using var response = await client.GetAsync(Relative("/miniapp/"), Ct);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound });
    }

    // ---- Вспомогательное ----

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    /// <summary>Смена (Иван 8 ч, Мария 4 ч), затем Ивану аванс 500 и штраф 300.</summary>
    private async Task EarnAsync()
    {
        using var cashier = app.CreateCashierClient();
        using var root = await app.CreateRootClientAsync();

        var shift = new ShiftInput
        {
            FactCash = 8000,
            FactNonCash = 12000,
            SkladCash = 2000,
            SkladNonCash = 3000,
            LangameCash = 6000,
            LangameNonCash = 9000,
            FactSafe = 0,
            Employees = [new(_ivan, 8, 0), new(_maria, 4, 0)],
        };

        (await PostAsync(cashier, "/api/shifts", shift)).EnsureSuccessStatusCode();
        (await PostAsync(cashier, "/api/payouts", new PayoutRequest(_ivan, 500, IsSalary: false, "до зарплаты"))).EnsureSuccessStatusCode();
        (await PostAsync(root, "/api/root/fines", new EmployeeAmountRequest(_ivan, 300, "опоздание"))).EnsureSuccessStatusCode();
    }

    private async Task<HttpStatusCode> StatusAsync(string initData)
    {
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.TryAddWithoutValidation("Authorization", "tma " + initData);
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    private Task<string?> UsernameAsync() => app.QueryDatabaseAsync(db => db.Employees
        .Where(employee => employee.Id == _ivan).Select(employee => employee.TelegramUsername).SingleAsync(Ct));

    private Task<Guid> CreateEmployeeAsync(long telegramId, string name, int hourlyRate) => app.QueryDatabaseAsync(async db =>
    {
        var employee = Employee.Create(telegramId, name, hourlyRate);
        db.Employees.Add(employee);
        await db.SaveChangesAsync(Ct);
        return employee.Id;
    });

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        return await client.SendAsync(request, Ct);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path) =>
        await client.GetFromJsonAsync<T>(Relative(path), Json, Ct)
        ?? throw new InvalidOperationException($"Пустой ответ от {path}.");
}
