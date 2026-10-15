using DotValet.Models;
using DotValet.Services;

namespace DotValet.Tests;

public class DomainParserTests
{
    [Theory]
    [InlineData("app", "app.test", null)]
    [InlineData("app.test", "app.test", null)]
    [InlineData("APP2.test:5172", "app2.test", 5172)]
    [InlineData("http://api.local/", "api.local", null)]
    [InlineData("https://shop.test:7001/path", "shop.test", 7001)]
    [InlineData("loja:5000", "loja.test", 5000)]
    public void Parses_valid_domains(string input, string host, int? port)
    {
        Assert.True(DomainParser.TryParse(input, "test", out var parsed, out var error), error);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("app.test:0")]
    [InlineData("app.test:99999")]
    [InlineData("app.test:abc")]
    [InlineData("my_app.test")]
    [InlineData("-app.test")]
    [InlineData("localhost")]
    public void Rejects_invalid_domains(string input)
    {
        Assert.False(DomainParser.TryParse(input, "test", out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("MyCompany.Api", "mycompany-api")]
    [InlineData("loja_v2", "loja-v2")]
    [InlineData("---", "app")]
    public void Slugify(string name, string expected) => Assert.Equal(expected, DomainParser.Slugify(name));

    [Fact]
    public void Proxy_mode_endpoints()
    {
        var site = new Site { Domain = "app", AppPort = 5100 };
        var ep = SiteEndpoints.TryResolve(site, new AppConfig(), out _)!;
        Assert.False(ep.IsDirect);
        Assert.Equal("http://localhost:5100", ep.BindingUrl);
        Assert.Equal("http://app.test", ep.PrimaryUrl);
    }

    [Fact]
    public void Direct_mode_endpoints()
    {
        var site = new Site { Domain = "app2.test:5172", AppPort = 5100 };
        var ep = SiteEndpoints.TryResolve(site, new AppConfig(), out _)!;
        Assert.True(ep.IsDirect);
        Assert.Equal(5172, ep.ListenPort);
        // Escuta só em localhost (não expõe na rede); o navegador usa o domínio.
        Assert.Equal("http://localhost:5172", ep.BindingUrl);
        Assert.Equal("http://app2.test:5172", ep.PrimaryUrl);
        Assert.Equal("http://app2.test", ep.ProxyUrl);
    }

    [Fact]
    public void Secure_with_custom_proxy_port()
    {
        var site = new Site { Domain = "app", AppPort = 5100, Secure = true };
        var ep = SiteEndpoints.TryResolve(site, new AppConfig { HttpsPort = 8443 }, out _)!;
        Assert.Equal("https://app.test:8443", ep.PrimaryUrl);
    }
}
