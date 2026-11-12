using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Smena.Core.Accounting;
using Smena.Core.Shifts;
using Smena.Server.Accounting;
using Smena.Server.Banking;
using Smena.Server.Cashier;
using Smena.Server.Root;

namespace Smena.Server.Tests.Root;

/// <summary>Панель root через HTTP: вход, справочники, денежные операции, отмена.</summary>
public sealed class RootApiTests(SmenaApp app) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask InitializeAsync() => await app.ResetLedgerAsync(minShiftPhotos: 0);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- Вход ----

    [Fact]
    public async Task Panel_api_requires_login_and_does_not_accept_a_cashier_token()
    {
        using var anonymous = app.CreateClient();
        using var cashier = app.CreateCashierClient();
        using var root = await app.CreateRootClientAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url("/api/root/overview"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync(Url("/api/root/overview"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await root.GetAsync(Url("/api/root/overview"), Ct)).StatusCode);

        // И наоборот: вход в панель не даёт прав кассы.
        Assert.Equal(HttpStatusCode.Unauthorized, (await root.GetAsync(Url("/api/safe"), Ct)).StatusCode);

        Assert.Equal("root", (await GetAsync<MeResponse>(root, "/api/root/me")).Username);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        using var root = await app.CreateRootClientAsync();

        using var logout = await root.PostAsync(Url("/api/root/logout"), null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await root.GetAsync(Url("/api/root/me"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_repeated_failures_lock_the_login()
    {
        using var client = app.CreateClient();

        for (var attempt = 1; attempt < RootUser.MaxFailedLogins; attempt++)
        {
            using var wrong = await LoginAsync(client, "root", "wrong-password");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var unknownUser = await LoginAsync(client, "nobody", SmenaApp.RootPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);

        using var last = await LoginAsync(client, "root", "wrong-password");
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);

        // Пока действует блокировка, не пускает и верный пароль.
        using var correct = await LoginAsync(client, "root", SmenaApp.RootPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, correct.StatusCode);
    }

    [Fact]
    public async Task Password_can_be_changed_only_with_the_current_one()
    {
        const string NewPassword = "another-long-password";
        using var root = await app.CreateRootClientAsync();

        using var wrongCurrent = await PostAsync(root, "/api/root/password", new PasswordChangeRequest("nope", NewPassword));
        using var tooShort = await PostAsync(root, "/api/root/password", new PasswordChangeRequest(SmenaApp.RootPassword, "short"));
        using var changed = await PostAsync(root, "/api/root/password", new PasswordChangeRequest(SmenaApp.RootPassword, NewPassword));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        try
        {
            using var client = app.CreateClient();
            using var oldPassword = await LoginAsync(client, "root", SmenaApp.RootPassword);
            using var newPassword = await LoginAsync(client, "ROOT", NewPassword);   // имя — без учёта регистра
            Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
            Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
        }
        finally
        {
            using var restore = await PostAsync(root, "/api/root/password", new PasswordChangeRequest(NewPassword, SmenaApp.RootPassword));
            Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
        }
    }

    // ---- Сотрудники и устройства ----

    [Fact]
    public async Task Employees_are_created_edited_and_switched_off()
    {
        using var root = await app.CreateRootClientAsync();
        using var cashier = app.CreateCashierClient();

        var ivan = await CreateEmployeeAsync(root, 1001, "Иван", 200);
        using var duplicate = await PostAsync(root, "/api/root/employees", new EmployeeCreateRequest(1001, "Двойник", 100, null));
        using var noTelegram = await PostAsync(root, "/api/root/employees", new EmployeeCreateRequest(0, "Без телеграма", 100, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noTelegram.StatusCode);

        using var update = await root.PutAsJsonAsync(
            $"/api/root/employees/{ivan}", new EmployeeUpdateRequest("Иван Петров", 250, IsActive: false, SalaryThreadId: 77), Json, Ct);
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var stored = Assert.Single(await GetAsync<List<RootEmployeeResponse>>(root, "/api/root/employees"));
        Assert.Equal(("Иван Петров", 250, false, 77, 1001L), (stored.Name, stored.HourlyRate, stored.IsActive, stored.SalaryThreadId!.Value, stored.TelegramId));

        // Отключённого сотрудника касса не видит.
        Assert.Empty(await GetAsync<List<EmployeeResponse>>(cashier, "/api/employees"));
    }

    [Fact]
    public async Task Device_token_is_shown_once_and_stops_working_after_revocation()
    {
        using var root = await app.CreateRootClientAsync();

        using var created = await PostAsync(root, "/api/root/devices", new DeviceCreateRequest("Касса 2"));
        var device = await ReadAsync<DeviceCreatedResponse>(created);

        using var cashier = app.CreateClient();
        cashier.DefaultRequestHeaders.Authorization = new("Bearer", device.Token);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync(Url("/api/safe"), Ct)).StatusCode);

        var devices = await GetAsync<List<DeviceResponse>>(root, "/api/root/devices");
        Assert.Contains(devices, d => d.Id == device.Id && d.RevokedAt is null);

        using var revoke = await root.PostAsync(Url($"/api/root/devices/{device.Id}/revoke"), null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync(Url("/api/safe"), Ct)).StatusCode);
    }

    // ---- Денежные операции root ----

    [Fact]
    public async Task Opening_balances_set_safe_bank_and_employee_balances()
    {
        using var root = await app.CreateRootClientAsync();
        var ivan = await CreateEmployeeAsync(root, 1001, "Иван", 200);
        var maria = await CreateEmployeeAsync(root, 1002, "Мария", 180);

        await OkAsync(root, "/api/root/opening", new OpeningRequest(OpeningTarget.Safe, null, 50000, null));
        await OkAsync(root, "/api/root/opening", new OpeningRequest(OpeningTarget.Bank, null, 120000, null));
        await OkAsync(root, "/api/root/opening", new OpeningRequest(OpeningTarget.Employee, ivan, 2400, null));
        await OkAsync(root, "/api/root/opening", new OpeningRequest(OpeningTarget.Employee, maria, -1500, "старый долг"));

        var overview = await GetAsync<OverviewResponse>(root, "/api/root/overview");
        Assert.Equal(50000, overview.Safe);
        Assert.Equal((120000, 0), (overview.BankConfirmed, overview.BankInTransit));   // стартовый остаток уже на счёте
        Assert.Equal((2400, 1500), (overview.OwedToEmployees, overview.EmployeeDebts));
        Assert.Equal(0, overview.PendingReceipts);

        var employees = await GetAsync<List<RootEmployeeResponse>>(root, "/api/root/employees");
        Assert.Equal([2400, -1500], employees.Select(e => e.Balance));

        using var zero = await PostAsync(root, "/api/root/opening", new OpeningRequest(OpeningTarget.Safe, null, 0, null));
        using var noEmployee = await PostAsync(root, "/api/root/opening", new OpeningRequest(OpeningTarget.Employee, null, 100, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, zero.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noEmployee.StatusCode);
    }

    [Fact]
    public async Task Root_can_pay_from_bank_and_beyond_the_balance_only_explicitly()
    {
        using var root = await app.CreateRootClientAsync();
        var ivan = await CreateEmployeeAsync(root, 1001, "Иван", 200);
        await OkAsync(root, "/api/root/opening", new OpeningRequest(OpeningTarget.Safe, null, 1000, null));
        await OkAsync(root, "/api/root/opening", new OpeningRequest(OpeningTarget.Employee, ivan, 2400, null));

        using var overBalance = await PostAsync(root, "/api/root/payouts", new RootPayoutRequest(ivan, 3000, FromBank: true, IsSalary: true, AllowOverdraft: false, null));
        using var overSafe = await PostAsync(root, "/api/root/payouts", new RootPayoutRequest(ivan, 1500, FromBank: false, IsSalary: true, AllowOverdraft: true, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overBalance.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overSafe.StatusCode);

        await OkAsync(root, "/api/root/payouts", new RootPayoutRequest(ivan, 3000, FromBank: true, IsSalary: true, AllowOverdraft: true, "в счёт будущих смен"));

        var employee = Assert.Single(await GetAsync<List<RootEmployeeResponse>>(root, "/api/root/employees"));
        Assert.Equal(-600, employee.Balance);
        Assert.Equal(-3000, await BalanceAsync(SystemAccounts.Bank));
        Assert.Equal(1000, await BalanceAsync(SystemAccounts.Safe));
    }

    [Fact]
    public async Task Fine_and_bonus_change_the_employee_balance_and_need_a_comment()
    {
        using var root = await app.CreateRootClientAsync();
        var ivan = await CreateEmployeeAsync(root, 1001, "Иван", 200);

        using var noComment = await PostAsync(root, "/api/root/fines", new EmployeeAmountRequest(ivan, 500, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noComment.StatusCode);

        await OkAsync(root, "/api/root/bonuses", new EmployeeAmountRequest(ivan, 2000, "за ночные смены"));
        await OkAsync(root, "/api/root/fines", new EmployeeAmountRequest(ivan, 500, "опоздание"));

        var employee = Assert.Single(await GetAsync<List<RootEmployeeResponse>>(root, "/api/root/employees"));
        Assert.Equal(1500, employee.Balance);
        Assert.Equal(2000, await BalanceAsync(SystemAccounts.Expense));   // премия — расход
        Assert.Equal(500, await BalanceAsync(SystemAccounts.Revenue));    // штраф — в выручку отдельной категорией
    }

    [Fact]
    public async Task Inventory_is_split_evenly_with_the_total_rounded_up()
    {
        using var root = await app.CreateRootClientAsync();
        var ids = new[]
        {
            await CreateEmployeeAsync(root, 1001, "Иван", 200),
            await CreateEmployeeAsync(root, 1002, "Мария", 180),
            await CreateEmployeeAsync(root, 1003, "Олег", 150),
        };

        using var unknown = await PostAsync(root, "/api/root/inventory", new InventoryRequest([ids[0], Guid.CreateVersion7()], 1000, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        await OkAsync(root, "/api/root/inventory", new InventoryRequest(ids, 1000, "ноябрь"));

        var employees = await GetAsync<List<RootEmployeeResponse>>(root, "/api/root/employees");
        Assert.Equal([-334, -334, -334], employees.Select(e => e.Balance));
        Assert.Equal(1002, await BalanceAsync(SystemAccounts.Revenue));
    }

    // ---- Категории и банк ----

    [Fact]
    public async Task Root_creates_categories_and_uses_them_for_bank_expenses()
    {
        using var root = await app.CreateRootClientAsync();

        using var noCounter = await PostAsync(root, "/api/root/categories", new CategoryCreateRequest(CategoryKind.BankExpense, "Аренда", null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noCounter.StatusCode);

        using var created = await PostAsync(root, "/api/root/categories", new CategoryCreateRequest(CategoryKind.BankExpense, "Аренда", "expense"));
        var rent = await ReadAsync<RootCategoryResponse>(created);
        using var duplicate = await PostAsync(root, "/api/root/categories", new CategoryCreateRequest(CategoryKind.BankExpense, "Аренда", "expense"));
        Assert.True(rent.Id >= 1000);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);

        // Категория расходов с тем же названием создалась сама — по ней строится P&L.
        var categories = await GetAsync<List<RootCategoryResponse>>(root, "/api/root/categories");
        var stored = categories.Single(c => c.Id == rent.Id);
        Assert.Equal(("Расходы", "Аренда"), (stored.CounterAccount, stored.CounterCategory));
        Assert.Contains(categories, c => c is { Kind: CategoryKind.Expense, Name: "Аренда", IsSystem: false });

        using var noComment = await PostAsync(root, "/api/root/bank/expense", new SafeMoveRequest(rent.Id, 40000, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noComment.StatusCode);
        await OkAsync(root, "/api/root/bank/expense", new SafeMoveRequest(rent.Id, 40000, "аренда за ноябрь"));

        Assert.Equal(-40000, await BalanceAsync(SystemAccounts.Bank));
        Assert.Equal(40000, await BalanceAsync(SystemAccounts.Expense));
    }

    [Fact]
    public async Task Archived_category_disappears_from_the_cashier_and_system_ones_are_protected()
    {
        using var root = await app.CreateRootClientAsync();
        using var cashier = app.CreateCashierClient();

        using var created = await PostAsync(root, "/api/root/categories", new CategoryCreateRequest(CategoryKind.SafeExpense, "Канцелярия", "expense"));
        var category = await ReadAsync<RootCategoryResponse>(created);
        Assert.Contains(await GetAsync<List<CategoryResponse>>(cashier, "/api/categories?kind=safeExpense"), c => c.Id == category.Id);

        using var archive = await root.PutAsJsonAsync($"/api/root/categories/{category.Id}", new CategoryUpdateRequest("Канцтовары", IsArchived: true), Json, Ct);
        using var system = await root.PutAsJsonAsync(
            $"/api/root/categories/{SystemCategories.SafeIncassation.Id}", new CategoryUpdateRequest("Другое", IsArchived: true), Json, Ct);

        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, system.StatusCode);
        Assert.DoesNotContain(await GetAsync<List<CategoryResponse>>(cashier, "/api/categories?kind=safeExpense"), c => c.Id == category.Id);
    }

    [Fact]
    public async Task Bank_receipt_is_confirmed_and_unconfirmed_through_the_panel()
    {
        using var root = await app.CreateRootClientAsync();
        using var cashier = app.CreateCashierClient();
        await OkAsync(root, "/api/root/opening", new OpeningRequest(OpeningTarget.Safe, null, 50000, null));
        using var incassation = await PostAsync(cashier, "/api/safe/expense", new SafeMoveRequest(SystemCategories.SafeIncassation.Id, 30000, null));
        var incassationId = (await ReadAsync<CashOperationResponse>(incassation)).OperationId;

        var receipt = Assert.Single(await GetAsync<List<ReceiptResponse>>(root, "/api/root/bank/receipts?pending=true"));
        Assert.Equal((30000, "Инкассация", true), (receipt.Amount, receipt.Channel, receipt.IsDue));

        using var tooMuch = await PostAsync(root, $"/api/root/bank/receipts/{receipt.Id}/confirm", new ReceiptConfirmRequest(30001));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.StatusCode);

        using var confirmed = await PostAsync(root, $"/api/root/bank/receipts/{receipt.Id}/confirm", new ReceiptConfirmRequest(29850));
        Assert.Equal(150, (await ReadAsync<ReceiptConfirmResponse>(confirmed)).Fee);
        Assert.Empty(await GetAsync<List<ReceiptResponse>>(root, "/api/root/bank/receipts?pending=true"));
        Assert.Equal(new BankBalance(29850, 0), await BankBalanceAsync());

        // Пока зачисление подтверждено, исходную операцию не отменить; комиссию отдельно — тоже.
        using var cancelSource = await PostAsync(root, $"/api/root/operations/{incassationId}/cancel", new CancelRequest("ошибка"));
        var feeOperation = (await GetAsync<List<OperationListItem>>(root, "/api/root/operations?type=BankReceipt")).Single();
        using var cancelFee = await PostAsync(root, $"/api/root/operations/{feeOperation.Id}/cancel", new CancelRequest("ошибка"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cancelSource.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cancelFee.StatusCode);

        using var unconfirm = await root.PostAsync(Url($"/api/root/bank/receipts/{receipt.Id}/unconfirm"), null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, unconfirm.StatusCode);
        Assert.Equal(new BankBalance(30000, 30000), await BankBalanceAsync());
        Assert.Equal(0, await BalanceAsync(SystemAccounts.Expense));   // комиссия отменена вместе с подтверждением

        // Теперь и саму инкассацию можно отменить: деньги возвращаются в сейф.
        using var cancelAgain = await PostAsync(root, $"/api/root/operations/{incassationId}/cancel", new CancelRequest("инкассации не было"));
        Assert.Equal(HttpStatusCode.NoContent, cancelAgain.StatusCode);
        Assert.Equal(50000, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(new BankBalance(0, 0), await BankBalanceAsync());
    }

    // ---- Операции и отмена ----

    [Fact]
    public async Task Operation_is_listed_shown_in_detail_and_cancelled_with_a_reason()
    {
        using var root = await app.CreateRootClientAsync();
        var ivan = await CreateEmployeeAsync(root, 1001, "Иван", 200);
        await OkAsync(root, "/api/root/opening", new OpeningRequest(OpeningTarget.Employee, ivan, 2400, null));
        var fineId = await OkAsync(root, "/api/root/fines", new EmployeeAmountRequest(ivan, 500, "опоздание"));

        var list = await GetAsync<List<OperationListItem>>(root, "/api/root/operations");
        Assert.Equal([OperationType.Fine, OperationType.Opening], list.Select(o => o.Type));
        Assert.Equal(("Штраф", "опоздание", "root", false), (list[0].TypeName, list[0].Comment, list[0].ActorName, list[0].IsCancelled));
        Assert.Equal(new OperationLineResponse(500, "Штраф: Иван"), Assert.Single(list[0].Lines));

        var detail = await GetAsync<OperationDetail>(root, $"/api/root/operations/{fineId}");
        Assert.Equal(
            [new EntryResponse("Иван", -1, "Штраф"), new EntryResponse("Выручка", 1, "Штрафы")],
            Assert.Single(detail.Transactions).Entries);
        Assert.Null(detail.Shift);

        using var noReason = await PostAsync(root, $"/api/root/operations/{fineId}/cancel", new CancelRequest(" "));
        using var cancel = await PostAsync(root, $"/api/root/operations/{fineId}/cancel", new CancelRequest("штраф снят"));
        using var again = await PostAsync(root, $"/api/root/operations/{fineId}/cancel", new CancelRequest("ещё раз"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);

        var cancelled = await GetAsync<OperationDetail>(root, $"/api/root/operations/{fineId}");
        Assert.Equal(("штраф снят", "root"), (cancelled.CancelReason, cancelled.CancelledBy));
        Assert.NotNull(cancelled.CancelledAt);

        Assert.Equal(2400, Assert.Single(await GetAsync<List<RootEmployeeResponse>>(root, "/api/root/employees")).Balance);
        Assert.Single(await GetAsync<List<OperationListItem>>(root, "/api/root/operations?cancelled=true"));
        Assert.Single(await GetAsync<List<OperationListItem>>(root, "/api/root/operations?cancelled=false"));
    }

    [Fact]
    public async Task Only_the_last_shift_can_be_cancelled()
    {
        using var root = await app.CreateRootClientAsync();
        using var cashier = app.CreateCashierClient();
        var ivan = await CreateEmployeeAsync(root, 1001, "Иван", 200);
        await OkAsync(root, "/api/root/opening", new OpeningRequest(OpeningTarget.Safe, null, 50000, null));

        var first = await CloseShiftAsync(cashier, ivan, factSafe: 50000);
        var second = await CloseShiftAsync(cashier, ivan, factSafe: 57000);
        Assert.Equal(64000, await BalanceAsync(SystemAccounts.Safe));

        var detail = await GetAsync<OperationDetail>(root, $"/api/root/operations/{second}");
        Assert.NotNull(detail.Shift);
        Assert.Equal((8000, 57000, 57000), (detail.Shift.FactCash, detail.Shift.FactSafe, detail.Shift.ExpectedSafe));
        Assert.Equal(new ShiftEmployeeDetail("Иван", 12, 200, 0), Assert.Single(detail.Shift.Employees));

        using var cancelFirst = await PostAsync(root, $"/api/root/operations/{first}/cancel", new CancelRequest("ошибка в первой"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cancelFirst.StatusCode);
        Assert.Contains("последнюю смену", (await ReadAsync<ErrorResponse>(cancelFirst)).Message, StringComparison.Ordinal);

        using var cancelSecond = await PostAsync(root, $"/api/root/operations/{second}/cancel", new CancelRequest("ошибка во второй"));
        Assert.Equal(HttpStatusCode.NoContent, cancelSecond.StatusCode);
        Assert.Equal(57000, await BalanceAsync(SystemAccounts.Safe));

        // После отмены второй последней стала первая — теперь можно отменить и её.
        using var cancelFirstNow = await PostAsync(root, $"/api/root/operations/{first}/cancel", new CancelRequest("и первая неверна"));
        Assert.Equal(HttpStatusCode.NoContent, cancelFirstNow.StatusCode);
        Assert.Equal(50000, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(new BankBalance(0, 0), await BankBalanceAsync());   // безнал отменённых смен в пути не числится
    }

    [Fact]
    public async Task Money_operations_need_an_idempotency_key_and_are_not_repeated()
    {
        using var root = await app.CreateRootClientAsync();
        var key = Guid.CreateVersion7();
        var request = new OpeningRequest(OpeningTarget.Safe, null, 1000, null);

        using var noKey = await root.PostAsJsonAsync("/api/root/opening", request, Json, Ct);
        using var first = await PostAsync(root, "/api/root/opening", request, key);
        using var second = await PostAsync(root, "/api/root/opening", request, key);

        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.False((await ReadAsync<OperationDoneResponse>(first)).Replayed);
        Assert.True((await ReadAsync<OperationDoneResponse>(second)).Replayed);
        Assert.Equal(1000, await BalanceAsync(SystemAccounts.Safe));
    }

    // ---- Настройки ----

    [Fact]
    public async Task Settings_are_read_changed_and_validated()
    {
        using var root = await app.CreateRootClientAsync();
        using var cashier = app.CreateCashierClient();

        var settings = await GetAsync<SettingsBody>(root, "/api/root/settings");
        Assert.Equal((1000, 3, 12, "Europe/Moscow"), (settings.CashFloat, settings.MaxEmployeesPerShift, settings.MaxHoursPerShift, settings.TimeZoneId));
        Assert.Equal(["Терминал", "Инкассация"], settings.Channels!.Select(c => c.Name));

        using var badZone = await root.PutAsJsonAsync("/api/root/settings", settings with { TimeZoneId = "Mars/Olympus" }, Json, Ct);
        using var badLimits = await root.PutAsJsonAsync("/api/root/settings", settings with { MaxEmployeesPerShift = 0 }, Json, Ct);
        using var badFee = await root.PutAsJsonAsync(
            "/api/root/settings", settings with { Channels = [settings.Channels![0] with { FeePercent = 150 }] }, Json, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badZone.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badLimits.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badFee.StatusCode);

        using var saved = await root.PutAsJsonAsync(
            "/api/root/settings",
            settings with
            {
                CashFloat = 2000,
                TelegramChatId = -100500,
                TelegramReportsThreadId = 7,
                Channels = [settings.Channels![0] with { DelayDays = 2, FeePercent = 1.8m }],
            },
            Json,
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var updated = await GetAsync<SettingsBody>(root, "/api/root/settings");
        Assert.Equal((2000, -100500L, 7), (updated.CashFloat, updated.TelegramChatId!.Value, updated.TelegramReportsThreadId!.Value));
        Assert.Equal((2, 1.8m), (updated.Channels![0].DelayDays!.Value, updated.Channels[0].FeePercent!.Value));

        // Касса сразу видит новый размен.
        Assert.Equal(2000, (await GetAsync<ConfigResponse>(cashier, "/api/config")).CashFloat);
    }

    // ---- Вспомогательное ----

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password) =>
        client.PostAsJsonAsync("/api/root/login", new LoginRequest(username, password), Json, Ct);

    private static async Task<T> GetAsync<T>(HttpClient client, string path) =>
        await client.GetFromJsonAsync<T>(Url(path), Json, Ct) ?? throw new InvalidOperationException($"Пустой ответ от {path}.");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidOperationException("Пустое тело ответа.");
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T body, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.CreateVersion7()).ToString());
        return await client.SendAsync(request, Ct);
    }

    /// <summary>Проводит денежную операцию root и возвращает её идентификатор.</summary>
    private static async Task<Guid> OkAsync<T>(HttpClient root, string path, T body)
    {
        using var response = await PostAsync(root, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<OperationDoneResponse>(text, Json)!.OperationId;
    }

    private static async Task<Guid> CreateEmployeeAsync(HttpClient root, long telegramId, string name, int hourlyRate)
    {
        using var response = await PostAsync(root, "/api/root/employees", new EmployeeCreateRequest(telegramId, name, hourlyRate, null));
        return (await ReadAsync<RootEmployeeResponse>(response)).Id;
    }

    /// <summary>Смена без расхождений: 7000 в сейф, 12000 безнала. Возвращает операцию закрытия.</summary>
    private async Task<Guid> CloseShiftAsync(HttpClient cashier, Guid employeeId, int factSafe)
    {
        var input = new ShiftInput
        {
            FactCash = 8000,
            FactNonCash = 12000,
            SkladCash = 2000,
            SkladNonCash = 3000,
            LangameCash = 6000,
            LangameNonCash = 9000,
            FactSafe = factSafe,
            Employees = [new(employeeId, 12, 0)],
        };

        using var response = await PostAsync(cashier, "/api/shifts", input);
        var closed = await ReadAsync<ShiftClosedResponse>(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await app.QueryDatabaseAsync(db => db.Shifts.Where(s => s.Id == closed.ShiftId).Select(s => s.OperationId).SingleAsync(Ct));
    }

    private Task<int> BalanceAsync(AccountRef account) => app.InScopeAsync(services =>
        services.GetRequiredService<Ledger>().GetBalanceAsync(account, Ct));

    private Task<BankBalance> BankBalanceAsync() => app.InScopeAsync(services =>
        services.GetRequiredService<BankReceiptService>().GetBalanceAsync(Ct));
}
