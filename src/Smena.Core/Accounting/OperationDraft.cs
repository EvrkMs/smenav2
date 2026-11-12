namespace Smena.Core.Accounting;

/// <summary>
/// Операция до записи в базу: что произошло с точки зрения человека
/// (закрытие смены, аванс, расход) и из каких транзакций это состоит.
/// </summary>
public sealed class OperationDraft
{
    public const int MaxCommentLength = 500;

    public OperationDraft(
        OperationType type,
        DateTimeOffset occurredAt,
        Actor actor,
        IReadOnlyList<TransactionDraft> transactions,
        string comment = "",
        Guid? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(comment);

        if (!Enum.IsDefined(type))
        {
            throw new LedgerRuleException($"Неизвестный тип операции: {type}.");
        }

        if (!Enum.IsDefined(actor.Kind) || string.IsNullOrWhiteSpace(actor.Name))
        {
            throw new LedgerRuleException("У операции не указан автор.");
        }

        if (comment.Length > MaxCommentLength)
        {
            throw new LedgerRuleException($"Комментарий длиннее {MaxCommentLength} символов.");
        }

        if (idempotencyKey == Guid.Empty)
        {
            throw new LedgerRuleException("Пустой ключ идемпотентности.");
        }

        Type = type;
        OccurredAt = occurredAt.ToUniversalTime();
        Actor = actor;
        Transactions = transactions;
        Comment = comment;
        IdempotencyKey = idempotencyKey;
    }

    public OperationType Type { get; }

    /// <summary>Момент операции, всегда в UTC.</summary>
    public DateTimeOffset OccurredAt { get; }

    public Actor Actor { get; }

    /// <summary>
    /// Может быть пустым: например, смена без выручки и без часов — операция есть,
    /// движения денег нет.
    /// </summary>
    public IReadOnlyList<TransactionDraft> Transactions { get; }

    public string Comment { get; }

    /// <summary>Ключ запроса клиента: повтор с тем же ключом не создаёт вторую операцию.</summary>
    public Guid? IdempotencyKey { get; }
}
