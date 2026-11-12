using Smena.Core.Accounting;

namespace Smena.Core.Shifts;

/// <summary>
/// Счета и категории, по которым проводится смена. Ядро не знает номеров из базы —
/// их передаёт сервер.
/// </summary>
public readonly record struct ShiftChart
{
    public required AccountRef Safe { get; init; }

    public required AccountRef Bank { get; init; }

    public required AccountRef Revenue { get; init; }

    public required AccountRef Expense { get; init; }

    public required CategoryRef RevenueShift { get; init; }

    public required CategoryRef RevenueShortageWithheld { get; init; }

    public required CategoryRef RevenueSafeSurplus { get; init; }

    public required CategoryRef ExpensePayroll { get; init; }

    public required CategoryRef SafeShiftCash { get; init; }

    public required CategoryRef SafeFloatRefill { get; init; }

    public required CategoryRef SafeSurplus { get; init; }

    public required CategoryRef SafeShortage { get; init; }

    public required CategoryRef BankAcquiring { get; init; }

    public required CategoryRef EmployeeAccrual { get; init; }

    public required CategoryRef EmployeeShortage { get; init; }
}

/// <summary>Превращает расчёт смены в транзакции. Таблица проводок — docs/PLAN.md, раздел 3.</summary>
public static class ShiftPostings
{
    /// <param name="calculation">Расчёт без проблем.</param>
    /// <param name="chart">Счета и категории. Передаётся по ссылке: структура большая, копировать её незачем.</param>
    public static List<TransactionDraft> Build(ShiftCalculation calculation, in ShiftChart chart)
    {
        ArgumentNullException.ThrowIfNull(calculation);

        if (!calculation.CanClose)
        {
            throw new InvalidOperationException("Нельзя строить проводки по расчёту, в котором есть проблемы.");
        }

        var lines = calculation.Lines;

        // Наличка, безнал, излишек сейфа и до трёх транзакций на сотрудника.
        var transactions = new List<TransactionDraft>(capacity: 3 + (lines.Count * 3));

        if (calculation.CashToSafe > 0)
        {
            transactions.Add(TransactionDraft.Create(
                calculation.CashToSafe,
                Leg.Increase(chart.Safe, chart.SafeShiftCash),
                Leg.Increase(chart.Revenue, chart.RevenueShift),
                "Наличка в сейф"));
        }
        else if (calculation.CashToSafe < 0)
        {
            // В кассе меньше размена: недостающее берётся из сейфа, выручка наличными отрицательная.
            transactions.Add(TransactionDraft.Create(
                -calculation.CashToSafe,
                Leg.Decrease(chart.Safe, chart.SafeFloatRefill),
                Leg.Decrease(chart.Revenue, chart.RevenueShift),
                "Пополнение размена из сейфа"));
        }

        if (calculation.NonCash > 0)
        {
            transactions.Add(TransactionDraft.Create(
                calculation.NonCash,
                Leg.Increase(chart.Bank, chart.BankAcquiring),
                Leg.Increase(chart.Revenue, chart.RevenueShift),
                "Безнал"));
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            if (line.Accrued > 0)
            {
                transactions.Add(TransactionDraft.Create(
                    line.Accrued,
                    Leg.Increase(chart.Expense, chart.ExpensePayroll),
                    Leg.Increase(line.Account, chart.EmployeeAccrual),
                    $"ЗП: {line.Name}"));
            }

            if (line.CashShortageShare > 0)
            {
                transactions.Add(TransactionDraft.Create(
                    line.CashShortageShare,
                    Leg.Decrease(line.Account, chart.EmployeeShortage),
                    Leg.Increase(chart.Revenue, chart.RevenueShortageWithheld),
                    $"Недостача кассы: {line.Name}"));
            }

            if (line.SafeShortageShare > 0)
            {
                transactions.Add(TransactionDraft.Create(
                    line.SafeShortageShare,
                    Leg.Decrease(chart.Safe, chart.SafeShortage),
                    Leg.Decrease(line.Account, chart.EmployeeShortage),
                    $"Недостача сейфа: {line.Name}"));
            }
        }

        if (calculation.SafeSurplus > 0)
        {
            transactions.Add(TransactionDraft.Create(
                calculation.SafeSurplus,
                Leg.Increase(chart.Safe, chart.SafeSurplus),
                Leg.Increase(chart.Revenue, chart.RevenueSafeSurplus),
                "Излишек сейфа"));
        }

        return transactions;
    }
}
