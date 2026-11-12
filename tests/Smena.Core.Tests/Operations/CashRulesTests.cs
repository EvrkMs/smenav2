using Smena.Core.Accounting;
using Smena.Core.Banking;
using Smena.Core.Operations;

namespace Smena.Core.Tests.Operations;

public sealed class CashRulesTests
{
    [Theory]
    [InlineData(1000, 2400, 50000)]   // обычный аванс
    [InlineData(2400, 2400, 50000)]   // вся ЗП
    [InlineData(2400, 2400, 2400)]    // весь сейф
    public void Payout_within_balance_and_safe_is_allowed(int amount, int employeeBalance, int safeBalance)
    {
        Assert.Null(CashRules.CheckPayout(amount, employeeBalance, safeBalance));
    }

    [Theory]
    [InlineData(0, 2400, 50000, "от 1")]
    [InlineData(-5, 2400, 50000, "от 1")]
    [InlineData(100, 0, 50000, "нет ЗП")]
    [InlineData(100, -300, 50000, "нет ЗП")]            // сотрудник в минусе
    [InlineData(2401, 2400, 50000, "больше ЗП")]
    [InlineData(2000, 2400, 1500, "В сейфе")]
    public void Payout_is_rejected(int amount, int employeeBalance, int safeBalance, string expected)
    {
        Assert.Contains(expected, CashRules.CheckPayout(amount, employeeBalance, safeBalance), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1000, 3, 334)]     // 1002 на троих
    [InlineData(999, 3, 333)]      // делится нацело — без округления
    [InlineData(1000, 1, 1000)]
    [InlineData(1, 3, 1)]
    [InlineData(500, 2, 250)]
    public void Inventory_is_split_evenly_rounding_the_total_up(int total, int employees, int share)
    {
        Assert.Equal(share, CashRules.InventoryShare(total, employees));
        Assert.True(share * employees >= total);
        Assert.True((share * employees) - total < employees);
    }

    [Fact]
    public void Root_payout_may_exceed_balance_only_when_asked_explicitly()
    {
        Assert.NotNull(CashRules.CheckRootPayout(3000, employeeBalance: 2400, allowOverdraft: false, fromSafe: false, safeBalance: 0));
        Assert.Null(CashRules.CheckRootPayout(3000, employeeBalance: 2400, allowOverdraft: true, fromSafe: false, safeBalance: 0));
        Assert.Null(CashRules.CheckRootPayout(3000, employeeBalance: -500, allowOverdraft: true, fromSafe: true, safeBalance: 3000));

        // Сейф в минус не уходит даже у root.
        Assert.NotNull(CashRules.CheckRootPayout(3000, employeeBalance: 5000, allowOverdraft: true, fromSafe: true, safeBalance: 2999));
        Assert.NotNull(CashRules.CheckRootPayout(0, employeeBalance: 5000, allowOverdraft: true, fromSafe: false, safeBalance: 0));
    }

    [Fact]
    public void Safe_expense_cannot_exceed_the_safe()
    {
        Assert.Null(CashRules.CheckSafeExpense(500, 500));
        Assert.NotNull(CashRules.CheckSafeExpense(501, 500));
        Assert.NotNull(CashRules.CheckSafeExpense(1, -100));
        Assert.NotNull(CashRules.CheckSafeExpense(0, 500));
    }

    [Fact]
    public void Comment_is_checked_for_presence_and_length()
    {
        Assert.Null(CashRules.CheckComment(null, required: false));
        Assert.NotNull(CashRules.CheckComment("  ", required: true));
        Assert.Null(CashRules.CheckComment("вода", required: true));
        Assert.NotNull(CashRules.CheckComment(new string('я', CashRules.MaxCommentLength + 1), required: false));
    }

    [Theory]
    [InlineData(12000, "2", 11760)]
    [InlineData(12000, "0", 12000)]
    [InlineData(999, "1.5", 984)]      // комиссия 14,985 → 15
    [InlineData(100, "0.5", 99)]       // комиссия 0,5 → 1 (округление от нуля)
    [InlineData(1, "100", 0)]
    public void Expected_amount_subtracts_rounded_fee(int amount, string feePercent, int expected)
    {
        var fee = decimal.Parse(feePercent, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, BankReceiptMath.ExpectedAmount(amount, fee));
    }

    [Fact]
    public void Channel_without_terms_gives_no_expectation()
    {
        Assert.Null(BankReceiptMath.ExpectedAmount(12000, feePercent: null));
        Assert.Null(BankReceiptMath.ExpectedAt(DateTimeOffset.UtcNow, delayDays: null));
    }

    [Fact]
    public void Expected_date_adds_delay_in_utc()
    {
        var sent = new DateTimeOffset(2026, 11, 10, 23, 30, 0, TimeSpan.FromHours(3));

        var expected = BankReceiptMath.ExpectedAt(sent, delayDays: 2);

        Assert.Equal(new DateTimeOffset(2026, 11, 12, 20, 30, 0, TimeSpan.Zero), expected);
    }

    [Fact]
    public void Confirmed_amount_must_be_between_zero_and_sent()
    {
        Assert.Null(BankReceiptMath.CheckConfirmedAmount(12000, 11748));
        Assert.Null(BankReceiptMath.CheckConfirmedAmount(12000, 12000));
        Assert.Null(BankReceiptMath.CheckConfirmedAmount(12000, 0));
        Assert.NotNull(BankReceiptMath.CheckConfirmedAmount(12000, 12001));
        Assert.NotNull(BankReceiptMath.CheckConfirmedAmount(12000, -1));
    }

    public static TheoryData<Leg, AccountRef, Direction> BalancingCases => new()
    {
        // Расход сейфа: инкассация и покупка.
        { Leg.Decrease(Safe), Bank, Direction.Increase },
        { Leg.Decrease(Safe), Expense, Direction.Increase },
        // Приход сейфа: снятие с банка и прочее поступление.
        { Leg.Increase(Safe), Bank, Direction.Decrease },
        { Leg.Increase(Safe), Revenue, Direction.Increase },
        // Выплата сотруднику.
        { Leg.Decrease(Safe), Employee, Direction.Decrease },
    };

    private static readonly AccountRef Safe = new(1, AccountKind.Safe);
    private static readonly AccountRef Bank = new(2, AccountKind.Bank);
    private static readonly AccountRef Expense = new(3, AccountKind.Expense);
    private static readonly AccountRef Revenue = new(4, AccountKind.Revenue);
    private static readonly AccountRef Employee = new(100, AccountKind.Employee);

    [Theory]
    [MemberData(nameof(BalancingCases))]
    public void Balancing_leg_always_makes_a_valid_transaction(Leg primary, AccountRef counter, Direction expected)
    {
        var balancing = Leg.Balancing(primary, counter);

        Assert.Equal(expected, balancing.Direction);
        Assert.Equal(counter, balancing.Account);
        Assert.NotNull(TransactionDraft.Create(100, primary, balancing));
    }
}
