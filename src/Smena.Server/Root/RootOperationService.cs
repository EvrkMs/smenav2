using Microsoft.EntityFrameworkCore;
using Smena.Core.Accounting;
using Smena.Core.Operations;
using Smena.Server.Accounting;
using Smena.Server.Cashier;
using Smena.Server.Database;
using Smena.Server.Notifications;

namespace Smena.Server.Root;

/// <summary>Куда вносится стартовый остаток.</summary>
public enum OpeningTarget
{
    Safe = 1,
    Bank = 2,
    Employee = 3,
}

/// <summary>
/// Денежные операции, доступные только root: выплаты с банка и сверх баланса,
/// штрафы, премии, инвентаризация, стартовые остатки.
/// </summary>
public sealed class RootOperationService(
    SmenaDbContext db,
    Ledger ledger,
    NotificationService notifications,
    TimeProvider time)
{
    /// <summary>Выплата сотруднику из сейфа или с банка; по явному флагу — больше его баланса.</summary>
    public Task<CashOperationResult> PayoutAsync(
        Guid employeeId,
        int amount,
        bool fromBank,
        bool isSalary,
        bool allowOverdraft,
        string? comment,
        Guid idempotencyKey,
        Actor actor,
        CancellationToken cancellationToken) =>
        RunAsync(idempotencyKey, comment, commentRequired: false, actor, cancellationToken, async () =>
        {
            if (await FindEmployeeAsync(employeeId, cancellationToken) is not { } employee)
            {
                return EmployeeNotFound;
            }

            var employeeBalance = await ledger.GetBalanceAsync(employee.Account, cancellationToken);
            var safeBalance = fromBank ? 0 : await ledger.GetBalanceAsync(SystemAccounts.Safe, cancellationToken);

            if (CashRules.CheckRootPayout(amount, employeeBalance, allowOverdraft, fromSafe: !fromBank, safeBalance) is { } error)
            {
                return error;
            }

            var source = fromBank
                ? Leg.Decrease(SystemAccounts.Bank, SystemCategories.BankPayout)
                : Leg.Decrease(SystemAccounts.Safe, SystemCategories.SafePayout);
            var label = isSalary ? "ЗП" : "Аванс";

            return Draft(
                OperationType.Payout,
                TransactionDraft.Create(
                    amount,
                    source,
                    Leg.Decrease(employee.Account, isSalary ? SystemCategories.EmployeeSalary : SystemCategories.EmployeeAdvance),
                    $"{label}: {employee.Name}"));
        });

    /// <summary>Штраф: уменьшает то, что клуб должен сотруднику; идёт в выручку отдельной категорией.</summary>
    public Task<CashOperationResult> FineAsync(
        Guid employeeId,
        int amount,
        string? comment,
        Guid idempotencyKey,
        Actor actor,
        CancellationToken cancellationToken) =>
        RunAsync(idempotencyKey, comment, commentRequired: true, actor, cancellationToken, async () =>
        {
            if (CashRules.CheckAmount(amount) is { } error)
            {
                return error;
            }

            if (await FindEmployeeAsync(employeeId, cancellationToken) is not { } employee)
            {
                return EmployeeNotFound;
            }

            return Draft(
                OperationType.Fine,
                TransactionDraft.Create(
                    amount,
                    Leg.Decrease(employee.Account, SystemCategories.EmployeeFine),
                    Leg.Increase(SystemAccounts.Revenue, SystemCategories.RevenueFines),
                    $"Штраф: {employee.Name}"));
        });

    /// <summary>Премия: увеличивает то, что клуб должен сотруднику; идёт в расходы.</summary>
    public Task<CashOperationResult> BonusAsync(
        Guid employeeId,
        int amount,
        string? comment,
        Guid idempotencyKey,
        Actor actor,
        CancellationToken cancellationToken) =>
        RunAsync(idempotencyKey, comment, commentRequired: true, actor, cancellationToken, async () =>
        {
            if (CashRules.CheckAmount(amount) is { } error)
            {
                return error;
            }

            if (await FindEmployeeAsync(employeeId, cancellationToken) is not { } employee)
            {
                return EmployeeNotFound;
            }

            return Draft(
                OperationType.Bonus,
                TransactionDraft.Create(
                    amount,
                    Leg.Increase(SystemAccounts.Expense, SystemCategories.ExpenseBonus),
                    Leg.Increase(employee.Account, SystemCategories.EmployeeBonus),
                    $"Премия: {employee.Name}"));
        });

    /// <summary>
    /// Инвентаризация: недостача товара делится поровну между выбранными
    /// сотрудниками, с округлением суммы вверх до делящейся нацело.
    /// </summary>
    public Task<CashOperationResult> InventoryAsync(
        IReadOnlyList<Guid> employeeIds,
        int totalAmount,
        string? comment,
        Guid idempotencyKey,
        Actor actor,
        CancellationToken cancellationToken) =>
        RunAsync(idempotencyKey, comment, commentRequired: false, actor, cancellationToken, async () =>
        {
            if (CashRules.CheckAmount(totalAmount) is { } error)
            {
                return error;
            }

            var ids = (employeeIds ?? []).Distinct().ToArray();
            if (ids.Length == 0)
            {
                return "Выберите сотрудников.";
            }

            var employees = await db.Employees
                .AsNoTracking()
                .Where(employee => ids.Contains(employee.Id))
                .OrderBy(employee => employee.Name)
                .Select(employee => new EmployeeRef(employee.Name, new AccountRef(employee.Account.Id, AccountKind.Employee)))
                .ToListAsync(cancellationToken);

            // Все или никто: иначе сумма молча разделилась бы между найденными.
            if (employees.Count != ids.Length)
            {
                return EmployeeNotFound;
            }

            var share = CashRules.InventoryShare(totalAmount, employees.Count);
            var transactions = new TransactionDraft[employees.Count];
            for (var index = 0; index < transactions.Length; index++)
            {
                var employee = employees[index];
                transactions[index] = TransactionDraft.Create(
                    share,
                    Leg.Decrease(employee.Account, SystemCategories.EmployeeInventory),
                    Leg.Increase(SystemAccounts.Revenue, SystemCategories.RevenueInventory),
                    $"Инвентаризация: {employee.Name}");
            }

            return Draft(OperationType.Inventory, transactions);
        });

    /// <summary>
    /// Стартовый остаток при переходе на систему. Для сейфа и банка сумма
    /// положительная; для сотрудника положительная — клуб должен ему, отрицательная —
    /// он должен клубу.
    /// </summary>
    public Task<CashOperationResult> OpeningAsync(
        OpeningTarget target,
        Guid? employeeId,
        int amount,
        string? comment,
        Guid idempotencyKey,
        Actor actor,
        CancellationToken cancellationToken) =>
        RunAsync(idempotencyKey, comment, commentRequired: false, actor, cancellationToken, async () =>
        {
            var magnitude = target == OpeningTarget.Employee ? Math.Abs((long)amount) : amount;
            if (magnitude is <= 0 or > CashRules.MaxAmount)
            {
                return target == OpeningTarget.Employee
                    ? $"Сумма должна быть ненулевой и не больше {CashRules.MaxAmount} по модулю."
                    : $"Сумма должна быть от 1 до {CashRules.MaxAmount}.";
            }

            switch (target)
            {
                case OpeningTarget.Safe:
                    return Draft(OperationType.Opening, TransactionDraft.Create(
                        amount,
                        Leg.Increase(SystemAccounts.Safe, SystemCategories.SafeOpening),
                        Leg.Increase(SystemAccounts.Opening),
                        "Стартовый остаток сейфа"));

                case OpeningTarget.Bank:
                    // Запись ожидания не создаётся: эти деньги уже лежат на счёте.
                    return Draft(OperationType.Opening, TransactionDraft.Create(
                        amount,
                        Leg.Increase(SystemAccounts.Bank, SystemCategories.BankOpening),
                        Leg.Increase(SystemAccounts.Opening),
                        "Стартовый остаток банка"));

                case OpeningTarget.Employee:
                    if (employeeId is not { } id || await FindEmployeeAsync(id, cancellationToken) is not { } employee)
                    {
                        return EmployeeNotFound;
                    }

                    var own = new Leg(
                        employee.Account,
                        amount > 0 ? Direction.Increase : Direction.Decrease,
                        SystemCategories.EmployeeOpening);

                    return Draft(OperationType.Opening, TransactionDraft.Create(
                        (int)magnitude,
                        own,
                        Leg.Balancing(own, SystemAccounts.Opening),
                        $"Стартовый остаток: {employee.Name}"));

                default:
                    return "Неизвестный вид остатка.";
            }
        });

    private const string EmployeeNotFound = "Сотрудник не найден. Обновите список сотрудников.";

    /// <summary>
    /// Общая обвязка денежной операции: блокировка, проверка повтора, проверка
    /// комментария, проводка, сообщения, фиксация. Шаг <paramref name="build"/>
    /// возвращает либо текст отказа, либо черновик.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1068:CancellationToken parameters must come last",
        Justification = "Шаг сборки идёт последним, чтобы вызовы читались как блок кода.")]
    private async Task<CashOperationResult> RunAsync(
        Guid idempotencyKey,
        string? comment,
        bool commentRequired,
        Actor actor,
        CancellationToken cancellationToken,
        Func<Task<BuildResult>> build)
    {
        await using var transaction = await ledger.BeginAsync(cancellationToken);

        if (await ledger.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken) is { } existing)
        {
            return new CashOperationResult(existing.Id, Replayed: true, Error: null);
        }

        if (CashRules.CheckComment(comment, commentRequired) is { } commentError)
        {
            return CashOperationResult.Rejected(commentError);
        }

        var built = await build();
        if (built.Error is { } error)
        {
            return CashOperationResult.Rejected(error);
        }

        var operation = ledger.Post(new OperationDraft(
            built.Type,
            time.GetUtcNow(),
            actor,
            built.Transactions!,
            comment?.Trim() ?? string.Empty,
            idempotencyKey));

        await db.SaveChangesAsync(cancellationToken);
        await notifications.EnqueueOperationAsync(operation, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CashOperationResult(operation.Id, Replayed: false, Error: null);
    }

    private static BuildResult Draft(OperationType type, params TransactionDraft[] transactions) => new(type, transactions, Error: null);

    private async Task<EmployeeRef?> FindEmployeeAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var found = await db.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Select(employee => new { employee.Name, AccountId = employee.Account.Id })
            .FirstOrDefaultAsync(cancellationToken);

        return found is null ? null : new EmployeeRef(found.Name, new AccountRef(found.AccountId, AccountKind.Employee));
    }

    private readonly record struct EmployeeRef(string Name, AccountRef Account);

    private readonly record struct BuildResult(OperationType Type, TransactionDraft[]? Transactions, string? Error)
    {
        public static implicit operator BuildResult(string error) => new(default, null, error);
    }
}
