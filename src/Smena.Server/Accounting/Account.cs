using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Smena.Core.Accounting;
using Smena.Server.Employees;

namespace Smena.Server.Accounting;

public sealed class Account
{
    public const int NameLength = 100;

    private Account()
    {
    }

    public int Id { get; private set; }

    public AccountKind Kind { get; private set; }

    /// <summary>Заполнен только у счёта сотрудника.</summary>
    public Guid? EmployeeId { get; private set; }

    public string Name { get; set; } = string.Empty;

    public AccountRef Ref => new(Id, Kind);

    internal static Account ForEmployee(Guid employeeId, string name) =>
        new() { Kind = AccountKind.Employee, EmployeeId = employeeId, Name = name };

    internal static Account System(AccountRef account, string name) =>
        new() { Id = account.Id, Kind = account.Kind, Name = name };
}

/// <summary>
/// Системные счета. Создаются миграцией с фиксированными номерами, поэтому
/// ссылаться на них можно без запроса в базу.
/// </summary>
public static class SystemAccounts
{
    /// <summary>С этого номера база выдаёт идентификаторы счетам сотрудников.</summary>
    internal const int FirstEmployeeAccountId = 100;

    public static AccountRef Safe { get; } = new(1, AccountKind.Safe);

    public static AccountRef Bank { get; } = new(2, AccountKind.Bank);

    public static AccountRef Expense { get; } = new(3, AccountKind.Expense);

    public static AccountRef Revenue { get; } = new(4, AccountKind.Revenue);

    public static AccountRef Opening { get; } = new(5, AccountKind.Opening);
}

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", static table =>
            table.HasCheckConstraint(
                "ck_accounts_employee",
                $"(kind_id = {(short)AccountKind.Employee}) = (employee_id IS NOT NULL)"));

        builder.HasKey(static account => account.Id);
        builder.Property(static account => account.Id)
            .UseIdentityByDefaultColumn()
            .HasIdentityOptions(startValue: SystemAccounts.FirstEmployeeAccountId);

        builder.Property(static account => account.Kind).HasColumnName("kind_id");
        builder.Property(static account => account.Name).HasMaxLength(Account.NameLength);
        builder.Ignore(static account => account.Ref);

        builder.HasOne<AccountKindRow>()
            .WithMany()
            .HasForeignKey(static account => account.Kind)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithOne(static employee => employee.Account)
            .HasForeignKey<Account>(static account => account.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        // Системный счёт каждого вида ровно один.
        builder.HasIndex(static account => account.Kind)
            .IsUnique()
            .HasFilter("employee_id IS NULL")
            .HasDatabaseName("ux_accounts_system_kind");

        builder.HasData(
            Account.System(SystemAccounts.Safe, "Сейф"),
            Account.System(SystemAccounts.Bank, "Банк"),
            Account.System(SystemAccounts.Expense, "Расходы"),
            Account.System(SystemAccounts.Revenue, "Выручка"),
            Account.System(SystemAccounts.Opening, "Стартовые остатки"));
    }
}
