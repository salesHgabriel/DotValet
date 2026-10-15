using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotValet.Models;

namespace DotValet.Services;

/// <summary>
/// Escreve a URL do Dot Valet no Properties/launchSettings.json (suportado desde o ASP.NET Core 1.x/2.x),
/// para que `dotnet run`, Visual Studio, Rider e VS Code subam o app já no domínio .test.
/// </summary>
public static class LaunchSettingsService
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string GetPath(string projectFile) =>
        Path.Combine(Path.GetDirectoryName(projectFile)!, "Properties", "launchSettings.json");

    /// <summary>O perfil que o Dot Valet cria/atualiza.</summary>
    public static JsonObject BuildProfile(SiteEndpoints ep, JsonObject? template)
    {
        var env = template?["environmentVariables"]?.DeepClone() as JsonObject
                  ?? new JsonObject { ["ASPNETCORE_ENVIRONMENT"] = "Development" };

        return new JsonObject
        {
            ["commandName"] = "Project",
            ["dotnetRunMessages"] = true,
            ["launchBrowser"] = true,
            // URL absoluta: a IDE abre direto no domínio (ex.: http://app.test ou http://app2.test:5172).
            ["launchUrl"] = ep.PrimaryUrl,
            ["applicationUrl"] = ep.BindingUrl,
            ["environmentVariables"] = env,
        };
    }

    public static string PreviewProfile(string profileName, SiteEndpoints ep) =>
        new JsonObject { [profileName] = BuildProfile(ep, null) }.ToJsonString(WriteOptions);

    public static void Apply(Site site, SiteEndpoints ep)
    {
        var path = GetPath(site.ProjectPath);
        var root = Load(path);
        var profiles = root["profiles"] as JsonObject ?? new JsonObject();

        // Desfaz o que foi aplicado antes (troca de domínio/porta/nome do perfil).
        if (site.AppliedProfileName is { } oldName && oldName != site.LaunchProfileName)
            profiles.Remove(oldName);
        if (site.AppliedBindingUrl is { } oldUrl)
            RemoveUrlFromProfiles(profiles, oldUrl);

        var template = profiles
            .Where(p => p.Key != site.LaunchProfileName)
            .Select(p => p.Value as JsonObject)
            .FirstOrDefault(p => p?["commandName"]?.GetValue<string>() == "Project");

        var ours = BuildProfile(ep, template);

        if (site.AddToExistingProfiles)
        {
            foreach (var (name, node) in profiles)
            {
                if (name == site.LaunchProfileName || node is not JsonObject p
                    || p["commandName"]?.GetValue<string>() != "Project")
                    continue;
                var urls = SplitUrls(p["applicationUrl"]?.GetValue<string>());
                if (!urls.Contains(ep.BindingUrl, StringComparer.OrdinalIgnoreCase))
                    urls.Add(ep.BindingUrl);
                p["applicationUrl"] = string.Join(';', urls);
            }
        }

        // Nosso perfil fica em primeiro: `dotnet run` usa o primeiro perfil "Project".
        var reordered = new JsonObject { [site.LaunchProfileName] = ours };
        foreach (var (name, node) in profiles.ToList())
        {
            if (name == site.LaunchProfileName)
                continue;
            profiles.Remove(name);
            reordered[name] = node;
        }
        root["profiles"] = reordered;

        Save(path, root);
        site.AppliedBindingUrl = ep.BindingUrl;
        site.AppliedProfileName = site.LaunchProfileName;
    }

    public static void Remove(Site site)
    {
        var path = GetPath(site.ProjectPath);
        if (!File.Exists(path) || (site.AppliedProfileName is null && site.AppliedBindingUrl is null))
            return;

        var root = Load(path);
        if (root["profiles"] is JsonObject profiles)
        {
            if (site.AppliedProfileName is { } name)
                profiles.Remove(name);
            if (site.AppliedBindingUrl is { } url)
                RemoveUrlFromProfiles(profiles, url);
        }
        Save(path, root);
        site.AppliedBindingUrl = null;
        site.AppliedProfileName = null;
    }

    private static void RemoveUrlFromProfiles(JsonObject profiles, string url)
    {
        foreach (var (_, node) in profiles)
        {
            if (node is not JsonObject p || p["applicationUrl"]?.GetValue<string>() is not { } current)
                continue;
            var urls = SplitUrls(current);
            if (urls.RemoveAll(u => u.Equals(url, StringComparison.OrdinalIgnoreCase)) == 0)
                continue;
            if (urls.Count == 0)
                p.Remove("applicationUrl");
            else
                p["applicationUrl"] = string.Join(';', urls);
        }
    }

    private static List<string> SplitUrls(string? value) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path))
            return new JsonObject { ["profiles"] = new JsonObject() };
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject { ["profiles"] = new JsonObject() };
        return JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
               ?? throw new InvalidDataException($"{path} não é um objeto JSON.");
    }

    private static void Save(string path, JsonObject root)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            PlatformInfo.GiveBackToUser(dir);
        }
        // Backup do original na primeira alteração (comentários do JSON não são preservados).
        var backup = path + ".dotvalet.bak";
        if (File.Exists(path) && !File.Exists(backup))
        {
            File.Copy(path, backup);
            PlatformInfo.GiveBackToUser(backup);
        }
        File.WriteAllText(path, root.ToJsonString(WriteOptions) + Environment.NewLine);
        PlatformInfo.GiveBackToUser(path);
    }
}
