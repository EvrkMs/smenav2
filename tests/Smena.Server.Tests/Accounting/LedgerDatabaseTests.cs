using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Smena.Core.Accounting;
using Smena.Server.Accounting;
using Smena.Server.Database;
using Smena.Server.Employees;

namespace Smena.Server.Tests.Accounting;

/// <summary>
/// То, что база гарантирует сама, без участия сервера: правки руками через SQL
/// не должны оставлять учёт несбалансированным или с хвостами.
/// </summary>
public sealed class LedgerDatabaseTests(SmenaApp app) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await app.ResetLedgerAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Deleting_an_operation_removes_its_transactions_and_entries()
    {
        var operationId = await PostTwoTransactionsAsync();

        await ExecuteAsync($"DELETE FROM operations WHERE id = {operationId}");

        Assert.Equal((0, 0, 0), await CountRowsAsync());
        Assert.Equal(0, await BalanceAsync(SystemAccounts.Safe));
    }

    [Fact]
    public async Task Deleting_one_transaction_removes_both_its_entries_and_keeps_the_rest()
    {
        var operationId = await PostTwoTransactionsAsync();

        await ExecuteAsync($"DELETE FROM transactions WHERE operation_id = {operationId} AND position = 0");

        // Осталась вторая транзакция (расход 300 из сейфа) с обеими проводками.
        Assert.Equal((1, 1, 2), await CountRowsAsync());
        Assert.Equal(-300, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(300, await BalanceAsync(SystemAccounts.Expense));
        Assert.Equal(0, await BalanceAsync(SystemAccounts.Revenue));
    }

    [Fact]
    public async Task Deleting_a_single_entry_is_rejected()
    {
        await PostTwoTransactionsAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"DELETE FROM entries WHERE id = (SELECT min(id) FROM entries)"));

        Assert.Equal("ledger_transaction_two_entries", error.ConstraintName);
        Assert.Equal((1, 2, 4), await CountRowsAsync());
    }

    [Fact]
    public async Task Flipping_direction_of_one_entry_is_rejected()
    {
        await PostTwoTransactionsAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"UPDATE entries SET direction = -direction WHERE id = (SELECT min(id) FROM entries)"));

        Assert.Equal("ledger_transaction_balanced", error.ConstraintName);
    }

    [Fact]
    public async Task Hand_written_balanced_transaction_is_accepted()
    {
        var (operationId, transactionId) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        await ExecuteAsync($"""
            INSERT INTO operations (id, type_id, occurred_at, comment, actor_kind_id, actor_name)
            VALUES ({operationId}, 3, now(), 'руками', 2, 'root');
            INSERT INTO transactions (id, operation_id, position, amount) VALUES ({transactionId}, {operationId}, 0, 250);
            INSERT INTO entries (transaction_id, account_id, direction)
            VALUES ({transactionId}, 1, 1), ({transactionId}, 4, 1);
            """);

        Assert.Equal(250, await BalanceAsync(SystemAccounts.Safe));
        Assert.Equal(250, await BalanceAsync(SystemAccounts.Revenue));
    }

    [Fact]
    public async Task Hand_written_unbalanced_transaction_is_rejected()
    {
        var (operationId, transactionId) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        // Сейф + и банк +: деньги появились в двух местах сразу.
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"""
            INSERT INTO operations (id, type_id, occurred_at, comment, actor_kind_id, actor_name)
            VALUES ({operationId}, 3, now(), 'руками', 2, 'root');
            INSERT INTO transactions (id, operation_id, position, amount) VALUES ({transactionId}, {operationId}, 0, 250);
            INSERT INTO entries (transaction_id, account_id, direction)
            VALUES ({transactionId}, 1, 1), ({transactionId}, 2, 1);
            """));

        Assert.Equal("ledger_transaction_balanced", error.ConstraintName);
        Assert.Equal((0, 0, 0), await CountRowsAsync());
    }

    [Fact]
    public async Task Transaction_without_entries_is_rejected()
    {
        var (operationId, transactionId) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"""
            INSERT INTO operations (id, type_id, occurred_at, comment, actor_kind_id, actor_name)
            VALUES ({operationId}, 3, now(), 'руками', 2, 'root');
            INSERT INTO transactions (id, operation_id, position, amount) VALUES ({transactionId}, {operationId}, 0, 250);
            """));

        Assert.Equal("ledger_transaction_two_entries", error.ConstraintName);
    }

    [Theory]
    [InlineData("UPDATE transactions SET amount = 0", "ck_transactions_amount")]
    [InlineData("UPDATE transactions SET amount = -5", "ck_transactions_amount")]
    [InlineData("UPDATE entries SET direction = 2", "ck_entries_direction")]
    [InlineData("UPDATE entries SET direction = 0", "ck_entries_direction")]
    [InlineData("UPDATE entries SET account_id = 1", "ix_entries_transaction_id_account_id")]
    [InlineData("UPDATE operations SET type_id = 999", "fk_operations_operation_type_row_type_id")]
    [InlineData("UPDATE operations SET cancelled_at = now()", "ck_operations_cancel")]
    [InlineData("DELETE FROM accounts WHERE id = 1", "fk_entries_accounts_account_id")]
    public async Task Invalid_values_are_rejected(string sql, string constraint)
    {
        await PostTwoTransactionsAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, Ct)));

        Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task Lookup_tables_match_core_enums()
    {
        var (accountKinds, categoryKinds, operationTypes, actorKinds) = await app.QueryDatabaseAsync(async db => (
            await db.Set<AccountKindRow>().AsNoTracking().ToListAsync(Ct),
            await db.Set<CategoryKindRow>().AsNoTracking().ToListAsync(Ct),
            await db.Set<OperationTypeRow>().AsNoTracking().ToListAsync(Ct),
            await db.Set<ActorKindRow>().AsNoTracking().ToListAsync(Ct)));

        Assert.Equal(Enum.GetValues<AccountKind>().Order(), accountKinds.Select(row => row.Id).Order());
        Assert.Equal(Enum.GetValues<CategoryKind>().Order(), categoryKinds.Select(row => row.Id).Order());
        Assert.Equal(Enum.GetValues<OperationType>().Order(), operationTypes.Select(row => row.Id).Order());
        Assert.Equal(Enum.GetValues<ActorKind>().Order(), actorKinds.Select(row => row.Id).Order());

        // Триггер баланса читает сторону из базы, код — из ядра. Они обязаны совпадать.
        Assert.All(accountKinds, row => Assert.Equal(row.Id.Side(), row.Side));
        Assert.All(categoryKinds, row =>
        {
            Assert.Equal(row.Id.Account(), row.AccountKind);
            Assert.Equal(row.Id.RequiredDirection(), row.Direction);
        });
    }

    [Fact]
    public async Task System_accounts_exist_with_fixed_ids()
    {
        var accounts = await app.QueryDatabaseAsync(db => db.Accounts
            .AsNoTracking()
            .OrderBy(account => account.Id)
            .ToListAsync(Ct));

        Assert.Equal(
            [SystemAccounts.Safe, SystemAccounts.Bank, SystemAccounts.Expense, SystemAccounts.Revenue, SystemAccounts.Opening],
            accounts.Select(account => account.Ref));
    }

    [Fact]
    public async Task Employee_gets_own_account_outside_system_range()
    {
        var employee = await app.QueryDatabaseAsync(async db =>
        {
            var created = Employee.Create(telegramId: 777, name: "Мария", hourlyRate: 180);
            db.Employees.Add(created);
            await db.SaveChangesAsync(Ct);
            return created;
        });

        Assert.Equal(7, employee.Id.Version);
        Assert.Equal(AccountKind.Employee, employee.Account.Kind);
        Assert.Equal(employee.Id, employee.Account.EmployeeId);
        Assert.True(employee.Account.Id >= 100);
    }

    [Theory]
    [InlineData("INSERT INTO accounts (kind_id, name) VALUES (1, 'Второй сейф')", "ux_accounts_system_kind")]
    [InlineData("INSERT INTO accounts (kind_id, name) VALUES (4, 'Сотрудник без сотрудника')", "ck_accounts_employee")]
    [InlineData("INSERT INTO accounts (kind_id, name) VALUES (99, 'Неизвестный вид')", "fk_accounts_account_kind_row_kind_id")]
    [InlineData("INSERT INTO employees (id, telegram_id, name, hourly_rate, is_active) VALUES (gen_random_uuid(), 5, 'А', -1, true)", "ck_employees_hourly_rate")]
    public async Task Invalid_accounts_and_employees_are_rejected(string sql, string constraint)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, Ct)));

        Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task Two_employees_cannot_share_telegram_id()
    {
        Task AddAsync() => app.QueryDatabaseAsync(async db =>
        {
            db.Employees.Add(Employee.Create(telegramId: 42, name: "Дубль", hourlyRate: 100));
            return await db.SaveChangesAsync(Ct);
        });

        await AddAsync();
        var error = await Assert.ThrowsAsync<DbUpdateException>(AddAsync);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<int> ExecuteAsync(FormattableString sql) =>
        app.QueryDatabaseAsync(db => db.Database.ExecuteSqlAsync(sql, Ct));

    private Task<int> BalanceAsync(AccountRef account) => app.InScopeAsync(services =>
        services.GetRequiredService<Ledger>().GetBalanceAsync(account, Ct));

    private Task<(int Operations, int Transactions, int Entries)> CountRowsAsync() => app.QueryDatabaseAsync(async db => (
        await db.Operations.CountAsync(Ct),
        await db.Transactions.CountAsync(Ct),
        await db.Entries.CountAsync(Ct)));

    /// <summary>Одна операция: 1000 в сейф как выручка (позиция 0) и расход 300 из сейфа (позиция 1).</summary>
    private Task<Guid> PostTwoTransactionsAsync() => app.InScopeAsync(async services =>
    {
        var ledger = services.GetRequiredService<Ledger>();
        await using var transaction = await ledger.BeginAsync(Ct);
        var operation = ledger.Post(new OperationDraft(
            OperationType.ShiftClose,
            DateTimeOffset.UtcNow,
            Actor.System,
            [
                TransactionDraft.Create(1000, Leg.Increase(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Revenue)),
                TransactionDraft.Create(300, Leg.Decrease(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Expense)),
            ]));
        await services.GetRequiredService<SmenaDbContext>().SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        return operation.Id;
    });
}
