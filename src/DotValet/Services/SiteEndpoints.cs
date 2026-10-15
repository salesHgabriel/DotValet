using DotValet.Models;

namespace DotValet.Services;

/// <summary>Todas as URLs de um site, calculadas a partir do domínio e da configuração.</summary>
public sealed record SiteEndpoints(
    string Host,
    int? ExplicitPort,
    int ListenPort,
    string BindingUrl,
    string ProxyUrl,
    string PrimaryUrl)
{
    public bool IsDirect => ExplicitPort is not null;

    public static SiteEndpoints? TryResolve(Site site, AppConfig config, out string? error)
    {
        if (!DomainParser.TryParse(site.Domain, config.Tld, out var parsed, out error))
            return null;

        var listenPort = parsed.Port ?? site.AppPort;

        // O Kestrel sempre escuta em localhost: com um domínio (http://app2.test:5172) ele escutaria em todas as
        // interfaces e o app ficaria acessível pela rede. Com porta explícita o navegador usa app2.test:5172 direto
        // (o hosts aponta o domínio para 127.0.0.1); sem porta, o proxy do Dot Valet atende app.test:80/443.
        var binding = $"http://localhost:{listenPort}";

        var secure = site.Secure && config.EnableHttps;
        var proxyPort = secure ? config.HttpsPort : config.HttpPort;
        var defaultPort = secure ? 443 : 80;
        var proxyUrl = $"{(secure ? "https" : "http")}://{parsed.Host}{(proxyPort == defaultPort ? "" : $":{proxyPort}")}";

        return new SiteEndpoints(parsed.Host, parsed.Port, listenPort, binding, proxyUrl,
            parsed.Port is int port ? $"http://{parsed.Host}:{port}" : proxyUrl);
    }
}
