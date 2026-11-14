using System.Net;
using Microsoft.Extensions.Options;

namespace Smena.Server.Network;

/// <summary>
/// Выход сервера в интернет. Внешних адресов у сервера два — Telegram Bot API и
/// API «МойСклад»; оба ходят через одну настройку (<c>Network__ProxyUri</c>).
/// </summary>
public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    /// <summary>
    /// Прокси для всех внешних запросов, например <c>socks5://192.168.88.92:10801</c>.
    /// Задан — запросы идут только через него: если прокси недоступен, запрос
    /// заканчивается ошибкой, а не уходит напрямую. Пусто — напрямую.
    /// </summary>
    public string? ProxyUri { get; set; }

    public bool HasProxy => !string.IsNullOrWhiteSpace(ProxyUri);
}

internal sealed class NetworkOptionsValidator : IValidateOptions<NetworkOptions>
{
    // Схемы, которые понимает SocketsHttpHandler.
    private static readonly string[] Schemes = ["socks5", "socks4", "socks4a", "http", "https"];

    public ValidateOptionsResult Validate(string? name, NetworkOptions options)
    {
        if (!options.HasProxy)
        {
            return ValidateOptionsResult.Success;
        }

        return Uri.TryCreate(options.ProxyUri, UriKind.Absolute, out var uri)
               && Array.IndexOf(Schemes, uri.Scheme) >= 0
               && uri.Port > 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                "Network:ProxyUri должен быть адресом вида socks5://хост:порт (поддерживаются socks5, socks4, socks4a, http, https).");
    }
}

/// <summary>Единственное место, где создаются соединения наружу.</summary>
public static class OutboundHttp
{
    public static IServiceCollection AddOutboundHttp(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<NetworkOptions>, NetworkOptionsValidator>();
        services.AddOptions<NetworkOptions>().BindConfiguration(NetworkOptions.SectionName).ValidateOnStart();
        return services;
    }

    /// <summary>
    /// Обработчик соединений для внешнего API. Прокси задаётся явно, а не берётся
    /// из переменных среды HTTP_PROXY/ALL_PROXY: иначе маршрут запросов зависел бы
    /// от окружения контейнера, а не от настройки сервера.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(NetworkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = options.HasProxy,
        };

        if (options.HasProxy)
        {
            // Прокси для любого адреса, без исключений для «локальных»: обход
            // мимо прокси и есть то, что эта настройка запрещает.
            handler.Proxy = new WebProxy(new Uri(options.ProxyUri!)) { BypassProxyOnLocal = false };
        }

        return handler;
    }
}
