using System.Text.Json.Serialization;
using Smena.Core.Shifts;

namespace Smena.Server.Cashier;

// Ответы API кассы. Запрос закрытия смены — ShiftInput из ядра как есть.

public sealed record ConfigResponse(int CashFloat, int MaxEmployees, int MaxTotalHours, int MinPhotos);

/// <param name="Balance">Сколько клуб должен сотруднику; отрицательное — сотрудник в минусе.</param>
public sealed record EmployeeResponse(Guid Id, string Name, int HourlyRate, int Balance);

public sealed record SafeResponse(int Balance);

public sealed record ShiftEmployeeResponse(Guid EmployeeId, string Name, int Hours, int HourlyRate, int Accrued, int Minus, int Payable);

public sealed record ShiftProblemResponse(ShiftProblemCode Code, string Message, Guid? EmployeeId);

public sealed record ShiftCalculationResponse
{
    public required int CashFloat { get; init; }

    public required int ExpectedSafe { get; init; }

    public required int ProgramTotal { get; init; }

    public required int Revenue { get; init; }

    public required int CashToSafe { get; init; }

    public required int NonCash { get; init; }

    public required int CashDiscrepancy { get; init; }

    public required int SafeDiscrepancy { get; init; }

    public required int RequiredMinus { get; init; }

    public required int EnteredMinus { get; init; }

    public required int PayrollAccrued { get; init; }

    public required int PayrollNet { get; init; }

    public required int Total { get; init; }

    public required int SafeAfter { get; init; }

    public required bool CanClose { get; init; }

    public required IReadOnlyList<ShiftEmployeeResponse> Employees { get; init; }

    public required IReadOnlyList<ShiftProblemResponse> Problems { get; init; }

    public static ShiftCalculationResponse From(ShiftCalculation calculation)
    {
        var lines = calculation.Lines;
        var employees = new ShiftEmployeeResponse[lines.Count];
        for (var index = 0; index < employees.Length; index++)
        {
            var line = lines[index];
            employees[index] = new ShiftEmployeeResponse(
                line.EmployeeId, line.Name, line.Hours, line.HourlyRate, line.Accrued, line.Minus, line.Payable);
        }

        var source = calculation.Problems;
        var problems = new ShiftProblemResponse[source.Count];
        for (var index = 0; index < problems.Length; index++)
        {
            var problem = source[index];
            problems[index] = new ShiftProblemResponse(problem.Code, problem.Message, problem.EmployeeId);
        }

        return new ShiftCalculationResponse
        {
            CashFloat = calculation.CashFloat,
            ExpectedSafe = calculation.ExpectedSafe,
            ProgramTotal = calculation.ProgramTotal,
            Revenue = calculation.Revenue,
            CashToSafe = calculation.CashToSafe,
            NonCash = calculation.NonCash,
            CashDiscrepancy = calculation.CashDiscrepancy,
            SafeDiscrepancy = calculation.SafeDiscrepancy,
            RequiredMinus = calculation.RequiredMinus,
            EnteredMinus = calculation.EnteredMinus,
            PayrollAccrued = calculation.PayrollAccrued,
            PayrollNet = calculation.PayrollNet,
            Total = calculation.Total,
            SafeAfter = calculation.SafeAfter,
            CanClose = calculation.CanClose,
            Employees = employees,
            Problems = problems,
        };
    }
}

/// <param name="Replayed">Запрос с этим ключом уже был выполнен; смена та же, расчёта в ответе нет.</param>
public sealed record ShiftClosedResponse(Guid ShiftId, bool Replayed, ShiftCalculationResponse? Calculation);

/// <summary>Смена не закрыта: в расчёте есть проблемы. В базу ничего не записано.</summary>
public sealed record ShiftRejectedResponse(string Message, ShiftCalculationResponse Calculation);

public sealed record ErrorResponse(string Message);

/// <param name="CommentRequired">Без комментария операция с этой категорией не пройдёт.</param>
public sealed record CategoryResponse(int Id, string Name, bool CommentRequired);

/// <param name="IsSalary">true — выплата ЗП, false — аванс. На учёт влияет только категорией.</param>
public sealed record PayoutRequest(Guid EmployeeId, int Amount, bool IsSalary, string? Comment);

public sealed record SafeMoveRequest(int CategoryId, int Amount, string? Comment);

/// <param name="SafeBalance">Баланс сейфа после операции.</param>
public sealed record CashOperationResponse(Guid OperationId, bool Replayed, int SafeBalance);

/// <summary>
/// Сериализация JSON, сгенерированная при компиляции: без рефлексии на каждом
/// запросе и с проверкой на этапе сборки, что все типы API описаны.
/// </summary>
[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ShiftInput))]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(List<EmployeeResponse>))]
[JsonSerializable(typeof(SafeResponse))]
[JsonSerializable(typeof(ShiftCalculationResponse))]
[JsonSerializable(typeof(ShiftClosedResponse))]
[JsonSerializable(typeof(ShiftRejectedResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(List<CategoryResponse>))]
[JsonSerializable(typeof(PayoutRequest))]
[JsonSerializable(typeof(SafeMoveRequest))]
[JsonSerializable(typeof(CashOperationResponse))]
[JsonSerializable(typeof(Photos.PhotoSessionResponse))]
[JsonSerializable(typeof(Photos.UploadStatusResponse))]
[JsonSerializable(typeof(Photos.PhotoResponse))]
[JsonSerializable(typeof(Stock.StockResponse))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
