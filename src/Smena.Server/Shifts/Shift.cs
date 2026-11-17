using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Smena.Core.Shifts;
using Smena.Server.Accounting;
using Smena.Server.Employees;

namespace Smena.Server.Shifts;

/// <summary>
/// Закрытая смена: то, что ввёл администратор, и то, что система посчитала в тот момент.
/// Деньги смены лежат в её операции; смена отменена, если отменена операция.
/// </summary>
public sealed class Shift
{
    private readonly List<ShiftEmployee> _employees = [];

    private Shift()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Операция закрытия. Удаление операции удаляет и смену.</summary>
    public Guid OperationId { get; private set; }

    public DateTimeOffset ClosedAt { get; private set; }

    public int FactCash { get; private set; }

    public int FactNonCash { get; private set; }

    public int SkladCash { get; private set; }

    public int SkladNonCash { get; private set; }

    public int LangameCash { get; private set; }

    public int LangameNonCash { get; private set; }

    public int FactSafe { get; private set; }

    /// <summary>Баланс сейфа по учёту в момент закрытия.</summary>
    public int ExpectedSafe { get; private set; }

    /// <summary>Размен, действовавший в момент закрытия: без него выручку смены потом не пересчитать.</summary>
    public int CashFloat { get; private set; }

    public int CashDiscrepancy { get; private set; }

    public int SafeDiscrepancy { get; private set; }

    public string? ShortageReason { get; private set; }

    public IReadOnlyList<ShiftEmployee> Employees => _employees;

    internal static Shift Create(Guid operationId, DateTimeOffset closedAt, ShiftInput input, ShiftCalculation calculation)
    {
        var shift = new Shift
        {
            Id = Guid.CreateVersion7(),
            OperationId = operationId,
            ClosedAt = closedAt.ToUniversalTime(),
            FactCash = input.FactCash,
            FactNonCash = input.FactNonCash,
            SkladCash = input.SkladCash,
            SkladNonCash = input.SkladNonCash,
            LangameCash = input.LangameCash,
            LangameNonCash = input.LangameNonCash,
            FactSafe = input.FactSafe,
            ExpectedSafe = calculation.ExpectedSafe,
            CashFloat = calculation.CashFloat,
            CashDiscrepancy = calculation.CashDiscrepancy,
            SafeDiscrepancy = calculation.SafeDiscrepancy,
            ShortageReason = calculation.RequiredMinus > 0 && !string.IsNullOrWhiteSpace(input.ShortageReason)
                ? input.ShortageReason.Trim()
                : null,
        };

        var lines = calculation.Lines;
        shift._employees.EnsureCapacity(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            shift._employees.Add(ShiftEmployee.FromLine(lines[index], (short)index));
        }

        return shift;
    }
}

public sealed class ShiftEmployee
{
    private ShiftEmployee()
    {
    }

    public Guid ShiftId { get; private set; }

    public Guid EmployeeId { get; private set; }

    /// <summary>Порядок строк в отчёте.</summary>
    public short Position { get; private set; }

    public int Hours { get; private set; }

    /// <summary>Ставка на момент смены.</summary>
    public int HourlyRate { get; private set; }

    public int Minus { get; private set; }

    internal static ShiftEmployee FromLine(in ShiftEmployeeLine line, short position) => new()
    {
        EmployeeId = line.EmployeeId,
        Position = position,
        Hours = line.Hours,
        HourlyRate = line.HourlyRate,
        Minus = line.Minus,
    };
}

internal sealed class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> builder)
    {
        builder.ToTable("shifts", static table =>
            table.HasCheckConstraint(
                "ck_shifts_amounts",
                "fact_cash >= 0 AND fact_non_cash >= 0 AND sklad_cash >= 0 AND sklad_non_cash >= 0 " +
                "AND langame_cash >= 0 AND langame_non_cash >= 0 AND fact_safe >= 0 AND cash_float >= 0"));

        builder.HasKey(static shift => shift.Id);
        builder.Property(static shift => shift.Id).ValueGeneratedNever();
        builder.Property(static shift => shift.ShortageReason).HasMaxLength(ShiftCalculator.MaxReasonLength);

        builder.HasIndex(static shift => shift.OperationId).IsUnique();
        builder.HasIndex(static shift => shift.ClosedAt);

        builder.HasOne<Operation>()
            .WithOne()
            .HasForeignKey<Shift>(static shift => shift.OperationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(static shift => shift.Employees)
            .WithOne()
            .HasForeignKey(static employee => employee.ShiftId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ShiftEmployeeConfiguration : IEntityTypeConfiguration<ShiftEmployee>
{
    public void Configure(EntityTypeBuilder<ShiftEmployee> builder)
    {
        builder.ToTable("shift_employees", static table =>
            table.HasCheckConstraint("ck_shift_employees_values", "hours >= 0 AND hourly_rate >= 0 AND minus >= 0"));

        builder.HasKey(static employee => new { employee.ShiftId, employee.EmployeeId });

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(static employee => employee.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
