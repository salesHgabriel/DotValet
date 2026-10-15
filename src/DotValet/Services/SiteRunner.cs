using System.Collections.Concurrent;
using System.Diagnostics;
using DotValet.Models;

namespace DotValet.Services;

/// <summary>Sobe/derruba `dotnet run` (ou `dotnet watch`) para cada site.</summary>
public sealed class SiteRunner
{
    private readonly ConcurrentDictionary<string, Process> _processes = new();

    public bool IsRunning(string siteId) => _processes.TryGetValue(siteId, out var p) && !p.HasExited;

    public void Start(Site site, SiteEndpoints ep, string dotnetPath, Action<string> onOutput, Action<int> onExit)
    {
        if (IsRunning(site.Id))
            return;

        var psi = new ProcessStartInfo(dotnetPath)
        {
            WorkingDirectory = Path.GetDirectoryName(site.ProjectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (site.UseWatch)
            psi.ArgumentList.Add("watch");
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(site.ProjectPath);
        // As URLs vêm de ASPNETCORE_URLS, que funciona do ASP.NET Core 2.x ao atual,
        // independentemente do launchSettings.json.
        psi.ArgumentList.Add("--no-launch-profile");

        psi.Environment["ASPNETCORE_URLS"] = ep.BindingUrl;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["DOTNET_ENVIRONMENT"] = "Development";
        psi.Environment["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        PlatformInfo.EnrichPath(psi);
        // Com o Dot Valet rodando via sudo, bin/ e obj/ do projeto ficariam do root.
        PlatformInfo.RunAsUser(psi);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
        process.Exited += (_, _) =>
        {
            _processes.TryRemove(site.Id, out _);
            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            process.Dispose();
            onExit(code);
        };

        onOutput($"$ {(site.UseWatch ? "dotnet watch run" : "dotnet run")} --no-launch-profile  (ASPNETCORE_URLS={ep.BindingUrl})");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes[site.Id] = process;
    }

    public void Stop(string siteId)
    {
        if (_processes.TryRemove(siteId, out var p))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* já encerrado */ }
        }
    }

    public void StopAll()
    {
        foreach (var id in _processes.Keys.ToList())
            Stop(id);
    }
}
