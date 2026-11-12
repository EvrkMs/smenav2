using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Smena.Core.Accounting;
using Smena.Server.Accounting;
using Smena.Server.Banking;
using Smena.Server.Cashier;
using Smena.Server.Database;
using Smena.Server.Photos;

namespace Smena.Server.Root;

/// <summary>Денежная часть API панели: операции, отмена, зачисления в банк, категории.</summary>
internal static class RootMoneyEndpoints
{
    private const int DefaultListSize = 50;
    private const int MaxListSize = 200;

    public static void MapRootMoneyApi(this RouteGroupBuilder api)
    {
        api.MapGet("/operations", GetOperationsAsync);
        api.MapGet("/operations/{id:guid}", GetOperationAsync);
        api.MapPost("/operations/{id:guid}/cancel", CancelOperationAsync);
        api.MapGet("/photos/{id:guid}", GetPhotoAsync);

        api.MapPost("/payouts", PayoutAsync);
        api.MapPost("/fines", FineAsync);
        api.MapPost("/bonuses", BonusAsync);
        api.MapPost("/inventory", InventoryAsync);
        api.MapPost("/opening", OpeningAsync);
        api.MapPost("/bank/income", static (
                SafeMoveRequest request, [FromHeader(Name = CashierEndpoints.IdempotencyHeader)] string? key,
                ClaimsPrincipal user, CashOperationService operations, CancellationToken cancellationToken) =>
            MoveBankAsync(isIncome: true, request, key, user, operations, cancellationToken));
        api.MapPost("/bank/expense", static (
                SafeMoveRequest request, [FromHeader(Name = CashierEndpoints.IdempotencyHeader)] string? key,
                ClaimsPrincipal user, CashOperationService operations, CancellationToken cancellationToken) =>
            MoveBankAsync(isIncome: false, request, key, user, operations, cancellationToken));

        api.MapGet("/bank/receipts", GetReceiptsAsync);
        api.MapPost("/bank/receipts/{id:guid}/confirm", ConfirmReceiptAsync);
        api.MapPost("/bank/receipts/{id:guid}/unconfirm", UnconfirmReceiptAsync);

        api.MapGet("/categories", GetCategoriesAsync);
        api.MapPost("/categories", CreateCategoryAsync);
        api.MapPut("/categories/{id:int}", UpdateCategoryAsync);
    }

    // ---- Операции ----

    private static async Task<IResult> GetOperationsAsync(
        OperationType? type,
        bool? cancelled,
        int? take,
        SmenaDbContext db,
        CancellationToken cancellationToken)
    {
        var query = db.Operations.AsNoTracking();

        if (type is { } wantedType)
        {
            query = query.Where(operation => operation.Type == wantedType);
        }

        if (cancelled is { } wantCancelled)
        {
            query = query.Where(operation => (operation.CancelledAt != null) == wantCancelled);
        }

        var operations = await query
            .OrderByDescending(static operation => operation.OccurredAt)
            .ThenByDescending(static operation => operation.Id)
            .Take(Math.Clamp(take ?? DefaultListSize, 1, MaxListSize))
            .Include(static operation => operation.Transactions.OrderBy(transaction => transaction.Position))
            .ToListAsync(cancellationToken);

        var typeNames = await TypeNamesAsync(db, cancellationToken);

        return TypedResults.Ok(operations.ConvertAll(operation => new OperationListItem(
            operation.Id,
            operation.Type,
            typeNames[operation.Type],
            operation.OccurredAt,
            operation.Comment,
            operation.ActorName,
            operation.IsCancelled,
            operation.Transactions.Select(static transaction => new OperationLineResponse(transaction.Amount, transaction.Note)).ToArray())));
    }

    private static async Task<IResult> GetOperationAsync(Guid id, SmenaDbContext db, CancellationToken cancellationToken)
    {
        var operation = await db.Operations
            .AsNoTracking()
            .Include(static o => o.Transactions.OrderBy(transaction => transaction.Position))
            .ThenInclude(static transaction => transaction.Entries.OrderBy(entry => entry.Id))
            .AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (operation is null)
        {
            return TypedResults.NotFound();
        }

        var entries = operation.Transactions.SelectMany(static transaction => transaction.Entries).ToArray();
        var accountIds = entries.Select(static entry => entry.AccountId).Distinct().ToArray();
        var categoryIds = entries.Select(static entry => entry.CategoryId).OfType<int>().Distinct().ToArray();

        var accountNames = await db.Accounts
            .AsNoTracking()
            .Where(account => accountIds.Contains(account.Id))
            .ToDictionaryAsync(static account => account.Id, static account => account.Name, cancellationToken);
        var categoryNames = await db.Categories
            .AsNoTracking()
            .Where(category => categoryIds.Contains(category.Id))
            .ToDictionaryAsync(static category => category.Id, static category => category.Name, cancellationToken);

        var transactions = operation.Transactions
            .Select(transaction => new TransactionResponse(
                transaction.Amount,
                transaction.Note,
                transaction.Entries
                    .Select(entry => new EntryResponse(
                        accountNames[entry.AccountId],
                        (int)entry.Direction,
                        entry.CategoryId is { } categoryId ? categoryNames[categoryId] : null))
                    .ToArray()))
            .ToArray();

        var typeNames = await TypeNamesAsync(db, cancellationToken);

        return TypedResults.Ok(new OperationDetail(
            operation.Id,
            operation.Type,
            typeNames[operation.Type],
            operation.OccurredAt,
            operation.Comment,
            operation.ActorName,
            operation.CancelledAt,
            operation.CancelledBy,
            operation.CancelReason,
            transactions,
            await LoadShiftAsync(db, operation.Id, cancellationToken)));
    }

    private static async Task<ShiftDetail?> LoadShiftAsync(SmenaDbContext db, Guid operationId, CancellationToken cancellationToken)
    {
        var shift = await db.Shifts
            .AsNoTracking()
            .Include(static s => s.Employees.OrderBy(employee => employee.Position))
            .FirstOrDefaultAsync(s => s.OperationId == operationId, cancellationToken);

        if (shift is null)
        {
            return null;
        }

        var employeeIds = shift.Employees.Select(static employee => employee.EmployeeId).ToArray();
        var names = await db.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .ToDictionaryAsync(static employee => employee.Id, static employee => employee.Name, cancellationToken);

        var photos = await db.Photos
            .AsNoTracking()
            .Where(photo => photo.ShiftId == shift.Id)
            .OrderBy(static photo => photo.Id)
            .Select(static photo => new PhotoResponse(photo.Id, photo.TakenAt, photo.IsStale, photo.UploadedAt, photo.PastedAtCashier))
            .ToListAsync(cancellationToken);

        return new ShiftDetail(
            shift.Id,
            shift.FactCash,
            shift.FactNonCash,
            shift.SkladCash,
            shift.SkladNonCash,
            shift.LangameCash,
            shift.LangameNonCash,
            shift.FactSafe,
            shift.ExpectedSafe,
            shift.CashFloat,
            shift.CashDiscrepancy,
            shift.SafeDiscrepancy,
            shift.ShortageReason,
            shift.Employees
                .Select(employee => new ShiftEmployeeDetail(names[employee.EmployeeId], employee.Hours, employee.HourlyRate, employee.Minus))
                .ToArray(),
            photos);
    }

    /// <summary>Снимок, приложенный к смене. Непривязанные фото панель не показывает.</summary>
    private static async Task<IResult> GetPhotoAsync(
        Guid id,
        HttpContext context,
        PhotoStorage storage,
        SmenaDbContext db,
        CancellationToken cancellationToken)
    {
        var path = await db.Photos
            .Where(photo => photo.Id == id && photo.ShiftId != null)
            .Select(static photo => photo.Path)
            .FirstOrDefaultAsync(cancellationToken);

        return path is null ? TypedResults.NotFound() : PhotoEndpoints.PhotoFile(storage, path, context);
    }

    private static async Task<IResult> CancelOperationAsync(
        Guid id,
        CancelRequest request,
        ClaimsPrincipal user,
        OperationCancelService cancellation,
        CancellationToken cancellationToken)
    {
        var error = await cancellation.CancelAsync(id, request.Reason, RootEndpoints.RootActor(user), cancellationToken);
        return error is null ? TypedResults.NoContent() : RootEndpoints.Rejected(error);
    }

    private static Task<Dictionary<OperationType, string>> TypeNamesAsync(SmenaDbContext db, CancellationToken cancellationToken) =>
        db.Set<OperationTypeRow>().AsNoTracking().ToDictionaryAsync(static row => row.Id, static row => row.Name, cancellationToken);

    // ---- Денежные операции root ----

    private static Task<IResult> PayoutAsync(
        RootPayoutRequest request,
        [FromHeader(Name = CashierEndpoints.IdempotencyHeader)] string? key,
        ClaimsPrincipal user,
        RootOperationService operations,
        CancellationToken cancellationToken) =>
        RunAsync(key, requestKey => operations.PayoutAsync(
            request.EmployeeId, request.Amount, request.FromBank, request.IsSalary, request.AllowOverdraft,
            request.Comment, requestKey, RootEndpoints.RootActor(user), cancellationToken));

    private static Task<IResult> FineAsync(
        EmployeeAmountRequest request,
        [FromHeader(Name = CashierEndpoints.IdempotencyHeader)] string? key,
        ClaimsPrincipal user,
        RootOperationService operations,
        CancellationToken cancellationToken) =>
        RunAsync(key, requestKey => operations.FineAsync(
            request.EmployeeId, request.Amount, request.Comment, requestKey, RootEndpoints.RootActor(user), cancellationToken));

    private static Task<IResult> BonusAsync(
        EmployeeAmountRequest request,
        [FromHeader(Name = CashierEndpoints.IdempotencyHeader)] string? key,
        ClaimsPrincipal user,
        RootOperationService operations,
        CancellationToken cancellationToken) =>
        RunAsync(key, requestKey => operations.BonusAsync(
            request.EmployeeId, request.Amount, request.Comment, requestKey, RootEndpoints.RootActor(user), cancellationToken));

    private static Task<IResult> InventoryAsync(
        InventoryRequest request,
        [FromHeader(Name = CashierEndpoints.IdempotencyHeader)] string? key,
        ClaimsPrincipal user,
        RootOperationService operations,
        CancellationToken cancellationToken) =>
        RunAsync(key, requestKey => operations.InventoryAsync(
            request.EmployeeIds ?? [], request.TotalAmount, request.Comment, requestKey, RootEndpoints.RootActor(user), cancellationToken));

    private static Task<IResult> OpeningAsync(
        OpeningRequest request,
        [FromHeader(Name = CashierEndpoints.IdempotencyHeader)] string? key,
        ClaimsPrincipal user,
        RootOperationService operations,
        CancellationToken cancellationToken) =>
        RunAsync(key, requestKey => operations.OpeningAsync(
            request.Target, request.EmployeeId, request.Amount, request.Comment, requestKey, RootEndpoints.RootActor(user), cancellationToken));

    private static Task<IResult> MoveBankAsync(
        bool isIncome,
        SafeMoveRequest request,
        string? key,
        ClaimsPrincipal user,
        CashOperationService operations,
        CancellationToken cancellationToken) =>
        RunAsync(key, requestKey => operations.MoveBankAsync(
            isIncome, request.CategoryId, request.Amount, request.Comment, requestKey, RootEndpoints.RootActor(user), cancellationToken));

    private static async Task<IResult> RunAsync(string? key, Func<Guid, Task<CashOperationResult>> run)
    {
        if (!Guid.TryParse(key, out var requestKey) || requestKey == Guid.Empty)
        {
            return TypedResults.BadRequest(new ErrorResponse(
                $"Нужен заголовок {CashierEndpoints.IdempotencyHeader} с уникальным UUID запроса."));
        }

        var result = await run(requestKey);
        return result.OperationId is { } operationId
            ? TypedResults.Ok(new OperationDoneResponse(operationId, result.Replayed))
            : RootEndpoints.Rejected(result.Error ?? "Операция отклонена.");
    }

    // ---- Зачисления в банк ----

    private static async Task<IResult> GetReceiptsAsync(
        bool? pending,
        SmenaDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        var query =
            from receipt in db.BankReceipts.AsNoTracking()
            join transaction in db.Transactions on receipt.TransactionId equals transaction.Id
            join operation in db.Operations on transaction.OperationId equals operation.Id
            join channel in db.ReceiptChannels on receipt.Channel equals channel.Id
            where operation.CancelledAt == null
            select new { receipt, transaction.Amount, transaction.Note, OperationId = operation.Id, operation.OccurredAt, Channel = channel.Name };

        if (pending == true)
        {
            query = query.Where(row => row.receipt.ConfirmedAt == null);
        }

        var rows = await query
            .OrderByDescending(row => row.OccurredAt)
            .Take(MaxListSize)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(rows.ConvertAll(row => new ReceiptResponse(
            row.receipt.Id,
            row.OperationId,
            row.Channel,
            row.Note,
            row.Amount,
            row.OccurredAt,
            row.receipt.ExpectedAt,
            row.receipt.ExpectedAmount,
            row.receipt.ConfirmedAt,
            row.receipt.ConfirmedAmount,
            IsDue: !row.receipt.IsConfirmed && (row.receipt.ExpectedAt is not { } expectedAt || expectedAt <= now))));
    }

    private static async Task<IResult> ConfirmReceiptAsync(
        Guid id,
        ReceiptConfirmRequest request,
        ClaimsPrincipal user,
        BankReceiptService receipts,
        CancellationToken cancellationToken)
    {
        var confirmation = await receipts.ConfirmAsync(id, request.Amount, RootEndpoints.RootActor(user), cancellationToken);
        return confirmation.IsConfirmed
            ? TypedResults.Ok(new ReceiptConfirmResponse(confirmation.Fee))
            : RootEndpoints.Rejected(confirmation.Error!);
    }

    private static async Task<IResult> UnconfirmReceiptAsync(
        Guid id,
        ClaimsPrincipal user,
        OperationCancelService cancellation,
        CancellationToken cancellationToken)
    {
        var error = await cancellation.UnconfirmReceiptAsync(id, RootEndpoints.RootActor(user), cancellationToken);
        return error is null ? TypedResults.NoContent() : RootEndpoints.Rejected(error);
    }

    // ---- Категории ----

    private static async Task<IResult> GetCategoriesAsync(CategoryKind? kind, SmenaDbContext db, CancellationToken cancellationToken)
    {
        var categories = await db.Categories
            .AsNoTracking()
            .Where(category => kind == null || category.Kind == kind)
            .OrderBy(static category => category.Kind)
            .ThenBy(static category => category.IsArchived)
            .ThenBy(static category => category.Name)
            .ToListAsync(cancellationToken);

        var accountNames = await db.Accounts
            .AsNoTracking()
            .Where(static account => account.EmployeeId == null)
            .ToDictionaryAsync(static account => account.Id, static account => account.Name, cancellationToken);
        var categoryNames = categories.ToDictionary(static category => category.Id, static category => category.Name);

        if (kind is not null)
        {
            // При фильтре по справочнику категории второго счёта в выборку не попали.
            var counterIds = categories.Select(static category => category.CounterCategoryId).OfType<int>().Distinct().ToArray();
            foreach (var (id, name) in await db.Categories
                         .AsNoTracking()
                         .Where(category => counterIds.Contains(category.Id))
                         .ToDictionaryAsync(static category => category.Id, static category => category.Name, cancellationToken))
            {
                categoryNames[id] = name;
            }
        }

        return TypedResults.Ok(categories.ConvertAll(category => new RootCategoryResponse(
            category.Id,
            category.Kind,
            category.Name,
            category.IsSystem,
            category.IsArchived,
            category.CounterAccountId is { } accountId ? accountNames.GetValueOrDefault(accountId) : null,
            category.CounterCategoryId is { } categoryId ? categoryNames.GetValueOrDefault(categoryId) : null)));
    }

    private static async Task<IResult> CreateCategoryAsync(CategoryCreateRequest request, SmenaDbContext db, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Kind))
        {
            return RootEndpoints.Rejected("Неизвестный справочник категорий.");
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > Category.NameLength)
        {
            return RootEndpoints.Rejected($"Укажите название категории (до {Category.NameLength} символов).");
        }

        var category = Category.Create(request.Kind, name);

        // Второй счёт имеет смысл только у категорий сейфа и банка: по нему
        // операция с такой категорией сама получается сбалансированной.
        var ownAccount = request.Kind.Account();
        if (ownAccount is AccountKind.Safe or AccountKind.Bank)
        {
            AccountRef? counter = request.Counter switch
            {
                "expense" => SystemAccounts.Expense,
                "revenue" => SystemAccounts.Revenue,
                "bank" => SystemAccounts.Bank,
                "safe" => SystemAccounts.Safe,
                _ => null,
            };

            if (counter is not { } counterAccount || counterAccount.Kind == ownAccount)
            {
                return RootEndpoints.Rejected("Для категории сейфа или банка укажите второй счёт: расходы, выручка, банк или сейф.");
            }

            category.CounterAccountId = counterAccount.Id;

            // Для расходов и выручки нужна категория и на втором счёте — по ней строится P&L.
            // Берём одноимённую, а если её нет — создаём.
            var counterKind = counterAccount.Kind switch
            {
                AccountKind.Expense => CategoryKind.Expense,
                AccountKind.Revenue => CategoryKind.Revenue,
                _ => (CategoryKind?)null,
            };

            if (counterKind is { } kind)
            {
                var counterCategory = await db.Categories.FirstOrDefaultAsync(c => c.Kind == kind && c.Name == name, cancellationToken);
                if (counterCategory is null)
                {
                    counterCategory = Category.Create(kind, name);
                    db.Categories.Add(counterCategory);
                    await db.SaveChangesAsync(cancellationToken);
                }

                category.CounterCategoryId = counterCategory.Id;
            }
        }

        db.Categories.Add(category);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return RootEndpoints.Rejected("Категория с таким названием в этом справочнике уже есть.");
        }

        return TypedResults.Ok(new RootCategoryResponse(
            category.Id, category.Kind, category.Name, IsSystem: false, IsArchived: false, CounterAccount: null, CounterCategory: null));
    }

    private static async Task<IResult> UpdateCategoryAsync(
        int id,
        CategoryUpdateRequest request,
        SmenaDbContext db,
        CancellationToken cancellationToken)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (category is null)
        {
            return TypedResults.NotFound();
        }

        if (category.IsSystem)
        {
            return RootEndpoints.Rejected("Системную категорию нельзя менять: на неё опирается код.");
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > Category.NameLength)
        {
            return RootEndpoints.Rejected($"Укажите название категории (до {Category.NameLength} символов).");
        }

        category.Name = name;
        category.IsArchived = request.IsArchived;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return RootEndpoints.Rejected("Категория с таким названием в этом справочнике уже есть.");
        }

        return TypedResults.NoContent();
    }
}
