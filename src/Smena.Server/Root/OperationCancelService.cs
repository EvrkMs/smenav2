using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Smena.Core.Accounting;
using Smena.Server.Accounting;
using Smena.Server.Database;
using Smena.Server.Notifications;

namespace Smena.Server.Root;

/// <summary>
/// Отмена операций и подтверждений. Только root. Отмена — флаг на всей операции:
/// её транзакции перестают учитываться в балансах, но остаются в истории.
/// </summary>
public sealed class OperationCancelService(SmenaDbContext db, Ledger ledger, TimeProvider time)
{
    public const int MaxReasonLength = Operation.CancelReasonLength;

    /// <returns>Текст отказа либо null, если операция отменена.</returns>
    public async Task<string?> CancelAsync(Guid operationId, string? reason, Actor actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "Укажите причину отмены.";
        }

        if (reason.Length > MaxReasonLength)
        {
            return $"Причина длиннее {MaxReasonLength} символов.";
        }

        await using var transaction = await ledger.BeginAsync(cancellationToken);

        var operation = await db.Operations.FirstOrDefaultAsync(o => o.Id == operationId, cancellationToken);
        if (operation is null)
        {
            return "Операция не найдена.";
        }

        if (operation.IsCancelled)
        {
            return "Операция уже отменена.";
        }

        if (operation.Type == OperationType.BankReceipt)
        {
            // Комиссия — часть подтверждения зачисления; отдельно от него она не отменяется.
            return "Это комиссия подтверждённого зачисления. Отмените подтверждение зачисления.";
        }

        if (operation.Type == OperationType.ShiftClose && await IsNotLastShiftAsync(operationId, cancellationToken))
        {
            // Расхождение сейфа каждой смены считается от баланса на её момент.
            // Отмена смены из середины сделала бы неверными все следующие.
            return "Отменить можно только последнюю смену. Более ранние исправляются корректирующей операцией.";
        }

        var hasConfirmedReceipt = await (
            from receipt in db.BankReceipts
            where receipt.ConfirmedAt != null
            join ledgerTransaction in db.Transactions on receipt.TransactionId equals ledgerTransaction.Id
            where ledgerTransaction.OperationId == operationId
            select receipt.Id).AnyAsync(cancellationToken);

        if (hasConfirmedReceipt)
        {
            return "По этой операции уже подтверждено зачисление в банк. Сначала отмените подтверждение.";
        }

        var now = time.GetUtcNow();
        await ledger.CancelAsync(operationId, actor.Name, reason.Trim(), now, cancellationToken);
        await EnqueueCancelNoticeAsync(operation, reason.Trim(), actor, now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    /// <summary>
    /// Отмена подтверждения зачисления: деньги снова «в пути», комиссия (если была)
    /// отменяется вместе с подтверждением.
    /// </summary>
    /// <returns>Текст отказа либо null.</returns>
    public async Task<string?> UnconfirmReceiptAsync(Guid receiptId, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await ledger.BeginAsync(cancellationToken);

        var receipt = await db.BankReceipts.FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);
        if (receipt is null)
        {
            return "Зачисление не найдено.";
        }

        if (!receipt.IsConfirmed)
        {
            return "Зачисление не подтверждено.";
        }

        if (receipt.FeeTransactionId is { } feeTransactionId)
        {
            var feeOperationId = await db.Transactions
                .Where(t => t.Id == feeTransactionId)
                .Select(t => t.OperationId)
                .SingleAsync(cancellationToken);

            await ledger.CancelAsync(feeOperationId, actor.Name, "Отмена подтверждения зачисления", time.GetUtcNow(), cancellationToken);
        }

        receipt.Unconfirm();

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    private async Task<bool> IsNotLastShiftAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var lastActiveShiftOperation = await (
            from shift in db.Shifts
            join operation in db.Operations on shift.OperationId equals operation.Id
            where operation.CancelledAt == null
            orderby shift.ClosedAt descending
            select (Guid?)operation.Id).FirstOrDefaultAsync(cancellationToken);

        return lastActiveShiftOperation != operationId;
    }

    /// <summary>Администраторы видели исходное сообщение — должны увидеть и отмену.</summary>
    private async Task EnqueueCancelNoticeAsync(
        Operation operation,
        string reason,
        Actor actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var settings = await db.ClubSettings.AsNoTracking().SingleAsync(cancellationToken);
        if (settings.ReportsRoute is not { } route)
        {
            return;
        }

        var typeName = await db.Set<OperationTypeRow>()
            .Where(row => row.Id == operation.Type)
            .Select(row => row.Name)
            .SingleAsync(cancellationToken);

        var occurredAt = TimeZoneInfo.ConvertTime(operation.OccurredAt, settings.GetTimeZone())
            .ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

        var text = $"ОТМЕНЕНО: {typeName} от {occurredAt}\nПричина: {reason}\nОтменил: {actor.Name}";
        db.OutboxMessages.Add(OutboxMessage.Create(route, text, operation.Id, now));
    }
}
