using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Smena.Core.Accounting;

namespace Smena.Server.Accounting;

/// <summary>
/// Строка представления <c>account_movements</c>: проводка вместе с суммой, датой,
/// комментарием и признаком отмены. Балансы, отчёты и ручные запросы читают его,
/// а не соединяют три таблицы каждый раз.
/// </summary>
public sealed class AccountMovement
{
    public long EntryId { get; private set; }

    public int AccountId { get; private set; }

    public AccountKind AccountKind { get; private set; }

    public Direction Direction { get; private set; }

    public int Amount { get; private set; }

    /// <summary>Сумма со знаком направления: то, на сколько изменился баланс счёта.</summary>
    public int SignedAmount { get; private set; }

    public int? CategoryId { get; private set; }

    public Guid TransactionId { get; private set; }

    public string? Note { get; private set; }

    public Guid OperationId { get; private set; }

    public OperationType OperationType { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string Comment { get; private set; } = string.Empty;

    public bool IsCancelled { get; private set; }
}

internal sealed class AccountMovementConfiguration : IEntityTypeConfiguration<AccountMovement>
{
    public void Configure(EntityTypeBuilder<AccountMovement> builder)
    {
        // Представление создаётся SQL в миграции LedgerObjects; EF его только читает.
        builder.HasNoKey();
        builder.ToView("account_movements");
    }
}
