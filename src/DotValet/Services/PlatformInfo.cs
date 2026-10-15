using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotValet.Services;

public enum OsKind { Windows, MacOS, Linux }

public static class PlatformInfo
{
    public static OsKind Os { get; } =
        OperatingSystem.IsWindows() ? OsKind.Windows :
        OperatingSystem.IsMacOS() ? OsKind.MacOS : OsKind.Linux;

    public static string DisplayName => Os switch
    {
        OsKind.Windows => $"Windows ({RuntimeInformation.OSArchitecture})",
        OsKind.MacOS => $"macOS {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor} ({RuntimeInformation.OSArchitecture})",
        _ => $"Linux {LinuxDistro()} ({RuntimeInformation.OSArchitecture})".Replace("  ", " "),
    };

    /// <summary>Qual ferramenta do mundo PHP serve de referência para a estratégia nesta plataforma.</summary>
    public static string Strategy => Os switch
    {
        OsKind.Windows => "Como o Laragon: entradas no arquivo hosts + proxy reverso local.",
        OsKind.MacOS => "Como o Laravel Valet: dnsmasq + /etc/resolver (ou arquivo hosts) + proxy reverso local.",
        _ => "Como o valet-linux: dnsmasq via NetworkManager (ou arquivo hosts) + proxy reverso local.",
    };

    /// <summary>
    /// Processo rodando como root/administrador (ex.: `sudo dotnet run` para usar as portas 80/443 no Linux,
    /// ou "Executar como administrador" no Windows). Nesse caso não é preciso pedir elevação.
    /// </summary>
    public static bool IsElevated { get; } = Environment.IsPrivilegedProcess;

    /// <summary>Usuário que chamou o sudo (null quando não está rodando via sudo).</summary>
    public static SudoUser? SudoInvoker { get; } = OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess
        ? null
        : SudoUser.FromEnvironment(name => Environment.GetEnvironmentVariable(name));

    /// <summary>Home do usuário real — o de quem chamou o sudo, não o /root.</summary>
    public static string UserHome => SudoInvoker?.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ElevationDescription => SudoInvoker is { } s
        ? $"rodando como root via sudo — arquivos ficam com o usuário {s.Name} e os apps sobem como {s.Name}"
        : IsElevated
        ? (Os == OsKind.Windows ? "rodando como administrador" : "rodando como root")
        : "usuário comum (pede permissão de administrador quando necessário)";

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, int uid, int gid);

    /// <summary>
    /// Rodando via sudo, devolve ao usuário real a posse do arquivo/pasta criado pelo Dot Valet
    /// (config, certificados, launchSettings.json) — senão ele ficaria do root e o próximo uso sem sudo falharia.
    /// </summary>
    public static void GiveBackToUser(string path)
    {
        if (SudoInvoker is not { } s || !Path.Exists(path))
            return;
        if (Chown(path, s.Uid, s.Gid) != 0)
            Trace.WriteLine($"chown {path} falhou: errno {Marshal.GetLastPInvokeError()}");
    }

    /// <summary>Rodando via sudo, faz o processo rodar como o usuário real (`sudo -u usuario -H env ...`).</summary>
    public static void RunAsUser(ProcessStartInfo psi)
    {
        if (SudoInvoker is not { } s)
            return;
        var file = psi.FileName;
        var args = psi.ArgumentList.ToList();
        psi.FileName = FindExecutable("sudo", "/usr/bin/sudo", "/bin/sudo") ?? "sudo";
        psi.ArgumentList.Clear();
        foreach (var a in new[] { "-u", s.Name, "-H", "--", "/usr/bin/env" })
            psi.ArgumentList.Add(a);
        // sudo descarta o ambiente; repassa explicitamente o que importa para o dotnet/brew.
        foreach (var (key, value) in psi.Environment)
        {
            if (value is not null && (key == "PATH" || key.StartsWith("ASPNETCORE_") || key.StartsWith("DOTNET_")))
                psi.ArgumentList.Add($"{key}={value}");
        }
        psi.ArgumentList.Add(file);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
    }

    /// <summary>DOTVALET_HOSTS_FILE permite apontar para outro arquivo (útil para testes).</summary>
    public static string HostsPath => Environment.GetEnvironmentVariable("DOTVALET_HOSTS_FILE") is { Length: > 0 } custom
        ? custom
        : Os == OsKind.Windows
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
        : "/etc/hosts";

    public static string DataDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("DOTVALET_HOME") is { Length: > 0 } custom
                ? custom
                : Path.Combine(UserHome, ".dotvalet");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                GiveBackToUser(dir);
            }
            return dir;
        }
    }

    private static string LinuxDistro()
    {
        try
        {
            var line = File.ReadLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME="));
            return line?["PRETTY_NAME=".Length..].Trim('"') ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static readonly string[] ExtraUnixPaths =
        ["/opt/homebrew/bin", "/opt/homebrew/sbin", "/usr/local/bin", "/usr/local/sbin", "/usr/local/share/dotnet", "/usr/bin", "/usr/sbin", "/bin", "/sbin"];

    /// <summary>
    /// Apps GUI no macOS abertos pelo Finder herdam um PATH mínimo; acrescenta os caminhos usuais
    /// para que dotnet/brew sejam encontrados.
    /// </summary>
    public static void EnrichPath(ProcessStartInfo psi)
    {
        if (Os == OsKind.Windows)
            return;
        var current = psi.Environment.TryGetValue("PATH", out var p) ? p ?? "" : "";
        var parts = current.Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        var home = UserHome;
        foreach (var extra in ExtraUnixPaths.Append(Path.Combine(home, ".dotnet")))
        {
            if (!parts.Contains(extra))
                parts.Add(extra);
        }
        psi.Environment["PATH"] = string.Join(':', parts);
    }

    public static string? FindExecutable(string name, params string[] knownLocations)
    {
        foreach (var loc in knownLocations)
        {
            if (File.Exists(loc))
                return loc;
        }
        var exe = Os == OsKind.Windows && !name.EndsWith(".exe") ? name + ".exe" : name;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (Os != OsKind.Windows)
            dirs = dirs.Concat(ExtraUnixPaths).ToArray();
        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static void OpenUrl(string url)
    {
        try
        {
            if (Os == OsKind.Windows)
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else
                Process.Start(Os == OsKind.MacOS ? "open" : "xdg-open", url);
        }
        catch
        {
            // sem navegador padrão: nada a fazer
        }
    }
}

public sealed record SudoUser(string Name, int Uid, int Gid, string Home)
{
    /// <summary>Lê SUDO_USER/SUDO_UID/SUDO_GID (definidos pelo sudo) e acha a home em /etc/passwd.</summary>
    public static SudoUser? FromEnvironment(Func<string, string?> env, string passwdFile = "/etc/passwd")
    {
        if (env("SUDO_USER") is not { Length: > 0 } name || name == "root"
            || !int.TryParse(env("SUDO_UID"), out var uid) || !int.TryParse(env("SUDO_GID"), out var gid))
            return null;
        return new SudoUser(name, uid, gid, HomeOf(name, passwdFile));
    }

    private static string HomeOf(string name, string passwdFile)
    {
        try
        {
            // nome:senha:uid:gid:gecos:home:shell
            var fields = File.ReadLines(passwdFile).Select(l => l.Split(':')).FirstOrDefault(f => f.Length >= 7 && f[0] == name);
            if (fields is not null && fields[5].Length > 0)
                return fields[5];
        }
        catch
        {
            // sem /etc/passwd legível
        }
        // macOS guarda usuários no Directory Services, não no /etc/passwd.
        return OperatingSystem.IsMacOS() ? Path.Combine("/Users", name) : Path.Combine("/home", name);
    }
}
