namespace DotValet.Models;

public enum DnsMode
{
    /// <summary>Uma entrada por site no arquivo hosts (funciona em qualquer SO, como o Laragon).</summary>
    HostsFile,

    /// <summary>Wildcard *.tld via dnsmasq (como o Laravel Valet / valet-linux).</summary>
    Dnsmasq,
}

public sealed class AppConfig
{
    public string Tld { get; set; } = "test";
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public bool EnableHttps { get; set; } = true;
    public bool AutoStartProxy { get; set; } = true;
    public DnsMode DnsMode { get; set; } = DnsMode.HostsFile;
    public bool CaTrusted { get; set; }

    /// <summary>Backup do hosts (principal + último) antes de cada alteração.</summary>
    public bool BackupHostsFile { get; set; } = true;

    /// <summary>Authtoken do ngrok (dashboard.ngrok.com); vazio = usa o que estiver no ngrok.yml do usuário.</summary>
    public string? NgrokAuthToken { get; set; }

    /// <summary>Publicar os sites também pelo nginx do Laravel Valet (`valet proxy`), quando ele estiver instalado.</summary>
    public bool ValetIntegration { get; set; }

    /// <summary>O que foi registrado no Valet por último, para conseguir desfazer.</summary>
    public List<DotValet.Services.ValetProxy> ValetProxies { get; set; } = [];
    public List<string> ParkedPaths { get; set; } = [];
    public List<Site> Sites { get; set; } = [];
}
