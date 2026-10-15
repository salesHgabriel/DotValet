using System.Net;
using DotValet.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DotValet.Tests;

public class ProxyServerTests
{
    [Fact]
    public async Task Routes_by_host_and_reports_missing_sites()
    {
        var backendPort = PortAllocator.Next([]);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://localhost:{backendPort}");
        await using var backend = builder.Build();
        backend.MapGet("/hello", (HttpContext ctx) => $"oi de {ctx.Request.Host.Host}");
        await backend.StartAsync();

        var proxyPort = PortAllocator.Next([backendPort]);
        var deadPort = PortAllocator.Next([backendPort, proxyPort]);
        await using var proxy = new ProxyServer();
        await proxy.StartAsync(proxyPort, null, new CertificateService(Directory.CreateTempSubdirectory().FullName),
        [
            new ProxyRoute("1", "App", "app.test", backendPort, false),
            new ProxyRoute("2", "Parado", "parado.test", deadPort, false),
        ]);

        using var http = new HttpClient();
        async Task<HttpResponseMessage> Get(string host, string path = "/hello")
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{proxyPort}{path}");
            req.Headers.Host = host;
            return await http.SendAsync(req);
        }

        var ok = await Get("app.test");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("oi de app.test", await ok.Content.ReadAsStringAsync());

        var down = await Get("parado.test");
        Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
        Assert.Contains("não está rodando", await down.Content.ReadAsStringAsync());

        var missing = await Get("outro.test");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("app.test", await missing.Content.ReadAsStringAsync());

        // rotas dinâmicas
        proxy.UpdateRoutes([new ProxyRoute("3", "Novo", "novo.test", backendPort, false)]);
        Assert.Equal("oi de novo.test", await (await Get("novo.test")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Get("app.test")).StatusCode);
    }
}
