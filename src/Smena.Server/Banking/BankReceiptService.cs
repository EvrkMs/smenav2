using Microsoft.EntityFrameworkCore;
using Smena.Core.Accounting;
using Smena.Core.Banking;
using Smena.Server.Accounting;
using Smena.Server.Database;

namespace Smena.Server.Banking;

/// <summary>
/// Банк двумя числами. Простая сумма проводок счёта включает деньги, которые
/// банк ещё не зачислил, поэтому остаток всегда читается отсюда.
/// </summary>
/// <param name="Total">Баланс счёта по проводкам.</param>
/// <param name="InTransit">Отправлено, но ещё не подтверждено.</param>
public readonly record struct BankBalance(int Total, int InTransit)
{
    /// <summary>То, что должно совпадать с выпиской.</summary>
    public int Confirmed => Total - InTransit;
}

/// <summary>Итог подтверждения зачисления: комиссия либо причина отказа.</summary>
public readonly record struct BankReceiptConfirmation(int Fee, string? Error)
{
    public bool IsConfirmed => Error is null;

    public static BankReceiptConfirmation Rejected(string error) => new(0, error);
}

public sealed class BankReceiptService(SmenaDbContext db, Ledger ledger, TimeProvider time)
{
    /// <summary>
    /// Создаёт ожидаемое зачисление для каждой транзакции операции, которая
    /// кладёт деньги на «Банк». Вызывается до SaveChanges вызывающего: запись
    /// попадает в базу вместе с самой операцией.
    /// </summary>
    public async Task TrackIncomingAsync(
        Operation operation,
        ReceiptChannelKind channelKind,
        CancellationToken cancellationToken)
    {
        ReceiptChannel? channel = null;

        foreach (var transaction in operation.Transactions)
        {
            if (!PutsMoneyIntoBank(transaction))
            {
                continue;
            }

            channel ??= await db.ReceiptChannels.AsNoTracking().SingleAsync(c => c.Id == channelKind, cancellationToken);
            db.BankReceipts.Add(BankReceipt.Create(transaction, channel, operation.OccurredAt));
        }
    }

    public async Task<BankBalance> GetBalanceAsync(CancellationToken cancellationToken)
    {
        var total = await ledger.GetBalanceAsync(SystemAccounts.Bank, cancellationToken);

        var inTransit = await (
            from receipt in db.BankReceipts
            where receipt.ConfirmedAt == null
            join transaction in db.Transactions on receipt.TransactionId equals transaction.Id
            join operation in db.Operations on transaction.OperationId equals operation.Id
            where operation.CancelledAt == null
            select transaction.Amount).SumAsync(cancellationToken);

        return new BankBalance(total, inTransit);
    }

    /// <summary>
    /// Root подтверждает, что деньги пришли, и вводит фактическую сумму. Разница
    /// с отправленным проводится одной транзакцией расхода на комиссию; повторной
    /// транзакции «зачисление» нет.
    /// </summary>
    public async Task<BankReceiptConfirmation> ConfirmAsync(
        Guid receiptId,
        int confirmedAmount,
        Actor actor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await ledger.BeginAsync(cancellationToken);

        var receipt = await db.BankReceipts.FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);
        if (receipt is null)
        {
            return BankReceiptConfirmation.Rejected("Зачисление не найдено.");
        }

        if (receipt.IsConfirmed)
        {
            return BankReceiptConfirmation.Rejected("Зачисление уже подтверждено.");
        }

        var sent = await (
            from ledgerTransaction in db.Transactions
            where ledgerTransaction.Id == receipt.TransactionId
            join operation in db.Operations on ledgerTransaction.OperationId equals operation.Id
            select new { ledgerTransaction.Amount, ledgerTransaction.Note, IsCancelled = operation.CancelledAt != null })
            .SingleAsync(cancellationToken);

        if (sent.IsCancelled)
        {
            return BankReceiptConfirmation.Rejected("Исходная операция отменена.");
        }

        if (BankReceiptMath.CheckConfirmedAmount(sent.Amount, confirmedAmount) is { } error)
        {
            return BankReceiptConfirmation.Rejected(error);
        }

        var now = time.GetUtcNow();
        var fee = sent.Amount - confirmedAmount;
        Guid? feeTransactionId = null;

        if (fee > 0)
        {
            var feeOperation = ledger.Post(new OperationDraft(
                OperationType.BankReceipt,
                now,
                actor,
                [
                    TransactionDraft.Create(
                        fee,
                        Leg.Decrease(SystemAccounts.Bank, SystemCategories.BankFee),
                        Leg.Increase(SystemAccounts.Expense, SystemCategories.ExpenseBankFee),
                        $"Комиссия: {sent.Note}"),
                ]));
            feeTransactionId = feeOperation.Transactions[0].Id;
        }

        receipt.Confirm(now, actor.Name, confirmedAmount, feeTransactionId);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new BankReceiptConfirmation(fee, Error: null);
    }

    private static bool PutsMoneyIntoBank(LedgerTransaction transaction)
    {
        foreach (var entry in transaction.Entries)
        {
            if (entry.AccountId == SystemAccounts.Bank.Id && entry.Direction == Direction.Increase)
            {
                return true;
            }
        }

        return false;
    }
}
