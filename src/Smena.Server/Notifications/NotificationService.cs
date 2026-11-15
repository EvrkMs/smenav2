using Microsoft.EntityFrameworkCore;
using Smena.Core.Accounting;
using Smena.Core.Notifications;
using Smena.Core.Shifts;
using Smena.Server.Accounting;
using Smena.Server.Database;
using Smena.Server.Photos;
using Smena.Server.Settings;

namespace Smena.Server.Notifications;

/// <summary>
/// Ставит сообщения для Telegram в очередь. Вызывается после того, как операция
/// записана (балансы «после» уже видны внутри транзакции), но до фиксации:
/// сообщения попадают в базу той же транзакцией, что и операция.
/// </summary>
/// <remarks>
/// Telegram необязателен: если чат в настройках клуба не задан, сообщения
/// этого вида просто не создаются.
/// </remarks>
public sealed class NotificationService(SmenaDbContext db, Ledger ledger, TimeProvider time)
{
    /// <summary>Отчёт о смене плюс движения по сейфу и сотрудникам.</summary>
    public async Task EnqueueShiftAsync(
        Operation operation,
        Guid shiftId,
        ShiftInput input,
        ShiftCalculation calculation,
        IReadOnlyList<Photo> photos,
        CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        if (!settings.HasTelegram)
        {
            return;
        }

        if (settings.ReportsRoute is { } reports)
        {
            var pasted = 0;
            foreach (var photo in photos)
            {
                pasted += photo.PastedAtCashier ? 1 : 0;
            }

            var report = NotificationTexts.ShiftReport(input, calculation, LocalTime(operation, settings), photos.Count, pasted);

            if (photos.Count == 0)
            {
                Add(reports, report, operation);
            }
            else
            {
                // Фото идут первыми, отчёт — подписью к ним. Подпись в Telegram короче
                // сообщения: длинный отчёт уходит отдельным сообщением сразу за альбомом.
                // В сообщении-альбоме только ссылка на смену: какие файлы слать,
                // отправитель узнаёт из базы в момент отправки.
                var fitsCaption = report.Length <= OutboxMessage.CaptionLength;
                db.OutboxMessages.Add(OutboxMessage.CreatePhotos(
                    reports, fitsCaption ? report : string.Empty, operation.Id, shiftId, time.GetUtcNow()));

                if (!fitsCaption)
                {
                    Add(reports, report, operation);
                }
            }
        }

        await EnqueueMovementsAsync(operation, settings, cancellationToken);
    }

    /// <summary>Движения по сейфу и сотрудникам; для расхода из сейфа — ещё и запись в тему расходов.</summary>
    public async Task EnqueueOperationAsync(Operation operation, CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        if (!settings.HasTelegram)
        {
            return;
        }

        if (operation.Type == OperationType.SafeExpense
            && settings.ExpensesRoute is { } expenses
            && operation.Transactions is [var expense, ..])
        {
            Add(
                expenses,
                NotificationTexts.Expense(
                    expense.Amount, expense.Note ?? string.Empty, operation.Comment, operation.ActorName, LocalTime(operation, settings)),
                operation);
        }

        await EnqueueMovementsAsync(operation, settings, cancellationToken);
    }

    private async Task EnqueueMovementsAsync(Operation operation, ClubSettings settings, CancellationToken cancellationToken)
    {
        List<MoneyLine>? safeLines = null;
        Dictionary<int, List<(int Signed, int? CategoryId, string? Note)>>? byEmployeeAccount = null;

        foreach (var transaction in operation.Transactions)
        {
            foreach (var entry in transaction.Entries)
            {
                var signed = transaction.Amount * (int)entry.Direction;

                if (entry.AccountId == SystemAccounts.Safe.Id)
                {
                    (safeLines ??= []).Add(new MoneyLine(signed, transaction.Note ?? string.Empty));
                }
                else if (entry.AccountId >= SystemAccounts.FirstEmployeeAccountId)
                {
                    byEmployeeAccount ??= [];
                    if (!byEmployeeAccount.TryGetValue(entry.AccountId, out var lines))
                    {
                        byEmployeeAccount[entry.AccountId] = lines = [];
                    }

                    lines.Add((signed, entry.CategoryId, transaction.Note));
                }
            }
        }

        if (safeLines is not null && settings.SafeRoute is { } safeRoute)
        {
            var balance = await ledger.GetBalanceAsync(SystemAccounts.Safe, cancellationToken);
            Add(safeRoute, NotificationTexts.SafeMovements(safeLines, balance, LocalTime(operation, settings)), operation);
        }

        if (byEmployeeAccount is not null && settings.TelegramSalaryChatId is { } salaryChatId)
        {
            await EnqueueEmployeeMovementsAsync(
                operation, byEmployeeAccount, salaryChatId, LocalTime(operation, settings), cancellationToken);
        }
    }

    /// <summary>
    /// Отмена операции: на каждое её сообщение в Telegram — ответ «Отмена» в той же теме.
    /// Ответ ставится и на ещё не отправленные сообщения: очередь идёт по порядку,
    /// поэтому к отправке ответа исходное уже получит номер в Telegram.
    /// </summary>
    public async Task EnqueueCancelAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var originals = await db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.OperationId == operationId
                              && message.ReplyToSequence == null
                              && message.Status != OutboxStatus.Failed)
            .OrderBy(static message => message.Sequence)
            .ToListAsync(cancellationToken);

        var now = time.GetUtcNow();
        foreach (var original in originals)
        {
            // Альбом без подписи всегда идёт вместе с отдельным текстом отчёта:
            // хватит одного ответа — на текст.
            if (original.PhotosShiftId is not null && original.Text.Length == 0)
            {
                continue;
            }

            db.OutboxMessages.Add(OutboxMessage.CreateReply(original, NotificationTexts.Cancelled, now));
        }
    }

    private async Task EnqueueEmployeeMovementsAsync(
        Operation operation,
        Dictionary<int, List<(int Signed, int? CategoryId, string? Note)>> byEmployeeAccount,
        long salaryChatId,
        DateTimeOffset occurredAtLocal,
        CancellationToken cancellationToken)
    {
        var accountIds = byEmployeeAccount.Keys.ToArray();

        // Личная тема есть не у каждого: кому она не задана, тому сообщение не шлём.
        var threads = await db.Employees
            .AsNoTracking()
            .Where(employee => accountIds.Contains(employee.Account.Id) && employee.SalaryThreadId != null)
            .Select(employee => new { AccountId = employee.Account.Id, ThreadId = employee.SalaryThreadId!.Value })
            .ToListAsync(cancellationToken);

        if (threads.Count == 0)
        {
            return;
        }

        var categoryIds = byEmployeeAccount.Values
            .SelectMany(static lines => lines)
            .Select(static line => line.CategoryId)
            .OfType<int>()
            .Distinct()
            .ToArray();

        var categoryNames = await db.Categories
            .AsNoTracking()
            .Where(category => categoryIds.Contains(category.Id))
            .ToDictionaryAsync(static category => category.Id, static category => category.Name, cancellationToken);

        foreach (var thread in threads)
        {
            var source = byEmployeeAccount[thread.AccountId];
            var lines = new MoneyLine[source.Count];
            for (var index = 0; index < lines.Length; index++)
            {
                var (signed, categoryId, note) = source[index];

                // В личной теме имя сотрудника в подписи лишнее — берём название категории.
                var label = categoryId is { } id && categoryNames.TryGetValue(id, out var name) ? name : note ?? string.Empty;
                lines[index] = new MoneyLine(signed, label);
            }

            var balance = await ledger.GetBalanceAsync(new AccountRef(thread.AccountId, AccountKind.Employee), cancellationToken);
            Add(new TelegramRoute(salaryChatId, thread.ThreadId), NotificationTexts.EmployeeMovements(lines, balance, occurredAtLocal), operation);
        }
    }

    private static DateTimeOffset LocalTime(Operation operation, ClubSettings settings) =>
        TimeZoneInfo.ConvertTime(operation.OccurredAt, settings.GetTimeZone());

    private void Add(TelegramRoute route, string text, Operation operation) =>
        db.OutboxMessages.Add(OutboxMessage.Create(route, text, operation.Id, time.GetUtcNow()));

    private Task<ClubSettings> LoadSettingsAsync(CancellationToken cancellationToken) =>
        db.ClubSettings.AsNoTracking().SingleAsync(cancellationToken);
}
