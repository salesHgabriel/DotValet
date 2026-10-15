using System.Text.Json;

namespace DotValet.Services;

/// <summary>Site .NET publicado pelo nginx do Laravel Valet (`valet proxy host target`).</summary>
public sealed record ValetProxy(string Host, string Target, bool Secure);

/// <summary>
/// Convivência com o Laravel Valet (macOS). O nginx dele ocupa 127.0.0.1:80/443 e o dnsmasq dele resolve *.test,
/// então o recomendado é parar (`valet stop`) ou desinstalar (`valet uninstall`). Como alternativa, o Dot Valet
/// pode registrar os sites .NET no próprio Valet com `valet proxy`.
/// </summary>
public sealed class ValetService
{
    public const string StopCommand = "valet stop";
    public const string StartCommand = "valet start";
    public const string UninstallCommand = "valet uninstall";

    private readonly string _home;
    private readonly string? _valetPath;

    /// <param name="home">Home do usuário (null = a do usuário real, mesmo via sudo).</param>
    /// <param name="valetPath">Executável do valet (null = procura no PATH e no Composer global).</param>
    public ValetService(string? home = null, string? valetPath = null)
    {
        _home = home ?? PlatformInfo.UserHome;
        _valetPath = valetPath;
    }

    public string ConfigDirectory => Path.Combine(_home, ".config", "valet");

    public string? ValetPath => _valetPath ?? PlatformInfo.FindExecutable("valet",
        Path.Combine(_home, ".composer", "vendor", "bin", "valet"),
        Path.Combine(_home, ".config", "composer", "vendor", "bin", "valet"));

    public bool IsInstalled => PlatformInfo.Os == OsKind.MacOS
                               && (ValetPath is not null || File.Exists(Path.Combine(ConfigDirectory, "config.json")));

    /// <summary>TLD configurado no Valet (`valet tld`).</summary>
    public string Tld
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ConfigDirectory, "config.json")));
                return doc.RootElement.TryGetProperty("tld", out var tld) && tld.GetString() is { Length: > 0 } t ? t : "test";
            }
            catch
            {
                return "test";
            }
        }
    }

    /// <summary>O nginx do Valet está ocupando a porta (ele escuta em 127.0.0.1:80/443).</summary>
    public async Task<bool> IsUsingPortAsync(int port) =>
        IsInstalled && await ProxyServer.IsPortInUseAsync(port) && await ProxyServer.FindPortOwnerAsync(port) is "nginx" or null;

    /// <summary>
    /// O script do valet se re-executa com sudo quando não é root; já rodando como admin, ele chama o PHP direto
    /// e usa USER/HOME para achar ~/.config/valet. O PATH do root não tem o Homebrew (php, nginx, brew).
    /// </summary>
    public string BuildScript(IEnumerable<IReadOnlyList<string>> commands)
    {
        var user = PlatformInfo.SudoInvoker?.Name ?? Environment.UserName;
        var valet = Elevation.Sh(ValetPath ?? "valet");
        var lines = commands.Select(c => valet + " " + string.Join(' ', c.Select(Elevation.Sh)));
        return $"cd / && export USER={Elevation.Sh(user)} HOME={Elevation.Sh(_home)} " +
               "PATH=\"/opt/homebrew/bin:/opt/homebrew/sbin:/usr/local/bin:/usr/local/sbin:$PATH\" && " +
               string.Join(" && ", lines);
    }

    /// <summary>Comandos `valet unproxy`/`valet proxy` para sair do estado aplicado e chegar no desejado.</summary>
    public static List<IReadOnlyList<string>> PlanSync(IReadOnlyCollection<ValetProxy> desired, IReadOnlyCollection<ValetProxy> applied)
    {
        var commands = new List<IReadOnlyList<string>>();
        foreach (var old in applied.Where(a => !desired.Contains(a)))
            commands.Add(["unproxy", old.Host]);
        foreach (var add in desired.Where(d => !applied.Contains(d)))
            commands.Add(add.Secure ? ["proxy", add.Host, add.Target, "--secure"] : ["proxy", add.Host, add.Target]);
        return commands;
    }

    /// <returns>null quando não há nada a mudar.</returns>
    public async Task<ProcessResult?> SyncProxiesAsync(IReadOnlyCollection<ValetProxy> desired, IReadOnlyCollection<ValetProxy> applied)
    {
        var commands = PlanSync(desired, applied);
        if (commands.Count == 0)
            return null;
        if (ValetPath is null)
            return new ProcessResult(-1, "", "comando valet não encontrado");
        // Um único diálogo de senha para todas as mudanças.
        return await Elevation.RunAsync(BuildScript(commands), "publicar sites .NET no Laravel Valet");
    }

    public Task<ProcessResult> StopAsync() => ValetPath is null
        ? Task.FromResult(new ProcessResult(-1, "", "comando valet não encontrado"))
        : Elevation.RunAsync(BuildScript([["stop"]]), "parar o Laravel Valet");
}
