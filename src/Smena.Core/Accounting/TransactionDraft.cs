namespace Smena.Core.Accounting;

/// <summary>
/// Транзакция до записи в базу: одно движение одной суммы между двумя счетами.
/// Экземпляр можно получить только через <see cref="Create"/>, поэтому
/// несбалансированной транзакции не существует в принципе.
/// </summary>
/// <remarks>
/// Класс, а не структура: у структуры есть <c>default</c> с нулевой суммой,
/// который обошёл бы проверки.
/// </remarks>
public sealed class TransactionDraft
{
    private TransactionDraft(int amount, in Leg first, in Leg second, string? note)
    {
        Amount = amount;
        First = first;
        Second = second;
        Note = note;
    }

    /// <summary>Сумма в рублях, всегда больше нуля. Одна на обе стороны.</summary>
    public int Amount { get; }

    public Leg First { get; }

    public Leg Second { get; }

    /// <summary>Подпись строки внутри операции, например «Наличка в сейф».</summary>
    public string? Note { get; }

    /// <exception cref="LedgerRuleException">Транзакция нарушает правила учёта.</exception>
    public static TransactionDraft Create(int amount, in Leg first, in Leg second, string? note = null)
    {
        if (amount <= 0)
        {
            throw new LedgerRuleException($"Сумма транзакции должна быть больше нуля, получено {amount}.");
        }

        ValidateLeg(first);
        ValidateLeg(second);

        if (first.Account.Id == second.Account.Id)
        {
            throw new LedgerRuleException("Обе стороны транзакции указывают на один и тот же счёт.");
        }

        if (!IsBalanced(first, second))
        {
            throw new LedgerRuleException(
                $"Транзакция не сбалансирована: {Describe(first)} и {Describe(second)}.");
        }

        return new TransactionDraft(amount, first, second, note);
    }

    /// <summary>
    /// Правило двойной записи для двух сторон с одной суммой.
    /// Счета одной стороны («где деньги» или «чьи деньги») меняются в разные
    /// стороны — это перемещение. Счета разных сторон меняются в одну сторону —
    /// деньги появились или ушли вместе с тем, чьи они.
    /// </summary>
    public static bool IsBalanced(in Leg first, in Leg second)
    {
        var sameSide = first.Account.Kind.Side() == second.Account.Kind.Side();
        var sameDirection = first.Direction == second.Direction;
        return sameSide != sameDirection;
    }

    private static void ValidateLeg(in Leg leg)
    {
        if (leg.Direction is not (Direction.Increase or Direction.Decrease))
        {
            throw new LedgerRuleException($"У стороны транзакции не задано направление (счёт {leg.Account.Id}).");
        }

        if (!Enum.IsDefined(leg.Account.Kind))
        {
            throw new LedgerRuleException($"У счёта {leg.Account.Id} неизвестный вид.");
        }

        if (leg.Category is not { } category)
        {
            return;
        }

        if (category.Kind.Account() != leg.Account.Kind)
        {
            throw new LedgerRuleException(
                $"Категория {category.Id} ({category.Kind}) не подходит счёту вида {leg.Account.Kind}.");
        }

        if (category.Kind.RequiredDirection() is { } required && required != leg.Direction)
        {
            throw new LedgerRuleException(
                $"Категория {category.Id} ({category.Kind}) требует направление {required}, указано {leg.Direction}.");
        }
    }

    private static string Describe(in Leg leg) =>
        $"{leg.Account.Kind} {(leg.Direction == Direction.Increase ? "+" : "−")}";
}
