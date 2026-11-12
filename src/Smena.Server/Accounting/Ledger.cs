using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Smena.Core.Accounting;
using Smena.Server.Database;

namespace Smena.Server.Accounting;

/// <summary>
/// Единственная точка, через которую деньги попадают в базу и читаются из неё.
/// </summary>
/// <remarks>
/// Порядок работы любой денежной операции:
/// <code>
/// await using var transaction = await ledger.BeginAsync(ct);   // блокировка
/// var balance = await ledger.GetBalanceAsync(account, ct);      // чтение под блокировкой
/// ledger.Post(draft);                                           // проводки в контекст
/// await db.SaveChangesAsync(ct);
/// await transaction.CommitAsync(ct);                            // здесь база проверяет баланс
/// </code>
/// </remarks>
public sealed class Ledger(SmenaDbContext db)
{
    // Одна блокировка на все денежные операции. Касса одна, нагрузки нет:
    // это убирает гонки («прочитал баланс — записал») без блокировок на каждый счёт.
    private const long MoneyLockKey = 0x534D_454E_41; // "SMENA"

    // Запросы скомпилированы один раз: EF не разбирает дерево выражения при каждом вызове.
    private static readonly Func<SmenaDbContext, int, CancellationToken, Task<int>> BalanceQuery =
        EF.CompileAsyncQuery((SmenaDbContext context, int accountId, CancellationToken _) =>
            context.AccountMovements
                .Where(movement => movement.AccountId == accountId && !movement.IsCancelled)
                .Sum(movement => movement.SignedAmount));

    private static readonly Func<SmenaDbContext, Guid, CancellationToken, Task<Operation?>> ByIdempotencyKeyQuery =
        EF.CompileAsyncQuery((SmenaDbContext context, Guid key, CancellationToken _) =>
            context.Operations.AsNoTracking().FirstOrDefault(operation => operation.IdempotencyKey == key));

    /// <summary>
    /// Открывает транзакцию базы и берёт денежную блокировку. Блокировка
    /// снимается сама при фиксации или откате.
    /// </summary>
    public async Task<IDbContextTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({MoneyLockKey})", cancellationToken);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Добавляет операцию со всеми транзакциями и проводками в контекст.
    /// В базу она попадает при SaveChanges вызывающего — вместе с остальными
    /// его изменениями (сменой, фото), одной транзакцией базы.
    /// </summary>
    public Operation Post(OperationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        RequireTransaction();

        var operation = Operation.FromDraft(draft);
        db.Operations.Add(operation);
        return operation;
    }

    /// <summary>Баланс счёта по неотменённым операциям.</summary>
    public Task<int> GetBalanceAsync(AccountRef account, CancellationToken cancellationToken) =>
        BalanceQuery(db, account.Id, cancellationToken);

    /// <summary>Операция, уже созданная запросом с таким ключом, либо null.</summary>
    public Task<Operation?> FindByIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken) =>
        ByIdempotencyKeyQuery(db, idempotencyKey, cancellationToken);

    /// <summary>Отменяет операцию целиком: её транзакции перестают учитываться в балансах.</summary>
    /// <returns>Отменённая операция либо null, если такой нет.</returns>
    public async Task<Operation?> CancelAsync(
        Guid operationId,
        string cancelledBy,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        RequireTransaction();

        var operation = await db.Operations.FirstOrDefaultAsync(o => o.Id == operationId, cancellationToken);
        operation?.Cancel(at, cancelledBy, reason);
        return operation;
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Изменять учёт можно только внутри транзакции, открытой через Ledger.BeginAsync.");
        }
    }
}
