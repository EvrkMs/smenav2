namespace Smena.Core.Shifts;

/// <summary>
/// Расчёт закрытия смены. Единственное место с формулами: его вызывают и
/// предпросмотр, и само закрытие, поэтому они не могут разойтись.
/// Правила — docs/PLAN.md, раздел 4.
/// </summary>
public static class ShiftCalculator
{
    /// <summary>Верхняя граница любой вводимой суммы. Защищает от переполнения и опечаток.</summary>
    public const int MaxAmount = 99_999_999;

    public const int MaxReasonLength = 500;

    /// <param name="input">Ввод администратора.</param>
    /// <param name="rules">Действующие настройки клуба.</param>
    /// <param name="expectedSafe">Баланс сейфа по учёту.</param>
    /// <param name="photoCount">Сколько фото загружено в сессию из ввода.</param>
    /// <param name="employees">Сотрудники из базы, упомянутые во вводе.</param>
    public static ShiftCalculation Calculate(
        ShiftInput input,
        ShiftRules rules,
        int expectedSafe,
        int photoCount,
        IReadOnlyDictionary<Guid, ShiftEmployeeInfo> employees)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(employees);

        var problems = new List<ShiftProblem>();

        // Суммы вне диапазона для расчёта заменяются нулём: проблема уже записана,
        // а предпросмотр не должен падать на переполнении.
        var factCash = Amount(input.FactCash, "Факт нал.", problems);
        var factNonCash = Amount(input.FactNonCash, "Факт безнал.", problems);
        var skladCash = Amount(input.SkladCash, "МойСклад нал.", problems);
        var skladNonCash = Amount(input.SkladNonCash, "МойСклад безнал.", problems);
        var langameCash = Amount(input.LangameCash, "LanGame нал.", problems);
        var langameNonCash = Amount(input.LangameNonCash, "LanGame безнал.", problems);
        var factSafe = Amount(input.FactSafe, "Факт в сейфе", problems);

        // Каждое слагаемое не больше MaxAmount, поэтому int не переполняется.
        var programTotal = skladCash + skladNonCash + langameCash + langameNonCash;
        var cashToSafe = factCash - rules.CashFloat;
        var revenue = cashToSafe + factNonCash;
        var cashDiscrepancy = factCash + factNonCash - programTotal;
        var safeDiscrepancy = factSafe - expectedSafe;

        // Излишек в одном месте не гасит недостачу в другом.
        var cashShortage = Math.Max(0, -cashDiscrepancy);
        var safeShortage = Math.Max(0, -safeDiscrepancy);
        var requiredMinus = cashShortage + safeShortage;

        var lines = BuildLines(input.Employees, rules, employees, problems, out var totals);

        if (totals.Minus != requiredMinus)
        {
            problems.Add(new ShiftProblem(
                ShiftProblemCode.MinusMismatch,
                $"Сумма минусов должна быть равна {requiredMinus}, введено {totals.Minus}."));
        }

        if (requiredMinus > 0 && string.IsNullOrWhiteSpace(input.ShortageReason))
        {
            problems.Add(new ShiftProblem(ShiftProblemCode.ShortageReasonRequired, "Укажите причину недостачи."));
        }
        else if (input.ShortageReason is { Length: > MaxReasonLength })
        {
            problems.Add(new ShiftProblem(
                ShiftProblemCode.ShortageReasonRequired,
                $"Причина недостачи длиннее {MaxReasonLength} символов."));
        }

        // Что на снимках, система не проверяет — только количество.
        if (photoCount < rules.MinPhotos)
        {
            problems.Add(new ShiftProblem(
                ShiftProblemCode.NotEnoughPhotos,
                $"Нужно не меньше {rules.MinPhotos} фото, загружено {photoCount}."));
        }

        if (problems.Count == 0)
        {
            SplitShortage(lines, cashShortage);
        }

        return new ShiftCalculation
        {
            CashFloat = rules.CashFloat,
            ExpectedSafe = expectedSafe,
            ProgramTotal = programTotal,
            Revenue = revenue,
            CashToSafe = cashToSafe,
            NonCash = factNonCash,
            CashDiscrepancy = cashDiscrepancy,
            SafeDiscrepancy = safeDiscrepancy,
            RequiredMinus = requiredMinus,
            EnteredMinus = totals.Minus,
            PayrollAccrued = totals.Accrued,
            PayrollNet = totals.Accrued - totals.Minus,
            Total = revenue - (totals.Accrued - totals.Minus),
            // Сейф считали до того, как положили выручку, поэтому после закрытия
            // в нём фактическая сумма плюс наличные из кассы.
            SafeAfter = factSafe + cashToSafe,
            Lines = lines,
            Problems = problems,
        };
    }

    private static ShiftEmployeeLine[] BuildLines(
        IReadOnlyList<ShiftEmployeeInput> entries,
        ShiftRules rules,
        IReadOnlyDictionary<Guid, ShiftEmployeeInfo> employees,
        List<ShiftProblem> problems,
        out (int Accrued, int Minus, int Hours) totals)
    {
        totals = default;

        if (entries is null || entries.Count == 0)
        {
            problems.Add(new ShiftProblem(ShiftProblemCode.NoEmployees, "Добавьте сотрудника."));
            return [];
        }

        if (entries.Count > rules.MaxEmployees)
        {
            problems.Add(new ShiftProblem(
                ShiftProblemCode.TooManyEmployees,
                $"В смене может быть не больше {rules.MaxEmployees} сотрудников."));
        }

        var lines = new ShiftEmployeeLine[entries.Count];
        var count = 0;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            if (Contains(lines.AsSpan(0, count), entry.EmployeeId))
            {
                problems.Add(new ShiftProblem(
                    ShiftProblemCode.DuplicateEmployee,
                    "Один и тот же сотрудник указан дважды.",
                    entry.EmployeeId));
                continue;
            }

            if (!employees.TryGetValue(entry.EmployeeId, out var employee))
            {
                problems.Add(new ShiftProblem(
                    ShiftProblemCode.UnknownEmployee,
                    "Сотрудник не найден. Обновите список сотрудников.",
                    entry.EmployeeId));
                continue;
            }

            if (!employee.IsActive)
            {
                problems.Add(new ShiftProblem(
                    ShiftProblemCode.InactiveEmployee,
                    $"Сотрудник {employee.Name} отключён.",
                    entry.EmployeeId));
            }

            var hours = entry.Hours;
            if (hours < 0 || hours > rules.MaxTotalHours)
            {
                // 0 часов допустимо: сотрудник мог не выходить на смену.
                problems.Add(new ShiftProblem(
                    ShiftProblemCode.HoursOutOfRange,
                    $"Часы сотрудника {employee.Name} должны быть от 0 до {rules.MaxTotalHours}.",
                    entry.EmployeeId));
                hours = 0;
            }

            var minus = entry.Minus;
            if (minus < 0 || minus > MaxAmount)
            {
                problems.Add(new ShiftProblem(
                    ShiftProblemCode.MinusOutOfRange,
                    $"Минус сотрудника {employee.Name} должен быть от 0 до {MaxAmount}.",
                    entry.EmployeeId));
                minus = 0;
            }

            // Ставка × часы считается в long: ставка из базы ничем сверху не ограничена.
            var accrued = (int)Math.Min((long)employee.HourlyRate * hours, MaxAmount);

            if (minus > accrued)
            {
                // Обходного пути нет и намекать на него нельзя (PLAN.md, раздел 4).
                problems.Add(new ShiftProblem(
                    ShiftProblemCode.MinusExceedsPay,
                    $"Минус сотрудника {employee.Name} не может быть больше его ЗП за смену ({accrued}).",
                    entry.EmployeeId));
            }

            lines[count++] = new ShiftEmployeeLine
            {
                EmployeeId = employee.Id,
                Name = employee.Name,
                Account = employee.Account,
                Hours = hours,
                HourlyRate = employee.HourlyRate,
                Accrued = accrued,
                Minus = minus,
            };

            totals.Accrued += accrued;
            totals.Minus += minus;
            totals.Hours += hours;
        }

        if (totals.Hours > rules.MaxTotalHours)
        {
            problems.Add(new ShiftProblem(
                ShiftProblemCode.TooManyHours,
                $"Суммарно часов не может быть больше {rules.MaxTotalHours}, указано {totals.Hours}."));
        }

        return count == lines.Length ? lines : lines[..count];
    }

    /// <summary>
    /// Делит минус каждого сотрудника на часть за кассу и часть за сейф:
    /// сначала закрывается недостача кассы в порядке строк, остальное идёт на сейф.
    /// В сумме доли всегда равны минусу сотрудника; деление нужно только для проводок,
    /// потому что недостача кассы и недостача сейфа проводятся по разным счетам.
    /// </summary>
    private static void SplitShortage(Span<ShiftEmployeeLine> lines, int cashShortage)
    {
        var cashLeft = cashShortage;

        // ref: строка — структура в массиве, правим её на месте, а не копию.
        foreach (ref var line in lines)
        {
            var cashShare = Math.Min(line.Minus, cashLeft);
            cashLeft -= cashShare;
            line = line with { CashShortageShare = cashShare, SafeShortageShare = line.Minus - cashShare };
        }
    }

    private static bool Contains(ReadOnlySpan<ShiftEmployeeLine> lines, Guid employeeId)
    {
        foreach (ref readonly var line in lines)
        {
            if (line.EmployeeId == employeeId)
            {
                return true;
            }
        }

        return false;
    }

    private static int Amount(int value, string field, List<ShiftProblem> problems)
    {
        if (value is >= 0 and <= MaxAmount)
        {
            return value;
        }

        problems.Add(new ShiftProblem(
            ShiftProblemCode.AmountOutOfRange,
            $"«{field}»: значение должно быть от 0 до {MaxAmount}."));
        return 0;
    }
}
