using System.Xml.Linq;

namespace DotValet.Services;

public sealed record ProjectInfo(
    string ProjectFile,
    string Name,
    IReadOnlyList<string> TargetFrameworks,
    bool IsWeb)
{
    public string FrameworkLabel => TargetFrameworks.Count == 0
        ? "framework desconhecido"
        : string.Join(", ", TargetFrameworks.Select(ProjectInspector.Describe).Distinct());

    /// <summary>Versão major.minor do runtime ASP.NET Core exigido (o maior entre os TFMs).</summary>
    public Version? RuntimeVersion => TargetFrameworks
        .Select(ProjectInspector.RuntimeVersionOf)
        .Where(v => v is not null)
        .Max();
}

public static class ProjectInspector
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", ".vs", ".idea", "wwwroot", "packages" };

    public static bool IsProjectFile(string path) =>
        ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Recebe uma pasta ou arquivo de projeto e devolve o projeto web mais provável
    /// (procura até 2 níveis abaixo, como em uma solução com src/Projeto).
    /// </summary>
    public static string? FindProjectFile(string path)
    {
        if (File.Exists(path))
            return IsProjectFile(path) ? Path.GetFullPath(path) : null;
        if (!Directory.Exists(path))
            return null;

        var candidates = EnumerateProjects(path, depth: 0, maxDepth: 2).ToList();
        if (candidates.Count == 0)
            return null;

        return candidates
            .Select(f => (File: f, Info: TryInspect(f)))
            .OrderByDescending(c => c.Info?.IsWeb == true)
            .ThenBy(c => Path.GetRelativePath(path, c.File).Count(ch => ch == Path.DirectorySeparatorChar))
            .ThenBy(c => c.File.Contains("test", StringComparison.OrdinalIgnoreCase))
            .First().File;
    }

    private static IEnumerable<string> EnumerateProjects(string dir, int depth, int maxDepth)
    {
        string[] files, dirs;
        try
        {
            files = Directory.GetFiles(dir);
            dirs = Directory.GetDirectories(dir);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        foreach (var f in files.Where(IsProjectFile).Order())
            yield return f;

        if (depth >= maxDepth)
            yield break;

        foreach (var d in dirs.Order())
        {
            if (SkipDirs.Contains(Path.GetFileName(d)))
                continue;
            foreach (var f in EnumerateProjects(d, depth + 1, maxDepth))
                yield return f;
        }
    }

    public static ProjectInfo? TryInspect(string projectFile)
    {
        try { return Inspect(projectFile); }
        catch { return null; }
    }

    public static ProjectInfo Inspect(string projectFile)
    {
        var doc = XDocument.Load(projectFile);
        var root = doc.Root ?? throw new InvalidDataException("Projeto vazio");

        var sdks = new List<string>();
        if (root.Attribute("Sdk")?.Value is { } sdkAttr)
            sdks.AddRange(sdkAttr.Split(';'));
        sdks.AddRange(root.Descendants().Where(e => e.Name.LocalName == "Sdk")
            .Select(e => e.Attribute("Name")?.Value ?? ""));
        sdks.AddRange(root.Descendants().Where(e => e.Name.LocalName == "Import")
            .Select(e => e.Attribute("Sdk")?.Value ?? ""));

        var packages = root.Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "FrameworkReference")
            .Select(e => e.Attribute("Include")?.Value ?? "")
            .ToList();

        var isWeb = sdks.Any(s => s.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)
                                  || s.StartsWith("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase))
                    || packages.Any(p => p.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));

        var tfms = ReadTargetFrameworks(root);
        if (tfms.Count == 0)
            tfms = ReadTargetFrameworksFromDirectoryBuildProps(Path.GetDirectoryName(projectFile)!);

        return new ProjectInfo(Path.GetFullPath(projectFile), Path.GetFileNameWithoutExtension(projectFile), tfms, isWeb);
    }

    private static List<string> ReadTargetFrameworks(XElement root)
    {
        var value = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value
                    ?? root.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value
                    ?? "";
        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !t.Contains("$(")) // propriedades MSBuild não resolvidas
            .ToList();
    }

    private static List<string> ReadTargetFrameworksFromDirectoryBuildProps(string dir)
    {
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var props = Path.Combine(d.FullName, "Directory.Build.props");
            if (!File.Exists(props))
                continue;
            try
            {
                var tfms = ReadTargetFrameworks(XDocument.Load(props).Root!);
                if (tfms.Count > 0)
                    return tfms;
            }
            catch
            {
                // arquivo inválido: ignora
            }
        }
        return [];
    }

    /// <summary>netcoreapp2.1 → ".NET Core 2.1", net8.0 → ".NET 8".</summary>
    public static string Describe(string tfm)
    {
        var t = tfm.ToLowerInvariant();
        var platform = t.IndexOf('-');
        if (platform >= 0)
            t = t[..platform];

        if (t.StartsWith("netcoreapp", StringComparison.Ordinal))
            return $".NET Core {t["netcoreapp".Length..]}";
        if (t.StartsWith("netstandard", StringComparison.Ordinal))
            return $".NET Standard {t["netstandard".Length..]}";
        if (t.StartsWith("net", StringComparison.Ordinal))
        {
            var v = t[3..];
            if (Version.TryParse(v, out var ver) && ver.Major >= 5)
                return ver.Minor == 0 ? $".NET {ver.Major}" : $".NET {ver.Major}.{ver.Minor}";
            return $".NET Framework {v}";
        }
        return tfm;
    }

    public static Version? RuntimeVersionOf(string tfm)
    {
        var t = tfm.ToLowerInvariant();
        var platform = t.IndexOf('-');
        if (platform >= 0)
            t = t[..platform];

        string? v = null;
        if (t.StartsWith("netcoreapp", StringComparison.Ordinal))
            v = t["netcoreapp".Length..];
        else if (t.StartsWith("net", StringComparison.Ordinal) && !t.StartsWith("netstandard", StringComparison.Ordinal))
            v = t[3..];

        if (v is null || !Version.TryParse(v, out var ver))
            return null;
        // net48, net472... são .NET Framework (sem ponto) e não rodam no Kestrel do .NET Core
        if (!v.Contains('.'))
            return null;
        if (!t.StartsWith("netcoreapp", StringComparison.Ordinal) && ver.Major < 5)
            return null;
        return new Version(ver.Major, ver.Minor);
    }
}
