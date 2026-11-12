namespace Smena.Core.Accounting;

/// <summary>
/// Попытка собрать транзакцию, нарушающую правила учёта. Это ошибка в коде,
/// который собирает проводки, а не ошибка пользователя.
/// </summary>
public sealed class LedgerRuleException : Exception
{
    public LedgerRuleException()
    {
    }

    public LedgerRuleException(string message)
        : base(message)
    {
    }

    public LedgerRuleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
