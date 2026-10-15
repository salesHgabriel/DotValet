using System.Diagnostics;

namespace DotValet.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
    public string Output => string.IsNullOrWhiteSpace(StdErr) ? StdOut : $"{StdOut}\n{StdErr}".Trim();
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args,
        string? workingDirectory = null, CancellationToken ct = default, bool asUser = false)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        PlatformInfo.EnrichPath(psi);
        if (asUser)
            PlatformInfo.RunAsUser(psi);

        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return new ProcessResult(p.ExitCode, await stdout, await stderr);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, "", e.Message);
        }
    }
}
