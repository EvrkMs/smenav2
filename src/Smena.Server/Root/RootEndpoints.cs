using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Smena.Core.Accounting;
using Smena.Server.Accounting;
using Smena.Server.Banking;
using Smena.Server.Cashier;
using Smena.Server.Database;
using Smena.Server.Employees;
using Smena.Server.Notifications;
using Smena.Server.Settings;

namespace Smena.Server.Root;

/// <summary>
/// API панели: вход, сводка, сотрудники, устройства кассы, настройки.
/// Денежная часть — в <see cref="RootMoneyEndpoints"/>.
/// </summary>
internal static class RootEndpoints
{
    public static IEndpointRouteBuilder MapRootApi(this IEndpointRouteBuilder routes)
    {
        // Вход — единственный адрес панели, доступный без cookie.
        routes.MapPost("/api/root/login", LoginAsync);

        var api = routes.MapGroup("/api/root").RequireAuthorization(RootSetup.RootPolicy);

        // Приведение к Delegate: иначе метод с единственным параметром HttpContext
        // считается «сырым» обработчиком, и его результат отбрасывается.
        api.MapPost("/logout", (Delegate)LogoutAsync);
        api.MapGet("/me", static (ClaimsPrincipal user) => TypedResults.Ok(new MeResponse(user.Identity?.Name ?? string.Empty)));
        api.MapPost("/password", ChangePasswordAsync);
        api.MapGet("/overview", GetOverviewAsync);

        api.MapGet("/employees", GetEmployeesAsync);
        api.MapPost("/employees", CreateEmployeeAsync);
        api.MapPut("/employees/{id:guid}", UpdateEmployeeAsync);

        api.MapGet("/devices", GetDevicesAsync);
        api.MapPost("/devices", CreateDeviceAsync);
        api.MapPost("/devices/{id:guid}/revoke", RevokeDeviceAsync);

        api.MapGet("/settings", GetSettingsAsync);
        api.MapPut("/settings", UpdateSettingsAsync);

        api.MapRootMoneyApi();
        return routes;
    }

    public static Actor RootActor(ClaimsPrincipal user) => new(ActorKind.Root, user.Identity?.Name ?? "root");

    public static IResult Rejected(string message) => TypedResults.UnprocessableEntity(new ErrorResponse(message));

    // ---- Вход ----

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        RootAuthService auth,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var (outcome, user) = await auth.LoginAsync(request.Username, request.Password, cancellationToken);

        switch (outcome)
        {
            case RootLoginOutcome.Success:
                await http.SignInAsync(RootSetup.SchemeName, RootSetup.CreatePrincipal(user!));
                return TypedResults.Ok(new MeResponse(user!.Username));

            case RootLoginOutcome.Locked:
                return TypedResults.Json(
                    new ErrorResponse("Слишком много неверных попыток. Вход временно заблокирован, попробуйте через несколько минут."),
                    RootJsonContext.Default.ErrorResponse,
                    statusCode: StatusCodes.Status429TooManyRequests);

            default:
                // Одинаковый ответ для неверного имени и неверного пароля.
                return TypedResults.Json(
                    new ErrorResponse("Неверное имя или пароль."),
                    RootJsonContext.Default.ErrorResponse,
                    statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(RootSetup.SchemeName);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(
        PasswordChangeRequest request,
        ClaimsPrincipal user,
        RootAuthService auth,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var error = await auth.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, cancellationToken);
        return error is null ? TypedResults.NoContent() : Rejected(error);
    }

    // ---- Сводка ----

    private static async Task<IResult> GetOverviewAsync(
        SmenaDbContext db,
        Ledger ledger,
        BankReceiptService receipts,
        CancellationToken cancellationToken)
    {
        var safe = await ledger.GetBalanceAsync(SystemAccounts.Safe, cancellationToken);
        var bank = await receipts.GetBalanceAsync(cancellationToken);

        var employeeBalances = await db.AccountMovements
            .Where(static movement => movement.AccountKind == AccountKind.Employee && !movement.IsCancelled)
            .GroupBy(static movement => movement.AccountId)
            .Select(static group => group.Sum(movement => movement.SignedAmount))
            .ToListAsync(cancellationToken);

        var pendingReceipts = await (
            from receipt in db.BankReceipts
            where receipt.ConfirmedAt == null
            join transaction in db.Transactions on receipt.TransactionId equals transaction.Id
            join operation in db.Operations on transaction.OperationId equals operation.Id
            where operation.CancelledAt == null
            select receipt.Id).CountAsync(cancellationToken);

        var outbox = await db.OutboxMessages
            .Where(static message => message.Status != OutboxStatus.Sent)
            .GroupBy(static message => message.Status)
            .Select(static group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new OverviewResponse(
            Safe: safe,
            BankConfirmed: bank.Confirmed,
            BankInTransit: bank.InTransit,
            OwedToEmployees: employeeBalances.Where(static balance => balance > 0).Sum(),
            EmployeeDebts: -employeeBalances.Where(static balance => balance < 0).Sum(),
            PendingReceipts: pendingReceipts,
            OutboxPending: outbox.FirstOrDefault(static row => row.Status == OutboxStatus.Pending)?.Count ?? 0,
            OutboxFailed: outbox.FirstOrDefault(static row => row.Status == OutboxStatus.Failed)?.Count ?? 0));
    }

    // ---- Сотрудники ----

    private static async Task<IResult> GetEmployeesAsync(SmenaDbContext db, CancellationToken cancellationToken)
    {
        var employees = await db.Employees
            .AsNoTracking()
            .OrderByDescending(static employee => employee.IsActive)
            .ThenBy(static employee => employee.Name)
            .Select(employee => new RootEmployeeResponse(
                employee.Id,
                employee.Name,
                employee.TelegramId,
                employee.TelegramUsername,
                employee.HourlyRate,
                employee.IsActive,
                employee.SalaryThreadId,
                db.AccountMovements
                    .Where(movement => movement.AccountId == employee.Account.Id && !movement.IsCancelled)
                    .Sum(movement => movement.SignedAmount)))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(employees);
    }

    private static async Task<IResult> CreateEmployeeAsync(
        EmployeeCreateRequest request,
        SmenaDbContext db,
        CancellationToken cancellationToken)
    {
        if (CheckEmployee(request.Name, request.HourlyRate) is { } error)
        {
            return Rejected(error);
        }

        if (request.TelegramId <= 0)
        {
            return Rejected("Укажите Telegram ID сотрудника: по нему он входит в личный кабинет.");
        }

        var employee = Employee.Create(request.TelegramId, request.Name!.Trim(), request.HourlyRate);
        employee.SalaryThreadId = request.SalaryThreadId;
        db.Employees.Add(employee);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Rejected("Сотрудник с таким Telegram ID уже есть.");
        }

        return TypedResults.Ok(new RootEmployeeResponse(
            employee.Id, employee.Name, employee.TelegramId, null, employee.HourlyRate, true, employee.SalaryThreadId, Balance: 0));
    }

    private static async Task<IResult> UpdateEmployeeAsync(
        Guid id,
        EmployeeUpdateRequest request,
        SmenaDbContext db,
        CancellationToken cancellationToken)
    {
        if (CheckEmployee(request.Name, request.HourlyRate) is { } error)
        {
            return Rejected(error);
        }

        var employee = await db.Employees.Include(static e => e.Account).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (employee is null)
        {
            return TypedResults.NotFound();
        }

        // Ставка меняется только для будущих смен: в закрытых она сохранена отдельно.
        employee.Name = request.Name!.Trim();
        employee.Account.Name = employee.Name;
        employee.HourlyRate = request.HourlyRate;
        employee.IsActive = request.IsActive;
        employee.SalaryThreadId = request.SalaryThreadId;

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static string? CheckEmployee(string? name, int hourlyRate)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > Employee.NameLength)
        {
            return $"Укажите имя сотрудника (до {Employee.NameLength} символов).";
        }

        return hourlyRate is < 0 or > 100_000 ? "Ставка должна быть от 0 до 100 000 ₽ в час." : null;
    }

    // ---- Устройства кассы ----

    private static async Task<IResult> GetDevicesAsync(SmenaDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await db.Devices
            .AsNoTracking()
            .OrderBy(static device => device.CreatedAt)
            .Select(static device => new DeviceResponse(device.Id, device.Name, device.CreatedAt, device.RevokedAt))
            .ToListAsync(cancellationToken));

    private static async Task<IResult> CreateDeviceAsync(
        DeviceCreateRequest request,
        SmenaDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > Device.NameLength)
        {
            return Rejected($"Укажите название устройства (до {Device.NameLength} символов).");
        }

        var token = Device.GenerateToken();
        var device = Device.Create(request.Name.Trim(), token, time.GetUtcNow());
        db.Devices.Add(device);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new DeviceCreatedResponse(device.Id, device.Name, token));
    }

    private static async Task<IResult> RevokeDeviceAsync(Guid id, SmenaDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (device is null)
        {
            return TypedResults.NotFound();
        }

        device.RevokedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // ---- Настройки ----

    private static async Task<IResult> GetSettingsAsync(SmenaDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.ClubSettings.AsNoTracking().SingleAsync(cancellationToken);
        var channels = await db.ReceiptChannels
            .AsNoTracking()
            .OrderBy(static channel => channel.Id)
            .Select(static channel => new ChannelSettings((short)channel.Id, channel.Name, channel.DelayDays, channel.FeePercent))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new SettingsBody(
            settings.CashFloat,
            settings.MaxEmployeesPerShift,
            settings.MaxHoursPerShift,
            settings.MinShiftPhotos,
            settings.PhotoMaxAgeHours,
            settings.TimeZoneId,
            settings.TelegramChatId,
            settings.TelegramReportsThreadId,
            settings.TelegramSafeThreadId,
            settings.TelegramExpensesThreadId,
            settings.TelegramSalaryChatId,
            channels));
    }

    private static async Task<IResult> UpdateSettingsAsync(SettingsBody request, SmenaDbContext db, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TimeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZoneId, out _))
        {
            return Rejected("Неизвестный часовой пояс. Пример: Europe/Moscow.");
        }

        var settings = await db.ClubSettings.SingleAsync(cancellationToken);
        settings.CashFloat = request.CashFloat;
        settings.MaxEmployeesPerShift = request.MaxEmployeesPerShift;
        settings.MaxHoursPerShift = request.MaxHoursPerShift;
        settings.MinShiftPhotos = request.MinShiftPhotos;
        settings.PhotoMaxAgeHours = request.PhotoMaxAgeHours;
        settings.TimeZoneId = request.TimeZoneId.Trim();
        settings.TelegramChatId = request.TelegramChatId;
        settings.TelegramReportsThreadId = request.TelegramReportsThreadId;
        settings.TelegramSafeThreadId = request.TelegramSafeThreadId;
        settings.TelegramExpensesThreadId = request.TelegramExpensesThreadId;
        settings.TelegramSalaryChatId = request.TelegramSalaryChatId;

        try
        {
            // Те же границы, что проверяет расчёт смены: сохранить настройки,
            // с которыми смену потом не закрыть, нельзя.
            _ = settings.ToShiftRules();
        }
        catch (ArgumentOutOfRangeException)
        {
            return Rejected("Значения вне допустимых границ: размен ≥ 0, сотрудников и часов ≥ 1, фото ≥ 0, давность фото ≥ 1 ч.");
        }

        foreach (var channel in request.Channels ?? [])
        {
            if (channel.DelayDays is < 0 or > 60 || channel.FeePercent is < 0 or > 100)
            {
                return Rejected("Задержка зачисления — от 0 до 60 дней, комиссия — от 0 до 100 %.");
            }

            var stored = await db.ReceiptChannels.FirstOrDefaultAsync(c => c.Id == (ReceiptChannelKind)channel.Id, cancellationToken);
            if (stored is not null)
            {
                // Меняются условия только для будущих зачислений: в уже созданных они сохранены.
                stored.DelayDays = channel.DelayDays;
                stored.FeePercent = channel.FeePercent;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
