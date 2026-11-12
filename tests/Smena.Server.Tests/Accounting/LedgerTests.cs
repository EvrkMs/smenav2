using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Smena.Core.Accounting;
using Smena.Server.Accounting;
using Smena.Server.Database;
using Smena.Server.Employees;

namespace Smena.Server.Tests.Accounting;

/// <summary>Проведение, балансы и отмена — через тот же сервис, которым пользуется сервер.</summary>
public sealed class LedgerTests(SmenaApp app) : IAsyncLifetime
{
    private static readonly Actor Cashier = new(ActorKind.Cashier, "касса");

    public async ValueTask InitializeAsync() => await app.ResetLedgerAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Posting_changes_balances_of_both_accounts()
    {
        var employee = await CreateEmployeeAsync();

        await PostAsync(
            OperationType.ShiftClose,
            TransactionDraft.Create(7000, Leg.Increase(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Revenue), "Наличка в сейф"),
            TransactionDraft.Create(12000, Leg.Increase(SystemAccounts.Bank), Leg.Increase(SystemAccounts.Revenue), "Безнал"),
            TransactionDraft.Create(2400, Leg.Increase(SystemAccounts.Expense), Leg.Increase(employee), "ЗП"));
        await PostAsync(
            OperationType.Payout,
            TransactionDraft.Create(1000, Leg.Decrease(SystemAccounts.Safe), Leg.Decrease(employee)));

        Assert.Equal(6000, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(12000, await BalanceAsync(SystemAccounts.Bank));
        Assert.Equal(19000, await BalanceAsync(SystemAccounts.Revenue));
        Assert.Equal(2400, await BalanceAsync(SystemAccounts.Expense));
        Assert.Equal(1400, await BalanceAsync(employee));
    }

    [Fact]
    public async Task Employee_balance_can_go_negative()
    {
        var employee = await CreateEmployeeAsync();

        await PostAsync(
            OperationType.Payout,
            TransactionDraft.Create(3000, Leg.Decrease(SystemAccounts.Bank), Leg.Decrease(employee)));

        Assert.Equal(-3000, await BalanceAsync(employee));
    }

    [Fact]
    public async Task Cancelling_an_operation_reverts_all_its_transactions()
    {
        var employee = await CreateEmployeeAsync();
        await PostAsync(
            OperationType.ShiftClose,
            TransactionDraft.Create(7000, Leg.Increase(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Revenue)),
            TransactionDraft.Create(2400, Leg.Increase(SystemAccounts.Expense), Leg.Increase(employee)));
        var payout = await PostAsync(
            OperationType.Payout,
            TransactionDraft.Create(1000, Leg.Decrease(SystemAccounts.Safe), Leg.Decrease(employee)));

        var cancelled = await app.InScopeAsync(async services =>
        {
            var ledger = services.GetRequiredService<Ledger>();
            await using var transaction = await ledger.BeginAsync(Ct);
            var operation = await ledger.CancelAsync(payout, "root", "выдали по ошибке", DateTimeOffset.UtcNow, Ct);
            await services.GetRequiredService<SmenaDbContext>().SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
            return operation;
        });

        Assert.NotNull(cancelled);
        Assert.True(cancelled.IsCancelled);
        Assert.Equal(7000, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(2400, await BalanceAsync(employee));
    }

    [Fact]
    public async Task Cancelling_twice_is_rejected()
    {
        var operationId = await PostAsync(
            OperationType.SafeIncome,
            TransactionDraft.Create(100, Leg.Increase(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Opening)));

        Task CancelAsync() => app.InScopeAsync(async services =>
        {
            var ledger = services.GetRequiredService<Ledger>();
            await using var transaction = await ledger.BeginAsync(Ct);
            await ledger.CancelAsync(operationId, "root", "причина", DateTimeOffset.UtcNow, Ct);
            await services.GetRequiredService<SmenaDbContext>().SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
            return 0;
        });

        await CancelAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(CancelAsync);
    }

    [Fact]
    public async Task Posting_outside_ledger_transaction_is_rejected()
    {
        var draft = new OperationDraft(OperationType.SafeIncome, DateTimeOffset.UtcNow, Cashier, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.InScopeAsync(services =>
        {
            services.GetRequiredService<Ledger>().Post(draft);
            return Task.FromResult(0);
        }));
    }

    [Fact]
    public async Task Operation_is_found_by_idempotency_key_and_cannot_be_duplicated()
    {
        var key = Guid.CreateVersion7();
        var first = await PostWithKeyAsync(key);

        var found = await app.InScopeAsync(services =>
            services.GetRequiredService<Ledger>().FindByIdempotencyKeyAsync(key, Ct));
        var missing = await app.InScopeAsync(services =>
            services.GetRequiredService<Ledger>().FindByIdempotencyKeyAsync(Guid.CreateVersion7(), Ct));

        Assert.Equal(first, found?.Id);
        Assert.Null(missing);

        // Даже если код забудет проверить ключ, вторую операцию не пустит база.
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => PostWithKeyAsync(key));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        Assert.Equal(100, await BalanceAsync(SystemAccounts.Safe));
    }

    [Fact]
    public async Task Operation_keeps_order_notes_and_utc_time()
    {
        var moscowEvening = new DateTimeOffset(2026, 11, 10, 21, 30, 0, TimeSpan.FromHours(3));

        var operationId = await app.InScopeAsync(async services =>
        {
            var ledger = services.GetRequiredService<Ledger>();
            await using var transaction = await ledger.BeginAsync(Ct);
            var operation = ledger.Post(new OperationDraft(
                OperationType.ShiftClose,
                moscowEvening,
                Cashier,
                [
                    TransactionDraft.Create(1, Leg.Increase(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Revenue), "первая"),
                    TransactionDraft.Create(2, Leg.Increase(SystemAccounts.Bank), Leg.Increase(SystemAccounts.Revenue), "вторая"),
                    TransactionDraft.Create(3, Leg.Decrease(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Expense), "третья"),
                ],
                comment: "смена"));
            await services.GetRequiredService<SmenaDbContext>().SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
            return operation.Id;
        });

        var stored = await app.QueryDatabaseAsync(db => db.Operations
            .AsNoTracking()
            .Include(operation => operation.Transactions.OrderBy(transaction => transaction.Position))
            .ThenInclude(transaction => transaction.Entries)
            .SingleAsync(operation => operation.Id == operationId));

        Assert.Equal(moscowEvening, stored.OccurredAt);
        Assert.Equal(TimeSpan.Zero, stored.OccurredAt.Offset);
        Assert.Equal("смена", stored.Comment);
        Assert.Equal(ActorKind.Cashier, stored.ActorKind);
        Assert.Equal(["первая", "вторая", "третья"], stored.Transactions.Select(transaction => transaction.Note));
        Assert.Equal([1, 2, 3], stored.Transactions.Select(transaction => transaction.Amount));
        Assert.All(stored.Transactions, transaction => Assert.Equal(2, transaction.Entries.Count));
    }

    [Fact]
    public async Task Movements_view_shows_entries_with_amount_and_cancel_flag()
    {
        var operationId = await PostAsync(
            OperationType.SafeExpense,
            TransactionDraft.Create(450, Leg.Decrease(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Expense), "Вода"));

        var movements = await app.QueryDatabaseAsync(db => db.AccountMovements
            .Where(movement => movement.OperationId == operationId)
            .OrderBy(movement => movement.AccountId)
            .ToListAsync());

        Assert.Collection(
            movements,
            safe =>
            {
                Assert.Equal(SystemAccounts.Safe.Id, safe.AccountId);
                Assert.Equal(AccountKind.Safe, safe.AccountKind);
                Assert.Equal(450, safe.Amount);
                Assert.Equal(-450, safe.SignedAmount);
                Assert.Equal("Вода", safe.Note);
                Assert.Equal(OperationType.SafeExpense, safe.OperationType);
                Assert.False(safe.IsCancelled);
            },
            expense =>
            {
                Assert.Equal(SystemAccounts.Expense.Id, expense.AccountId);
                Assert.Equal(450, expense.SignedAmount);
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Guid> PostAsync(OperationType type, params TransactionDraft[] transactions) =>
        PostAsync(new OperationDraft(type, DateTimeOffset.UtcNow, Cashier, transactions));

    private Task<Guid> PostWithKeyAsync(Guid key) => PostAsync(new OperationDraft(
        OperationType.SafeIncome,
        DateTimeOffset.UtcNow,
        Cashier,
        [TransactionDraft.Create(100, Leg.Increase(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Opening))],
        idempotencyKey: key));

    private Task<Guid> PostAsync(OperationDraft draft) => app.InScopeAsync(async services =>
    {
        var ledger = services.GetRequiredService<Ledger>();
        await using var transaction = await ledger.BeginAsync(Ct);
        var operation = ledger.Post(draft);
        await services.GetRequiredService<SmenaDbContext>().SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        return operation.Id;
    });

    private Task<int> BalanceAsync(AccountRef account) => app.InScopeAsync(services =>
        services.GetRequiredService<Ledger>().GetBalanceAsync(account, Ct));

    private Task<AccountRef> CreateEmployeeAsync() => app.QueryDatabaseAsync(async db =>
    {
        var employee = Employee.Create(telegramId: 1001, name: "Иван", hourlyRate: 200);
        db.Employees.Add(employee);
        await db.SaveChangesAsync(Ct);
        return employee.Account.Ref;
    });
}
