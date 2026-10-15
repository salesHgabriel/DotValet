using DotValet.Services;

namespace DotValet.Tests;

public class ValetTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("dotvalet-valet").FullName;

    public ValetTests() => Environment.SetEnvironmentVariable("DOTVALET_ELEVATION", "simulate");

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void Plans_only_the_differences()
    {
        var a = new ValetProxy("a.test", "http://localhost:5100", false);
        var b = new ValetProxy("b.test", "http://localhost:5101", true);
        var bMoved = b with { Target = "http://localhost:5200" };

        Assert.Empty(ValetService.PlanSync([a, b], [a, b]));
        Assert.Equal(
            [["unproxy", "b.test"], ["proxy", "b.test", "http://localhost:5200", "--secure"]],
            ValetService.PlanSync([a, bMoved], [a, b]).Select(c => c.ToArray()));
        Assert.Equal([["unproxy", "a.test"], ["unproxy", "b.test"]], ValetService.PlanSync([], [a, b]).Select(c => c.ToArray()));
    }

    [MacFact]
    public void Detects_installation_and_tld_from_home()
    {
        var none = new ValetService(_home, valetPath: null);
        Assert.Equal("test", none.Tld);

        Directory.CreateDirectory(Path.Combine(_home, ".config", "valet"));
        File.WriteAllText(Path.Combine(_home, ".config", "valet", "config.json"), """{ "tld": "local", "paths": [] }""");
        var valet = new ValetService(_home);
        Assert.True(valet.IsInstalled);
        Assert.Equal("local", valet.Tld);
    }

    [UnixFact]
    public async Task Runs_valet_once_with_user_home_and_homebrew_path()
    {
        if (OperatingSystem.IsWindows())
            return;
        // valet falso que registra argumentos e ambiente, como o script real veria rodando como root.
        var log = Path.Combine(_home, "valet.log");
        var fake = Path.Combine(_home, "valet com espaço");
        File.WriteAllText(fake, $"#!/bin/sh\necho \"$USER|$HOME|$PATH|$*\" >> {Elevation.Sh(log)}\n");
        File.SetUnixFileMode(fake, (UnixFileMode)0b111_101_101);
        var valet = new ValetService(_home, fake);

        var desired = new[] { new ValetProxy("app.test", "http://localhost:5100", false), new ValetProxy("seguro.test", "http://localhost:5101", true) };
        var result = await valet.SyncProxiesAsync(desired, [new ValetProxy("velho.test", "http://localhost:5000", false)]);

        Assert.True(result!.Success, result.Output);
        var lines = File.ReadAllLines(log);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.StartsWith($"{Environment.UserName}|{_home}|/opt/homebrew/bin:", l));
        Assert.EndsWith("|unproxy velho.test", lines[0]);
        Assert.EndsWith("|proxy app.test http://localhost:5100", lines[1]);
        Assert.EndsWith("|proxy seguro.test http://localhost:5101 --secure", lines[2]);

        Assert.Null(await valet.SyncProxiesAsync(desired, desired));

        Assert.True((await valet.StopAsync()).Success);
        Assert.EndsWith("|stop", File.ReadAllLines(log).Last());
    }

    [UnixFact]
    public async Task Failure_in_valet_is_reported()
    {
        if (OperatingSystem.IsWindows())
            return;
        var fake = Path.Combine(_home, "valet");
        File.WriteAllText(fake, "#!/bin/sh\necho 'nginx: configuration test failed' >&2\nexit 1\n");
        File.SetUnixFileMode(fake, (UnixFileMode)0b111_101_101);

        var result = await new ValetService(_home, fake).SyncProxiesAsync([new ValetProxy("app.test", "http://localhost:5100", false)], []);

        Assert.False(result!.Success);
        Assert.Contains("configuration test failed", result.Output);
    }
}
