using Microsoft.EntityFrameworkCore;
using Smena.Core.Accounting;
using Smena.Core.Shifts;
using Smena.Server.Accounting;
using Smena.Server.Banking;
using Smena.Server.Database;
using Smena.Server.Notifications;
using Smena.Server.Photos;

namespace Smena.Server.Shifts;

/// <summary>Итог попытки закрыть смену.</summary>
/// <param name="ShiftId">Заполнен, если смена закрыта (сейчас или предыдущим запросом с тем же ключом).</param>
/// <param name="Calculation">Расчёт; null только при повторе запроса.</param>
/// <param name="Replayed">Запрос с таким ключом уже был выполнен — вторая смена не создана.</param>
public readonly record struct ShiftCloseResult(Guid? ShiftId, ShiftCalculation? Calculation, bool Replayed)
{
    public bool IsClosed => ShiftId is not null;
}

public sealed class ShiftService(
    SmenaDbContext db,
    Ledger ledger,
    BankReceiptService receipts,
    NotificationService notifications,
    PhotoService photos,
    TimeProvider time)
{
    private static readonly ShiftChart Chart = new()
    {
        Safe = SystemAccounts.Safe,
        Bank = SystemAccounts.Bank,
        Revenue = SystemAccounts.Revenue,
        Expense = SystemAccounts.Expense,
        RevenueShift = SystemCategories.RevenueShift,
        RevenueShortageWithheld = SystemCategories.RevenueShortageWithheld,
        RevenueSafeSurplus = SystemCategories.RevenueSafeSurplus,
        ExpensePayroll = SystemCategories.ExpensePayroll,
        SafeShiftCash = SystemCategories.SafeShiftCash,
        SafeFloatRefill = SystemCategories.SafeFloatRefill,
        SafeSurplus = SystemCategories.SafeSurplus,
        SafeShortage = SystemCategories.SafeShortage,
        BankAcquiring = SystemCategories.BankAcquiring,
        EmployeeAccrual = SystemCategories.EmployeeAccrual,
        EmployeeShortage = SystemCategories.EmployeeShortage,
    };

    /// <summary>Предпросмотр: тот же расчёт, что при закрытии, но без записи и без блокировки.</summary>
    public Task<ShiftCalculation> CalculateAsync(ShiftInput input, CancellationToken cancellationToken) =>
        CalculateCoreAsync(input, cancellationToken);

    /// <summary>
    /// Закрывает смену: расчёт, проводки и запись смены — одной транзакцией базы.
    /// Если в расчёте есть проблемы, в базу не попадает ничего.
    /// </summary>
    public async Task<ShiftCloseResult> CloseAsync(
        ShiftInput input,
        Guid idempotencyKey,
        Actor actor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await ledger.BeginAsync(cancellationToken);

        // Под блокировкой: два одинаковых запроса не пройдут эту проверку одновременно.
        if (await ledger.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken) is { } existing)
        {
            var existingShiftId = await db.Shifts
                .Where(shift => shift.OperationId == existing.Id)
                .Select(shift => (Guid?)shift.Id)
                .FirstOrDefaultAsync(cancellationToken);

            return existingShiftId is null
                ? throw new InvalidOperationException("Ключ запроса уже использован другой операцией.")
                : new ShiftCloseResult(existingShiftId, Calculation: null, Replayed: true);
        }

        // С этого момента в сессию нельзя ни догрузить фото, ни удалить их:
        // сколько посчитано ниже, столько и будет привязано к смене.
        if (input.PhotoSessionId is { } lockedSessionId)
        {
            await photos.LockOpenSessionAsync(lockedSessionId, cancellationToken);
        }

        // Баланс сейфа читается под той же блокировкой, поэтому расхождение
        // считается от значения, которое никто не изменит до фиксации.
        var calculation = await CalculateCoreAsync(input, cancellationToken);
        if (!calculation.CanClose)
        {
            return new ShiftCloseResult(ShiftId: null, calculation, Replayed: false);
        }

        var now = time.GetUtcNow();
        var operation = ledger.Post(new OperationDraft(
            OperationType.ShiftClose,
            now,
            actor,
            ShiftPostings.Build(calculation, Chart),
            comment: calculation.RequiredMinus > 0 ? input.ShortageReason?.Trim() ?? string.Empty : string.Empty,
            idempotencyKey: idempotencyKey));

        var shift = Shift.Create(operation.Id, now, input, calculation);
        db.Shifts.Add(shift);

        // Фото сняты до отправки отчёта и привязываются той же транзакцией базы:
        // либо смена закрыта вместе с фото, либо не записано ничего.
        IReadOnlyList<Photo> shiftPhotos = input.PhotoSessionId is { } sessionId
            ? await photos.AttachToShiftAsync(sessionId, shift.Id, now, cancellationToken)
            : [];

        // Безнал смены уже на счёте «Банк», но банк зачислит его позже.
        await receipts.TrackIncomingAsync(operation, ReceiptChannelKind.Terminal, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        // Сообщения ставятся в очередь после записи операции (нужны балансы «после»),
        // но в той же транзакции базы: без смены не будет и сообщений о ней.
        await notifications.EnqueueShiftAsync(operation, shift.Id, input, calculation, shiftPhotos, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new ShiftCloseResult(shift.Id, calculation, Replayed: false);
    }

    private async Task<ShiftCalculation> CalculateCoreAsync(ShiftInput input, CancellationToken cancellationToken)
    {
        var settings = await db.ClubSettings.AsNoTracking().SingleAsync(cancellationToken);
        var expectedSafe = await ledger.GetBalanceAsync(SystemAccounts.Safe, cancellationToken);

        var ids = (input.Employees ?? []).Select(static employee => employee.EmployeeId).Distinct().ToArray();
        var employees = await db.Employees
            .AsNoTracking()
            .Where(employee => ids.Contains(employee.Id))
            .Select(employee => new { employee.Id, employee.Name, employee.HourlyRate, employee.IsActive, AccountId = employee.Account.Id })
            .ToDictionaryAsync(
                static employee => employee.Id,
                static employee => new ShiftEmployeeInfo(
                    employee.Id,
                    employee.Name,
                    employee.HourlyRate,
                    new AccountRef(employee.AccountId, AccountKind.Employee),
                    employee.IsActive),
                cancellationToken);

        var photoCount = await photos.CountPendingAsync(input.PhotoSessionId, cancellationToken);

        return ShiftCalculator.Calculate(input, settings.ToShiftRules(), expectedSafe, photoCount, employees);
    }
}
