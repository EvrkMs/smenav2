using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Smena.Server.Accounting;
using Smena.Server.Banking;
using Smena.Server.Cashier;
using Smena.Server.Database;
using Smena.Server.MiniApp;
using Smena.Server.Notifications;
using Smena.Server.Photos;
using Smena.Server.Root;
using Smena.Server.Shifts;
using Smena.Server.Stock;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(static options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(1, RootJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(2, MiniAppJsonContext.Default);
});

// Неразборчивое тело запроса — всегда ответ 400, в том числе в Development:
// по умолчанию там бросается исключение, и клиент получает 500.
builder.Services.Configure<RouteHandlerOptions>(static options => options.ThrowOnBadRequest = false);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSmenaDatabase();
builder.Services.AddCashierAuthentication();
builder.Services.AddRootAuthentication();
builder.Services.AddMiniAppAuthentication();
builder.Services.AddScoped<Ledger>();
builder.Services.AddNotifications();
builder.Services.AddPhotos();
builder.Services.AddStock();
builder.Services.AddScoped<BankReceiptService>();
builder.Services.AddScoped<ShiftService>();
builder.Services.AddScoped<CashOperationService>();
builder.Services.AddScoped<RootOperationService>();
builder.Services.AddScoped<OperationCancelService>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<SmenaDbContext>(name: "database", tags: [HealthTags.Ready]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// live — процесс жив; ready — можно принимать запросы (база доступна).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = static check => check.Tags.Contains(HealthTags.Ready),
});
app.MapCashierApi();
app.MapPhotoApi();
app.MapStockApi();
app.MapRootApi();
app.MapMiniAppApi();
app.MapCashierUi();

await app.MigrateDatabaseAsync();
await app.EnsureBootstrapDeviceAsync();
await app.EnsureBootstrapRootAsync();
await app.RunAsync();

internal static class HealthTags
{
    public const string Ready = "ready";
}
