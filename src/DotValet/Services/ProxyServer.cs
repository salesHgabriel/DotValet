using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace DotValet.Services;

public sealed record ProxyRoute(string SiteId, string Name, string Host, int TargetPort, bool Secure);

/// <summary>Outro processo já escuta na porta em localhost (ex.: o nginx do Laravel Valet).</summary>
public sealed class PortInUseException(int port, string? owner)
    : IOException($"a porta {port} já está em uso em localhost{(owner is null ? "" : $" por {owner}")}")
{
    public int Port { get; } = port;
    public string? Owner { get; } = owner;
}

/// <summary>
/// Proxy reverso local (Kestrel + YARP) que faz o papel do nginx no Laravel Valet:
/// app.test:80/443 → http://127.0.0.1:{porta do app}.
/// </summary>
public sealed class ProxyServer : IAsyncDisposable
{
    private WebApplication? _app;
    private InMemoryConfigProvider? _config;
    private volatile IReadOnlyList<ProxyRoute> _routes = [];

    public bool IsRunning => _app is not null;
    public int HttpPort { get; private set; }
    public int? HttpsPort { get; private set; }

    /// <summary>Escutando em todas as interfaces (com filtro que só aceita conexões da própria máquina).</summary>
    public bool ListensOnAllInterfaces { get; private set; }

    /// <summary>
    /// No macOS, usuário comum pode usar portas &lt; 1024 só em endereço curinga (0.0.0.0/::), não em 127.0.0.1/::1.
    /// Nesse caso o proxy escuta em todas as interfaces e recusa conexões que não sejam de loopback.
    /// </summary>
    public static bool NeedsWildcardBinding(OsKind os, bool elevated, params int[] ports) =>
        os == OsKind.MacOS && !elevated && ports.Any(p => p < 1024);

    public static bool IsLocal(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>
    /// Há algo escutando na porta em localhost? Testa conectando (não exige privilégio). Necessário porque,
    /// com o bind curinga, o macOS aceitaria subir o proxy mesmo com 127.0.0.1:porta ocupado — e o tráfego
    /// para 127.0.0.1 continuaria indo para o outro processo.
    /// </summary>
    public static async Task<bool> IsPortInUseAsync(int port)
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            using var client = new TcpClient(address.AddressFamily);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                await client.ConnectAsync(address, port, cts.Token);
                return true;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
            }
        }
        return false;
    }

    /// <summary>Nome do processo que escuta na porta (via lsof; null se não der para descobrir).</summary>
    public static async Task<string?> FindPortOwnerAsync(int port)
    {
        if (PlatformInfo.Os == OsKind.Windows || PlatformInfo.FindExecutable("lsof", "/usr/sbin/lsof", "/usr/bin/lsof") is not { } lsof)
            return null;
        var result = await ProcessRunner.RunAsync(lsof, ["-nP", $"-iTCP:{port}", "-sTCP:LISTEN", "-Fc"]);
        return result.StdOut.Split('\n').Where(l => l.StartsWith('c')).Select(l => l[1..]).FirstOrDefault();
    }

    /// <param name="bindAllInterfaces">null = decide pela plataforma (<see cref="NeedsWildcardBinding"/>).</param>
    public async Task StartAsync(int httpPort, int? httpsPort, CertificateService certs, IReadOnlyList<ProxyRoute> routes,
        bool? bindAllInterfaces = null)
    {
        await StopAsync();
        _routes = routes;

        var ports = httpsPort is int sp ? new[] { httpPort, sp } : [httpPort];
        foreach (var port in ports)
        {
            if (await IsPortInUseAsync(port))
                throw new PortInUseException(port, await FindPortOwnerAsync(port));
        }
        var wildcard = bindAllInterfaces ?? NeedsWildcardBinding(PlatformInfo.Os, PlatformInfo.IsElevated, ports);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            void Listen(int port, Action<ListenOptions>? configure = null)
            {
                if (!wildcard)
                {
                    k.ListenLocalhost(port, o => configure?.Invoke(o));
                    return;
                }
                k.ListenAnyIP(port, o =>
                {
                    // Antes do TLS: conexão de fora da máquina é derrubada sem nem ver o certificado.
                    o.Use(next => connection => IsLocal((connection.RemoteEndPoint as IPEndPoint)?.Address)
                        ? next(connection)
                        : Task.CompletedTask);
                    configure?.Invoke(o);
                });
            }

            Listen(httpPort);
            if (httpsPort is int sslPort)
            {
                Listen(sslPort, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(https =>
                        https.ServerCertificateSelector = (_, name) => certs.GetSiteCertificate(name));
                });
            }
        });

        var (yarpRoutes, clusters) = BuildConfig(routes);
        builder.Services.AddReverseProxy().LoadFromMemory(yarpRoutes, clusters);

        var app = builder.Build();
        _config = app.Services.GetRequiredService<InMemoryConfigProvider>();

        // Sites marcados como seguros redirecionam HTTP → HTTPS (como "valet secure").
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.IsHttps && httpsPort is int sslPort
                && _routes.FirstOrDefault(r => r.Host.Equals(ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase)) is { Secure: true } route)
            {
                var port = sslPort == 443 ? "" : $":{sslPort}";
                ctx.Response.Redirect($"https://{route.Host}{port}{ctx.Request.PathBase}{ctx.Request.Path}{ctx.Request.QueryString}");
                return;
            }
            await next();
        });

        app.MapReverseProxy(pipeline =>
        {
            pipeline.Use(async (ctx, next) =>
            {
                await next();
                var error = ctx.GetForwarderErrorFeature();
                if (error is not null && !ctx.Response.HasStarted)
                    await WriteNotRunningAsync(ctx);
            });
        });
        app.MapFallback(WriteNotFoundAsync);

        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        _app = app;
        HttpPort = httpPort;
        HttpsPort = httpsPort;
        ListensOnAllInterfaces = wildcard;
    }

    public void UpdateRoutes(IReadOnlyList<ProxyRoute> routes)
    {
        _routes = routes;
        if (_config is null)
            return;
        var (r, c) = BuildConfig(routes);
        _config.Update(r, c);
    }

    public async Task StopAsync()
    {
        if (_app is null)
            return;
        var app = _app;
        _app = null;
        _config = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(cts.Token);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private static (IReadOnlyList<RouteConfig>, IReadOnlyList<ClusterConfig>) BuildConfig(IReadOnlyList<ProxyRoute> routes)
    {
        var yarpRoutes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();
        foreach (var route in routes.GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            var id = $"site-{route.SiteId}";
            yarpRoutes.Add(new RouteConfig
            {
                RouteId = id,
                ClusterId = id,
                Match = new RouteMatch { Hosts = [route.Host], Path = "{**catch-all}" },
                // Mantém o Host original (app.test) para que links absolutos e cookies funcionem.
                Transforms = [new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" }],
            });
            clusters.Add(new ClusterConfig
            {
                ClusterId = id,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["app"] = new() { Address = $"http://127.0.0.1:{route.TargetPort}" },
                },
                HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMinutes(10) },
            });
        }
        return (yarpRoutes, clusters);
    }

    private Task WriteNotRunningAsync(HttpContext ctx)
    {
        var route = _routes.FirstOrDefault(r => r.Host.Equals(ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase));
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var name = WebUtility.HtmlEncode(route?.Name ?? ctx.Request.Host.Host);
        var port = route?.TargetPort.ToString() ?? "?";
        return ctx.Response.WriteAsync(Page("O site não está rodando",
            $"<p><b>{name}</b> não respondeu em <code>http://127.0.0.1:{port}</code>.</p>" +
            "<p>Inicie o projeto pelo Dot Valet ou pela sua IDE usando o perfil <code>DotValet</code> do launchSettings.json.</p>"));
    }

    private Task WriteNotFoundAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var items = string.Join("", _routes.OrderBy(r => r.Host).Select(r =>
        {
            var host = WebUtility.HtmlEncode(r.Host);
            return $"<li><a href=\"//{host}\">{host}</a></li>";
        }));
        return ctx.Response.WriteAsync(Page("Site não encontrado",
            $"<p>Nenhum site configurado para <code>{WebUtility.HtmlEncode(ctx.Request.Host.Host)}</code>.</p>" +
            (items.Length > 0 ? $"<p>Sites disponíveis:</p><ul>{items}</ul>" : "")));
    }

    private static string Page(string title, string body) => $$$"""
        <!doctype html><html lang="pt-br"><head><meta charset="utf-8"><title>{{{title}}} · Dot Valet</title>
        <style>body{font-family:system-ui,sans-serif;max-width:640px;margin:10vh auto;padding:0 16px;color:#222}
        h1{font-size:1.4rem}code{background:#f2f2f2;padding:2px 5px;border-radius:4px}a{color:#5b3fd6}
        @media (prefers-color-scheme:dark){body{background:#151515;color:#ddd}code{background:#2a2a2a}a{color:#a996ff}}</style>
        </head><body><h1>{{{title}}}</h1>{{{body}}}<p style="opacity:.6;font-size:.85rem">Dot Valet</p></body></html>
        """;
}
