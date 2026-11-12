using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Smena.Core.Accounting;
using Smena.Core.Shifts;
using Smena.Server.Accounting;
using Smena.Server.Cashier;
using Smena.Server.Database;
using Smena.Server.Employees;
using Smena.Server.Notifications;

namespace Smena.Server.Tests.Notifications;

/// <summary>Сообщения для Telegram: что попадает в очередь и как очередь отправляется.</summary>
public sealed class OutboxTests(SmenaApp app) : IAsyncLifetime
{
    private const long AdminChat = -1001;
    private const long SalaryChat = -1002;
    private const int ReportsThread = 2;
    private const int SafeThread = 3;
    private const int ExpensesThread = 4;
    private const int IvanThread = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Guid _ivan;
    private Guid _maria;

    public async ValueTask InitializeAsync()
    {
        await app.ResetLedgerAsync(minShiftPhotos: 0);

        // У Ивана есть личная тема в зарплатном чате, у Марии — нет.
        (_ivan, _maria) = await app.QueryDatabaseAsync(async db =>
        {
            var ivan = Employee.Create(1001, "Иван", 200);
            ivan.SalaryThreadId = IvanThread;
            var maria = Employee.Create(1002, "Мария", 180);
            db.Employees.AddRange(ivan, maria);
            await db.SaveChangesAsync(Ct);
            return (ivan.Id, maria.Id);
        });

        await app.InScopeAsync(async services =>
        {
            var ledger = services.GetRequiredService<Ledger>();
            await using var transaction = await ledger.BeginAsync(Ct);
            ledger.Post(new OperationDraft(
                OperationType.Opening,
                DateTimeOffset.UtcNow,
                new Actor(ActorKind.Root, "root"),
                [TransactionDraft.Create(50000, Leg.Increase(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Opening))]));
            await services.GetRequiredService<SmenaDbContext>().SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
            return 0;
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Nothing_is_queued_while_telegram_chats_are_not_set()
    {
        using var client = app.CreateCashierClient();

        using var response = await CloseShiftAsync(client, ShortageShift());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Closing_a_shift_queues_report_safe_and_salary_messages()
    {
        await ConfigureTelegramAsync();
        using var client = app.CreateCashierClient();

        using var response = await CloseShiftAsync(client, ShortageShift());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var queue = await QueueAsync();

        // Отчёт, сейф и личная тема Ивана. У Марии темы нет — ей сообщения нет.
        Assert.Equal(
            [(AdminChat, ReportsThread), (AdminChat, SafeThread), (SalaryChat, IvanThread)],
            queue.Select(message => (message.ChatId, message.ThreadId!.Value)));
        Assert.All(queue, message => Assert.Equal(OutboxStatus.Pending, message.Status));
        Assert.Single(queue.Select(message => message.OperationId).Distinct());

        Assert.Contains("Выручка: 18 500", queue[0].Text, StringComparison.Ordinal);
        Assert.Contains("- Иван: 8 ч, минус 300, ЗП 1 300", queue[0].Text, StringComparison.Ordinal);
        Assert.Contains("Причина недостачи: не сошлась касса", queue[0].Text, StringComparison.Ordinal);

        Assert.Equal("+6 500 — Наличка в сейф\nВ сейфе: 56 500", queue[1].Text.ReplaceLineEndings("\n"));

        Assert.Equal(
            "+1 600 — Начисление за смену\n-300 — Удержание за недостачу\nБаланс: 1 300",
            queue[2].Text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Rejected_shift_queues_nothing()
    {
        await ConfigureTelegramAsync();
        using var client = app.CreateCashierClient();

        using var response = await CloseShiftAsync(client, ShortageShift() with { ShortageReason = null });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Payout_and_expense_queue_their_messages()
    {
        await ConfigureTelegramAsync();
        using var client = app.CreateCashierClient();
        using var shift = await CloseShiftAsync(client, ShortageShift());
        await ClearQueueAsync();

        using var payout = await PostAsync(client, "/api/payouts", new PayoutRequest(_ivan, 1000, IsSalary: false, Comment: null));
        using var expense = await PostAsync(
            client, "/api/safe/expense", new SafeMoveRequest(SystemCategories.SafeHousehold.Id, 450, "вода"));
        Assert.Equal(HttpStatusCode.OK, payout.StatusCode);
        Assert.Equal(HttpStatusCode.OK, expense.StatusCode);

        var queue = await QueueAsync();

        Assert.Equal(
            [
                (AdminChat, SafeThread, "-1 000 — Аванс: Иван\nВ сейфе: 55 500"),
                (SalaryChat, IvanThread, "-1 000 — Аванс\nБаланс: 300"),
                (AdminChat, ExpensesThread, "Расход 450 — Хозяйственные\nвода\nПровёл: Касса"),
                (AdminChat, SafeThread, "-450 — Хозяйственные\nВ сейфе: 55 050"),
            ],
            queue.Select(message => (message.ChatId, message.ThreadId!.Value, message.Text.ReplaceLineEndings("\n"))));
    }

    [Fact]
    public async Task Dispatcher_sends_the_queue_in_order_and_marks_messages_sent()
    {
        await ConfigureTelegramAsync();
        using var client = app.CreateCashierClient();
        using var shift = await CloseShiftAsync(client, ShortageShift());

        var sent = await DispatchAsync();

        Assert.Equal(3, sent);
        Assert.Equal(
            [(AdminChat, ReportsThread), (AdminChat, SafeThread), (SalaryChat, IvanThread)],
            app.Telegram.Sent.Select(message => (message.ChatId, message.ThreadId!.Value)));

        var queue = await QueueAsync();
        Assert.All(queue, message =>
        {
            Assert.Equal(OutboxStatus.Sent, message.Status);
            Assert.NotNull(message.TelegramMessageId);
            Assert.NotNull(message.SentAt);
            Assert.Equal(1, message.Attempts);
        });

        // Повторный проход ничего не шлёт заново.
        Assert.Equal(0, await DispatchAsync());
        Assert.Equal(3, app.Telegram.Attempts);
    }

    [Fact]
    public async Task Unreachable_telegram_keeps_messages_waiting_and_in_order()
    {
        await ConfigureTelegramAsync();
        using var client = app.CreateCashierClient();

        // Смена закрывается, хотя Telegram недоступен: очередь никого не ждёт.
        using var shift = await CloseShiftAsync(client, ShortageShift());
        Assert.Equal(HttpStatusCode.OK, shift.StatusCode);

        app.Telegram.Script(new TelegramSendResult(TelegramOutcome.Transient, Error: "Нет связи с Telegram: proxy down"));
        Assert.Equal(0, await DispatchAsync());

        var queue = await QueueAsync();
        Assert.Equal((OutboxStatus.Pending, 1), (queue[0].Status, queue[0].Attempts));
        Assert.Contains("proxy down", queue[0].LastError, StringComparison.Ordinal);
        Assert.True(queue[0].NextAttemptAt > DateTimeOffset.UtcNow);
        Assert.Equal(0, queue[1].Attempts);                      // остальные даже не пробовали

        // Пока первое сообщение ждёт повтора, следующие его не обгоняют.
        Assert.Equal(0, await DispatchAsync());
        Assert.Equal(1, app.Telegram.Attempts);

        // Срок повтора настал, связь вернулась — уходит всё и в исходном порядке.
        await app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE outbox_messages SET next_attempt_at = now() - interval '1 second'", Ct));
        Assert.Equal(3, await DispatchAsync());
        Assert.Equal(
            [ReportsThread, SafeThread, IvanThread],
            app.Telegram.Sent.Select(message => message.ThreadId!.Value));
    }

    [Fact]
    public async Task Telegram_rate_limit_delay_is_respected()
    {
        await ConfigureTelegramAsync();
        using var client = app.CreateCashierClient();
        using var shift = await CloseShiftAsync(client, ShortageShift());

        app.Telegram.Script(new TelegramSendResult(
            TelegramOutcome.Transient, Error: "Telegram 429", RetryAfter: TimeSpan.FromMinutes(5)));
        await DispatchAsync();

        var first = (await QueueAsync())[0];
        var wait = first.NextAttemptAt - DateTimeOffset.UtcNow;
        Assert.InRange(wait, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Message_rejected_by_telegram_is_marked_failed_and_does_not_block_others()
    {
        await ConfigureTelegramAsync();
        using var client = app.CreateCashierClient();
        using var shift = await CloseShiftAsync(client, ShortageShift());

        app.Telegram.Script(new TelegramSendResult(TelegramOutcome.Permanent, Error: "Telegram 400: message thread not found"));
        var sent = await DispatchAsync();

        var queue = await QueueAsync();
        Assert.Equal(2, sent);
        Assert.Equal(OutboxStatus.Failed, queue[0].Status);
        Assert.Contains("thread not found", queue[0].LastError, StringComparison.Ordinal);
        Assert.Equal([OutboxStatus.Sent, OutboxStatus.Sent], queue.Skip(1).Select(message => message.Status));

        // Отклонённое сообщение больше не отправляется.
        Assert.Equal(0, await DispatchAsync());
    }

    [Fact]
    public async Task Deleting_the_operation_by_hand_removes_its_queued_messages()
    {
        await ConfigureTelegramAsync();
        using var client = app.CreateCashierClient();
        using var shift = await CloseShiftAsync(client, ShortageShift());
        Assert.Equal(3, (await QueueAsync()).Count);

        await app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("DELETE FROM operations WHERE type_id = 1", Ct));

        Assert.Empty(await QueueAsync());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ShiftInput ShortageShift() => new()
    {
        FactCash = 7500,
        FactNonCash = 12000,
        SkladCash = 2000,
        SkladNonCash = 3000,
        LangameCash = 6000,
        LangameNonCash = 9000,
        FactSafe = 50000,
        ShortageReason = "не сошлась касса",
        Employees = [new(_ivan, 8, 300), new(_maria, 4, 200)],
    };

    private Task<int> ConfigureTelegramAsync() => app.QueryDatabaseAsync(db => db.Database.ExecuteSqlAsync(
        $"""
         UPDATE club_settings
            SET telegram_chat_id = {AdminChat}, telegram_reports_thread_id = {ReportsThread},
                telegram_safe_thread_id = {SafeThread}, telegram_expenses_thread_id = {ExpensesThread},
                telegram_salary_chat_id = {SalaryChat}
         """,
        Ct));

    private Task<List<OutboxMessage>> QueueAsync() => app.QueryDatabaseAsync(db => db.OutboxMessages
        .AsNoTracking()
        .OrderBy(message => message.Sequence)
        .ToListAsync(Ct));

    private Task<int> ClearQueueAsync() => app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("DELETE FROM outbox_messages", Ct));

    private Task<int> DispatchAsync() => app.InScopeAsync(services =>
        services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync(Ct));

    private static Task<HttpResponseMessage> CloseShiftAsync(HttpClient client, ShiftInput input) =>
        PostAsync(client, "/api/shifts", input);

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        return await client.SendAsync(request, Ct);
    }
}
