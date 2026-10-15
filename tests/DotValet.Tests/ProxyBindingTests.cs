using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DotValet.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace DotValet.Tests;

public class ProxyBindingTests
{
    private static CertificateService Certs() => new(Directory.CreateTempSubdirectory().FullName);

    /// <summary>Porta efêmera escolhida pelo SO (o PortAllocator começa sempre em 5100 e colide entre testes paralelos).</summary>
    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.IPv6Any, 0) { Server = { DualMode = true } };
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public void Wildcard_only_for_low_ports_on_macos_without_root()
    {
        Assert.True(ProxyServer.NeedsWildcardBinding(OsKind.MacOS, elevated: false, 80, 443));
        Assert.True(ProxyServer.NeedsWildcardBinding(OsKind.MacOS, elevated: false, 8080, 443));
        Assert.False(ProxyServer.NeedsWildcardBinding(OsKind.MacOS, elevated: false, 8080, 8443));
        Assert.False(ProxyServer.NeedsWildcardBinding(OsKind.MacOS, elevated: true, 80, 443));
        Assert.False(ProxyServer.NeedsWildcardBinding(OsKind.Linux, elevated: false, 80));
        Assert.False(ProxyServer.NeedsWildcardBinding(OsKind.Windows, elevated: false, 80));
    }

    [Fact]
    public void Only_loopback_addresses_are_local()
    {
        Assert.True(ProxyServer.IsLocal(IPAddress.Loopback));
        Assert.True(ProxyServer.IsLocal(IPAddress.IPv6Loopback));
        Assert.True(ProxyServer.IsLocal(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(ProxyServer.IsLocal(IPAddress.Parse("192.168.0.10")));
        Assert.False(ProxyServer.IsLocal(IPAddress.Parse("::ffff:192.168.0.10")));
        Assert.False(ProxyServer.IsLocal(null));
    }

    [Fact]
    public async Task Wildcard_proxy_answers_locally_and_drops_lan_connections()
    {
        var port = FreePort();
        await using var proxy = new ProxyServer();
        await proxy.StartAsync(port, null, Certs(), [], bindAllInterfaces: true);
        Assert.True(proxy.ListensOnAllInterfaces);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{port}/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://[::1]:{port}/")).StatusCode);

        var lan = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (lan is null)
            return; // sem interface de rede além do loopback

        // O pacote sai e volta pela própria máquina, mas com origem = IP da rede: tem de ser recusado.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync($"http://{lan}:{port}/"));
    }

    [Fact]
    public async Task Refuses_to_start_when_localhost_port_is_taken()
    {
        var port = FreePort();
        using var other = new TcpListener(IPAddress.Loopback, port);
        other.Start();

        await using var proxy = new ProxyServer();
        // Com bind curinga o SO poderia aceitar o bind e o tráfego de 127.0.0.1 continuaria indo para o outro processo.
        var e = await Assert.ThrowsAsync<PortInUseException>(() => proxy.StartAsync(port, null, Certs(), [], bindAllInterfaces: true));
        Assert.Equal(port, e.Port);
        Assert.False(proxy.IsRunning);
    }

    [Fact]
    public async Task Direct_mode_app_is_reachable_by_domain_but_not_from_the_network()
    {
        var port = FreePort();
        var site = new DotValet.Models.Site { Domain = $"direto.test:{port}" };
        var ep = SiteEndpoints.TryResolve(site, new DotValet.Models.AppConfig(), out _)!;
        Assert.Equal($"http://direto.test:{port}", ep.PrimaryUrl);

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(ep.BindingUrl); // o mesmo que ASPNETCORE_URLS / applicationUrl
        await using var app = builder.Build();
        app.MapGet("/", () => "ok");
        await app.StartAsync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
        req.Headers.Host = $"direto.test:{port}";
        Assert.Equal("ok", await (await http.SendAsync(req)).Content.ReadAsStringAsync());
        if (LanAddress() is { } lan)
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync($"http://{lan}:{port}/"));
    }

    private static IPAddress? LanAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    [MacFact]
    public async Task Macos_user_can_bind_low_port_with_automatic_wildcard()
    {
        // 81: porta < 1024 que normalmente está livre. Antes, ListenLocalhost dava "Permission denied".
        const int port = 81;
        if (await ProxyServer.IsPortInUseAsync(port) || Environment.IsPrivilegedProcess)
            return;

        await using var proxy = new ProxyServer();
        await proxy.StartAsync(port, null, Certs(), [new ProxyRoute("1", "App", "app.test", 1, false)]);
        Assert.True(proxy.ListensOnAllInterfaces);

        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
        req.Headers.Host = "app.test";
        Assert.Equal(HttpStatusCode.BadGateway, (await http.SendAsync(req)).StatusCode);
    }

    [MacFact]
    public async Task Detects_who_owns_a_busy_port()
    {
        var port = FreePort();
        using var other = new TcpListener(IPAddress.Loopback, port);
        other.Start();
        Assert.True(await ProxyServer.IsPortInUseAsync(port));
        Assert.NotNull(await ProxyServer.FindPortOwnerAsync(port));
    }
}
