using Smena.Core.Accounting;
using Smena.Core.Notifications;
using Smena.Core.Shifts;

namespace Smena.Core.Tests.Notifications;

public sealed class NotificationTextsTests
{
    private static readonly ShiftRules Rules = new(cashFloat: 1000, maxEmployees: 3, maxTotalHours: 12, minPhotos: 3, photoMaxAgeHours: 5);

    private static readonly ShiftEmployeeInfo Ivan = new(Guid.CreateVersion7(), "Иван", 200, new AccountRef(100, AccountKind.Employee), IsActive: true);
    private static readonly ShiftEmployeeInfo Maria = new(Guid.CreateVersion7(), "Мария", 180, new AccountRef(101, AccountKind.Employee), IsActive: true);

    private static readonly Dictionary<Guid, ShiftEmployeeInfo> Employees =
        new[] { Ivan, Maria }.ToDictionary(employee => employee.Id);

    [Fact]
    public void Shift_report_lists_fact_programs_discrepancies_and_employees()
    {
        var input = new ShiftInput
        {
            FactCash = 7500,
            FactNonCash = 12000,
            SkladCash = 2000,
            SkladNonCash = 3000,
            LangameCash = 6000,
            LangameNonCash = 9000,
            FactSafe = 50000,
            ShortageReason = " не сошлась касса ",
            Employees = [new(Ivan.Id, 8, 300), new(Maria.Id, 4, 200)],
        };
        var calculation = ShiftCalculator.Calculate(input, Rules, expectedSafe: 50000, photoCount: 3, Employees);
        var closedAt = new DateTimeOffset(2026, 11, 10, 21, 30, 0, TimeSpan.FromHours(3));

        var text = NotificationTexts.ShiftReport(input, calculation, closedAt);

        Assert.Equal(
            """
            2026.11.10 21:30

            Нал: 7500
            Б/Н: 12000
            Выручка: 18500
            Итог: 16680

            Расхождение по кассе: -500
            Расхождение по сейфу: 0

            Факт сейфа: 50000

            == программные данные ==
            LanGame: нал 6000, б/н 9000
            МойСклад: нал 2000, б/н 3000
            Сейф: 50000

            Причина недостачи: не сошлась касса

            Сотрудники:
            - Иван: 8 ч, минус 300, ЗП 1300
            - Мария: 4 ч, минус 200, ЗП 520

            Всего ЗП: 1820
            """.ReplaceLineEndings(),
            text.ReplaceLineEndings());
    }

    [Fact]
    public void Clean_shift_report_has_no_reason_and_no_minus()
    {
        var input = new ShiftInput
        {
            FactCash = 8000,
            FactNonCash = 12000,
            SkladCash = 2000,
            SkladNonCash = 3000,
            LangameCash = 6000,
            LangameNonCash = 9000,
            FactSafe = 50250,
            Employees = [new(Ivan.Id, 12, 0)],
        };
        var calculation = ShiftCalculator.Calculate(input, Rules, expectedSafe: 50000, photoCount: 3, Employees);

        var text = NotificationTexts.ShiftReport(input, calculation, DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain("Причина", text, StringComparison.Ordinal);
        Assert.DoesNotContain("минус", text, StringComparison.Ordinal);
        Assert.Contains("Расхождение по сейфу: +250", text, StringComparison.Ordinal);
        Assert.Contains("- Иван: 12 ч, ЗП 2400", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Shift_report_counts_photos_but_not_their_age()
    {
        var input = new ShiftInput
        {
            FactCash = 8000,
            FactNonCash = 12000,
            SkladCash = 2000,
            SkladNonCash = 3000,
            LangameCash = 6000,
            LangameNonCash = 9000,
            FactSafe = 50000,
            Employees = [new(Ivan.Id, 12, 0)],
        };
        var calculation = ShiftCalculator.Calculate(input, Rules, expectedSafe: 50000, photoCount: 3, Employees);

        var text = NotificationTexts.ShiftReport(input, calculation, DateTimeOffset.UnixEpoch, photoCount: 3, pastedPhotoCount: 1);

        Assert.EndsWith("Фото: 3 (с кассы: 1)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Safe_movements_show_time_signed_lines_and_balance()
    {
        var text = NotificationTexts.SafeMovements(
            [new MoneyLine(6500, "Пополнение сейфа (касса)"), new MoneyLine(-300, "Недостача сейфа: Иван")],
            balanceAfter: 56200,
            At);

        Assert.Equal(
            """
            2026.11.10 21:30
            +6500 Пополнение сейфа (касса)
            -300 Недостача сейфа: Иван
            В сейфе: 56200
            """.ReplaceLineEndings(),
            text.ReplaceLineEndings());
    }

    [Fact]
    public void Employee_movements_show_negative_balance_plainly()
    {
        var text = NotificationTexts.EmployeeMovements([new MoneyLine(-3000, "Аванс")], balanceAfter: -1200, At);

        Assert.Equal("2026.11.10 21:30\n-3000 Аванс\nБаланс: -1200", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Expense_shows_time_category_comment_and_author()
    {
        Assert.Equal(
            "2026.11.10 21:30\nРасход 450 Хозяйственные\nвода, 3 бутыли\nПровёл: Касса",
            NotificationTexts.Expense(450, "Хозяйственные", " вода, 3 бутыли ", "Касса", At).ReplaceLineEndings("\n"));

        Assert.Equal(
            "2026.11.10 21:30\nРасход 30000 Инкассация\nПровёл: Касса",
            NotificationTexts.Expense(30000, "Инкассация", null, "Касса", At).ReplaceLineEndings("\n"));
    }

    private static readonly DateTimeOffset At = new(2026, 11, 10, 21, 30, 0, TimeSpan.FromHours(3));
}
