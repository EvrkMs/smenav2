namespace Smena.Core.Accounting;

// Маленькие неизменяемые значения без собственных инвариантов — структуры:
// живут на стеке, не создают объектов в куче, сравниваются по значению.
// Инварианты проверяет TransactionDraft, когда собирает из них транзакцию.

/// <summary>Ссылка на счёт вместе с его видом: вид нужен для проверки баланса без обращения к базе.</summary>
public readonly record struct AccountRef(int Id, AccountKind Kind);

/// <summary>Ссылка на категорию вместе со справочником, которому она принадлежит.</summary>
public readonly record struct CategoryRef(int Id, CategoryKind Kind);

/// <summary>Сторона транзакции: какой счёт и в какую сторону меняется.</summary>
public readonly record struct Leg(AccountRef Account, Direction Direction, CategoryRef? Category = null)
{
    public static Leg Increase(AccountRef account, CategoryRef? category = null) =>
        new(account, Direction.Increase, category);

    public static Leg Decrease(AccountRef account, CategoryRef? category = null) =>
        new(account, Direction.Decrease, category);

    /// <summary>
    /// Вторая сторона, уравновешивающая первую: направление подбирается по правилу
    /// баланса. Нужна там, где второй счёт задан категорией и заранее неизвестен
    /// (приход и расход сейфа): «Сейф −» даёт «Банк +» для инкассации и «Расходы +»
    /// для покупки, а «Сейф +» даёт «Банк −» для снятия и «Выручка +» для прихода.
    /// </summary>
    public static Leg Balancing(in Leg primary, AccountRef counter, CategoryRef? category = null)
    {
        var sameSide = primary.Account.Kind.Side() == counter.Kind.Side();
        var direction = sameSide
            ? (primary.Direction == Direction.Increase ? Direction.Decrease : Direction.Increase)
            : primary.Direction;

        return new Leg(counter, direction, category);
    }
}

/// <summary>Кто совершил операцию.</summary>
public readonly record struct Actor(ActorKind Kind, string Name)
{
    public static Actor System { get; } = new(ActorKind.System, "system");
}
