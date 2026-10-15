namespace DotValet.Services;

public sealed record DotnetInstall(string Name, string Version);

/// <summary>Localiza o dotnet e lista SDKs/runtimes instalados.</summary>
public sealed class DotnetEnvironment
{
    public string? DotnetPath { get; private set; }
    public IReadOnlyList<string> Sdks { get; private set; } = [];
    public IReadOnlyList<DotnetInstall> Runtimes { get; private set; } = [];

    public IEnumerable<string> AspNetRuntimes => Runtimes
        .Where(r => r.Name is "Microsoft.AspNetCore.App" or "Microsoft.AspNetCore.All")
        .Select(r => r.Version)
        .Distinct();

    public static string? LocateDotnet()
    {
        var home = PlatformInfo.UserHome;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var exe = PlatformInfo.Os == OsKind.Windows ? "dotnet.exe" : "dotnet";
        var known = new List<string>();
        if (!string.IsNullOrEmpty(root))
            known.Add(Path.Combine(root, exe));
        known.AddRange(PlatformInfo.Os switch
        {
            OsKind.Windows =>
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", exe),
                Path.Combine(home, ".dotnet", exe),
            ],
            OsKind.MacOS => ["/usr/local/share/dotnet/dotnet", "/opt/homebrew/bin/dotnet", Path.Combine(home, ".dotnet", "dotnet")],
            _ => ["/usr/share/dotnet/dotnet", "/usr/lib/dotnet/dotnet", "/snap/bin/dotnet", Path.Combine(home, ".dotnet", "dotnet")],
        });
        return PlatformInfo.FindExecutable("dotnet", known.ToArray());
    }

    public async Task RefreshAsync()
    {
        DotnetPath = LocateDotnet();
        if (DotnetPath is null)
            return;

        var sdks = await ProcessRunner.RunAsync(DotnetPath, ["--list-sdks"]);
        Sdks = sdks.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(' ')[0])
            .ToList();

        var runtimes = await ProcessRunner.RunAsync(DotnetPath, ["--list-runtimes"]);
        Runtimes = runtimes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(' '))
            .Where(p => p.Length >= 2)
            .Select(p => new DotnetInstall(p[0], p[1]))
            .ToList();
    }

    /// <summary>
    /// Verifica se existe runtime ASP.NET Core para o major.minor exigido.
    /// (Por padrão o .NET só faz roll-forward de patch, então major.minor precisa existir.)
    /// </summary>
    public bool HasAspNetRuntime(Version required) =>
        AspNetRuntimes.Any(v => Version.TryParse(v.Split('-')[0], out var ver)
                                && ver.Major == required.Major && ver.Minor == required.Minor);
}
