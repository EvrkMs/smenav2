using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Smena.Server.Accounting;
using Smena.Server.Shifts;

namespace Smena.Server.Notifications;

/// <summary>Идентификаторы строк справочника outbox_statuses.</summary>
public enum OutboxStatus : short
{
    /// <summary>Ждёт отправки (в том числе после временного сбоя).</summary>
    Pending = 1,

    Sent = 2,

    /// <summary>Telegram отказал окончательно: повторять бессмысленно.</summary>
    Failed = 3,
}

public sealed class OutboxStatusRow
{
    public OutboxStatus Id { get; init; }

    public required string Code { get; init; }

    public required string Name { get; init; }
}

/// <summary>Куда отправить сообщение: чат и, если это форум, тема в нём.</summary>
public readonly record struct TelegramRoute(long ChatId, int? ThreadId);

/// <summary>
/// Сообщение для Telegram, ожидающее отправки. Создаётся в той же транзакции
/// базы, что и сама операция: либо есть и операция, и сообщение, либо нет ничего.
/// Отправляет его отдельный фоновый процесс, поэтому недоступность Telegram
/// не мешает закрыть смену.
/// </summary>
public sealed class OutboxMessage
{
    public const int TextLength = 4096; // предел Telegram для одного сообщения
    public const int CaptionLength = 1024; // предел Telegram для подписи к фото
    public const int ErrorLength = 500;

    private OutboxMessage()
    {
    }

    /// <summary>
    /// Первичный ключ и одновременно порядок создания: сообщения уходят строго по нему.
    /// Выдаётся в момент добавления в контекст, поэтому сообщения одной операции
    /// нумеруются в том порядке, в каком были созданы.
    /// </summary>
    public long Sequence { get; private set; }

    /// <summary>Операция, о которой сообщение. Удаление операции удаляет и его.</summary>
    public Guid? OperationId { get; private set; }

    /// <summary>Чат и тема запоминаются при создании: смена настроек старые сообщения не перенаправляет.</summary>
    public long ChatId { get; private set; }

    public int? ThreadId { get; private set; }

    /// <summary>Готовый текст. Отправитель ничего не считает и не форматирует.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>
    /// Если задано, сообщение — альбом с фото этой смены, а <see cref="Text"/> — подпись к нему.
    /// Удаление смены удаляет и сообщение.
    /// </summary>
    public Guid? PhotosShiftId { get; private set; }

    /// <summary>
    /// Если задано, сообщение — ответ на это сообщение очереди (например, «Отмена»).
    /// Номер сообщения в Telegram берётся у исходного в момент отправки: исходное
    /// стоит в очереди раньше, поэтому к этому времени оно уже отправлено или отклонено.
    /// Удаление исходного удаляет и ответ.
    /// </summary>
    public long? ReplyToSequence { get; private set; }

    public OutboxStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    public long? TelegramMessageId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    internal static OutboxMessage Create(TelegramRoute route, string text, Guid? operationId, DateTimeOffset now) => new()
    {
        OperationId = operationId,
        ChatId = route.ChatId,
        ThreadId = route.ThreadId,
        Text = text.Length <= TextLength ? text : text[..TextLength],
        Status = OutboxStatus.Pending,
        NextAttemptAt = now.ToUniversalTime(),
        CreatedAt = now.ToUniversalTime(),
    };

    internal static OutboxMessage CreatePhotos(TelegramRoute route, string caption, Guid operationId, Guid shiftId, DateTimeOffset now)
    {
        var message = Create(route, caption, operationId, now);
        message.PhotosShiftId = shiftId;
        return message;
    }

    /// <summary>Ответ на сообщение очереди: в тот же чат и ту же тему.</summary>
    internal static OutboxMessage CreateReply(OutboxMessage original, string text, DateTimeOffset now)
    {
        var message = Create(new TelegramRoute(original.ChatId, original.ThreadId), text, original.OperationId, now);
        message.ReplyToSequence = original.Sequence;
        return message;
    }

    internal void MarkSent(long telegramMessageId, DateTimeOffset now)
    {
        Status = OutboxStatus.Sent;
        Attempts++;
        TelegramMessageId = telegramMessageId;
        SentAt = now.ToUniversalTime();
        LastError = null;
    }

    internal void MarkRetry(string error, DateTimeOffset nextAttemptAt)
    {
        Attempts++;
        NextAttemptAt = nextAttemptAt.ToUniversalTime();
        LastError = Truncate(error);
    }

    internal void MarkFailed(string error)
    {
        Status = OutboxStatus.Failed;
        Attempts++;
        LastError = Truncate(error);
    }

    private static string Truncate(string error) => error.Length <= ErrorLength ? error : error[..ErrorLength];
}

internal sealed class OutboxStatusRowConfiguration : IEntityTypeConfiguration<OutboxStatusRow>
{
    public void Configure(EntityTypeBuilder<OutboxStatusRow> builder)
    {
        builder.ToTable("outbox_statuses");

        builder.HasKey(static row => row.Id);
        builder.Property(static row => row.Id).ValueGeneratedNever();
        builder.Property(static row => row.Code).HasMaxLength(32);
        builder.Property(static row => row.Name).HasMaxLength(64);
        builder.HasIndex(static row => row.Code).IsUnique();

        builder.HasData(
            new OutboxStatusRow { Id = OutboxStatus.Pending, Code = "pending", Name = "Ждёт отправки" },
            new OutboxStatusRow { Id = OutboxStatus.Sent, Code = "sent", Name = "Отправлено" },
            new OutboxStatusRow { Id = OutboxStatus.Failed, Code = "failed", Name = "Отказ Telegram" });
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages", static table =>
        {
            table.HasCheckConstraint(
                "ck_outbox_messages_sent",
                $"(status_id = {(short)OutboxStatus.Sent}) = (sent_at IS NOT NULL)");

            // Ответ — всегда текст: альбом ответом не отправляется.
            table.HasCheckConstraint(
                "ck_outbox_messages_reply_is_text",
                "reply_to_sequence IS NULL OR photos_shift_id IS NULL");
        });

        builder.HasKey(static message => message.Sequence);

        // Номер выдаётся в момент добавления сообщения в контекст (блоками из
        // последовательности базы), а не при вставке: несколько сообщений одной
        // операции сохраняются одним пакетом, и база могла бы пронумеровать их
        // не в том порядке, в каком они были созданы.
        builder.Property(static message => message.Sequence).UseHiLo("outbox_messages_sequence");
        builder.Property(static message => message.Status).HasColumnName("status_id");
        builder.Property(static message => message.Text).HasMaxLength(OutboxMessage.TextLength);
        builder.Property(static message => message.LastError).HasMaxLength(OutboxMessage.ErrorLength);

        // Отправитель выбирает только ожидающие — частичный индекс остаётся маленьким,
        // сколько бы отправленных сообщений ни накопилось.
        builder.HasIndex(static message => message.Sequence)
            .HasFilter($"status_id = {(short)OutboxStatus.Pending}")
            .HasDatabaseName("ix_outbox_messages_pending");

        builder.HasOne<OutboxStatusRow>()
            .WithMany()
            .HasForeignKey(static message => message.Status)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Operation>()
            .WithMany()
            .HasForeignKey(static message => message.OperationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Shift>()
            .WithMany()
            .HasForeignKey(static message => message.PhotosShiftId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<OutboxMessage>()
            .WithMany()
            .HasForeignKey(static message => message.ReplyToSequence)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
