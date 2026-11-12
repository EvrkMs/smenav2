using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Smena.Server.Database;
using Smena.Server.Employees;

namespace Smena.Server.MiniApp;

/// <param name="Name">Имя сотрудника, как оно заведено в панели.</param>
/// <param name="HourlyRate">Ставка, ₽ в час.</param>
/// <param name="Balance">Сколько клуб должен сотруднику; отрицательное — сотрудник должен клубу.</param>
public sealed record EmployeeProfileResponse(string Name, int HourlyRate, int Balance);

/// <param name="Id">Номер строки; передаётся в <c>before</c>, чтобы получить более старые.</param>
/// <param name="OccurredAt">Когда проведена операция.</param>
/// <param name="Amount">На сколько изменился баланс: плюс — начислено, минус — выплачено или удержано.</param>
/// <param name="Title">За что: начисление за смену, аванс, штраф…</param>
/// <param name="Comment">Комментарий к операции, если есть.</param>
public sealed record EmployeeMovementResponse(long Id, DateTimeOffset OccurredAt, int Amount, string Title, string? Comment);

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EmployeeProfileResponse))]
[JsonSerializable(typeof(List<EmployeeMovementResponse>))]
internal sealed partial class MiniAppJsonContext : JsonSerializerContext;

/// <summary>
/// Кабинет сотрудника (Telegram MiniApp): свой баланс и своя история. Чей кабинет —
/// определяет только подпись Telegram; идентификатор сотрудника в запросах не передаётся,
/// поэтому посмотреть чужие данные нельзя.
/// </summary>
internal static class MiniAppEndpoints
{
    private const int DefaultPageSize = 30;
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapMiniAppApi(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/me").RequireAuthorization(MiniAppAuthenticationHandler.EmployeePolicy);

        api.MapGet("/", GetProfileAsync);
        api.MapGet("/operations", GetOperationsAsync);

        return routes;
    }

    private static async Task<Results<Ok<EmployeeProfileResponse>, NotFound>> GetProfileAsync(
        ClaimsPrincipal user,
        SmenaDbContext db,
        CancellationToken cancellationToken)
    {
        var employeeId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var accountId = AccountId(user);

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        if (employee is null)
        {
            return TypedResults.NotFound();
        }

        // Имя в Telegram панель показывает рядом с сотрудником; обновляем его при входе.
        var username = user.FindFirstValue(MiniAppAuthenticationHandler.UsernameClaim);
        var current = string.IsNullOrEmpty(username) ? null : Truncate(username, Employee.UsernameLength);
        if (employee.TelegramUsername != current)
        {
            employee.TelegramUsername = current;
            await db.SaveChangesAsync(cancellationToken);
        }

        var balance = await db.AccountMovements
            .Where(movement => movement.AccountId == accountId && !movement.IsCancelled)
            .SumAsync(static movement => movement.SignedAmount, cancellationToken);

        return TypedResults.Ok(new EmployeeProfileResponse(employee.Name, employee.HourlyRate, balance));
    }

    /// <summary>
    /// История движений по счёту сотрудника, от новых к старым. Отменённые операции
    /// не показываются: в балансе их тоже нет, и сумма истории всегда равна балансу.
    /// </summary>
    private static async Task<Ok<List<EmployeeMovementResponse>>> GetOperationsAsync(
        long? before,
        int? take,
        ClaimsPrincipal user,
        SmenaDbContext db,
        CancellationToken cancellationToken)
    {
        var accountId = AccountId(user);

        var movements = db.AccountMovements
            .AsNoTracking()
            .Where(movement => movement.AccountId == accountId && !movement.IsCancelled);

        if (before is { } cursor)
        {
            movements = movements.Where(movement => movement.EntryId < cursor);
        }

        // Номера проводок растут вместе со временем, поэтому это и порядок «от новых
        // к старым», и устойчивая точка продолжения для следующей страницы.
        var page = await (
            from movement in movements
            join category in db.Categories.AsNoTracking() on movement.CategoryId equals (int?)category.Id into categories
            from category in categories.DefaultIfEmpty()
            orderby movement.EntryId descending
            select new EmployeeMovementResponse(
                movement.EntryId,
                movement.OccurredAt,
                movement.SignedAmount,
                category != null ? category.Name : movement.Note ?? string.Empty,
                movement.Comment == string.Empty ? null : movement.Comment))
            .Take(Math.Clamp(take ?? DefaultPageSize, 1, MaxPageSize))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(page);
    }

    private static int AccountId(ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(MiniAppAuthenticationHandler.AccountClaim)!, CultureInfo.InvariantCulture);

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
