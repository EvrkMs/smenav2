using Smena.Core.Accounting;
using Smena.Core.Shifts;

namespace Smena.Core.Tests.Shifts;

/// <summary>
/// Проводки смены проверяются по их итогу: применяем транзакции к пустым счетам
/// и смотрим, что балансы изменились ровно так, как договорено.
/// </summary>
public sealed class ShiftPostingsTests
{
    private static readonly ShiftRules Rules = new(cashFloat: 1000, maxEmployees: 3, maxTotalHours: 12, minPhotos: 3, photoMaxAgeHours: 5);

    private static readonly ShiftChart Chart = new()
    {
        Safe = new AccountRef(1, AccountKind.Safe),
        Bank = new AccountRef(2, AccountKind.Bank),
        Expense = new AccountRef(3, AccountKind.Expense),
        Revenue = new AccountRef(4, AccountKind.Revenue),
        RevenueShift = new CategoryRef(1, CategoryKind.Revenue),
        RevenueShortageWithheld = new CategoryRef(2, CategoryKind.Revenue),
        RevenueSafeSurplus = new CategoryRef(3, CategoryKind.Revenue),
        ExpensePayroll = new CategoryRef(4, CategoryKind.Expense),
        SafeShiftCash = new CategoryRef(5, CategoryKind.SafeIncome),
        SafeSurplus = new CategoryRef(6, CategoryKind.SafeIncome),
        SafeShortage = new CategoryRef(7, CategoryKind.SafeExpense),
        SafeFloatRefill = new CategoryRef(8, CategoryKind.SafeExpense),
        BankAcquiring = new CategoryRef(9, CategoryKind.BankIncome),
        EmployeeAccrual = new CategoryRef(10, CategoryKind.Employee),
        EmployeeShortage = new CategoryRef(11, CategoryKind.Employee),
    };

    private static readonly ShiftEmployeeInfo Ivan = new(Guid.CreateVersion7(), "Иван", 200, new AccountRef(100, AccountKind.Employee), IsActive: true);
    private static readonly ShiftEmployeeInfo Maria = new(Guid.CreateVersion7(), "Мария", 180, new AccountRef(101, AccountKind.Employee), IsActive: true);

    private static readonly Dictionary<Guid, ShiftEmployeeInfo> Employees =
        new[] { Ivan, Maria }.ToDictionary(employee => employee.Id);

    private static ShiftInput Shift(int factCash, int factSafe, int ivanMinus = 0, int mariaMinus = 0, int factNonCash = 12000) => new()
    {
        FactCash = factCash,
        FactNonCash = factNonCash,
        SkladCash = 2000,
        SkladNonCash = 3000,
        LangameCash = 6000,
        LangameNonCash = 9000,
        FactSafe = factSafe,
        ShortageReason = "причина",
        Employees = [new(Ivan.Id, 8, ivanMinus), new(Maria.Id, 4, mariaMinus)],
    };

    [Fact]
    public void Clean_shift_posts_cash_non_cash_and_payroll()
    {
        var (calculation, balances) = Post(Shift(factCash: 8000, factSafe: 50000));

        Assert.Equal(7000, balances[Chart.Safe]);
        Assert.Equal(12000, balances[Chart.Bank]);
        Assert.Equal(19000, balances[Chart.Revenue]);
        Assert.Equal(2320, balances[Chart.Expense]);
        Assert.Equal(1600, balances[Ivan.Account]);
        Assert.Equal(720, balances[Maria.Account]);
        AssertMatchesCalculation(calculation, balances, expectedSafe: 50000);
    }

    [Fact]
    public void Cash_shortage_is_withheld_from_employees_into_revenue()
    {
        var (calculation, balances) = Post(Shift(factCash: 7500, factSafe: 50000, ivanMinus: 300, mariaMinus: 200));

        Assert.Equal(6500, balances[Chart.Safe]);            // в сейф ушло то, что реально было в кассе
        Assert.Equal(1300, balances[Ivan.Account]);          // 1600 − 300
        Assert.Equal(520, balances[Maria.Account]);          // 720 − 200

        // Фактическая выручка 18500 + удержание 500 = сумма по программам за вычетом размена.
        Assert.Equal(19000, balances[Chart.Revenue]);
        Assert.Equal(calculation.ProgramTotal - Rules.CashFloat, balances[Chart.Revenue]);
        AssertMatchesCalculation(calculation, balances, expectedSafe: 50000);
    }

    [Fact]
    public void Safe_shortage_reduces_safe_and_employee_without_touching_revenue()
    {
        var (calculation, balances) = Post(Shift(factCash: 8000, factSafe: 49700, ivanMinus: 300));

        Assert.Equal(7000 - 300, balances[Chart.Safe]);
        Assert.Equal(1300, balances[Ivan.Account]);
        Assert.Equal(19000, balances[Chart.Revenue]);
        AssertMatchesCalculation(calculation, balances, expectedSafe: 50000);
    }

    [Fact]
    public void Safe_surplus_goes_to_safe_and_revenue()
    {
        var (calculation, balances) = Post(Shift(factCash: 8000, factSafe: 50250));

        Assert.Equal(7250, balances[Chart.Safe]);
        Assert.Equal(19250, balances[Chart.Revenue]);
        AssertMatchesCalculation(calculation, balances, expectedSafe: 50000);
    }

    [Fact]
    public void Both_shortages_at_once_are_split_between_cash_and_safe()
    {
        var (calculation, balances) = Post(Shift(factCash: 7500, factSafe: 49700, ivanMinus: 600, mariaMinus: 200));

        Assert.Equal(6500 - 300, balances[Chart.Safe]);
        Assert.Equal(18500 + 500, balances[Chart.Revenue]);
        Assert.Equal(1000, balances[Ivan.Account]);
        Assert.Equal(520, balances[Maria.Account]);
        AssertMatchesCalculation(calculation, balances, expectedSafe: 50000);
    }

    [Fact]
    public void Cash_below_float_takes_money_from_the_safe()
    {
        var (calculation, balances) = Post(Shift(factCash: 800, factSafe: 50000, factNonCash: 19200));

        Assert.Equal(-200, balances[Chart.Safe]);
        Assert.Equal(19200, balances[Chart.Bank]);
        Assert.Equal(19000, balances[Chart.Revenue]);
        AssertMatchesCalculation(calculation, balances, expectedSafe: 50000);
    }

    [Fact]
    public void Shift_without_money_and_hours_posts_nothing()
    {
        var input = new ShiftInput
        {
            FactCash = 1000,
            FactNonCash = 0,
            SkladCash = 0,
            SkladNonCash = 0,
            LangameCash = 1000,
            LangameNonCash = 0,
            FactSafe = 50000,
            Employees = [new(Ivan.Id, 0, 0)],
        };

        var calculation = ShiftCalculator.Calculate(input, Rules, expectedSafe: 50000, photoCount: 3, Employees);

        Assert.True(calculation.CanClose);
        Assert.Empty(ShiftPostings.Build(calculation, Chart));
    }

    [Fact]
    public void Every_leg_carries_a_category_of_its_account()
    {
        var calculation = ShiftCalculator.Calculate(
            Shift(factCash: 7500, factSafe: 49700, ivanMinus: 600, mariaMinus: 200), Rules, expectedSafe: 50000, photoCount: 3, Employees);

        var transactions = ShiftPostings.Build(calculation, Chart);

        Assert.All(transactions, transaction =>
        {
            Assert.NotNull(transaction.First.Category);
            Assert.NotNull(transaction.Second.Category);
            Assert.False(string.IsNullOrEmpty(transaction.Note));
        });
    }

    [Fact]
    public void Calculation_with_problems_cannot_be_posted()
    {
        var calculation = ShiftCalculator.Calculate(
            Shift(factCash: 7500, factSafe: 50000), Rules, expectedSafe: 50000, photoCount: 3, Employees);

        Assert.False(calculation.CanClose);
        Assert.Throws<InvalidOperationException>(() => ShiftPostings.Build(calculation, Chart));
    }

    /// <summary>Общие инварианты любой закрытой смены.</summary>
    private static void AssertMatchesCalculation(
        ShiftCalculation calculation,
        Dictionary<AccountRef, int> balances,
        int expectedSafe)
    {
        // Сейф после смены — фактический пересчёт плюс наличные из кассы.
        Assert.Equal(calculation.SafeAfter, expectedSafe + balances[Chart.Safe]);

        // Сотрудникам начислено ровно «к выплате».
        var owedToEmployees = balances.Where(pair => pair.Key.Kind == AccountKind.Employee).Sum(pair => pair.Value);
        Assert.Equal(calculation.PayrollNet, owedToEmployees);

        // «Итог» отчёта (выручка − ЗП к выплате) и прибыль по счетам (выручка − расходы)
        // отличаются на недостачу сейфа: удержание за неё уменьшает ЗП к выплате, но
        // в выручку не попадает — оно лишь возмещает деньги, пропавшие из сейфа.
        // Излишек сейфа в «Итог» отчёта не входит.
        var profit = balances[Chart.Revenue] - calculation.SafeSurplus - balances[Chart.Expense];
        Assert.Equal(calculation.Total, profit + calculation.SafeShortage);

        // Двойная запись: «где деньги» = «чьи деньги».
        var assets = balances.Where(pair => pair.Key.Kind.Side() == AccountSide.Asset).Sum(pair => pair.Value);
        var sources = balances.Where(pair => pair.Key.Kind.Side() == AccountSide.Source).Sum(pair => pair.Value);
        Assert.Equal(assets, sources);
    }

    private static (ShiftCalculation Calculation, Dictionary<AccountRef, int> Balances) Post(ShiftInput input)
    {
        var calculation = ShiftCalculator.Calculate(input, Rules, expectedSafe: 50000, photoCount: 3, Employees);
        Assert.Empty(calculation.Problems);

        var balances = new Dictionary<AccountRef, int>
        {
            [Chart.Safe] = 0,
            [Chart.Bank] = 0,
            [Chart.Revenue] = 0,
            [Chart.Expense] = 0,
            [Ivan.Account] = 0,
            [Maria.Account] = 0,
        };

        foreach (var transaction in ShiftPostings.Build(calculation, Chart))
        {
            balances[transaction.First.Account] += transaction.Amount * (int)transaction.First.Direction;
            balances[transaction.Second.Account] += transaction.Amount * (int)transaction.Second.Direction;
        }

        return (calculation, balances);
    }
}
