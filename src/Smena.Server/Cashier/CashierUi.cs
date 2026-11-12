using Microsoft.Net.Http.Headers;

namespace Smena.Server.Cashier;

/// <summary>
/// Раздача интерфейсов: касса (сборка web/cashier в wwwroot/cashier), панель root
/// (web/panel в wwwroot/panel) и кабинет сотрудника (web/miniapp в wwwroot/miniapp). Интерфейсы живут на сервере, а не в программе на
/// кассе: обновили сервер — обновились и они.
/// </summary>
internal static class CashierUi
{
    public static IEndpointRouteBuilder MapCashierUi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/", static () => Results.Redirect("/cashier/"));

        MapApp(routes, "cashier");
        MapApp(routes, "panel");
        MapApp(routes, "miniapp");

        return routes;
    }

    private static void MapApp(IEndpointRouteBuilder routes, string name)
    {
        // Сама страница не кешируется, чтобы браузер сразу получал новую версию;
        // скрипты и стили лежат в assets/ под именами с хешем и кешируются.
        routes.MapFallbackToFile($"/{name}/{{*path:nonfile}}", $"{name}/index.html", new StaticFileOptions
        {
            OnPrepareResponse = static context =>
                context.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache",
        });
    }
}
