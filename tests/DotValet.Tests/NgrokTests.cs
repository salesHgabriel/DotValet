using System.Text.Json.Nodes;
using DotValet.Models;
using DotValet.Services;

namespace DotValet.Tests;

public class NgrokTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotvalet-ngrok").FullName;
    private string Project => Path.Combine(_dir, "App.csproj");
    private string SettingsPath => Path.Combine(_dir, "Properties", "launchSettings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JsonObject Profiles() => (JsonObject)JsonNode.Parse(File.ReadAllText(SettingsPath))!["profiles"]!;

    private (Site, SiteEndpoints) Make(string domain = "app")
    {
        var site = new Site { ProjectPath = Project, Domain = domain, AppPort = 5100 };
        return (site, SiteEndpoints.TryResolve(site, new AppConfig(), out _)!);
    }

    [Theory]
    [InlineData("meu-app.ngrok-free.app", "meu-app.ngrok-free.app")]
    [InlineData("  Meu-App.ngrok-free.app  ", "meu-app.ngrok-free.app")]
    [InlineData("https://meu-app.ngrok-free.app/", "meu-app.ngrok-free.app")]
    [InlineData("https://meu-app.ngrok.app/alguma/rota", "meu-app.ngrok.app")]
    [InlineData("api.minhaempresa.com.br", "api.minhaempresa.com.br")]
    public void Normalizes_domain(string input, string expected)
    {
        Assert.True(NgrokService.TryNormalizeDomain(input, out var domain, out var error));
        Assert.Null(error);
        Assert.Equal(expected, domain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_domain_means_random_url(string? input)
    {
        Assert.True(NgrokService.TryNormalizeDomain(input, out var domain, out _));
        Assert.Null(domain);
    }

    [Theory]
    [InlineData("meu app.ngrok-free.app")]
    [InlineData("ftp://meu-app.ngrok-free.app")]
    [InlineData("localhost")]
    [InlineData("-x.ngrok.app")]
    public void Rejects_invalid_domain(string input)
    {
        Assert.False(NgrokService.TryNormalizeDomain(input, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Builds_arguments_with_url_or_legacy_domain_flag()
    {
        Assert.Equal(["http", "http://localhost:5100", "--log", "stdout", "--log-format", "json"],
            NgrokService.BuildArguments("http://localhost:5100", null));
        Assert.Contains("--url=https://meu-app.ngrok-free.app", NgrokService.BuildArguments("http://localhost:5100", "meu-app.ngrok-free.app"));
        Assert.Contains("--domain=meu-app.ngrok-free.app",
            NgrokService.BuildArguments("http://localhost:5100", "meu-app.ngrok-free.app", legacyDomainFlag: true));
    }

    [Fact]
    public void Parses_started_tunnel_and_errors_from_log()
    {
        var started = NgrokService.ParseLogLine(
            """{"addr":"http://localhost:5100","lvl":"info","msg":"started tunnel","name":"command_line","obj":"tunnels","t":"2026-10-16T10:00:00-03:00","url":"https://meu-app.ngrok-free.app"}""");
        Assert.Equal("https://meu-app.ngrok-free.app", started?.PublicUrl);

        var failed = NgrokService.ParseLogLine(
            """{"err":"authentication failed: The authtoken you specified does not look like a proper ngrok tunnel authtoken.\n\nERR_NGROK_105","lvl":"eror","msg":"failed to reconnect session","obj":"tunnels.session"}""");
        Assert.Contains("ERR_NGROK_105", failed?.Error);
        Assert.NotNull(NgrokService.Hint(failed!.Error!));

        Assert.Equal("ERR_NGROK_108", NgrokService.ParseLogLine("ERROR:  ERR_NGROK_108")?.Error);
        Assert.Null(NgrokService.ParseLogLine("""{"lvl":"info","msg":"client session established","obj":"tunnels.session"}"""));
        var refused = NgrokService.ParseLogLine(
            """{"lvl":"warn","msg":"failed to open private leg","err":"dial tcp [::1]:5100: connect: connection refused","obj":"tunnels.session"}""");
        Assert.Contains("connection refused", refused?.Error);
        Assert.Contains("▶ Iniciar", NgrokService.Hint(refused!.Error!));
        Assert.Contains("▶ Iniciar", NgrokService.Hint("ERR_NGROK_8012"));
        Assert.Null(NgrokService.ParseLogLine("texto qualquer"));
        Assert.Null(NgrokService.ParseLogLine("{json quebrado"));
    }

    [Fact]
    public void Ngrok_profile_comes_after_main_profile_and_keeps_local_binding()
    {
        var (site, ep) = Make();
        LaunchSettingsService.Apply(site, ep);
        LaunchSettingsService.ApplyNgrok(site, ep, "https://meu-app.ngrok-free.app");

        var profiles = Profiles();
        Assert.Equal(["DotValet", "DotValet-ngrok"], profiles.Select(p => p.Key).ToArray());
        var ngrok = profiles["DotValet-ngrok"]!;
        Assert.Equal("https://meu-app.ngrok-free.app", ngrok["launchUrl"]!.GetValue<string>());
        Assert.Equal("http://localhost:5100", ngrok["applicationUrl"]!.GetValue<string>());
        Assert.Equal("DotValet-ngrok", site.AppliedNgrokProfileName);

        // Reaplicar o perfil principal (ex.: troca de domínio) mantém o do ngrok depois dele.
        LaunchSettingsService.Apply(site, ep);
        LaunchSettingsService.ApplyNgrok(site, ep, "https://outro.ngrok-free.app");
        profiles = Profiles();
        Assert.Equal(["DotValet", "DotValet-ngrok"], profiles.Select(p => p.Key).ToArray());
        Assert.Equal("https://outro.ngrok-free.app", profiles["DotValet-ngrok"]!["launchUrl"]!.GetValue<string>());
    }

    [Fact]
    public void Renaming_profile_replaces_ngrok_profile_and_remove_undoes_everything()
    {
        var (site, ep) = Make();
        LaunchSettingsService.Apply(site, ep);
        LaunchSettingsService.ApplyNgrok(site, ep, "https://meu-app.ngrok-free.app");

        site.LaunchProfileName = "Local";
        LaunchSettingsService.Apply(site, ep);
        LaunchSettingsService.ApplyNgrok(site, ep, "https://meu-app.ngrok-free.app");
        Assert.Equal(["Local", "Local-ngrok"], Profiles().Select(p => p.Key).ToArray());

        LaunchSettingsService.RemoveNgrok(site);
        Assert.Equal(["Local"], Profiles().Select(p => p.Key).ToArray());
        Assert.Null(site.AppliedNgrokProfileName);

        LaunchSettingsService.ApplyNgrok(site, ep, "https://meu-app.ngrok-free.app");
        LaunchSettingsService.Remove(site);
        Assert.Empty(Profiles());
        Assert.Null(site.AppliedNgrokProfileName);
    }

    [Fact]
    public void Ngrok_profile_works_without_main_profile()
    {
        var (site, ep) = Make("app2.test:5172");
        site.UpdateLaunchSettings = false;
        LaunchSettingsService.ApplyNgrok(site, ep, "https://meu-app.ngrok-free.app");

        var profile = Profiles()["DotValet-ngrok"]!;
        Assert.Equal("http://localhost:5172", profile["applicationUrl"]!.GetValue<string>());
        LaunchSettingsService.Remove(site);
        Assert.Empty(Profiles());
    }

    [Fact]
    public void Config_with_token_is_readable_only_by_owner()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(_dir, "config.json");
        ConfigStore.Save(new AppConfig { NgrokAuthToken = "segredo" }, path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Contains("segredo", File.ReadAllText(path));
    }

    /// <summary>Script no lugar do ngrok: com --url responde "unknown flag" (agente antigo); com --domain abre o túnel.</summary>
    private string FakeNgrok(bool supportsUrlFlag)
    {
        var path = Path.Combine(_dir, "ngrok");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            for a in "$@"; do
              case "$a" in
                --url=*) if [ "{{(supportsUrlFlag ? "1" : "0")}}" = "0" ]; then echo "ERROR:  unknown flag: --url" >&2; exit 1; fi; host="${a#--url=https://}" ;;
                --domain=*) host="${a#--domain=}" ;;
              esac
            done
            [ -z "$NGROK_AUTHTOKEN" ] && { echo "ERROR:  ERR_NGROK_4018" >&2; exit 1; }
            echo '{"lvl":"info","msg":"started tunnel","url":"https://'"$host"'"}'
            sleep 30
            """.Replace("\r", ""));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task<(string? Url, string? Error, bool Exited, List<string> Log)> Share(NgrokService ngrok, string exe, string? token,
        bool stopAfterUrl = true)
    {
        var url = new TaskCompletionSource<string>();
        var exit = new TaskCompletionSource<string?>();
        var log = new List<string>();
        ngrok.Start("site", exe, "http://localhost:5100", "meu-app.ngrok-free.app", token,
            line => { lock (log) log.Add(line); }, u => url.TrySetResult(u), e => exit.TrySetResult(e));
        var first = await Task.WhenAny(url.Task, exit.Task).WaitAsync(TimeSpan.FromSeconds(15));
        if (first == url.Task && stopAfterUrl)
        {
            Assert.True(ngrok.IsSharing("site"));
            ngrok.Stop("site");
            await exit.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        return (url.Task.IsCompleted ? url.Task.Result : null, exit.Task.IsCompleted ? exit.Task.Result : null, exit.Task.IsCompleted, log);
    }

    [UnixFact]
    public async Task Opens_tunnel_and_stops_without_error()
    {
        var (url, error, exited, _) = await Share(new NgrokService(), FakeNgrok(supportsUrlFlag: true), "token");
        Assert.Equal("https://meu-app.ngrok-free.app", url);
        Assert.True(exited);
        Assert.Null(error);
    }

    [UnixFact]
    public async Task Falls_back_to_domain_flag_on_old_agents()
    {
        var (url, _, _, log) = await Share(new NgrokService(), FakeNgrok(supportsUrlFlag: false), "token");
        Assert.Equal("https://meu-app.ngrok-free.app", url);
        Assert.Contains(log, l => l.Contains("--domain"));
    }

    [UnixFact]
    public async Task Reports_missing_token()
    {
        var ngrok = new NgrokService();
        var (url, error, exited, _) = await Share(ngrok, FakeNgrok(supportsUrlFlag: true), token: null);
        Assert.Null(url);
        Assert.True(exited);
        Assert.Contains("ERR_NGROK_4018", error);
        Assert.Equal("Salve o authtoken do ngrok na aba Ambiente.", NgrokService.Hint(error!));
        Assert.False(ngrok.IsSharing("site"));
    }

    [Fact]
    public async Task Targets_the_loopback_address_the_app_listens_on()
    {
        // Só IPv4 (como um app em http://127.0.0.1:porta): "localhost" poderia virar [::1] no ngrok e falhar.
        using var v4 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        v4.Start();
        var port = ((System.Net.IPEndPoint)v4.LocalEndpoint).Port;
        Assert.Equal(($"http://127.0.0.1:{port}", true), await NgrokService.ResolveTargetAsync(port));
        v4.Stop();

        using var v6 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Loopback, port);
        v6.Start();
        Assert.Equal(($"http://[::1]:{port}", true), await NgrokService.ResolveTargetAsync(port));
        v6.Stop();

        Assert.Equal(($"http://127.0.0.1:{port}", false), await NgrokService.ResolveTargetAsync(port));
    }
}
