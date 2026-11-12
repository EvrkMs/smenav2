using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Smena.Server.Accounting;

namespace Smena.Server.Employees;

public sealed class Employee
{
    public const int NameLength = 100;
    public const int UsernameLength = 64;

    private Employee()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Обязателен: по нему сотрудник входит в MiniApp.</summary>
    public long TelegramId { get; private set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Заполняется при первом входе в MiniApp.</summary>
    public string? TelegramUsername { get; set; }

    /// <summary>Личная тема в зарплатном чате Telegram; null — сообщения сотруднику не шлются.</summary>
    public int? SalaryThreadId { get; set; }

    /// <summary>Ставка, ₽ в час.</summary>
    public int HourlyRate { get; set; }

    /// <summary>Сотрудника нельзя удалить (на нём висят проводки), только отключить.</summary>
    public bool IsActive { get; set; } = true;

    public Account Account { get; private set; } = null!;

    /// <summary>Создаёт сотрудника вместе с его счётом: одного без другого не бывает.</summary>
    public static Employee Create(long telegramId, string name, int hourlyRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(telegramId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(hourlyRate);

        var id = Guid.CreateVersion7();
        return new Employee
        {
            Id = id,
            TelegramId = telegramId,
            Name = name,
            HourlyRate = hourlyRate,
            Account = Account.ForEmployee(id, name),
        };
    }
}

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("employees", static table =>
        {
            table.HasCheckConstraint("ck_employees_hourly_rate", "hourly_rate >= 0");
            table.HasCheckConstraint("ck_employees_telegram_id", "telegram_id > 0");
        });

        builder.HasKey(static employee => employee.Id);
        builder.Property(static employee => employee.Id).ValueGeneratedNever();
        builder.Property(static employee => employee.Name).HasMaxLength(Employee.NameLength);
        builder.Property(static employee => employee.TelegramUsername).HasMaxLength(Employee.UsernameLength);
        builder.HasIndex(static employee => employee.TelegramId).IsUnique();
    }
}
