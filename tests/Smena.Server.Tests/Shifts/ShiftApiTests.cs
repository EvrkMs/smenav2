using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Smena.Core.Accounting;
using Smena.Core.Shifts;
using Smena.Server.Accounting;
using Smena.Server.Cashier;
using Smena.Server.Database;
using Smena.Server.Employees;

namespace Smena.Server.Tests.Shifts;

/// <summary>Закрытие смены через HTTP — так, как его вызывает клиент на кассе.</summary>
public sealed class ShiftApiTests(SmenaApp app) : IAsyncLifetime
{
    private const int OpeningSafe = 50000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private Guid _ivan;
    private Guid _maria;
    private AccountRef _ivanAccount;
    private AccountRef _mariaAccount;

    public async ValueTask InitializeAsync()
    {
        await app.ResetLedgerAsync(minShiftPhotos: 0);

        (_ivan, _ivanAccount) = await CreateEmployeeAsync(1001, "Иван", 200);
        (_maria, _mariaAccount) = await CreateEmployeeAsync(1002, "Мария", 180);

        // Стартовый остаток сейфа.
        await app.InScopeAsync(async services =>
        {
            var ledger = services.GetRequiredService<Ledger>();
            await using var transaction = await ledger.BeginAsync(Ct);
            ledger.Post(new OperationDraft(
                OperationType.Opening,
                DateTimeOffset.UtcNow,
                new Actor(ActorKind.Root, "root"),
                [TransactionDraft.Create(OpeningSafe, Leg.Increase(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Opening))]));
            await services.GetRequiredService<SmenaDbContext>().SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
            return 0;
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong-token-wrong-token-wrong-token-wrong")]
    [InlineData("Bearer short")]
    [InlineData("Basic " + SmenaApp.CashierToken)]
    public async Task Api_requires_a_valid_device_token(string? authorization)
    {
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/safe");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_device_is_rejected()
    {
        using var client = app.CreateCashierClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/safe", UriKind.Relative), Ct)).StatusCode);

        await app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE devices SET revoked_at = now()", Ct));
        try
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri("/api/safe", UriKind.Relative), Ct)).StatusCode);
        }
        finally
        {
            await app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE devices SET revoked_at = NULL", Ct));
        }
    }

    [Fact]
    public async Task Config_employees_and_safe_are_served()
    {
        using var client = app.CreateCashierClient();

        var config = await GetAsync<ConfigResponse>(client, "/api/config");
        var employees = await GetAsync<List<EmployeeResponse>>(client, "/api/employees");
        var safe = await GetAsync<SafeResponse>(client, "/api/safe");

        Assert.Equal(new ConfigResponse(CashFloat: 1000, MaxEmployees: 3, MaxTotalHours: 12, MinPhotos: 0), config);
        Assert.Equal(OpeningSafe, safe.Balance);
        Assert.Equal(
            [new EmployeeResponse(_ivan, "Иван", 200, 0), new EmployeeResponse(_maria, "Мария", 180, 0)],
            employees);
    }

    [Fact]
    public async Task Preview_calculates_without_writing_anything()
    {
        using var client = app.CreateCashierClient();

        using var response = await client.PostAsJsonAsync("/api/shifts/calc", ShortageShift(ivanMinus: 100, mariaMinus: 0), Json, Ct);
        var calculation = await ReadAsync<ShiftCalculationResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(18500, calculation.Revenue);
        Assert.Equal(-500, calculation.CashDiscrepancy);
        Assert.Equal(500, calculation.RequiredMinus);
        Assert.Equal(100, calculation.EnteredMinus);
        Assert.False(calculation.CanClose);
        Assert.Equal(ShiftProblemCode.MinusMismatch, Assert.Single(calculation.Problems).Code);
        Assert.Equal(0, await CountShiftsAsync());
        Assert.Equal(OpeningSafe, await BalanceAsync(SystemAccounts.Safe));
    }

    [Fact]
    public async Task Closing_a_shift_records_it_and_moves_the_money()
    {
        using var client = app.CreateCashierClient();

        using var response = await CloseAsync(client, ShortageShift(ivanMinus: 300, mariaMinus: 200), Guid.CreateVersion7());
        var closed = await ReadAsync<ShiftClosedResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(closed.Replayed);
        Assert.NotNull(closed.Calculation);
        Assert.Equal(16680, closed.Calculation.Total);

        Assert.Equal(OpeningSafe + 6500, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(12000, await BalanceAsync(SystemAccounts.Bank));
        Assert.Equal(19000, await BalanceAsync(SystemAccounts.Revenue));   // 18500 по факту + 500 удержано
        Assert.Equal(2320, await BalanceAsync(SystemAccounts.Expense));
        Assert.Equal(1300, await BalanceAsync(_ivanAccount));
        Assert.Equal(520, await BalanceAsync(_mariaAccount));

        var shift = await app.QueryDatabaseAsync(db => db.Shifts
            .AsNoTracking()
            .Include(shift => shift.Employees.OrderBy(employee => employee.Position))
            .SingleAsync(shift => shift.Id == closed.ShiftId, Ct));

        Assert.Equal((7500, 12000, 2000, 3000, 6000, 9000), (shift.FactCash, shift.FactNonCash, shift.SkladCash, shift.SkladNonCash, shift.LangameCash, shift.LangameNonCash));
        Assert.Equal((OpeningSafe, OpeningSafe, 1000), (shift.FactSafe, shift.ExpectedSafe, shift.CashFloat));
        Assert.Equal((-500, 0), (shift.CashDiscrepancy, shift.SafeDiscrepancy));
        Assert.Equal("не сошлась касса", shift.ShortageReason);
        Assert.Equal([(_ivan, 8, 200, 300), (_maria, 4, 180, 200)], shift.Employees.Select(e => (e.EmployeeId, e.Hours, e.HourlyRate, e.Minus)));

        var operation = await app.QueryDatabaseAsync(db => db.Operations.AsNoTracking().SingleAsync(o => o.Id == shift.OperationId, Ct));
        Assert.Equal(OperationType.ShiftClose, operation.Type);
        Assert.Equal((ActorKind.Cashier, "Касса"), (operation.ActorKind, operation.ActorName));

        var employees = await GetAsync<List<EmployeeResponse>>(client, "/api/employees");
        Assert.Equal([1300, 520], employees.Select(employee => employee.Balance));
    }

    [Fact]
    public async Task Repeating_a_request_with_the_same_key_does_not_close_twice()
    {
        using var client = app.CreateCashierClient();
        var key = Guid.CreateVersion7();

        using var first = await CloseAsync(client, CleanShift(), key);
        using var second = await CloseAsync(client, CleanShift(), key);
        var firstBody = await ReadAsync<ShiftClosedResponse>(first);
        var secondBody = await ReadAsync<ShiftClosedResponse>(second);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody.ShiftId, secondBody.ShiftId);
        Assert.True(secondBody.Replayed);
        Assert.Null(secondBody.Calculation);
        Assert.Equal(1, await CountShiftsAsync());
        Assert.Equal(OpeningSafe + 7000, await BalanceAsync(SystemAccounts.Safe));
    }

    [Fact]
    public async Task Rejected_shift_writes_nothing()
    {
        using var client = app.CreateCashierClient();

        using var response = await CloseAsync(client, ShortageShift(ivanMinus: 0, mariaMinus: 0), Guid.CreateVersion7());
        var rejected = await ReadAsync<ShiftRejectedResponse>(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(rejected.Calculation.Problems, problem => problem.Code == ShiftProblemCode.MinusMismatch);
        Assert.Equal(0, await CountShiftsAsync());
        Assert.Equal(OpeningSafe, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(0, await BalanceAsync(SystemAccounts.Revenue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Closing_requires_an_idempotency_key(string? key)
    {
        using var client = app.CreateCashierClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shifts") { Content = JsonContent.Create(CleanShift(), options: Json) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountShiftsAsync());
    }

    [Fact]
    public async Task Incomplete_body_is_rejected()
    {
        using var client = app.CreateCashierClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shifts/calc")
        {
            Content = new StringContent("""{"factCash": 8000}""", System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Next_shift_is_checked_against_the_safe_left_by_the_previous_one()
    {
        using var client = app.CreateCashierClient();
        using var first = await CloseAsync(client, CleanShift(), Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // После первой смены в сейфе 57000. Вторая смена насчитала 56800: недостача 200.
        var secondShift = CleanShift() with
        {
            FactSafe = 56800,
            ShortageReason = "сейф",
            Employees = [new(_ivan, 8, 200), new(_maria, 4, 0)],
        };
        using var second = await CloseAsync(client, secondShift, Guid.CreateVersion7());
        var closed = await ReadAsync<ShiftClosedResponse>(second);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(57000, closed.Calculation!.ExpectedSafe);
        Assert.Equal(-200, closed.Calculation.SafeDiscrepancy);
        Assert.Equal(56800 + 7000, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(1600 + 1400, await BalanceAsync(_ivanAccount));
        Assert.Equal(38000, await BalanceAsync(SystemAccounts.Revenue));   // недостача сейфа в выручку не попадает
    }

    [Fact]
    public async Task Deleting_the_operation_by_hand_removes_the_shift_too()
    {
        using var client = app.CreateCashierClient();
        using var response = await CloseAsync(client, CleanShift(), Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "DELETE FROM operations WHERE type_id = 1", Ct));

        Assert.Equal(0, await CountShiftsAsync());
        Assert.Equal(0, await app.QueryDatabaseAsync(db => db.Set<Smena.Server.Shifts.ShiftEmployee>().CountAsync(Ct)));
        Assert.Equal(OpeningSafe, await BalanceAsync(SystemAccounts.Safe));
    }

    [Fact]
    public async Task System_categories_have_fixed_ids_and_user_categories_start_after_them()
    {
        var created = await app.QueryDatabaseAsync(async db =>
        {
            var category = Category.Create(CategoryKind.SafeExpense, "Канцелярия");
            db.Categories.Add(category);
            await db.SaveChangesAsync(Ct);
            return category;
        });
        var system = await app.QueryDatabaseAsync(db => db.Categories
            .AsNoTracking()
            .Where(category => category.Code != null)
            .OrderBy(category => category.Id)
            .ToListAsync(Ct));

        Assert.True(created.Id >= 1000);
        Assert.False(created.IsSystem);
        Assert.Equal(
            [
                SystemCategories.RevenueShift, SystemCategories.RevenueShortageWithheld, SystemCategories.RevenueSafeSurplus,
                SystemCategories.ExpensePayroll, SystemCategories.SafeShiftCash, SystemCategories.SafeSurplus,
                SystemCategories.SafeShortage, SystemCategories.SafeFloatRefill, SystemCategories.BankAcquiring,
                SystemCategories.EmployeeAccrual, SystemCategories.EmployeeShortage,
            ],
            system.Take(11).Select(category => category.Ref));
        Assert.Equal(34, system.Count);
        Assert.Equal(Enumerable.Range(1, 34), system.Select(category => category.Id));
    }

    [Fact]
    public async Task Non_cash_of_a_shift_waits_for_bank_confirmation()
    {
        await app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE receipt_channels SET delay_days = 2, fee_percent = 2 WHERE id = 1", Ct));
        using var client = app.CreateCashierClient();

        using var response = await CloseAsync(client, CleanShift(), Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var receipt = await app.QueryDatabaseAsync(db => db.BankReceipts.AsNoTracking().SingleAsync(Ct));
        var operation = await app.QueryDatabaseAsync(db => db.Operations.AsNoTracking().SingleAsync(o => o.Type == OperationType.ShiftClose, Ct));
        var balance = await app.InScopeAsync(services =>
            services.GetRequiredService<Smena.Server.Banking.BankReceiptService>().GetBalanceAsync(Ct));

        Assert.Equal(Smena.Server.Banking.ReceiptChannelKind.Terminal, receipt.Channel);
        Assert.Equal((2, 2m), (receipt.DelayDays, receipt.FeePercent));       // условия канала скопированы в запись
        Assert.Equal(11760, receipt.ExpectedAmount);                           // 12000 − 2 %
        Assert.Equal(operation.OccurredAt.AddDays(2), receipt.ExpectedAt);
        Assert.False(receipt.IsConfirmed);
        Assert.Equal((12000, 12000, 0), (balance.Total, balance.InTransit, balance.Confirmed));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Смена без расхождений: в сейф уходит 7000, безнал 12000, ЗП 1600 и 720.</summary>
    private ShiftInput CleanShift() => new()
    {
        FactCash = 8000,
        FactNonCash = 12000,
        SkladCash = 2000,
        SkladNonCash = 3000,
        LangameCash = 6000,
        LangameNonCash = 9000,
        FactSafe = OpeningSafe,
        Employees = [new(_ivan, 8, 0), new(_maria, 4, 0)],
    };

    /// <summary>Та же смена, но в кассе не хватает 500.</summary>
    private ShiftInput ShortageShift(int ivanMinus, int mariaMinus) => CleanShift() with
    {
        FactCash = 7500,
        ShortageReason = "  не сошлась касса ",
        Employees = [new(_ivan, 8, ivanMinus), new(_maria, 4, mariaMinus)],
    };

    private static async Task<HttpResponseMessage> CloseAsync(HttpClient client, ShiftInput input, Guid key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shifts") { Content = JsonContent.Create(input, options: Json) };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await client.SendAsync(request, Ct);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path) =>
        await client.GetFromJsonAsync<T>(new Uri(path, UriKind.Relative), Json, Ct)
        ?? throw new InvalidOperationException($"Пустой ответ от {path}.");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(Json, Ct)
        ?? throw new InvalidOperationException("Пустое тело ответа.");

    private Task<int> BalanceAsync(AccountRef account) => app.InScopeAsync(services =>
        services.GetRequiredService<Ledger>().GetBalanceAsync(account, Ct));

    private Task<int> CountShiftsAsync() => app.QueryDatabaseAsync(db => db.Shifts.CountAsync(Ct));

    private Task<(Guid Id, AccountRef Account)> CreateEmployeeAsync(long telegramId, string name, int hourlyRate) =>
        app.QueryDatabaseAsync(async db =>
        {
            var employee = Employee.Create(telegramId, name, hourlyRate);
            db.Employees.Add(employee);
            await db.SaveChangesAsync(Ct);
            return (employee.Id, employee.Account.Ref);
        });
}
