using System.Text.Json.Nodes;
using DotValet.Models;
using DotValet.Services;

namespace DotValet.Tests;

public class LaunchSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotvalet-ls").FullName;
    private string Project => Path.Combine(_dir, "App.csproj");
    private string SettingsPath => Path.Combine(_dir, "Properties", "launchSettings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Existing = """
        {
          // comentário do template
          "profiles": {
            "http": {
              "commandName": "Project",
              "applicationUrl": "http://localhost:5050",
              "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", "FOO": "bar" }
            },
            "IIS Express": { "commandName": "IISExpress" },
          }
        }
        """;

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(SettingsPath))!;

    private (Site, SiteEndpoints) Make(string domain, bool addToExisting = false)
    {
        var site = new Site { ProjectPath = Project, Domain = domain, AppPort = 5100, AddToExistingProfiles = addToExisting };
        return (site, SiteEndpoints.TryResolve(site, new AppConfig(), out _)!);
    }

    [Fact]
    public void Creates_file_and_profile_when_missing()
    {
        var (site, ep) = Make("app2.test:5172");
        LaunchSettingsService.Apply(site, ep);

        var profile = Read()["profiles"]!["DotValet"]!;
        Assert.Equal("http://localhost:5172", profile["applicationUrl"]!.GetValue<string>());
        Assert.Equal("http://app2.test:5172", profile["launchUrl"]!.GetValue<string>());
        Assert.Equal("Project", profile["commandName"]!.GetValue<string>());
    }

    [Fact]
    public void Puts_profile_first_copies_env_and_keeps_others()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, Existing);

        var (site, ep) = Make("app", addToExisting: true);
        LaunchSettingsService.Apply(site, ep);

        var profiles = (JsonObject)Read()["profiles"]!;
        Assert.Equal(["DotValet", "http", "IIS Express"], profiles.Select(p => p.Key).ToArray());
        Assert.Equal("bar", profiles["DotValet"]!["environmentVariables"]!["FOO"]!.GetValue<string>());
        Assert.Equal("http://app.test", profiles["DotValet"]!["launchUrl"]!.GetValue<string>());
        Assert.Equal("http://localhost:5050;http://localhost:5100", profiles["http"]!["applicationUrl"]!.GetValue<string>());
        Assert.True(File.Exists(SettingsPath + ".dotvalet.bak"));
    }

    [Fact]
    public void Reapply_with_new_domain_replaces_previous_url_and_remove_restores()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, Existing);

        var (site, ep) = Make("app", addToExisting: true);
        LaunchSettingsService.Apply(site, ep);

        site.Domain = "app2.test:5172";
        site.LaunchProfileName = "Valet";
        LaunchSettingsService.Apply(site, SiteEndpoints.TryResolve(site, new AppConfig(), out _)!);

        var profiles = (JsonObject)Read()["profiles"]!;
        Assert.False(profiles.ContainsKey("DotValet"));
        Assert.Equal("http://localhost:5050;http://localhost:5172", profiles["http"]!["applicationUrl"]!.GetValue<string>());

        LaunchSettingsService.Remove(site);
        profiles = (JsonObject)Read()["profiles"]!;
        Assert.False(profiles.ContainsKey("Valet"));
        Assert.Equal("http://localhost:5050", profiles["http"]!["applicationUrl"]!.GetValue<string>());
    }
}
