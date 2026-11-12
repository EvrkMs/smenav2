using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Smena.Core.Accounting;
using Smena.Server.Accounting;
using Smena.Server.Banking;
using Smena.Server.Cashier;
using Smena.Server.Database;
using Smena.Server.Employees;

namespace Smena.Server.Tests.Cashier;

/// <summary>Выплаты, приход и расход сейфа, зачисления в банк — через HTTP, как с кассы.</summary>
public sealed class CashOperationsApiTests(SmenaApp app) : IAsyncLifetime
{
    private const int OpeningSafe = 50000;
    private const int IvanBalance = 2400;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Actor Root = new(ActorKind.Root, "root");

    private Guid _ivan;
    private AccountRef _ivanAccount;

    public async ValueTask InitializeAsync()
    {
        await app.ResetLedgerAsync(minShiftPhotos: 0);

        (_ivan, _ivanAccount) = await app.QueryDatabaseAsync(async db =>
        {
            var employee = Employee.Create(1001, "Иван", 200);
            db.Employees.Add(employee);
            await db.SaveChangesAsync(Ct);
            return (employee.Id, employee.Account.Ref);
        });

        // В сейфе 50 000, клуб должен Ивану 2400.
        await app.InScopeAsync(async services =>
        {
            var ledger = services.GetRequiredService<Ledger>();
            await using var transaction = await ledger.BeginAsync(Ct);
            ledger.Post(new OperationDraft(
                OperationType.Opening,
                DateTimeOffset.UtcNow,
                Root,
                [
                    TransactionDraft.Create(OpeningSafe, Leg.Increase(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Opening)),
                    TransactionDraft.Create(IvanBalance, Leg.Increase(_ivanAccount), Leg.Decrease(SystemAccounts.Opening)),
                ]));
            await services.GetRequiredService<SmenaDbContext>().SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
            return 0;
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData(false, "Аванс: Иван")]
    [InlineData(true, "ЗП: Иван")]
    public async Task Payout_takes_money_from_safe_and_reduces_what_club_owes(bool isSalary, string note)
    {
        using var client = app.CreateCashierClient();

        using var response = await PostAsync(client, "/api/payouts", new PayoutRequest(_ivan, 1000, isSalary, "до зарплаты"));
        var body = await ReadAsync<CashOperationResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OpeningSafe - 1000, body.SafeBalance);
        Assert.Equal(OpeningSafe - 1000, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(IvanBalance - 1000, await BalanceAsync(_ivanAccount));

        var operation = await app.QueryDatabaseAsync(db => db.Operations
            .AsNoTracking()
            .Include(o => o.Transactions)
            .SingleAsync(o => o.Id == body.OperationId, Ct));
        Assert.Equal(OperationType.Payout, operation.Type);
        Assert.Equal("до зарплаты", operation.Comment);
        Assert.Equal(note, Assert.Single(operation.Transactions).Note);
    }

    [Theory]
    [InlineData(2401, "больше ЗП")]
    [InlineData(0, "от 1")]
    [InlineData(-10, "от 1")]
    public async Task Payout_beyond_rules_is_rejected_and_writes_nothing(int amount, string expected)
    {
        using var client = app.CreateCashierClient();

        using var response = await PostAsync(client, "/api/payouts", new PayoutRequest(_ivan, amount, false, null));
        var error = await ReadAsync<ErrorResponse>(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Equal(OpeningSafe, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(IvanBalance, await BalanceAsync(_ivanAccount));
    }

    [Fact]
    public async Task Payout_cannot_be_repeated_until_balance_is_exhausted_twice()
    {
        using var client = app.CreateCashierClient();

        using var first = await PostAsync(client, "/api/payouts", new PayoutRequest(_ivan, IvanBalance, true, null));
        using var second = await PostAsync(client, "/api/payouts", new PayoutRequest(_ivan, 1, false, null));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Contains("нет ЗП", (await ReadAsync<ErrorResponse>(second)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Payout_to_unknown_employee_is_rejected()
    {
        using var client = app.CreateCashierClient();

        using var response = await PostAsync(client, "/api/payouts", new PayoutRequest(Guid.CreateVersion7(), 100, false, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_request_with_same_key_pays_once()
    {
        using var client = app.CreateCashierClient();
        var key = Guid.CreateVersion7();
        var request = new PayoutRequest(_ivan, 1000, false, null);

        using var first = await PostAsync(client, "/api/payouts", request, key);
        using var second = await PostAsync(client, "/api/payouts", request, key);
        var firstBody = await ReadAsync<CashOperationResponse>(first);
        var secondBody = await ReadAsync<CashOperationResponse>(second);

        Assert.Equal(firstBody.OperationId, secondBody.OperationId);
        Assert.True(secondBody.Replayed);
        Assert.Equal(IvanBalance - 1000, await BalanceAsync(_ivanAccount));
    }

    [Fact]
    public async Task Cashier_sees_only_categories_that_have_a_counter_account()
    {
        using var client = app.CreateCashierClient();

        var income = await client.GetFromJsonAsync<List<CategoryResponse>>(new Uri("/api/categories?kind=safeIncome", UriKind.Relative), Json, Ct);
        var expense = await client.GetFromJsonAsync<List<CategoryResponse>>(new Uri("/api/categories?kind=safeExpense", UriKind.Relative), Json, Ct);
        using var unknown = await client.GetAsync(new Uri("/api/categories?kind=revenue", UriKind.Relative), Ct);

        // Служебные категории смены и выплат (без второго счёта) в список не попадают.
        Assert.Equal(
            [new CategoryResponse(SystemCategories.SafeOtherIncome.Id, "Прочий приход", false), new CategoryResponse(SystemCategories.SafeFromBank.Id, "Снято с банка", false)],
            income);
        Assert.Equal(
            [new CategoryResponse(SystemCategories.SafeIncassation.Id, "Инкассация", false), new CategoryResponse(SystemCategories.SafeHousehold.Id, "Хозяйственные", true)],
            expense);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Household_expense_moves_money_from_safe_to_expenses()
    {
        using var client = app.CreateCashierClient();

        using var response = await PostAsync(
            client, "/api/safe/expense", new SafeMoveRequest(SystemCategories.SafeHousehold.Id, 450, "вода, 3 бутыли"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OpeningSafe - 450, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(450, await BalanceAsync(SystemAccounts.Expense));
        Assert.Equal(0, await app.QueryDatabaseAsync(db => db.BankReceipts.CountAsync(Ct)));

        // Обе стороны несут категорию: сейф — для ДДС, расходы — для P&L.
        var categories = await app.QueryDatabaseAsync(db => db.AccountMovements
            .OrderBy(m => m.AccountId)
            .Where(m => m.OperationType == OperationType.SafeExpense)
            .Select(m => m.CategoryId)
            .ToListAsync(Ct));
        Assert.Equal([SystemCategories.SafeHousehold.Id, SystemCategories.ExpenseHousehold.Id], categories);
    }

    [Theory]
    [InlineData(450, null, "комментарий")]          // трата без объяснения
    [InlineData(50001, "сейф пуст", "В сейфе")]     // больше, чем лежит в сейфе
    public async Task Bad_expense_is_rejected(int amount, string? comment, string expected)
    {
        using var client = app.CreateCashierClient();

        using var response = await PostAsync(
            client, "/api/safe/expense", new SafeMoveRequest(SystemCategories.SafeHousehold.Id, amount, comment));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(expected, (await ReadAsync<ErrorResponse>(response)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OpeningSafe, await BalanceAsync(SystemAccounts.Safe));
    }

    [Theory]
    [InlineData("/api/safe/expense", 20)]   // категория прихода в расходе
    [InlineData("/api/safe/income", 18)]    // категория расхода в приходе
    [InlineData("/api/safe/expense", 14)]   // служебная, без второго счёта
    [InlineData("/api/safe/expense", 99999)]
    public async Task Category_of_wrong_kind_is_rejected(string path, int categoryId)
    {
        using var client = app.CreateCashierClient();

        using var response = await PostAsync(client, path, new SafeMoveRequest(categoryId, 100, "что-то"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(OpeningSafe, await BalanceAsync(SystemAccounts.Safe));
    }

    [Fact]
    public async Task Withdrawal_from_bank_arrives_in_safe_at_once()
    {
        using var client = app.CreateCashierClient();

        using var response = await PostAsync(
            client, "/api/safe/income", new SafeMoveRequest(SystemCategories.SafeFromBank.Id, 3000, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OpeningSafe + 3000, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(-3000, await BalanceAsync(SystemAccounts.Bank));
        Assert.Equal(0, await app.QueryDatabaseAsync(db => db.BankReceipts.CountAsync(Ct)));
    }

    [Fact]
    public async Task Other_income_goes_to_safe_and_revenue()
    {
        using var client = app.CreateCashierClient();

        using var response = await PostAsync(
            client, "/api/safe/income", new SafeMoveRequest(SystemCategories.SafeOtherIncome.Id, 700, "вернули залог"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OpeningSafe + 700, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(700, await BalanceAsync(SystemAccounts.Revenue));
    }

    [Fact]
    public async Task Incassation_is_in_transit_until_root_confirms_it_with_a_fee()
    {
        using var client = app.CreateCashierClient();

        using var response = await PostAsync(
            client, "/api/safe/expense", new SafeMoveRequest(SystemCategories.SafeIncassation.Id, 30000, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Деньги ушли из сейфа и числятся на банке, но «в пути».
        var receipt = await app.QueryDatabaseAsync(db => db.BankReceipts.AsNoTracking().SingleAsync(Ct));
        Assert.Equal(OpeningSafe - 30000, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(new BankBalance(Total: 30000, InTransit: 30000), await BankBalanceAsync());
        Assert.Equal(ReceiptChannelKind.CashDeposit, receipt.Channel);
        Assert.Null(receipt.ExpectedAt);
        Assert.Null(receipt.ExpectedAmount);

        // Банк зачислил 29 850: 150 — комиссия.
        var confirmation = await ConfirmAsync(receipt.Id, 29850);

        Assert.True(confirmation.IsConfirmed);
        Assert.Equal(150, confirmation.Fee);
        Assert.Equal(new BankBalance(Total: 29850, InTransit: 0), await BankBalanceAsync());
        Assert.Equal(150, await BalanceAsync(SystemAccounts.Expense));

        var confirmed = await app.QueryDatabaseAsync(db => db.BankReceipts.AsNoTracking().SingleAsync(Ct));
        Assert.Equal((29850, "root"), (confirmed.ConfirmedAmount, confirmed.ConfirmedBy));
        Assert.NotNull(confirmed.FeeTransactionId);

        // Комиссия — единственная новая транзакция; повторного «зачисления» нет.
        var feeOperation = await app.QueryDatabaseAsync(db => db.Operations
            .AsNoTracking()
            .Include(o => o.Transactions)
            .SingleAsync(o => o.Type == OperationType.BankReceipt, Ct));
        Assert.Equal(150, Assert.Single(feeOperation.Transactions).Amount);
        Assert.Equal(confirmed.FeeTransactionId, feeOperation.Transactions[0].Id);

        // То же самое видно в представлении для ручных запросов.
        var view = await app.QueryDatabaseAsync(db => db.Database
            .SqlQueryRaw<int>("SELECT confirmed AS \"Value\" FROM bank_balance")
            .SingleAsync(Ct));
        Assert.Equal(29850, view);
    }

    [Fact]
    public async Task Confirmation_without_difference_creates_no_fee_transaction()
    {
        var receiptId = await IncassateAsync(10000);

        var confirmation = await ConfirmAsync(receiptId, 10000);

        Assert.True(confirmation.IsConfirmed);
        Assert.Equal(0, confirmation.Fee);
        Assert.Equal(new BankBalance(Total: 10000, InTransit: 0), await BankBalanceAsync());
        Assert.Equal(0, await app.QueryDatabaseAsync(db => db.Operations.CountAsync(o => o.Type == OperationType.BankReceipt, Ct)));
        Assert.Null((await app.QueryDatabaseAsync(db => db.BankReceipts.AsNoTracking().SingleAsync(Ct))).FeeTransactionId);
    }

    [Fact]
    public async Task Confirmation_is_rejected_when_amount_is_wrong_or_already_confirmed()
    {
        var receiptId = await IncassateAsync(10000);

        Assert.False((await ConfirmAsync(receiptId, 10001)).IsConfirmed);
        Assert.False((await ConfirmAsync(receiptId, -1)).IsConfirmed);
        Assert.False((await ConfirmAsync(Guid.CreateVersion7(), 100)).IsConfirmed);
        Assert.Equal(new BankBalance(Total: 10000, InTransit: 10000), await BankBalanceAsync());

        Assert.True((await ConfirmAsync(receiptId, 9900)).IsConfirmed);
        Assert.Contains("уже подтверждено", (await ConfirmAsync(receiptId, 9900)).Error, StringComparison.Ordinal);
        Assert.Equal(100, await BalanceAsync(SystemAccounts.Expense));
    }

    [Fact]
    public async Task Deleting_the_incassation_by_hand_removes_its_pending_receipt()
    {
        await IncassateAsync(10000);

        await app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "DELETE FROM operations WHERE type_id = 4", Ct));

        Assert.Equal(0, await app.QueryDatabaseAsync(db => db.BankReceipts.CountAsync(Ct)));
        Assert.Equal(new BankBalance(Total: 0, InTransit: 0), await BankBalanceAsync());
        Assert.Equal(OpeningSafe, await BalanceAsync(SystemAccounts.Safe));
    }

    [Theory]
    [InlineData("/api/payouts")]
    [InlineData("/api/safe/income")]
    [InlineData("/api/safe/expense")]
    public async Task Money_operations_require_token_and_idempotency_key(string path)
    {
        object body = path == "/api/payouts"
            ? new PayoutRequest(_ivan, 100, false, null)
            : new SafeMoveRequest(SystemCategories.SafeOtherIncome.Id, 100, "x");

        using var anonymous = app.CreateClient();
        using var noToken = await anonymous.PostAsJsonAsync(path, body, Json, Ct);
        using var cashier = app.CreateCashierClient();
        using var noKey = await cashier.PostAsJsonAsync(path, body, Json, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal(OpeningSafe, await BalanceAsync(SystemAccounts.Safe));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T body, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.CreateVersion7()).ToString());
        return await client.SendAsync(request, Ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(Json, Ct)
        ?? throw new InvalidOperationException("Пустое тело ответа.");

    private async Task<Guid> IncassateAsync(int amount)
    {
        using var client = app.CreateCashierClient();
        using var response = await PostAsync(
            client, "/api/safe/expense", new SafeMoveRequest(SystemCategories.SafeIncassation.Id, amount, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await app.QueryDatabaseAsync(db => db.BankReceipts.Select(r => r.Id).SingleAsync(Ct));
    }

    private Task<BankReceiptConfirmation> ConfirmAsync(Guid receiptId, int amount) => app.InScopeAsync(services =>
        services.GetRequiredService<BankReceiptService>().ConfirmAsync(receiptId, amount, Root, Ct));

    private Task<BankBalance> BankBalanceAsync() => app.InScopeAsync(services =>
        services.GetRequiredService<BankReceiptService>().GetBalanceAsync(Ct));

    private Task<int> BalanceAsync(AccountRef account) => app.InScopeAsync(services =>
        services.GetRequiredService<Ledger>().GetBalanceAsync(account, Ct));
}
