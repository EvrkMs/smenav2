using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Smena.Core.Accounting;

namespace Smena.Server.Accounting;

/// <summary>Операция: что произошло с точки зрения человека. Состоит из транзакций.</summary>
public sealed class Operation
{
    public const int ActorNameLength = 100;
    public const int CancelReasonLength = 500;

    private readonly List<LedgerTransaction> _transactions = [];

    private Operation()
    {
    }

    public Guid Id { get; private set; }

    public OperationType Type { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string Comment { get; private set; } = string.Empty;

    public ActorKind ActorKind { get; private set; }

    public string ActorName { get; private set; } = string.Empty;

    public Guid? IdempotencyKey { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? CancelledBy { get; private set; }

    public string? CancelReason { get; private set; }

    public bool IsCancelled => CancelledAt is not null;

    public IReadOnlyList<LedgerTransaction> Transactions => _transactions;

    internal static Operation FromDraft(OperationDraft draft)
    {
        var operation = new Operation
        {
            // UUID v7 начинается с времени: новые строки ложатся в конец индекса.
            Id = Guid.CreateVersion7(),
            Type = draft.Type,
            OccurredAt = draft.OccurredAt,
            Comment = draft.Comment,
            ActorKind = draft.Actor.Kind,
            ActorName = draft.Actor.Name,
            IdempotencyKey = draft.IdempotencyKey,
        };

        var transactions = draft.Transactions;
        operation._transactions.EnsureCapacity(transactions.Count);
        for (var position = 0; position < transactions.Count; position++)
        {
            operation._transactions.Add(LedgerTransaction.FromDraft(transactions[position], (short)position));
        }

        return operation;
    }

    internal void Cancel(DateTimeOffset at, string by, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (IsCancelled)
        {
            throw new InvalidOperationException($"Операция {Id} уже отменена.");
        }

        CancelledAt = at.ToUniversalTime();
        CancelledBy = by;
        CancelReason = reason;
    }
}

/// <summary>Транзакция: одно движение одной суммы между двумя счетами.</summary>
public sealed class LedgerTransaction
{
    public const int NoteLength = 200;

    private readonly List<LedgerEntry> _entries = new(capacity: 2);

    private LedgerTransaction()
    {
    }

    public Guid Id { get; private set; }

    public Guid OperationId { get; private set; }

    /// <summary>Порядок внутри операции — в каком виде её показывать.</summary>
    public short Position { get; private set; }

    /// <summary>Сумма в рублях, всегда больше нуля. Хранится только здесь.</summary>
    public int Amount { get; private set; }

    public string? Note { get; private set; }

    public IReadOnlyList<LedgerEntry> Entries => _entries;

    internal static LedgerTransaction FromDraft(TransactionDraft draft, short position)
    {
        var transaction = new LedgerTransaction
        {
            Id = Guid.CreateVersion7(),
            Position = position,
            Amount = draft.Amount,
            Note = draft.Note,
        };

        transaction._entries.Add(LedgerEntry.FromLeg(draft.First));
        transaction._entries.Add(LedgerEntry.FromLeg(draft.Second));
        return transaction;
    }
}

/// <summary>
/// Проводка: запись в счёте. Суммы, даты и комментария здесь нет — они берутся
/// из транзакции и операции.
/// </summary>
public sealed class LedgerEntry
{
    private LedgerEntry()
    {
    }

    public long Id { get; private set; }

    public Guid TransactionId { get; private set; }

    public int AccountId { get; private set; }

    /// <summary>Вырос или уменьшился баланс этого счёта.</summary>
    public Direction Direction { get; private set; }

    public int? CategoryId { get; private set; }

    internal static LedgerEntry FromLeg(in Leg leg) => new()
    {
        AccountId = leg.Account.Id,
        Direction = leg.Direction,
        CategoryId = leg.Category?.Id,
    };
}

internal sealed class OperationConfiguration : IEntityTypeConfiguration<Operation>
{
    public void Configure(EntityTypeBuilder<Operation> builder)
    {
        builder.ToTable("operations", static table =>
            table.HasCheckConstraint(
                "ck_operations_cancel",
                "(cancelled_at IS NULL) = (cancelled_by IS NULL) AND (cancelled_at IS NULL) = (cancel_reason IS NULL)"));

        builder.HasKey(static operation => operation.Id);
        builder.Property(static operation => operation.Id).ValueGeneratedNever();
        builder.Property(static operation => operation.Type).HasColumnName("type_id");
        builder.Property(static operation => operation.ActorKind).HasColumnName("actor_kind_id");
        builder.Property(static operation => operation.Comment).HasMaxLength(OperationDraft.MaxCommentLength);
        builder.Property(static operation => operation.ActorName).HasMaxLength(Operation.ActorNameLength);
        builder.Property(static operation => operation.CancelledBy).HasMaxLength(Operation.ActorNameLength);
        builder.Property(static operation => operation.CancelReason).HasMaxLength(Operation.CancelReasonLength);
        builder.Ignore(static operation => operation.IsCancelled);

        builder.HasIndex(static operation => operation.IdempotencyKey).IsUnique();
        builder.HasIndex(static operation => operation.OccurredAt);

        builder.HasOne<OperationTypeRow>()
            .WithMany()
            .HasForeignKey(static operation => operation.Type)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ActorKindRow>()
            .WithMany()
            .HasForeignKey(static operation => operation.ActorKind)
            .OnDelete(DeleteBehavior.Restrict);

        // Каскад сверху вниз: удалили операцию руками — исчезли её транзакции и проводки.
        builder.HasMany(static operation => operation.Transactions)
            .WithOne()
            .HasForeignKey(static transaction => transaction.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LedgerTransactionConfiguration : IEntityTypeConfiguration<LedgerTransaction>
{
    public void Configure(EntityTypeBuilder<LedgerTransaction> builder)
    {
        builder.ToTable("transactions", static table =>
            table.HasCheckConstraint("ck_transactions_amount", "amount > 0"));

        builder.HasKey(static transaction => transaction.Id);
        builder.Property(static transaction => transaction.Id).ValueGeneratedNever();
        builder.Property(static transaction => transaction.Note).HasMaxLength(LedgerTransaction.NoteLength);

        builder.HasIndex(static transaction => new { transaction.OperationId, transaction.Position }).IsUnique();

        builder.HasMany(static transaction => transaction.Entries)
            .WithOne()
            .HasForeignKey(static entry => entry.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("entries", static table =>
            table.HasCheckConstraint("ck_entries_direction", "direction IN (-1, 1)"));

        builder.HasKey(static entry => entry.Id);

        // Две стороны транзакции — всегда разные счета.
        builder.HasIndex(static entry => new { entry.TransactionId, entry.AccountId }).IsUnique();
        builder.HasIndex(static entry => entry.AccountId);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(static entry => entry.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(static entry => entry.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
