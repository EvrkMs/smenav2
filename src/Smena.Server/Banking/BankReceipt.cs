using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Smena.Core.Banking;
using Smena.Server.Accounting;

namespace Smena.Server.Banking;

/// <summary>Идентификаторы строк справочника receipt_channels.</summary>
public enum ReceiptChannelKind : short
{
    /// <summary>Безнал через терминал (эквайринг).</summary>
    Terminal = 1,

    /// <summary>Наличные, отнесённые в банк (инкассация).</summary>
    CashDeposit = 2,
}

/// <summary>
/// Канал, которым деньги попадают в банк, и его условия. Условия правит root;
/// когда у СБП окажутся свои задержка и комиссия, появится ещё одна строка.
/// </summary>
public sealed class ReceiptChannel
{
    private ReceiptChannel()
    {
    }

    public ReceiptChannelKind Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Через сколько календарных дней банк зачисляет деньги; null — срок неизвестен.</summary>
    public int? DelayDays { get; set; }

    /// <summary>Комиссия банка в процентах; null — неизвестна.</summary>
    public decimal? FeePercent { get; set; }

    internal static ReceiptChannel Seed(ReceiptChannelKind id, string code, string name, int? delayDays, decimal? feePercent) =>
        new() { Id = id, Code = code, Name = name, DelayDays = delayDays, FeePercent = feePercent };
}

/// <summary>
/// Ожидаемое зачисление в банк. Прикреплено к транзакции, которая положила
/// деньги на счёт «Банк»; пока не подтверждено, эти деньги считаются «в пути».
/// </summary>
public sealed class BankReceipt
{
    public const int ConfirmedByLength = 100;

    private BankReceipt()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Транзакция, положившая деньги на «Банк». Сумма отправленного — в ней.</summary>
    public Guid TransactionId { get; private set; }

    public ReceiptChannelKind Channel { get; private set; }

    /// <summary>Условия канала на момент создания: их последующая смена эту запись не трогает.</summary>
    public int? DelayDays { get; private set; }

    public decimal? FeePercent { get; private set; }

    public DateTimeOffset? ExpectedAt { get; private set; }

    /// <summary>Сколько должно прийти по формуле. Только подсказка для подтверждения.</summary>
    public int? ExpectedAmount { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    /// <summary>Сколько реально пришло — вводит root.</summary>
    public int? ConfirmedAmount { get; private set; }

    public string? ConfirmedBy { get; private set; }

    /// <summary>Транзакция расхода на комиссию; null, если пришло ровно столько, сколько отправлено.</summary>
    public Guid? FeeTransactionId { get; private set; }

    public bool IsConfirmed => ConfirmedAt is not null;

    internal static BankReceipt Create(LedgerTransaction transaction, ReceiptChannel channel, DateTimeOffset sentAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TransactionId = transaction.Id,
        Channel = channel.Id,
        DelayDays = channel.DelayDays,
        FeePercent = channel.FeePercent,
        ExpectedAt = BankReceiptMath.ExpectedAt(sentAt, channel.DelayDays),
        ExpectedAmount = BankReceiptMath.ExpectedAmount(transaction.Amount, channel.FeePercent),
    };

    internal void Confirm(DateTimeOffset at, string by, int amount, Guid? feeTransactionId)
    {
        ConfirmedAt = at.ToUniversalTime();
        ConfirmedBy = by;
        ConfirmedAmount = amount;
        FeeTransactionId = feeTransactionId;
    }

    /// <summary>Отмена подтверждения: деньги снова считаются «в пути».</summary>
    internal void Unconfirm()
    {
        ConfirmedAt = null;
        ConfirmedBy = null;
        ConfirmedAmount = null;
        FeeTransactionId = null;
    }
}

internal sealed class ReceiptChannelConfiguration : IEntityTypeConfiguration<ReceiptChannel>
{
    public void Configure(EntityTypeBuilder<ReceiptChannel> builder)
    {
        builder.ToTable("receipt_channels", static table =>
        {
            table.HasCheckConstraint("ck_receipt_channels_delay", "delay_days IS NULL OR delay_days >= 0");
            table.HasCheckConstraint("ck_receipt_channels_fee", "fee_percent IS NULL OR fee_percent BETWEEN 0 AND 100");
        });

        builder.HasKey(static channel => channel.Id);
        builder.Property(static channel => channel.Id).ValueGeneratedNever();
        builder.Property(static channel => channel.Code).HasMaxLength(32);
        builder.Property(static channel => channel.Name).HasMaxLength(64);
        builder.Property(static channel => channel.FeePercent).HasPrecision(5, 2);
        builder.HasIndex(static channel => channel.Code).IsUnique();

        // Настоящие задержка и комиссия терминала пока неизвестны: срок — следующий
        // день, комиссия 0 %. Root поправит их, когда станут известны. У инкассации
        // условий нет: она сразу ждёт подтверждения.
        builder.HasData(
            ReceiptChannel.Seed(ReceiptChannelKind.Terminal, "terminal", "Терминал", delayDays: 1, feePercent: 0m),
            ReceiptChannel.Seed(ReceiptChannelKind.CashDeposit, "cash_deposit", "Инкассация", delayDays: null, feePercent: null));
    }
}

internal sealed class BankReceiptConfiguration : IEntityTypeConfiguration<BankReceipt>
{
    public void Configure(EntityTypeBuilder<BankReceipt> builder)
    {
        builder.ToTable("bank_receipts", static table =>
        {
            table.HasCheckConstraint(
                "ck_bank_receipts_confirmation",
                "(confirmed_at IS NULL) = (confirmed_amount IS NULL) AND (confirmed_at IS NULL) = (confirmed_by IS NULL) " +
                "AND (confirmed_at IS NOT NULL OR fee_transaction_id IS NULL)");
            table.HasCheckConstraint("ck_bank_receipts_confirmed_amount", "confirmed_amount IS NULL OR confirmed_amount >= 0");
        });

        builder.HasKey(static receipt => receipt.Id);
        builder.Property(static receipt => receipt.Id).ValueGeneratedNever();
        builder.Property(static receipt => receipt.Channel).HasColumnName("channel_id");
        builder.Property(static receipt => receipt.FeePercent).HasPrecision(5, 2);
        builder.Property(static receipt => receipt.ConfirmedBy).HasMaxLength(BankReceipt.ConfirmedByLength);
        builder.Ignore(static receipt => receipt.IsConfirmed);

        builder.HasIndex(static receipt => receipt.TransactionId).IsUnique();
        builder.HasIndex(static receipt => receipt.ConfirmedAt);

        builder.HasOne<ReceiptChannel>()
            .WithMany()
            .HasForeignKey(static receipt => receipt.Channel)
            .OnDelete(DeleteBehavior.Restrict);

        // Удалили транзакцию (или её операцию) — исчезло и ожидание по ней.
        builder.HasOne<LedgerTransaction>()
            .WithOne()
            .HasForeignKey<BankReceipt>(static receipt => receipt.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<LedgerTransaction>()
            .WithMany()
            .HasForeignKey(static receipt => receipt.FeeTransactionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
