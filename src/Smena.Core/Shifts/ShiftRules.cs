namespace Smena.Core.Shifts;

/// <summary>
/// Правила смены, которые root меняет в панели. Экземпляр всегда корректен:
/// недопустимые значения отсекаются в конструкторе, поэтому расчёты ниже
/// по коду их не перепроверяют.
/// </summary>
/// <remarks>
/// Это класс, а не структура: у структуры есть <c>default</c>, который обошёл бы
/// проверки и дал «правила» из одних нулей.
/// </remarks>
public sealed record ShiftRules
{
    public ShiftRules(
        int cashFloat,
        int maxEmployees,
        int maxTotalHours,
        int minPhotos,
        int photoMaxAgeHours)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cashFloat);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEmployees, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTotalHours, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(minPhotos);
        ArgumentOutOfRangeException.ThrowIfLessThan(photoMaxAgeHours, 1);

        CashFloat = cashFloat;
        MaxEmployees = maxEmployees;
        MaxTotalHours = maxTotalHours;
        MinPhotos = minPhotos;
        PhotoMaxAgeHours = photoMaxAgeHours;
    }

    /// <summary>Наличные, которые остаются в кассе на размен, ₽.</summary>
    public int CashFloat { get; }

    /// <summary>Сколько сотрудников можно указать в одной смене.</summary>
    public int MaxEmployees { get; }

    /// <summary>Сколько часов в сумме можно указать на всех сотрудников смены.</summary>
    public int MaxTotalHours { get; }

    /// <summary>Сколько фото обязательно приложить, чтобы закрыть смену.</summary>
    public int MinPhotos { get; }

    /// <summary>Старше скольки часов снимок помечается в отчёте как устаревший.</summary>
    public int PhotoMaxAgeHours { get; }
}
