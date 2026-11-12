using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Smena.Core.Accounting;

namespace Smena.Server.Accounting;

/// <summary>
/// Категория проводки. У каждого вида счёта свой справочник (<see cref="CategoryKind"/>):
/// категории выручки и расходов строят P&amp;L, категории сейфа и банка — ДДС.
/// </summary>
public sealed class Category
{
    public const int NameLength = 100;
    public const int CodeLength = 50;

    private Category()
    {
    }

    public int Id { get; private set; }

    public CategoryKind Kind { get; private set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Заполнен у системных категорий, на которые ссылается код. Такую категорию
    /// нельзя архивировать, а её смысл нельзя менять.
    /// </summary>
    public string? Code { get; private set; }

    /// <summary>
    /// Второй счёт проводки для категорий сейфа и банка: админ выбирает только
    /// категорию, а транзакция получается сбалансированной сама.
    /// </summary>
    public int? CounterAccountId { get; set; }

    /// <summary>Категория на втором счёте, например «Хозяйственные» в расходах.</summary>
    public int? CounterCategoryId { get; set; }

    /// <summary>Вместо удаления: на категорию ссылаются проводки.</summary>
    public bool IsArchived { get; set; }

    public bool IsSystem => Code is not null;

    public CategoryRef Ref => new(Id, Kind);

    /// <summary>Категория, которую root заводит в панели.</summary>
    public static Category Create(CategoryKind kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Category { Kind = kind, Name = name };
    }

    /// <param name="category">Номер и справочник категории.</param>
    /// <param name="code">Код, по которому категорию узнаёт код.</param>
    /// <param name="name">Название для людей.</param>
    /// <param name="counterAccount">Второй счёт — для категорий сейфа, которые выбирает администратор.</param>
    /// <param name="counterCategory">Категория на втором счёте.</param>
    internal static Category System(
        CategoryRef category,
        string code,
        string name,
        AccountRef? counterAccount = null,
        CategoryRef? counterCategory = null) => new()
        {
            Id = category.Id,
            Kind = category.Kind,
            Code = code,
            Name = name,
            CounterAccountId = counterAccount?.Id,
            CounterCategoryId = counterCategory?.Id,
        };
}

/// <summary>
/// Системные категории: на них ссылается код, поэтому они создаются миграцией
/// с фиксированными номерами, как и системные счета.
/// </summary>
public static class SystemCategories
{
    /// <summary>С этого номера база выдаёт идентификаторы категориям, созданным в панели.</summary>
    internal const int FirstUserCategoryId = 1000;

    public static CategoryRef RevenueShift { get; } = new(1, CategoryKind.Revenue);

    public static CategoryRef RevenueShortageWithheld { get; } = new(2, CategoryKind.Revenue);

    public static CategoryRef RevenueSafeSurplus { get; } = new(3, CategoryKind.Revenue);

    public static CategoryRef ExpensePayroll { get; } = new(4, CategoryKind.Expense);

    public static CategoryRef SafeShiftCash { get; } = new(5, CategoryKind.SafeIncome);

    public static CategoryRef SafeSurplus { get; } = new(6, CategoryKind.SafeIncome);

    public static CategoryRef SafeShortage { get; } = new(7, CategoryKind.SafeExpense);

    public static CategoryRef SafeFloatRefill { get; } = new(8, CategoryKind.SafeExpense);

    public static CategoryRef BankAcquiring { get; } = new(9, CategoryKind.BankIncome);

    public static CategoryRef EmployeeAccrual { get; } = new(10, CategoryKind.Employee);

    public static CategoryRef EmployeeShortage { get; } = new(11, CategoryKind.Employee);

    public static CategoryRef EmployeeAdvance { get; } = new(12, CategoryKind.Employee);

    public static CategoryRef EmployeeSalary { get; } = new(13, CategoryKind.Employee);

    public static CategoryRef SafePayout { get; } = new(14, CategoryKind.SafeExpense);

    public static CategoryRef ExpenseBankFee { get; } = new(15, CategoryKind.Expense);

    public static CategoryRef BankFee { get; } = new(16, CategoryKind.BankExpense);

    public static CategoryRef BankIncassation { get; } = new(17, CategoryKind.BankIncome);

    public static CategoryRef SafeIncassation { get; } = new(18, CategoryKind.SafeExpense);

    public static CategoryRef BankCashWithdrawal { get; } = new(19, CategoryKind.BankExpense);

    public static CategoryRef SafeFromBank { get; } = new(20, CategoryKind.SafeIncome);

    public static CategoryRef ExpenseHousehold { get; } = new(21, CategoryKind.Expense);

    public static CategoryRef SafeHousehold { get; } = new(22, CategoryKind.SafeExpense);

    public static CategoryRef RevenueOther { get; } = new(23, CategoryKind.Revenue);

    public static CategoryRef SafeOtherIncome { get; } = new(24, CategoryKind.SafeIncome);

    public static CategoryRef EmployeeFine { get; } = new(25, CategoryKind.Employee);

    public static CategoryRef RevenueFines { get; } = new(26, CategoryKind.Revenue);

    public static CategoryRef EmployeeBonus { get; } = new(27, CategoryKind.Employee);

    public static CategoryRef ExpenseBonus { get; } = new(28, CategoryKind.Expense);

    public static CategoryRef EmployeeInventory { get; } = new(29, CategoryKind.Employee);

    public static CategoryRef RevenueInventory { get; } = new(30, CategoryKind.Revenue);

    public static CategoryRef BankPayout { get; } = new(31, CategoryKind.BankExpense);

    public static CategoryRef SafeOpening { get; } = new(32, CategoryKind.SafeIncome);

    public static CategoryRef BankOpening { get; } = new(33, CategoryKind.BankIncome);

    public static CategoryRef EmployeeOpening { get; } = new(34, CategoryKind.Employee);

    internal static Category[] Seed() =>
    [
        Category.System(RevenueShift, "revenue.shift", "Выручка смены"),
        Category.System(RevenueShortageWithheld, "revenue.shortage_withheld", "Удержание за недостачу"),
        Category.System(RevenueSafeSurplus, "revenue.safe_surplus", "Излишек сейфа"),
        Category.System(ExpensePayroll, "expense.payroll", "ФОТ"),
        Category.System(SafeShiftCash, "safe.shift_cash", "Выручка смены"),
        Category.System(SafeSurplus, "safe.surplus", "Излишек при пересчёте"),
        Category.System(SafeShortage, "safe.shortage", "Недостача при пересчёте"),
        Category.System(SafeFloatRefill, "safe.float_refill", "Пополнение размена кассы"),
        Category.System(BankAcquiring, "bank.acquiring", "Эквайринг"),
        Category.System(EmployeeAccrual, "employee.accrual", "Начисление за смену"),
        Category.System(EmployeeShortage, "employee.shortage", "Удержание за недостачу"),

        // Выплаты сотрудникам.
        Category.System(EmployeeAdvance, "employee.advance", "Аванс"),
        Category.System(EmployeeSalary, "employee.salary", "Выплата ЗП"),
        Category.System(SafePayout, "safe.payout", "Выплата сотруднику"),

        // Комиссия банка при подтверждении зачисления.
        Category.System(ExpenseBankFee, "expense.bank_fee", "Комиссия банка"),
        Category.System(BankFee, "bank.fee", "Комиссия банка"),

        // Категории сейфа, которые выбирает администратор: у них задан второй счёт.
        Category.System(BankIncassation, "bank.incassation", "Инкассация"),
        Category.System(SafeIncassation, "safe.incassation", "Инкассация", SystemAccounts.Bank, BankIncassation),
        Category.System(BankCashWithdrawal, "bank.cash_withdrawal", "Снятие наличных"),
        Category.System(SafeFromBank, "safe.from_bank", "Снято с банка", SystemAccounts.Bank, BankCashWithdrawal),
        Category.System(ExpenseHousehold, "expense.household", "Хозяйственные"),
        Category.System(SafeHousehold, "safe.household", "Хозяйственные", SystemAccounts.Expense, ExpenseHousehold),
        Category.System(RevenueOther, "revenue.other", "Прочие поступления"),
        Category.System(SafeOtherIncome, "safe.other_income", "Прочий приход", SystemAccounts.Revenue, RevenueOther),

        // Операции root: штраф, премия, инвентаризация, выплата с банка, стартовые остатки.
        Category.System(EmployeeFine, "employee.fine", "Штраф"),
        Category.System(RevenueFines, "revenue.fines", "Штрафы"),
        Category.System(EmployeeBonus, "employee.bonus", "Премия"),
        Category.System(ExpenseBonus, "expense.bonus", "Премии"),
        Category.System(EmployeeInventory, "employee.inventory", "Инвентаризация"),
        Category.System(RevenueInventory, "revenue.inventory", "Удержание за инвентаризацию"),
        Category.System(BankPayout, "bank.payout", "Выплата сотруднику"),
        Category.System(SafeOpening, "safe.opening", "Стартовый остаток"),
        Category.System(BankOpening, "bank.opening", "Стартовый остаток"),
        Category.System(EmployeeOpening, "employee.opening", "Стартовый остаток"),
    ];
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories", static table =>
            table.HasCheckConstraint("ck_categories_system_not_archived", "code IS NULL OR NOT is_archived"));

        builder.HasKey(static category => category.Id);
        builder.Property(static category => category.Id)
            .UseIdentityByDefaultColumn()
            .HasIdentityOptions(startValue: SystemCategories.FirstUserCategoryId);
        builder.Property(static category => category.Kind).HasColumnName("kind_id");
        builder.Property(static category => category.Name).HasMaxLength(Category.NameLength);
        builder.Property(static category => category.Code).HasMaxLength(Category.CodeLength);
        builder.Ignore(static category => category.IsSystem);
        builder.Ignore(static category => category.Ref);

        builder.HasIndex(static category => category.Code).IsUnique();
        builder.HasIndex(static category => new { category.Kind, category.Name }).IsUnique();

        builder.HasOne<CategoryKindRow>()
            .WithMany()
            .HasForeignKey(static category => category.Kind)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(static category => category.CounterAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(static category => category.CounterCategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasData(SystemCategories.Seed());
    }
}
