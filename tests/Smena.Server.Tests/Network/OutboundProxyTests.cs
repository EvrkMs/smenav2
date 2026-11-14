using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Smena.Server.Network;
using Smena.Server.Notifications;
using Smena.Server.Stock;

namespace Smena.Server.Tests.Network;

/// <summary>
/// Внешние запросы идут через прокси из Network:ProxyUri. Вместо настоящего прокси —
/// TCP-слушатель, который принимает соединение, запоминает первый байт (5 — начало
/// рукопожатия SOCKS5) и закрывает его. В интернет тесты не ходят.
/// </summary>
public sealed class OutboundProxyTests : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TaskCompletionSource<byte> _firstByte = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OutboundProxyTests()
    {
        _listener.Start();
        _ = AcceptOnceAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ProxyUri => $"socks5://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    [Fact]
    public async Task Telegram_goes_through_the_proxy_and_never_directly()
    {
        using var telegram = new TelegramClient(
            Options.Create(new TelegramOptions { BotToken = "0:test" }),
            Options.Create(new NetworkOptions { ProxyUri = ProxyUri }));

        var result = await telegram.SendMessageAsync(1, null, "проверка", Ct);

        Assert.Equal(5, await _firstByte.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        // Прокси оборвал соединение — сообщение ждёт повтора, прямого запроса в обход нет.
        Assert.Equal(TelegramOutcome.Transient, result.Outcome);
    }

    [Fact]
    public async Task MoySklad_goes_through_the_same_proxy()
    {
        await using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MoySklad:Token"] = "test-token",
                ["Network:ProxyUri"] = ProxyUri,
            }).Build())
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddOutboundHttp()
            .AddStock()
            .BuildServiceProvider();

        var source = services.GetRequiredService<IStockSource>();

        await Assert.ThrowsAsync<StockSourceException>(() => source.LoadAsync(Ct));
        Assert.Equal(5, await _firstByte.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("socks5://192.168.88.92:10801", true)]
    [InlineData("http://10.0.100.10:8080", true)]
    [InlineData("192.168.88.92:10801", false)]   // без схемы
    [InlineData("ftp://proxy:21", false)]
    [InlineData("socks5://proxy", false)]        // без порта
    public void Proxy_address_is_validated_at_startup(string? proxyUri, bool valid)
    {
        var result = new NetworkOptionsValidator().Validate(null, new NetworkOptions { ProxyUri = proxyUri });

        Assert.Equal(valid, result.Succeeded);
    }

    [Fact]
    public void Without_a_proxy_requests_go_directly_and_ignore_environment_proxies()
    {
        using var handler = OutboundHttp.CreateHandler(new NetworkOptions());

        Assert.False(handler.UseProxy);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Dispose();
        await Task.CompletedTask;
    }

    private async Task AcceptOnceAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var buffer = new byte[1];
            var read = await client.GetStream().ReadAsync(buffer);
            _firstByte.TrySetResult(read == 1 ? buffer[0] : (byte)0);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
        {
            _firstByte.TrySetException(e);
        }
    }
}
