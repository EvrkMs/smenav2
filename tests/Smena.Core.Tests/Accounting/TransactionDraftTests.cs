using Smena.Core.Accounting;

namespace Smena.Core.Tests.Accounting;

public sealed class TransactionDraftTests
{
    private static readonly AccountRef Safe = new(1, AccountKind.Safe);
    private static readonly AccountRef Bank = new(2, AccountKind.Bank);
    private static readonly AccountRef Expense = new(3, AccountKind.Expense);
    private static readonly AccountRef Revenue = new(4, AccountKind.Revenue);
    private static readonly AccountRef Opening = new(5, AccountKind.Opening);
    private static readonly AccountRef Employee = new(100, AccountKind.Employee);

    public static TheoryData<string, Leg, Leg> BalancedPairs => new()
    {
        // Операции из docs/PLAN.md, раздел 3.
        { "Смена: наличка", Leg.Increase(Safe), Leg.Increase(Revenue) },
        { "Смена: безнал", Leg.Increase(Bank), Leg.Increase(Revenue) },
        { "Смена: начисление ЗП", Leg.Increase(Expense), Leg.Increase(Employee) },
        { "Смена: недостача кассы", Leg.Decrease(Employee), Leg.Increase(Revenue) },
        { "Смена: недостача сейфа", Leg.Decrease(Safe), Leg.Decrease(Employee) },
        { "Смена: излишек сейфа", Leg.Increase(Safe), Leg.Increase(Revenue) },
        { "Выплата из сейфа", Leg.Decrease(Safe), Leg.Decrease(Employee) },
        { "Выплата с банка", Leg.Decrease(Bank), Leg.Decrease(Employee) },
        { "Расход из сейфа", Leg.Decrease(Safe), Leg.Increase(Expense) },
        { "Расход с банка", Leg.Decrease(Bank), Leg.Increase(Expense) },
        { "Снято с банка в сейф", Leg.Decrease(Bank), Leg.Increase(Safe) },
        { "Инкассация", Leg.Decrease(Safe), Leg.Increase(Bank) },
        { "Комиссия банка", Leg.Decrease(Bank), Leg.Increase(Expense) },
        { "Штраф", Leg.Decrease(Employee), Leg.Increase(Revenue) },
        { "Премия", Leg.Increase(Expense), Leg.Increase(Employee) },
        { "Стартовый остаток сейфа", Leg.Increase(Safe), Leg.Increase(Opening) },
        { "Стартовый долг клуба сотруднику", Leg.Increase(Employee), Leg.Decrease(Opening) },
    };

    public static TheoryData<string, Leg, Leg> UnbalancedPairs => new()
    {
        { "Деньги из ниоткуда в два места", Leg.Increase(Safe), Leg.Increase(Bank) },
        { "Деньги пропали из двух мест", Leg.Decrease(Safe), Leg.Decrease(Bank) },
        { "Сейф вырос, выручка упала", Leg.Increase(Safe), Leg.Decrease(Revenue) },
        { "Сейф упал, сотруднику должны больше", Leg.Decrease(Safe), Leg.Increase(Employee) },
        { "Должны больше и сотруднику, и выручке", Leg.Increase(Employee), Leg.Increase(Revenue) },
        { "Расход без источника", Leg.Increase(Expense), Leg.Decrease(Employee) },
    };

    [Theory]
    [MemberData(nameof(BalancedPairs))]
    public void Accepts_every_agreed_operation(string name, Leg first, Leg second)
    {
        var draft = TransactionDraft.Create(500, first, second, name);

        Assert.Equal(500, draft.Amount);
        Assert.Equal(first, draft.First);
        Assert.Equal(second, draft.Second);
        Assert.Equal(name, draft.Note);

        // Порядок сторон на результат не влияет.
        Assert.True(TransactionDraft.IsBalanced(second, first));
    }

    [Theory]
    [MemberData(nameof(UnbalancedPairs))]
    public void Rejects_unbalanced_pairs(string name, Leg first, Leg second)
    {
        var error = Assert.Throws<LedgerRuleException>(() => TransactionDraft.Create(500, first, second, name));

        Assert.Contains("не сбалансирована", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_non_positive_amount(int amount)
    {
        Assert.Throws<LedgerRuleException>(() =>
            TransactionDraft.Create(amount, Leg.Increase(Safe), Leg.Increase(Revenue)));
    }

    [Fact]
    public void Rejects_same_account_on_both_sides()
    {
        Assert.Throws<LedgerRuleException>(() =>
            TransactionDraft.Create(100, Leg.Increase(Safe), Leg.Decrease(Safe)));
    }

    [Fact]
    public void Rejects_leg_without_direction()
    {
        // default(Leg) — то, что получится, если сторону забыли заполнить.
        Assert.Throws<LedgerRuleException>(() =>
            TransactionDraft.Create(100, default, Leg.Increase(Revenue)));
    }

    [Fact]
    public void Accepts_category_of_matching_kind_and_direction()
    {
        var incassation = new CategoryRef(10, CategoryKind.SafeExpense);
        var bankIncome = new CategoryRef(11, CategoryKind.BankIncome);

        var draft = TransactionDraft.Create(
            5000,
            Leg.Decrease(Safe, incassation),
            Leg.Increase(Bank, bankIncome));

        Assert.Equal(incassation, draft.First.Category);
        Assert.Equal(bankIncome, draft.Second.Category);
    }

    [Fact]
    public void Rejects_category_of_another_account_kind()
    {
        var revenueCategory = new CategoryRef(10, CategoryKind.Revenue);

        Assert.Throws<LedgerRuleException>(() =>
            TransactionDraft.Create(100, Leg.Increase(Safe, revenueCategory), Leg.Increase(Revenue)));
    }

    [Fact]
    public void Rejects_income_category_on_decreasing_leg()
    {
        var safeIncome = new CategoryRef(10, CategoryKind.SafeIncome);

        Assert.Throws<LedgerRuleException>(() =>
            TransactionDraft.Create(100, Leg.Decrease(Safe, safeIncome), Leg.Increase(Expense)));
    }
}
