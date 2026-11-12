namespace Smena.Core.Accounting;

// Значения этих перечислений — идентификаторы строк справочников в базе
// (account_kinds, category_kinds, operation_types, actor_kinds). Менять числа нельзя;
// новое значение добавляется вместе с миграцией, которая создаёт строку справочника.

/// <summary>Сторона счёта для проверки баланса транзакции.</summary>
public enum AccountSide : short
{
    /// <summary>«Где деньги»: сейф, банк, расходы.</summary>
    Asset = 1,

    /// <summary>«Чьи деньги»: сотрудники, выручка, стартовые остатки.</summary>
    Source = 2,
}

public enum AccountKind : short
{
    Safe = 1,
    Bank = 2,
    Expense = 3,
    Employee = 4,
    Revenue = 5,
    Opening = 6,
}

/// <summary>Как проводка меняет баланс своего счёта.</summary>
public enum Direction : short
{
    Decrease = -1,
    Increase = 1,
}

/// <summary>Справочник, которому принадлежит категория. У каждого вида счёта категории свои.</summary>
public enum CategoryKind : short
{
    SafeIncome = 1,
    SafeExpense = 2,
    BankIncome = 3,
    BankExpense = 4,
    Revenue = 5,
    Expense = 6,
    Employee = 7,
}

public enum OperationType : short
{
    ShiftClose = 1,
    Payout = 2,
    SafeIncome = 3,
    SafeExpense = 4,
    BankIncome = 5,
    BankExpense = 6,
    BankReceipt = 7,
    Inventory = 8,
    Fine = 9,
    Bonus = 10,
    Opening = 11,
    BankAdjustment = 12,
}

public enum ActorKind : short
{
    Cashier = 1,
    Root = 2,
    System = 3,
}

public static class KindExtensions
{
    public static AccountSide Side(this AccountKind kind) => kind switch
    {
        AccountKind.Safe or AccountKind.Bank or AccountKind.Expense => AccountSide.Asset,
        AccountKind.Employee or AccountKind.Revenue or AccountKind.Opening => AccountSide.Source,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид счёта."),
    };

    /// <summary>Вид счёта, к проводкам которого применима категория этого справочника.</summary>
    public static AccountKind Account(this CategoryKind kind) => kind switch
    {
        CategoryKind.SafeIncome or CategoryKind.SafeExpense => AccountKind.Safe,
        CategoryKind.BankIncome or CategoryKind.BankExpense => AccountKind.Bank,
        CategoryKind.Revenue => AccountKind.Revenue,
        CategoryKind.Expense => AccountKind.Expense,
        CategoryKind.Employee => AccountKind.Employee,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид категории."),
    };

    /// <summary>
    /// Направление, которое обязана иметь проводка с категорией этого справочника.
    /// <c>null</c> — справочник не привязан к направлению.
    /// </summary>
    public static Direction? RequiredDirection(this CategoryKind kind) => kind switch
    {
        CategoryKind.SafeIncome or CategoryKind.BankIncome => Direction.Increase,
        CategoryKind.SafeExpense or CategoryKind.BankExpense => Direction.Decrease,
        _ => null,
    };
}
