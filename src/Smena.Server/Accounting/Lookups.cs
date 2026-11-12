using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Smena.Core.Accounting;

namespace Smena.Server.Accounting;

// Справочники: строки создаются миграцией и соответствуют перечислениям ядра один к одному.
// База ссылается на них внешними ключами, поэтому не примет несуществующий вид,
// а в SQL рядом с числом видно название. Из панели они не меняются.

public sealed class AccountKindRow
{
    public AccountKind Id { get; init; }

    public required string Code { get; init; }

    public required string Name { get; init; }

    public AccountSide Side { get; init; }
}

public sealed class CategoryKindRow
{
    public CategoryKind Id { get; init; }

    public required string Code { get; init; }

    public required string Name { get; init; }

    /// <summary>Вид счёта, к проводкам которого применимы категории этого справочника.</summary>
    public AccountKind AccountKind { get; init; }

    /// <summary>Направление, обязательное для проводки с такой категорией; null — любое.</summary>
    public Direction? Direction { get; init; }
}

public sealed class OperationTypeRow
{
    public OperationType Id { get; init; }

    public required string Code { get; init; }

    public required string Name { get; init; }
}

public sealed class ActorKindRow
{
    public ActorKind Id { get; init; }

    public required string Code { get; init; }

    public required string Name { get; init; }
}

internal static class LookupColumns
{
    public const int CodeLength = 32;
    public const int NameLength = 64;
}

internal sealed class AccountKindRowConfiguration : IEntityTypeConfiguration<AccountKindRow>
{
    public void Configure(EntityTypeBuilder<AccountKindRow> builder)
    {
        builder.ToTable("account_kinds", static table =>
            table.HasCheckConstraint("ck_account_kinds_side", "side IN (1, 2)"));

        builder.HasKey(static row => row.Id);
        builder.Property(static row => row.Id).ValueGeneratedNever();
        builder.Property(static row => row.Code).HasMaxLength(LookupColumns.CodeLength);
        builder.Property(static row => row.Name).HasMaxLength(LookupColumns.NameLength);
        builder.HasIndex(static row => row.Code).IsUnique();

        builder.HasData(
            Row(AccountKind.Safe, "safe", "Сейф"),
            Row(AccountKind.Bank, "bank", "Банк"),
            Row(AccountKind.Expense, "expense", "Расходы"),
            Row(AccountKind.Employee, "employee", "Сотрудник"),
            Row(AccountKind.Revenue, "revenue", "Выручка"),
            Row(AccountKind.Opening, "opening", "Стартовые остатки"));
    }

    // Сторона берётся из ядра, а не пишется второй раз: правило баланса в коде
    // и в триггере базы не могут разойтись.
    private static AccountKindRow Row(AccountKind kind, string code, string name) =>
        new() { Id = kind, Code = code, Name = name, Side = kind.Side() };
}

internal sealed class CategoryKindRowConfiguration : IEntityTypeConfiguration<CategoryKindRow>
{
    public void Configure(EntityTypeBuilder<CategoryKindRow> builder)
    {
        builder.ToTable("category_kinds", static table =>
            table.HasCheckConstraint("ck_category_kinds_direction", "direction IS NULL OR direction IN (-1, 1)"));

        builder.HasKey(static row => row.Id);
        builder.Property(static row => row.Id).ValueGeneratedNever();
        builder.Property(static row => row.Code).HasMaxLength(LookupColumns.CodeLength);
        builder.Property(static row => row.Name).HasMaxLength(LookupColumns.NameLength);
        builder.Property(static row => row.AccountKind).HasColumnName("account_kind_id");
        builder.HasIndex(static row => row.Code).IsUnique();

        builder.HasOne<AccountKindRow>()
            .WithMany()
            .HasForeignKey(static row => row.AccountKind)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasData(
            Row(CategoryKind.SafeIncome, "safe_income", "Приходы сейфа"),
            Row(CategoryKind.SafeExpense, "safe_expense", "Расходы сейфа"),
            Row(CategoryKind.BankIncome, "bank_income", "Приходы банка"),
            Row(CategoryKind.BankExpense, "bank_expense", "Расходы банка"),
            Row(CategoryKind.Revenue, "revenue", "Выручка"),
            Row(CategoryKind.Expense, "expense", "Расходы"),
            Row(CategoryKind.Employee, "employee", "Операции с сотрудником"));
    }

    private static CategoryKindRow Row(CategoryKind kind, string code, string name) => new()
    {
        Id = kind,
        Code = code,
        Name = name,
        AccountKind = kind.Account(),
        Direction = kind.RequiredDirection(),
    };
}

internal sealed class OperationTypeRowConfiguration : IEntityTypeConfiguration<OperationTypeRow>
{
    public void Configure(EntityTypeBuilder<OperationTypeRow> builder)
    {
        builder.ToTable("operation_types");

        builder.HasKey(static row => row.Id);
        builder.Property(static row => row.Id).ValueGeneratedNever();
        builder.Property(static row => row.Code).HasMaxLength(LookupColumns.CodeLength);
        builder.Property(static row => row.Name).HasMaxLength(LookupColumns.NameLength);
        builder.HasIndex(static row => row.Code).IsUnique();

        builder.HasData(
            Row(OperationType.ShiftClose, "shift_close", "Закрытие смены"),
            Row(OperationType.Payout, "payout", "Выплата сотруднику"),
            Row(OperationType.SafeIncome, "safe_income", "Приход в сейф"),
            Row(OperationType.SafeExpense, "safe_expense", "Расход из сейфа"),
            Row(OperationType.BankIncome, "bank_income", "Приход в банк"),
            Row(OperationType.BankExpense, "bank_expense", "Расход с банка"),
            Row(OperationType.BankReceipt, "bank_receipt", "Подтверждение зачисления"),
            Row(OperationType.Inventory, "inventory", "Инвентаризация"),
            Row(OperationType.Fine, "fine", "Штраф"),
            Row(OperationType.Bonus, "bonus", "Премия"),
            Row(OperationType.Opening, "opening", "Стартовые остатки"),
            Row(OperationType.BankAdjustment, "bank_adjustment", "Корректировка банка"));
    }

    private static OperationTypeRow Row(OperationType type, string code, string name) =>
        new() { Id = type, Code = code, Name = name };
}

internal sealed class ActorKindRowConfiguration : IEntityTypeConfiguration<ActorKindRow>
{
    public void Configure(EntityTypeBuilder<ActorKindRow> builder)
    {
        builder.ToTable("actor_kinds");

        builder.HasKey(static row => row.Id);
        builder.Property(static row => row.Id).ValueGeneratedNever();
        builder.Property(static row => row.Code).HasMaxLength(LookupColumns.CodeLength);
        builder.Property(static row => row.Name).HasMaxLength(LookupColumns.NameLength);
        builder.HasIndex(static row => row.Code).IsUnique();

        builder.HasData(
            new ActorKindRow { Id = ActorKind.Cashier, Code = "cashier", Name = "Администратор смены" },
            new ActorKindRow { Id = ActorKind.Root, Code = "root", Name = "Root" },
            new ActorKindRow { Id = ActorKind.System, Code = "system", Name = "Система" });
    }
}
