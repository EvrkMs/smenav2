using Smena.Core.Accounting;

namespace Smena.Core.Tests.Accounting;

public sealed class OperationDraftTests
{
    private static readonly Actor Root = new(ActorKind.Root, "root");

    [Fact]
    public void Stores_moment_in_utc()
    {
        var moscowEvening = new DateTimeOffset(2026, 11, 10, 21, 30, 0, TimeSpan.FromHours(3));

        var draft = new OperationDraft(OperationType.SafeIncome, moscowEvening, Root, []);

        Assert.Equal(TimeSpan.Zero, draft.OccurredAt.Offset);
        Assert.Equal(18, draft.OccurredAt.Hour);
        Assert.Equal(moscowEvening, draft.OccurredAt);
    }

    [Fact]
    public void Allows_operation_without_money_movement()
    {
        var draft = new OperationDraft(OperationType.ShiftClose, DateTimeOffset.UtcNow, Root, []);

        Assert.Empty(draft.Transactions);
    }

    [Fact]
    public void Rejects_unknown_type()
    {
        Assert.Throws<LedgerRuleException>(() =>
            new OperationDraft((OperationType)999, DateTimeOffset.UtcNow, Root, []));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Rejects_actor_without_name(string name)
    {
        Assert.Throws<LedgerRuleException>(() =>
            new OperationDraft(OperationType.Fine, DateTimeOffset.UtcNow, new Actor(ActorKind.Root, name), []));
    }

    [Fact]
    public void Rejects_default_actor()
    {
        Assert.Throws<LedgerRuleException>(() =>
            new OperationDraft(OperationType.Fine, DateTimeOffset.UtcNow, default, []));
    }

    [Fact]
    public void Rejects_too_long_comment()
    {
        var comment = new string('я', OperationDraft.MaxCommentLength + 1);

        Assert.Throws<LedgerRuleException>(() =>
            new OperationDraft(OperationType.Fine, DateTimeOffset.UtcNow, Root, [], comment));
    }

    [Fact]
    public void Rejects_empty_idempotency_key()
    {
        Assert.Throws<LedgerRuleException>(() =>
            new OperationDraft(OperationType.Fine, DateTimeOffset.UtcNow, Root, [], idempotencyKey: Guid.Empty));
    }
}
