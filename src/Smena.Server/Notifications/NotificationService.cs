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
            var closedAtLocal = TimeZoneInfo.ConvertTime(operation.OccurredAt, settings.GetTimeZone());

            var stale = 0;
            var pasted = 0;
            foreach (var photo in photos)
            {
                stale += photo.IsStale ? 1 : 0;
                pasted += photo.PastedAtCashier ? 1 : 0;
            }

            Add(reports, NotificationTexts.ShiftReport(input, calculation, closedAtLocal, photos.Count, stale, pasted), operation);

            // Альбом идёт следом за текстом отчёта. В сообщении только ссылка на смену:
            // какие файлы слать, отправитель узнаёт из базы в момент отправки.
            if (photos.Count > 0)
            {
                db.OutboxMessages.Add(OutboxMessage.CreatePhotos(
                    reports, NotificationTexts.ShiftPhotosCaption(closedAtLocal), operation.Id, shiftId, time.GetUtcNow()));
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
                NotificationTexts.Expense(expense.Amount, expense.Note ?? string.Empty, operation.Comment, operation.ActorName),
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
            Add(safeRoute, NotificationTexts.SafeMovements(safeLines, balance), operation);
        }

        if (byEmployeeAccount is not null && settings.TelegramSalaryChatId is { } salaryChatId)
        {
            await EnqueueEmployeeMovementsAsync(operation, byEmployeeAccount, salaryChatId, cancellationToken);
        }
    }

    private async Task EnqueueEmployeeMovementsAsync(
        Operation operation,
        Dictionary<int, List<(int Signed, int? CategoryId, string? Note)>> byEmployeeAccount,
        long salaryChatId,
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
            Add(new TelegramRoute(salaryChatId, thread.ThreadId), NotificationTexts.EmployeeMovements(lines, balance), operation);
        }
    }

    private void Add(TelegramRoute route, string text, Operation operation) =>
        db.OutboxMessages.Add(OutboxMessage.Create(route, text, operation.Id, time.GetUtcNow()));

    private Task<ClubSettings> LoadSettingsAsync(CancellationToken cancellationToken) =>
        db.ClubSettings.AsNoTracking().SingleAsync(cancellationToken);
}
