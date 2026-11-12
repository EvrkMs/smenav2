using Smena.Core.Accounting;

namespace Smena.Core.Shifts;

public enum ShiftProblemCode
{
    NoEmployees = 1,
    TooManyEmployees,
    DuplicateEmployee,
    UnknownEmployee,
    InactiveEmployee,
    AmountOutOfRange,
    HoursOutOfRange,
    TooManyHours,
    MinusOutOfRange,
    MinusMismatch,
    MinusExceedsPay,
    ShortageReasonRequired,
    NotEnoughPhotos,
}

/// <summary>Причина, по которой смену нельзя закрыть. Текст показывается администратору как есть.</summary>
public readonly record struct ShiftProblem(ShiftProblemCode Code, string Message, Guid? EmployeeId = null);

/// <summary>Рассчитанная строка сотрудника.</summary>
public readonly record struct ShiftEmployeeLine
{
    public required Guid EmployeeId { get; init; }

    public required string Name { get; init; }

    public required AccountRef Account { get; init; }

    public required int Hours { get; init; }

    /// <summary>Ставка на момент смены: сохраняется в смене, чтобы поздняя смена ставки не меняла историю.</summary>
    public required int HourlyRate { get; init; }

    /// <summary>Начислено: ставка × часы.</summary>
    public required int Accrued { get; init; }

    public required int Minus { get; init; }

    /// <summary>Часть минуса, закрывающая недостачу кассы.</summary>
    public int CashShortageShare { get; init; }

    /// <summary>Часть минуса, закрывающая недостачу сейфа.</summary>
    public int SafeShortageShare { get; init; }

    /// <summary>К выплате за смену: начислено − минус.</summary>
    public int Payable => Accrued - Minus;
}

/// <summary>
/// Результат расчёта смены. Числа посчитаны всегда — их показывает предпросмотр;
/// закрыть смену можно только при пустом <see cref="Problems"/>.
/// </summary>
public sealed class ShiftCalculation
{
    /// <summary>Размен, с которым считали.</summary>
    public required int CashFloat { get; init; }

    /// <summary>Баланс сейфа по учёту на момент расчёта.</summary>
    public required int ExpectedSafe { get; init; }

    /// <summary>Сумма четырёх программных значений.</summary>
    public required int ProgramTotal { get; init; }

    /// <summary>Выручка по факту: наличные − размен + безнал.</summary>
    public required int Revenue { get; init; }

    /// <summary>Сколько наличных уходит из кассы в сейф; отрицательное — размен пополняется из сейфа.</summary>
    public required int CashToSafe { get; init; }

    public required int NonCash { get; init; }

    /// <summary>Факт − программа одной суммой, со знаком.</summary>
    public required int CashDiscrepancy { get; init; }

    /// <summary>Факт сейфа − баланс по учёту, со знаком.</summary>
    public required int SafeDiscrepancy { get; init; }

    /// <summary>Недостача кассы + недостача сейфа. Излишки её не уменьшают.</summary>
    public required int RequiredMinus { get; init; }

    public required int EnteredMinus { get; init; }

    /// <summary>Начислено всем за смену.</summary>
    public required int PayrollAccrued { get; init; }

    /// <summary>К выплате всем за смену: начислено − минусы.</summary>
    public required int PayrollNet { get; init; }

    /// <summary>Итог: выручка − ЗП к выплате.</summary>
    public required int Total { get; init; }

    /// <summary>Баланс сейфа после закрытия смены.</summary>
    public required int SafeAfter { get; init; }

    public required IReadOnlyList<ShiftEmployeeLine> Lines { get; init; }

    public required IReadOnlyList<ShiftProblem> Problems { get; init; }

    public bool CanClose => Problems.Count == 0;

    public int CashShortage => Math.Max(0, -CashDiscrepancy);

    public int SafeShortage => Math.Max(0, -SafeDiscrepancy);

    public int SafeSurplus => Math.Max(0, SafeDiscrepancy);
}
