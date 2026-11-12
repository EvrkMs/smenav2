using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Smena.Core.Accounting;
using Smena.Core.Shifts;
using Smena.Server.Accounting;
using Smena.Server.Database;
using Smena.Server.Shifts;

namespace Smena.Server.Cashier;

/// <summary>API клиента на кассе. Все адреса требуют токен устройства.</summary>
internal static class CashierEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapCashierApi(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api").RequireAuthorization(DeviceAuthenticationHandler.CashierPolicy);

        api.MapGet("/config", GetConfigAsync);
        api.MapGet("/employees", GetEmployeesAsync);
        api.MapGet("/safe", GetSafeAsync);
        api.MapPost("/shifts/calc", CalculateShiftAsync);
        api.MapPost("/shifts", CloseShiftAsync);
        api.MapGet("/categories", GetCategoriesAsync);
        api.MapPost("/payouts", PayoutAsync);
        api.MapPost("/safe/income", static (
                SafeMoveRequest request,
                [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
                ClaimsPrincipal user,
                CashOperationService operations,
                Ledger ledger,
                CancellationToken cancellationToken) =>
            MoveSafeAsync(isIncome: true, request, idempotencyKey, user, operations, ledger, cancellationToken));
        api.MapPost("/safe/expense", static (
                SafeMoveRequest request,
                [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
                ClaimsPrincipal user,
                CashOperationService operations,
                Ledger ledger,
                CancellationToken cancellationToken) =>
            MoveSafeAsync(isIncome: false, request, idempotencyKey, user, operations, ledger, cancellationToken));

        return routes;
    }

    /// <summary>
    /// Категории, которые администратор может выбрать для прихода или расхода сейфа:
    /// не в архиве и с заданным вторым счётом.
    /// </summary>
    private static async Task<Results<Ok<List<CategoryResponse>>, BadRequest<ErrorResponse>>> GetCategoriesAsync(
        string? kind,
        SmenaDbContext db,
        CancellationToken cancellationToken)
    {
        CategoryKind? categoryKind = kind switch
        {
            "safeIncome" => CategoryKind.SafeIncome,
            "safeExpense" => CategoryKind.SafeExpense,
            _ => null,
        };

        if (categoryKind is not { } requested)
        {
            return TypedResults.BadRequest(new ErrorResponse("Параметр kind: safeIncome или safeExpense."));
        }

        var categories = await (
            from category in db.Categories.AsNoTracking()
            where category.Kind == requested && !category.IsArchived && category.CounterAccountId != null
            join account in db.Accounts.AsNoTracking() on category.CounterAccountId equals account.Id
            orderby category.Name
            select new CategoryResponse(category.Id, category.Name, account.Kind == AccountKind.Expense))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(categories);
    }

    private static async Task<Results<Ok<CashOperationResponse>, UnprocessableEntity<ErrorResponse>, BadRequest<ErrorResponse>>> PayoutAsync(
        PayoutRequest request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        ClaimsPrincipal user,
        CashOperationService operations,
        Ledger ledger,
        CancellationToken cancellationToken)
    {
        if (!TryReadKey(idempotencyKey, out var key))
        {
            return TypedResults.BadRequest(MissingKey);
        }

        var result = await operations.PayoutAsync(
            request.EmployeeId, request.Amount, request.IsSalary, request.Comment, key, CashierActor(user), cancellationToken);

        return await ToResponseAsync(result, ledger, cancellationToken);
    }

    private static async Task<Results<Ok<CashOperationResponse>, UnprocessableEntity<ErrorResponse>, BadRequest<ErrorResponse>>> MoveSafeAsync(
        bool isIncome,
        SafeMoveRequest request,
        string? idempotencyKey,
        ClaimsPrincipal user,
        CashOperationService operations,
        Ledger ledger,
        CancellationToken cancellationToken)
    {
        if (!TryReadKey(idempotencyKey, out var key))
        {
            return TypedResults.BadRequest(MissingKey);
        }

        var result = await operations.MoveSafeAsync(
            isIncome, request.CategoryId, request.Amount, request.Comment, key, CashierActor(user), cancellationToken);

        return await ToResponseAsync(result, ledger, cancellationToken);
    }

    private static async Task<Results<Ok<CashOperationResponse>, UnprocessableEntity<ErrorResponse>, BadRequest<ErrorResponse>>> ToResponseAsync(
        CashOperationResult result,
        Ledger ledger,
        CancellationToken cancellationToken)
    {
        if (result.OperationId is not { } operationId)
        {
            return TypedResults.UnprocessableEntity(new ErrorResponse(result.Error ?? "Операция отклонена."));
        }

        var safeBalance = await ledger.GetBalanceAsync(SystemAccounts.Safe, cancellationToken);
        return TypedResults.Ok(new CashOperationResponse(operationId, result.Replayed, safeBalance));
    }

    private static ErrorResponse MissingKey { get; } =
        new($"Нужен заголовок {IdempotencyHeader} с уникальным UUID запроса.");

    // Ключ обязателен: по нему повтор запроса (обрыв сети, двойной клик)
    // не проводит операцию второй раз.
    private static bool TryReadKey(string? header, out Guid key) =>
        Guid.TryParse(header, out key) && key != Guid.Empty;

    private static Actor CashierActor(ClaimsPrincipal user) =>
        new(ActorKind.Cashier, user.Identity?.Name ?? "касса");

    private static async Task<Ok<ConfigResponse>> GetConfigAsync(SmenaDbContext db, CancellationToken cancellationToken)
    {
        var rules = (await db.ClubSettings.AsNoTracking().SingleAsync(cancellationToken)).ToShiftRules();
        return TypedResults.Ok(new ConfigResponse(rules.CashFloat, rules.MaxEmployees, rules.MaxTotalHours, rules.MinPhotos));
    }

    private static async Task<Ok<List<EmployeeResponse>>> GetEmployeesAsync(SmenaDbContext db, CancellationToken cancellationToken)
    {
        var employees = await db.Employees
            .AsNoTracking()
            .Where(static employee => employee.IsActive)
            .OrderBy(static employee => employee.Name)
            .Select(employee => new EmployeeResponse(
                employee.Id,
                employee.Name,
                employee.HourlyRate,
                db.AccountMovements
                    .Where(movement => movement.AccountId == employee.Account.Id && !movement.IsCancelled)
                    .Sum(movement => movement.SignedAmount)))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(employees);
    }

    private static async Task<Ok<SafeResponse>> GetSafeAsync(Ledger ledger, CancellationToken cancellationToken) =>
        TypedResults.Ok(new SafeResponse(await ledger.GetBalanceAsync(SystemAccounts.Safe, cancellationToken)));

    private static async Task<Ok<ShiftCalculationResponse>> CalculateShiftAsync(
        ShiftInput input,
        ShiftService shifts,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(ShiftCalculationResponse.From(await shifts.CalculateAsync(input, cancellationToken)));

    private static async Task<Results<Ok<ShiftClosedResponse>, UnprocessableEntity<ShiftRejectedResponse>, BadRequest<ErrorResponse>>> CloseShiftAsync(
        ShiftInput input,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        ClaimsPrincipal user,
        ShiftService shifts,
        CancellationToken cancellationToken)
    {
        if (!TryReadKey(idempotencyKey, out var key))
        {
            return TypedResults.BadRequest(MissingKey);
        }

        var result = await shifts.CloseAsync(input, key, CashierActor(user), cancellationToken);

        if (result.ShiftId is { } shiftId)
        {
            var calculation = result.Calculation is null ? null : ShiftCalculationResponse.From(result.Calculation);
            return TypedResults.Ok(new ShiftClosedResponse(shiftId, result.Replayed, calculation));
        }

        return TypedResults.UnprocessableEntity(new ShiftRejectedResponse(
            "Смена не закрыта.",
            ShiftCalculationResponse.From(result.Calculation!)));
    }
}
