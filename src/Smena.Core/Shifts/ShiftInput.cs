using Smena.Core.Accounting;

namespace Smena.Core.Shifts;

/// <summary>Строка сотрудника в отчёте: сколько отработал и какую долю минуса берёт на себя.</summary>
public readonly record struct ShiftEmployeeInput(Guid EmployeeId, int Hours, int Minus);

/// <summary>То, что администратор вводит при закрытии смены. Все суммы — в рублях.</summary>
public sealed record ShiftInput
{
    /// <summary>Наличные в кассе по факту, вместе с разменом.</summary>
    public required int FactCash { get; init; }

    /// <summary>Безнал по сверке итогов терминала.</summary>
    public required int FactNonCash { get; init; }

    public required int SkladCash { get; init; }

    public required int SkladNonCash { get; init; }

    /// <summary>Наличные по LanGame; размен числится здесь.</summary>
    public required int LangameCash { get; init; }

    public required int LangameNonCash { get; init; }

    /// <summary>Сейф по факту. Считается до того, как в него положили выручку смены.</summary>
    public required int FactSafe { get; init; }

    public string? ShortageReason { get; init; }

    public required IReadOnlyList<ShiftEmployeeInput> Employees { get; init; }

    /// <summary>
    /// Сессия загрузки, в которую администратор снял фото до отправки отчёта.
    /// Её фото привязываются к смене при закрытии.
    /// </summary>
    public Guid? PhotoSessionId { get; init; }
}

/// <summary>То, что расчёту нужно знать о сотруднике из базы.</summary>
public readonly record struct ShiftEmployeeInfo(Guid Id, string Name, int HourlyRate, AccountRef Account, bool IsActive);
