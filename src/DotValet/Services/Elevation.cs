using System.Diagnostics;
using System.Text;

namespace DotValet.Services;

/// <summary>
/// Executa comandos como administrador: osascript no macOS, pkexec no Linux e UAC (runas) no Windows.
/// Com DOTVALET_ELEVATION=simulate o mesmo script roda sem pedir privilégio (usado nos testes).
/// Se o Dot Valet já roda como root/administrador, o script roda direto, sem diálogo.
/// </summary>
public static class Elevation
{
    public static bool Simulate =>
        string.Equals(Environment.GetEnvironmentVariable("DOTVALET_ELEVATION"), "simulate", StringComparison.OrdinalIgnoreCase);

    /// <summary>Aspas simples seguras para /bin/sh.</summary>
    public static string Sh(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>Aspas simples seguras para PowerShell.</summary>
    public static string Ps(string value) => "'" + value.Replace("'", "''") + "'";

    public static string AppleScriptString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string BuildAppleScript(string script, string reason, bool admin) => admin
        ? $"do shell script {AppleScriptString(script)} with prompt {AppleScriptString($"Dot Valet: {reason}")} with administrator privileges"
        : $"do shell script {AppleScriptString(script)}";

    /// <summary>Script PowerShell em UTF-16LE/base64 para -EncodedCommand (evita problemas de aspas no runas).</summary>
    public static string EncodePowerShell(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'\n" + script));

    /// <param name="script">Script sh (macOS/Linux) ou PowerShell (Windows).</param>
    public static async Task<ProcessResult> RunAsync(string script, string reason)
    {
        // Já privilegiado: osascript/pkexec/runas seriam redundantes (e o pkexec pode nem ter agente gráfico).
        var simulate = Simulate || PlatformInfo.IsElevated;
        switch (PlatformInfo.Os)
        {
            case OsKind.MacOS:
                return await ProcessRunner.RunAsync("/usr/bin/osascript", ["-e", BuildAppleScript(script, reason, admin: !simulate)]);

            case OsKind.Linux:
            {
                if (simulate)
                    return await ProcessRunner.RunAsync("/bin/sh", ["-c", script]);
                var pkexec = PlatformInfo.FindExecutable("pkexec");
                if (pkexec is null)
                    return new ProcessResult(-1, "", $"pkexec não encontrado. Execute manualmente com sudo:\n{script}");
                var result = await ProcessRunner.RunAsync(pkexec, ["/bin/sh", "-c", script]);
                // 126 = usuário cancelou / 127 = não autorizado
                return result.ExitCode is 126 or 127
                    ? result with { StdErr = "Operação cancelada ou não autorizada (pkexec). " + result.StdErr }
                    : result;
            }

            default:
            {
                var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {EncodePowerShell(script)}";
                if (simulate)
                    return await ProcessRunner.RunAsync("powershell.exe", args.Split(' '));

                var psi = new ProcessStartInfo("powershell.exe", args)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                try
                {
                    using var p = Process.Start(psi)!;
                    await p.WaitForExitAsync();
                    return new ProcessResult(p.ExitCode, "", p.ExitCode == 0 ? "" : $"PowerShell saiu com código {p.ExitCode}");
                }
                catch (System.ComponentModel.Win32Exception e)
                {
                    // 1223 = usuário cancelou o UAC
                    return new ProcessResult(-1, "", e.NativeErrorCode == 1223 ? "Operação cancelada pelo usuário." : e.Message);
                }
            }
        }
    }
}
