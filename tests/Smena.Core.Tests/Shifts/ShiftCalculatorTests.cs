using Smena.Core.Accounting;
using Smena.Core.Shifts;

namespace Smena.Core.Tests.Shifts;

public sealed class ShiftCalculatorTests
{
    private static readonly ShiftRules Rules = new(cashFloat: 1000, maxEmployees: 3, maxTotalHours: 12, minPhotos: 3, photoMaxAgeHours: 5);

    private static readonly ShiftEmployeeInfo Ivan = new(Guid.CreateVersion7(), "Иван", 200, new AccountRef(100, AccountKind.Employee), IsActive: true);
    private static readonly ShiftEmployeeInfo Maria = new(Guid.CreateVersion7(), "Мария", 180, new AccountRef(101, AccountKind.Employee), IsActive: true);
    private static readonly ShiftEmployeeInfo Oleg = new(Guid.CreateVersion7(), "Олег", 150, new AccountRef(102, AccountKind.Employee), IsActive: true);
    private static readonly ShiftEmployeeInfo Nina = new(Guid.CreateVersion7(), "Нина", 150, new AccountRef(103, AccountKind.Employee), IsActive: true);
    private static readonly ShiftEmployeeInfo Fired = new(Guid.CreateVersion7(), "Уволенный", 150, new AccountRef(104, AccountKind.Employee), IsActive: false);

    private static readonly Dictionary<Guid, ShiftEmployeeInfo> Employees =
        new[] { Ivan, Maria, Oleg, Nina, Fired }.ToDictionary(employee => employee.Id);

    /// <summary>Смена без расхождений: касса 8000 (с разменом), безнал 12000, сейф сходится.</summary>
    private static ShiftInput CleanShift(params ShiftEmployeeInput[] employees) => new()
    {
        FactCash = 8000,
        FactNonCash = 12000,
        SkladCash = 2000,
        SkladNonCash = 3000,
        LangameCash = 6000, // вместе с разменом 1000
        LangameNonCash = 9000,
        FactSafe = 50000,
        Employees = employees.Length > 0 ? employees : [E(Ivan, 12, 0)],
    };

    private static ShiftEmployeeInput E(ShiftEmployeeInfo employee, int hours, int minus) => new(employee.Id, hours, minus);

    private static ShiftEmployeeInput E(Guid employeeId, int hours, int minus) => new(employeeId, hours, minus);

    private static ShiftCalculation Calculate(ShiftInput input, int expectedSafe = 50000, int photoCount = 3) =>
        ShiftCalculator.Calculate(input, Rules, expectedSafe, photoCount, Employees);

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Too_few_photos_block_the_shift(int photoCount)
    {
        var result = Calculate(CleanShift(), photoCount: photoCount);

        Assert.False(result.CanClose);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(ShiftProblemCode.NotEnoughPhotos, problem.Code);
        Assert.Contains($"загружено {photoCount}", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extra_photos_are_allowed()
    {
        Assert.True(Calculate(CleanShift(), photoCount: 4).CanClose);
    }

    [Fact]
    public void Clean_shift_matches_the_agreed_formulas()
    {
        var result = Calculate(CleanShift(E(Ivan, 8, 0), E(Maria, 4, 0)));

        Assert.True(result.CanClose);
        Assert.Equal(20000, result.ProgramTotal);
        Assert.Equal(7000, result.CashToSafe);          // 8000 − размен 1000
        Assert.Equal(19000, result.Revenue);            // 7000 + 12000
        Assert.Equal(0, result.CashDiscrepancy);        // (8000 + 12000) − 20000: размен есть и в факте, и в программе
        Assert.Equal(0, result.SafeDiscrepancy);
        Assert.Equal(0, result.RequiredMinus);
        Assert.Equal(2320, result.PayrollAccrued);      // 200×8 + 180×4
        Assert.Equal(2320, result.PayrollNet);
        Assert.Equal(16680, result.Total);              // 19000 − 2320
        Assert.Equal(57000, result.SafeAfter);          // 50000 + 7000

        Assert.Collection(
            result.Lines,
            ivan => Assert.Equal((Ivan.Id, 8, 200, 1600, 0, 1600), (ivan.EmployeeId, ivan.Hours, ivan.HourlyRate, ivan.Accrued, ivan.Minus, ivan.Payable)),
            maria => Assert.Equal((Maria.Id, 4, 180, 720, 0, 720), (maria.EmployeeId, maria.Hours, maria.HourlyRate, maria.Accrued, maria.Minus, maria.Payable)));
    }

    [Fact]
    public void Discrepancy_is_one_sum_so_wrong_payment_type_does_not_matter()
    {
        // Оплату 500 картой пробили как наличные: по налу «минус» 500, по безналу «плюс» 500.
        var input = CleanShift() with { FactCash = 7500, FactNonCash = 12500 };

        var result = Calculate(input);

        Assert.Equal(0, result.CashDiscrepancy);
        Assert.Equal(0, result.RequiredMinus);
        Assert.True(result.CanClose);
    }

    [Fact]
    public void Cash_shortage_must_be_covered_by_entered_minus()
    {
        var input = CleanShift(E(Ivan, 8, 300), E(Maria, 4, 200)) with
        {
            FactCash = 7500,
            ShortageReason = "не сошлась касса",
        };

        var result = Calculate(input);

        Assert.True(result.CanClose);
        Assert.Equal(-500, result.CashDiscrepancy);
        Assert.Equal(500, result.RequiredMinus);
        Assert.Equal(500, result.EnteredMinus);
        Assert.Equal(18500, result.Revenue);             // выручка — по факту
        Assert.Equal(1820, result.PayrollNet);           // 2320 − 500
        Assert.Equal(16680, result.Total);               // 18500 − 1820: удержание вернуло итог к программному
        Assert.Equal([300, 200], result.Lines.Select(line => line.CashShortageShare));
        Assert.Equal([0, 0], result.Lines.Select(line => line.SafeShortageShare));
    }

    [Fact]
    public void Safe_shortage_is_added_to_cash_shortage()
    {
        var input = CleanShift(E(Ivan, 8, 600), E(Maria, 4, 200)) with
        {
            FactCash = 7500,     // касса −500
            FactSafe = 49700,    // сейф −300
            ShortageReason = "касса и сейф",
        };

        var result = Calculate(input);

        Assert.True(result.CanClose);
        Assert.Equal(-500, result.CashDiscrepancy);
        Assert.Equal(-300, result.SafeDiscrepancy);
        Assert.Equal(800, result.RequiredMinus);

        // Сначала закрывается касса по порядку строк, остаток уходит на сейф.
        Assert.Equal([500, 0], result.Lines.Select(line => line.CashShortageShare));
        Assert.Equal([100, 200], result.Lines.Select(line => line.SafeShortageShare));
        Assert.Equal(56200, result.SafeAfter);           // 49700 + 6500
    }

    [Fact]
    public void Cash_surplus_does_not_offset_safe_shortage()
    {
        var input = CleanShift(E(Ivan, 12, 100)) with
        {
            FactCash = 8100,     // касса +100
            FactSafe = 49900,    // сейф −100
            ShortageReason = "сейф",
        };

        var result = Calculate(input);

        Assert.Equal(100, result.CashDiscrepancy);
        Assert.Equal(-100, result.SafeDiscrepancy);
        Assert.Equal(100, result.RequiredMinus);
        Assert.True(result.CanClose);
        Assert.Equal(19100, result.Revenue);             // излишек кассы остаётся в выручке
    }

    [Fact]
    public void Safe_surplus_does_not_offset_cash_shortage()
    {
        var input = CleanShift(E(Ivan, 12, 100)) with
        {
            FactCash = 7900,     // касса −100
            FactSafe = 50100,    // сейф +100
            ShortageReason = "касса",
        };

        var result = Calculate(input);

        Assert.Equal(100, result.RequiredMinus);
        Assert.Equal(100, result.SafeSurplus);
        Assert.True(result.CanClose);
    }

    [Fact]
    public void Surpluses_alone_require_no_minus_and_no_reason()
    {
        var input = CleanShift() with { FactCash = 8200, FactSafe = 50300 };

        var result = Calculate(input);

        Assert.True(result.CanClose);
        Assert.Equal(0, result.RequiredMinus);
        Assert.Equal(300, result.SafeSurplus);
    }

    [Fact]
    public void Long_reason_is_ignored_when_there_is_no_shortage()
    {
        var input = CleanShift() with { ShortageReason = new string('x', 501) };

        var result = Calculate(input);

        Assert.True(result.CanClose);
        Assert.Empty(result.Problems);
    }

    [Theory]
    [InlineData(0)]      // не ввели вообще
    [InlineData(499)]    // недобрали
    [InlineData(501)]    // перебрали
    public void Entered_minus_must_equal_required_exactly(int entered)
    {
        var input = CleanShift(E(Ivan, 12, entered)) with { FactCash = 7500, ShortageReason = "причина" };

        var result = Calculate(input);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(ShiftProblemCode.MinusMismatch, problem.Code);
        Assert.Contains("500", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Minus_without_shortage_is_rejected()
    {
        var result = Calculate(CleanShift(E(Ivan, 12, 100)));

        Assert.Equal(ShiftProblemCode.MinusMismatch, Assert.Single(result.Problems).Code);
    }

    [Fact]
    public void Employee_share_cannot_exceed_own_pay_for_the_shift()
    {
        // Мария отработала 1 час (180), а берёт на себя 400.
        var input = CleanShift(E(Ivan, 11, 100), E(Maria, 1, 400)) with
        {
            FactCash = 7500,
            ShortageReason = "причина",
        };

        var result = Calculate(input);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(ShiftProblemCode.MinusExceedsPay, problem.Code);
        Assert.Equal(Maria.Id, problem.EmployeeId);
    }

    [Fact]
    public void Shift_cannot_close_when_shortage_exceeds_total_pay()
    {
        // ЗП смены 2400, недостача 3000: как ни дели, чья-то доля превысит ЗП.
        var input = CleanShift(E(Ivan, 12, 3000)) with { FactCash = 5000, ShortageReason = "причина" };

        var result = Calculate(input);

        Assert.False(result.CanClose);
        Assert.Contains(result.Problems, problem => problem.Code == ShiftProblemCode.MinusExceedsPay);
    }

    [Fact]
    public void Employee_with_zero_hours_is_allowed_but_cannot_take_minus()
    {
        var allowed = Calculate(CleanShift(E(Ivan, 12, 0), E(Maria, 0, 0)));
        Assert.True(allowed.CanClose);
        Assert.Equal(0, allowed.Lines[1].Accrued);

        var withMinus = Calculate(CleanShift(E(Ivan, 12, 0), E(Maria, 0, 100)) with
        {
            FactCash = 7900,
            ShortageReason = "причина",
        });
        Assert.Contains(withMinus.Problems, problem => problem.Code == ShiftProblemCode.MinusExceedsPay);
    }

    [Fact]
    public void Shortage_requires_a_reason()
    {
        var input = CleanShift(E(Ivan, 12, 500)) with { FactCash = 7500 };

        Assert.Equal(ShiftProblemCode.ShortageReasonRequired, Assert.Single(Calculate(input).Problems).Code);
        Assert.Equal(ShiftProblemCode.ShortageReasonRequired, Assert.Single(Calculate(input with { ShortageReason = "  " }).Problems).Code);
        Assert.True(Calculate(input with { ShortageReason = "ошиблись со сдачей" }).CanClose);
    }

    [Fact]
    public void Safe_shortage_alone_also_requires_a_reason()
    {
        var input = CleanShift(E(Ivan, 12, 200)) with { FactSafe = 49800 };

        Assert.Equal(ShiftProblemCode.ShortageReasonRequired, Assert.Single(Calculate(input).Problems).Code);
    }

    [Fact]
    public void Limits_on_employees_and_hours_are_enforced()
    {
        var noEmployees = Calculate(CleanShift() with { Employees = [] });
        Assert.Equal(ShiftProblemCode.NoEmployees, Assert.Single(noEmployees.Problems).Code);

        var fourEmployees = Calculate(CleanShift(E(Ivan, 3, 0), E(Maria, 3, 0), E(Oleg, 3, 0), E(Nina, 3, 0)));
        Assert.Equal(ShiftProblemCode.TooManyEmployees, Assert.Single(fourEmployees.Problems).Code);

        var thirteenHours = Calculate(CleanShift(E(Ivan, 7, 0), E(Maria, 6, 0)));
        Assert.Equal(ShiftProblemCode.TooManyHours, Assert.Single(thirteenHours.Problems).Code);

        var exactlyTwelve = Calculate(CleanShift(E(Ivan, 4, 0), E(Maria, 4, 0), E(Oleg, 4, 0)));
        Assert.True(exactlyTwelve.CanClose);
    }

    [Fact]
    public void Bad_employee_rows_are_reported_per_employee()
    {
        var unknownId = Guid.CreateVersion7();

        var result = Calculate(CleanShift(
            E(Ivan, 4, 0),
            E(Ivan, 4, 0),
            E(unknownId, 1, 0),
            E(Fired, 1, 0)) with
        {
            Employees = [E(Ivan, 4, 0), E(Ivan, 4, 0), E(unknownId, 1, 0)],
        });

        Assert.Contains(result.Problems, problem => problem is { Code: ShiftProblemCode.DuplicateEmployee } && problem.EmployeeId == Ivan.Id);
        Assert.Contains(result.Problems, problem => problem is { Code: ShiftProblemCode.UnknownEmployee } && problem.EmployeeId == unknownId);
        Assert.Single(result.Lines);

        var inactive = Calculate(CleanShift(E(Fired, 4, 0)));
        Assert.Equal(ShiftProblemCode.InactiveEmployee, Assert.Single(inactive.Problems).Code);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(13, 0)]
    public void Hours_out_of_range_are_rejected(int hours, int minus)
    {
        // Отрицательные часы в старой версии превращались в штраф и обходили лимит (−5 + 17 = 12).
        var result = Calculate(CleanShift(E(Ivan, hours, minus)));

        Assert.Contains(result.Problems, problem => problem.Code == ShiftProblemCode.HoursOutOfRange);
    }

    [Fact]
    public void Negative_and_huge_amounts_do_not_break_the_preview()
    {
        var input = CleanShift(E(Ivan, 12, -5)) with
        {
            FactCash = -1,
            FactNonCash = int.MaxValue,
            LangameCash = ShiftCalculator.MaxAmount + 1,
        };

        var result = Calculate(input);

        Assert.Equal(3, result.Problems.Count(problem => problem.Code == ShiftProblemCode.AmountOutOfRange));
        Assert.Contains(result.Problems, problem => problem.Code == ShiftProblemCode.MinusOutOfRange);
        Assert.False(result.CanClose);
    }

    [Fact]
    public void Cash_below_float_is_refilled_from_the_safe()
    {
        // Все платили картой, в кассе осталось 800 из 1000 размена.
        var input = CleanShift() with { FactCash = 800, FactNonCash = 19200 };

        var result = Calculate(input);

        Assert.True(result.CanClose);
        Assert.Equal(-200, result.CashToSafe);
        Assert.Equal(19000, result.Revenue);
        Assert.Equal(49800, result.SafeAfter);
    }

    [Fact]
    public void Float_from_settings_is_used()
    {
        var rules = new ShiftRules(cashFloat: 2000, maxEmployees: 3, maxTotalHours: 12, minPhotos: 3, photoMaxAgeHours: 5);

        var result = ShiftCalculator.Calculate(CleanShift(), rules, expectedSafe: 50000, photoCount: 3, Employees);

        Assert.Equal(6000, result.CashToSafe);
        Assert.Equal(18000, result.Revenue);
        Assert.Equal(2000, result.CashFloat);
    }
}
