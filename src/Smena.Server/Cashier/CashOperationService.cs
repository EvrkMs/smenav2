using Microsoft.EntityFrameworkCore;
using Smena.Core.Accounting;
using Smena.Core.Operations;
using Smena.Server.Accounting;
using Smena.Server.Banking;
using Smena.Server.Database;
using Smena.Server.Notifications;

namespace Smena.Server.Cashier;

/// <summary>Итог операции кассы: проведена, повторена либо отклонена с причиной.</summary>
public readonly record struct CashOperationResult(Guid? OperationId, bool Replayed, string? Error)
{
    public static CashOperationResult Rejected(string error) => new(null, Replayed: false, error);
}

/// <summary>Операции, которые администратор проводит с кассы помимо закрытия смены.</summary>
public sealed class CashOperationService(
    SmenaDbContext db,
    Ledger ledger,
    BankReceiptService receipts,
    NotificationService notifications,
    TimeProvider time)
{
    /// <summary>Аванс или выплата ЗП сотруднику из сейфа.</summary>
    public async Task<CashOperationResult> PayoutAsync(
        Guid employeeId,
        int amount,
        bool isSalary,
        string? comment,
        Guid idempotencyKey,
        Actor actor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await ledger.BeginAsync(cancellationToken);

        if (await ledger.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken) is { } existing)
        {
            return new CashOperationResult(existing.Id, Replayed: true, Error: null);
        }

        if (CashRules.CheckComment(comment, required: false) is { } commentError)
        {
            return CashOperationResult.Rejected(commentError);
        }

        var employee = await db.Employees
            .AsNoTracking()
            .Where(e => e.Id == employeeId)
            .Select(e => new { e.Name, AccountId = e.Account.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (employee is null)
        {
            return CashOperationResult.Rejected("Сотрудник не найден. Обновите список сотрудников.");
        }

        // Балансы читаются под блокировкой: два запроса подряд не выдадут ЗП дважды.
        var account = new AccountRef(employee.AccountId, AccountKind.Employee);
        var employeeBalance = await ledger.GetBalanceAsync(account, cancellationToken);
        var safeBalance = await ledger.GetBalanceAsync(SystemAccounts.Safe, cancellationToken);

        if (CashRules.CheckPayout(amount, employeeBalance, safeBalance) is { } error)
        {
            return CashOperationResult.Rejected(error);
        }

        var label = isSalary ? "ЗП" : "Аванс";
        var operation = ledger.Post(new OperationDraft(
            OperationType.Payout,
            time.GetUtcNow(),
            actor,
            [
                TransactionDraft.Create(
                    amount,
                    Leg.Decrease(SystemAccounts.Safe, SystemCategories.SafePayout),
                    Leg.Decrease(account, isSalary ? SystemCategories.EmployeeSalary : SystemCategories.EmployeeAdvance),
                    $"{label}: {employee.Name}"),
            ],
            comment?.Trim() ?? string.Empty,
            idempotencyKey));

        await db.SaveChangesAsync(cancellationToken);

        // Сообщения — в той же транзакции базы, что и операция.
        await notifications.EnqueueOperationAsync(operation, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new CashOperationResult(operation.Id, Replayed: false, Error: null);
    }

    /// <summary>
    /// Приход в сейф или расход из сейфа. Администратор выбирает только категорию:
    /// второй счёт и его категория заданы в ней, направление подбирается по правилу баланса.
    /// </summary>
    public Task<CashOperationResult> MoveSafeAsync(
        bool isIncome,
        int categoryId,
        int amount,
        string? comment,
        Guid idempotencyKey,
        Actor actor,
        CancellationToken cancellationToken) =>
        MoveAsync(SystemAccounts.Safe, isIncome, categoryId, amount, comment, idempotencyKey, actor, cancellationToken);

    /// <summary>
    /// Приход на банк или расход с банка, которые root вносит по выписке: аренда,
    /// налоги, прочие поступления. Устроено так же, как приход и расход сейфа.
    /// </summary>
    public Task<CashOperationResult> MoveBankAsync(
        bool isIncome,
        int categoryId,
        int amount,
        string? comment,
        Guid idempotencyKey,
        Actor actor,
        CancellationToken cancellationToken) =>
        MoveAsync(SystemAccounts.Bank, isIncome, categoryId, amount, comment, idempotencyKey, actor, cancellationToken);

    private async Task<CashOperationResult> MoveAsync(
        AccountRef account,
        bool isIncome,
        int categoryId,
        int amount,
        string? comment,
        Guid idempotencyKey,
        Actor actor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await ledger.BeginAsync(cancellationToken);

        if (await ledger.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken) is { } existing)
        {
            return new CashOperationResult(existing.Id, Replayed: true, Error: null);
        }

        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
        var isSafe = account.Kind == AccountKind.Safe;
        var expectedKind = (isSafe, isIncome) switch
        {
            (true, true) => CategoryKind.SafeIncome,
            (true, false) => CategoryKind.SafeExpense,
            (false, true) => CategoryKind.BankIncome,
            (false, false) => CategoryKind.BankExpense,
        };

        if (category is null || category.Kind != expectedKind || category.IsArchived || category.CounterAccountId is not { } counterAccountId)
        {
            return CashOperationResult.Rejected("Категория недоступна. Обновите список категорий.");
        }

        var counterAccount = await db.Accounts
            .AsNoTracking()
            .Where(a => a.Id == counterAccountId)
            .Select(a => new AccountRef(a.Id, a.Kind))
            .SingleAsync(cancellationToken);

        CategoryRef? counterCategory = category.CounterCategoryId is { } counterCategoryId
            ? await db.Categories
                .AsNoTracking()
                .Where(c => c.Id == counterCategoryId)
                .Select(c => new CategoryRef(c.Id, c.Kind))
                .SingleAsync(cancellationToken)
            : null;

        // Трата без объяснения бесполезна для отчётов; для инкассации и снятия
        // с банка комментарий не нужен.
        var commentRequired = counterAccount.Kind == AccountKind.Expense;
        if (CashRules.CheckComment(comment, commentRequired) is { } commentError)
        {
            return CashOperationResult.Rejected(commentError);
        }

        // Сейф в минус не уходит. Банк не проверяется: root вносит туда то, что уже
        // случилось по выписке, и отказ ничего бы не изменил.
        var amountError = isSafe && !isIncome
            ? CashRules.CheckSafeExpense(amount, await ledger.GetBalanceAsync(SystemAccounts.Safe, cancellationToken))
            : CashRules.CheckAmount(amount);
        if (amountError is not null)
        {
            return CashOperationResult.Rejected(amountError);
        }

        var ownLeg = isIncome
            ? Leg.Increase(account, category.Ref)
            : Leg.Decrease(account, category.Ref);

        TransactionDraft draft;
        try
        {
            draft = TransactionDraft.Create(amount, ownLeg, Leg.Balancing(ownLeg, counterAccount, counterCategory), category.Name);
        }
        catch (LedgerRuleException)
        {
            // Категорию завели в панели с несовместимым вторым счётом или его категорией.
            return CashOperationResult.Rejected($"Категория «{category.Name}» настроена неверно. Сообщите root.");
        }

        var operationType = (isSafe, isIncome) switch
        {
            (true, true) => OperationType.SafeIncome,
            (true, false) => OperationType.SafeExpense,
            (false, true) => OperationType.BankIncome,
            (false, false) => OperationType.BankExpense,
        };

        var operation = ledger.Post(new OperationDraft(
            operationType,
            time.GetUtcNow(),
            actor,
            [draft],
            comment?.Trim() ?? string.Empty,
            idempotencyKey));

        if (isSafe)
        {
            // Инкассация: деньги ушли в банк, но зачислены будут позже. Приход,
            // который root вносит прямо на банк, ожидания не создаёт: он уже в выписке.
            await receipts.TrackIncomingAsync(operation, ReceiptChannelKind.CashDeposit, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        // Сообщения — в той же транзакции базы, что и операция.
        await notifications.EnqueueOperationAsync(operation, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new CashOperationResult(operation.Id, Replayed: false, Error: null);
    }
}
