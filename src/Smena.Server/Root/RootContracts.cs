using System.Text.Json.Serialization;
using Smena.Core.Accounting;
using Smena.Server.Cashier;
using Smena.Server.Photos;

namespace Smena.Server.Root;

// Запросы и ответы API панели.

public sealed record LoginRequest(string? Username, string? Password);

public sealed record MeResponse(string Username);

public sealed record PasswordChangeRequest(string? CurrentPassword, string? NewPassword);

/// <summary>Сводка для первой страницы панели.</summary>
/// <param name="Safe">Баланс сейфа по учёту.</param>
/// <param name="BankConfirmed">Банк без денег «в пути» — должно совпадать с выпиской.</param>
/// <param name="BankInTransit">Отправлено в банк, но не подтверждено.</param>
/// <param name="OwedToEmployees">Сколько клуб должен сотрудникам (сумма положительных балансов).</param>
/// <param name="EmployeeDebts">Сколько сотрудники должны клубу (сумма отрицательных балансов, положительным числом).</param>
/// <param name="PendingReceipts">Зачислений, ожидающих подтверждения.</param>
/// <param name="OutboxPending">Сообщений Telegram в очереди.</param>
/// <param name="OutboxFailed">Сообщений, отклонённых Telegram.</param>
public sealed record OverviewResponse(
    int Safe,
    int BankConfirmed,
    int BankInTransit,
    int OwedToEmployees,
    int EmployeeDebts,
    int PendingReceipts,
    int OutboxPending,
    int OutboxFailed);

public sealed record RootEmployeeResponse(
    Guid Id,
    string Name,
    long TelegramId,
    string? TelegramUsername,
    int HourlyRate,
    bool IsActive,
    int? SalaryThreadId,
    int Balance);

public sealed record EmployeeCreateRequest(long TelegramId, string? Name, int HourlyRate, int? SalaryThreadId);

public sealed record EmployeeUpdateRequest(string? Name, int HourlyRate, bool IsActive, int? SalaryThreadId);

public sealed record OperationLineResponse(int Amount, string? Note);

public sealed record OperationListItem(
    Guid Id,
    OperationType Type,
    string TypeName,
    DateTimeOffset OccurredAt,
    string Comment,
    string ActorName,
    bool IsCancelled,
    IReadOnlyList<OperationLineResponse> Lines);

/// <param name="Direction">+1 — баланс счёта вырос, −1 — уменьшился.</param>
public sealed record EntryResponse(string Account, int Direction, string? Category);

public sealed record TransactionResponse(int Amount, string? Note, IReadOnlyList<EntryResponse> Entries);

public sealed record ShiftEmployeeDetail(string Name, int Hours, int HourlyRate, int Minus);

public sealed record ShiftDetail(
    Guid Id,
    int FactCash,
    int FactNonCash,
    int SkladCash,
    int SkladNonCash,
    int LangameCash,
    int LangameNonCash,
    int FactSafe,
    int ExpectedSafe,
    int CashFloat,
    int CashDiscrepancy,
    int SafeDiscrepancy,
    string? ShortageReason,
    IReadOnlyList<ShiftEmployeeDetail> Employees,
    IReadOnlyList<PhotoResponse> Photos);

public sealed record OperationDetail(
    Guid Id,
    OperationType Type,
    string TypeName,
    DateTimeOffset OccurredAt,
    string Comment,
    string ActorName,
    DateTimeOffset? CancelledAt,
    string? CancelledBy,
    string? CancelReason,
    IReadOnlyList<TransactionResponse> Transactions,
    ShiftDetail? Shift);

public sealed record CancelRequest(string? Reason);

public sealed record RootPayoutRequest(Guid EmployeeId, int Amount, bool FromBank, bool IsSalary, bool AllowOverdraft, string? Comment);

public sealed record EmployeeAmountRequest(Guid EmployeeId, int Amount, string? Comment);

public sealed record InventoryRequest(IReadOnlyList<Guid>? EmployeeIds, int TotalAmount, string? Comment);

/// <param name="Target">Safe, Bank или Employee.</param>
/// <param name="EmployeeId">Нужен только для Employee.</param>
/// <param name="Amount">Для сотрудника со знаком: плюс — клуб должен ему, минус — он клубу.</param>
/// <param name="Comment">Необязательный комментарий.</param>
public sealed record OpeningRequest(OpeningTarget Target, Guid? EmployeeId, int Amount, string? Comment);

public sealed record OperationDoneResponse(Guid OperationId, bool Replayed);

/// <param name="IsDue">Срок зачисления настал или не задан: пора сверять с выпиской.</param>
public sealed record ReceiptResponse(
    Guid Id,
    Guid OperationId,
    string Channel,
    string? Note,
    int Amount,
    DateTimeOffset SentAt,
    DateTimeOffset? ExpectedAt,
    int? ExpectedAmount,
    DateTimeOffset? ConfirmedAt,
    int? ConfirmedAmount,
    bool IsDue);

public sealed record ReceiptConfirmRequest(int Amount);

public sealed record ReceiptConfirmResponse(int Fee);

public sealed record RootCategoryResponse(
    int Id,
    CategoryKind Kind,
    string Name,
    bool IsSystem,
    bool IsArchived,
    string? CounterAccount,
    string? CounterCategory);

/// <param name="Kind">Справочник, в который добавляется категория.</param>
/// <param name="Name">Название.</param>
/// <param name="Counter">
/// Второй счёт для категорий сейфа и банка: «expense», «revenue», «bank» или «safe».
/// Для expense и revenue категория с тем же названием на втором счёте создаётся сама.
/// </param>
public sealed record CategoryCreateRequest(CategoryKind Kind, string? Name, string? Counter);

public sealed record CategoryUpdateRequest(string? Name, bool IsArchived);

public sealed record DeviceResponse(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

public sealed record DeviceCreateRequest(string? Name);

/// <param name="Token">Показывается один раз: в базе хранится только хеш.</param>
public sealed record DeviceCreatedResponse(Guid Id, string Name, string Token);

public sealed record ChannelSettings(short Id, string Name, int? DelayDays, decimal? FeePercent);

public sealed record SettingsBody(
    int CashFloat,
    int MaxEmployeesPerShift,
    int MaxHoursPerShift,
    int MinShiftPhotos,
    int PhotoMaxAgeHours,
    string? TimeZoneId,
    long? TelegramChatId,
    int? TelegramReportsThreadId,
    int? TelegramSafeThreadId,
    int? TelegramExpensesThreadId,
    long? TelegramSalaryChatId,
    IReadOnlyList<ChannelSettings>? Channels);

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(PasswordChangeRequest))]
[JsonSerializable(typeof(OverviewResponse))]
[JsonSerializable(typeof(List<RootEmployeeResponse>))]
[JsonSerializable(typeof(EmployeeCreateRequest))]
[JsonSerializable(typeof(EmployeeUpdateRequest))]
[JsonSerializable(typeof(List<OperationListItem>))]
[JsonSerializable(typeof(OperationDetail))]
[JsonSerializable(typeof(CancelRequest))]
[JsonSerializable(typeof(RootPayoutRequest))]
[JsonSerializable(typeof(EmployeeAmountRequest))]
[JsonSerializable(typeof(InventoryRequest))]
[JsonSerializable(typeof(OpeningRequest))]
[JsonSerializable(typeof(OperationDoneResponse))]
[JsonSerializable(typeof(List<ReceiptResponse>))]
[JsonSerializable(typeof(ReceiptConfirmRequest))]
[JsonSerializable(typeof(ReceiptConfirmResponse))]
[JsonSerializable(typeof(List<RootCategoryResponse>))]
[JsonSerializable(typeof(CategoryCreateRequest))]
[JsonSerializable(typeof(CategoryUpdateRequest))]
[JsonSerializable(typeof(List<DeviceResponse>))]
[JsonSerializable(typeof(DeviceCreateRequest))]
[JsonSerializable(typeof(DeviceCreatedResponse))]
[JsonSerializable(typeof(SettingsBody))]
[JsonSerializable(typeof(SafeMoveRequest))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class RootJsonContext : JsonSerializerContext;
