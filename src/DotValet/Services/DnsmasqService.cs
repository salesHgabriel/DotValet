namespace DotValet.Services;

public enum DnsResolverState
{
    /// <summary>Nada configurado para o TLD.</summary>
    None,

    /// <summary>Configurado pelo Dot Valet (pode ser removido por ele).</summary>
    Managed,

    /// <summary>Configurado por outra ferramenta (ex.: Laravel Valet) — nunca é alterado pelo Dot Valet.</summary>
    External,
}

/// <summary>
/// DNS wildcard para *.tld, seguindo o que o Laravel Valet (macOS) e o valet-linux fazem.
/// No Windows não há equivalente nativo e usamos o arquivo hosts (como o Laragon).
/// </summary>
public sealed class DnsmasqService
{
    public const string Marker = "# Gerenciado pelo Dot Valet";

    private readonly string _resolverDir;
    private readonly string _networkManagerDir;
    private readonly string? _brewPrefix;
    private readonly string? _restartCommand;

    /// <param name="resolverDir">macOS: diretório /etc/resolver.</param>
    /// <param name="networkManagerDir">Linux: diretório /etc/NetworkManager.</param>
    /// <param name="brewPrefix">macOS: prefixo do Homebrew (null = descobre via `brew --prefix`).</param>
    /// <param name="restartCommand">Comando que reinicia o DNS (null = padrão da plataforma).</param>
    public DnsmasqService(string resolverDir = "/etc/resolver", string networkManagerDir = "/etc/NetworkManager",
        string? brewPrefix = null, string? restartCommand = null)
    {
        _resolverDir = resolverDir;
        _networkManagerDir = networkManagerDir;
        _brewPrefix = brewPrefix;
        _restartCommand = restartCommand;
    }

    public bool IsSupported => PlatformInfo.Os switch
    {
        OsKind.MacOS => _brewPrefix is not null || BrewPath is not null,
        OsKind.Linux => Directory.Exists(_networkManagerDir),
        _ => false,
    };

    public static string? BrewPath => PlatformInfo.FindExecutable("brew", "/opt/homebrew/bin/brew", "/usr/local/bin/brew");

    public static string UnsupportedReason => PlatformInfo.Os switch
    {
        OsKind.MacOS => "Instale o Homebrew (https://brew.sh) para usar o dnsmasq.",
        OsKind.Linux => "NetworkManager não encontrado; use o arquivo hosts.",
        _ => "No Windows o Dot Valet usa o arquivo hosts (como o Laragon).",
    };

    private string ResolverFile(string tld) => Path.Combine(_resolverDir, tld);
    private string NmDnsmasqFile => Path.Combine(_networkManagerDir, "dnsmasq.d", "dotvalet.conf");
    private string NmMainFile => Path.Combine(_networkManagerDir, "conf.d", "dotvalet.conf");

    public DnsResolverState GetState(string tld)
    {
        switch (PlatformInfo.Os)
        {
            case OsKind.MacOS:
            {
                var file = ResolverFile(tld);
                if (!File.Exists(file))
                    return DnsResolverState.None;
                return SafeRead(file).Contains(Marker) ? DnsResolverState.Managed : DnsResolverState.External;
            }
            case OsKind.Linux:
            {
                // O arquivo é único para todos os TLDs: só vale para este se apontar para ele.
                if (SafeRead(NmDnsmasqFile).Contains($"/.{tld}/"))
                    return DnsResolverState.Managed;
                // valet-linux usa /etc/NetworkManager/dnsmasq.d/valet
                var dir = Path.Combine(_networkManagerDir, "dnsmasq.d");
                if (Directory.Exists(dir) && Directory.GetFiles(dir)
                        .Any(f => f != NmDnsmasqFile && SafeRead(f).Contains($"/.{tld}/")))
                    return DnsResolverState.External;
                return DnsResolverState.None;
            }
            default:
                return DnsResolverState.None;
        }
    }

    /// <summary>Há um DNS wildcard ativo para o TLD (do Dot Valet ou de outra ferramenta).</summary>
    public bool IsInstalled(string tld) => GetState(tld) != DnsResolverState.None;

    public string Describe(string tld) => GetState(tld) switch
    {
        DnsResolverState.Managed => $"configurado pelo Dot Valet para *.{tld}",
        DnsResolverState.External => $"*.{tld} já é resolvido por outra ferramenta (ex.: Laravel Valet) — reaproveitado, nunca alterado",
        _ => "não configurado",
    };

    public async Task<ProcessResult> InstallAsync(string tld, Action<string> log)
    {
        if (GetState(tld) == DnsResolverState.External)
        {
            // Ex.: o Laravel Valet já roda um dnsmasq com address=/.test/127.0.0.1.
            // Adicionar outro arquivo duplicaria listen-address e quebraria o serviço dele.
            return new ProcessResult(0, $"*.{tld} já é resolvido por outro dnsmasq; nada foi alterado.", "");
        }

        return PlatformInfo.Os switch
        {
            OsKind.MacOS => await InstallMacAsync(tld, log),
            OsKind.Linux => await InstallLinuxAsync(tld),
            _ => new ProcessResult(-1, "", UnsupportedReason),
        };
    }

    private async Task<ProcessResult> InstallMacAsync(string tld, Action<string> log)
    {
        var prefix = _brewPrefix;
        var brew = BrewPath;
        if (prefix is null)
        {
            if (brew is null)
                return new ProcessResult(-1, "", UnsupportedReason);
            // O Homebrew se recusa a rodar como root: se o Dot Valet foi aberto com sudo, roda como o usuário real.
            if (!(await ProcessRunner.RunAsync(brew, ["list", "--formula", "dnsmasq"], asUser: true)).Success)
            {
                log("Instalando dnsmasq via Homebrew (pode demorar)...");
                var install = await ProcessRunner.RunAsync(brew, ["install", "dnsmasq"], asUser: true);
                if (!install.Success)
                    return install;
            }
            prefix = (await ProcessRunner.RunAsync(brew, ["--prefix"], asUser: true)).StdOut.Trim();
        }

        var etc = Path.Combine(prefix, "etc");
        var confDir = Path.Combine(etc, "dnsmasq.d");
        Directory.CreateDirectory(confDir);

        // Mesmo layout do Valet: um arquivo próprio dentro de dnsmasq.d.
        var conf = Path.Combine(confDir, "dotvalet.conf");
        await File.WriteAllTextAsync(conf,
            $"{Marker}\naddress=/.{tld}/127.0.0.1\naddress=/.{tld}/::1\nlisten-address=127.0.0.1\n");
        PlatformInfo.GiveBackToUser(conf);

        var mainConf = Path.Combine(etc, "dnsmasq.conf");
        var include = $"conf-dir={confDir}/,*.conf";
        var content = File.Exists(mainConf) ? await File.ReadAllTextAsync(mainConf) : "";
        if (!content.Contains(include))
        {
            await File.AppendAllTextAsync(mainConf, $"\n{include}\n");
            PlatformInfo.GiveBackToUser(mainConf);
        }

        log("Configurando /etc/resolver e reiniciando o dnsmasq...");
        var restart = _restartCommand ?? (brew is null ? "true" : $"{Elevation.Sh(brew)} services restart dnsmasq");
        // Ao trocar de TLD, o resolver antigo criado pelo Dot Valet deixaria de ter dnsmasq por trás: remove.
        var stale = ManagedResolverFiles().Where(f => f != ResolverFile(tld)).Select(Elevation.Sh).ToList();
        var cleanup = stale.Count == 0 ? "" : $"rm -f {string.Join(' ', stale)} && ";
        var script = $"{cleanup}mkdir -p {Elevation.Sh(_resolverDir)} && printf '%s\\nnameserver 127.0.0.1\\n' {Elevation.Sh(Marker)} > {Elevation.Sh(ResolverFile(tld))} && {restart}";
        return await Elevation.RunAsync(script, $"configurar DNS para *.{tld}");
    }

    private Task<ProcessResult> InstallLinuxAsync(string tld)
    {
        // Mesma abordagem do valet-linux: plugin dnsmasq do NetworkManager.
        var restart = _restartCommand ?? "systemctl restart NetworkManager";
        var script = string.Join(" && ",
            $"mkdir -p {Elevation.Sh(Path.GetDirectoryName(NmDnsmasqFile)!)} {Elevation.Sh(Path.GetDirectoryName(NmMainFile)!)}",
            $"printf '%s\\naddress=/.{tld}/127.0.0.1\\n' {Elevation.Sh(Marker)} > {Elevation.Sh(NmDnsmasqFile)}",
            $"printf '%s\\n[main]\\ndns=dnsmasq\\n' {Elevation.Sh(Marker)} > {Elevation.Sh(NmMainFile)}",
            restart);
        return Elevation.RunAsync(script, $"configurar DNS para *.{tld}");
    }

    /// <summary>macOS: arquivos em /etc/resolver criados pelo Dot Valet (qualquer TLD).</summary>
    private IEnumerable<string> ManagedResolverFiles() => Directory.Exists(_resolverDir)
        ? Directory.GetFiles(_resolverDir).Where(f => SafeRead(f).Contains(Marker))
        : [];

    /// <summary>Há configuração do Dot Valet para algum TLD (ex.: um TLD antigo, antes de trocar nas configurações).</summary>
    private bool HasAnyManagedConfig => PlatformInfo.Os switch
    {
        OsKind.MacOS => ManagedResolverFiles().Any(),
        OsKind.Linux => File.Exists(NmDnsmasqFile) || File.Exists(NmMainFile),
        _ => false,
    };

    public async Task<ProcessResult> UninstallAsync(string tld)
    {
        // Nunca remove configuração de outra ferramenta (ex.: /etc/resolver/test do Laravel Valet).
        // Remove tudo o que é do Dot Valet, mesmo que tenha sido criado para um TLD anterior.
        if (GetState(tld) != DnsResolverState.Managed && !HasAnyManagedConfig)
            return new ProcessResult(0, "Nada a remover: a configuração de DNS não pertence ao Dot Valet.", "");

        switch (PlatformInfo.Os)
        {
            case OsKind.MacOS:
            {
                var brew = BrewPath;
                var prefix = _brewPrefix ?? (brew is null ? null : (await ProcessRunner.RunAsync(brew, ["--prefix"], asUser: true)).StdOut.Trim());
                if (prefix is not null)
                {
                    var conf = Path.Combine(prefix, "etc", "dnsmasq.d", "dotvalet.conf");
                    if (File.Exists(conf))
                        File.Delete(conf);
                }
                var restart = _restartCommand ?? (brew is null ? "true" : $"{Elevation.Sh(brew)} services restart dnsmasq");
                var files = string.Join(' ', ManagedResolverFiles().Select(Elevation.Sh));
                var rm = files.Length == 0 ? "" : $"rm -f {files} && ";
                return await Elevation.RunAsync($"{rm}{restart}", "remover configuração de DNS");
            }
            case OsKind.Linux:
            {
                var restart = _restartCommand ?? "systemctl restart NetworkManager";
                return await Elevation.RunAsync($"rm -f {Elevation.Sh(NmDnsmasqFile)} {Elevation.Sh(NmMainFile)} && {restart}",
                    "remover configuração de DNS");
            }
            default:
                return new ProcessResult(0, "", "");
        }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return ""; }
    }
}
