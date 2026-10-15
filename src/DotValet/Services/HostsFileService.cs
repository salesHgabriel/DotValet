using System.Text;

namespace DotValet.Services;

/// <summary>
/// Mantém um bloco próprio no arquivo hosts, sem tocar no resto do arquivo.
/// </summary>
public sealed class HostsFileService
{
    public const string BeginMarker = "# >>> Dot Valet (gerenciado automaticamente, não edite) >>>";
    public const string EndMarker = "# <<< Dot Valet <<<";

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string HostsPath { get; init; } = PlatformInfo.HostsPath;

    /// <summary>
    /// Backups do hosts (fora de /etc, para não exigir admin): o <b>principal</b> é o hosts como estava antes
    /// da primeira alteração do Dot Valet e nunca é sobrescrito; o <b>último</b> é o estado anterior à alteração
    /// mais recente e é substituído a cada mudança.
    /// </summary>
    public string BackupDirectory
    {
        get => _backupDirectory ??= Path.Combine(PlatformInfo.DataDirectory, "backups");
        init => _backupDirectory = value;
    }

    private string? _backupDirectory;

    public bool BackupEnabled { get; set; } = true;

    public string PrincipalBackupPath => Path.Combine(BackupDirectory, "hosts.principal.bak");
    public string LastBackupPath => Path.Combine(BackupDirectory, "hosts.ultimo.bak");

    public static string BuildContent(string existing, IEnumerable<string> hosts)
    {
        var newline = existing.Contains("\r\n") ? "\r\n" : "\n";
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();

        var output = new List<string>();
        var inBlock = false;
        foreach (var line in lines)
        {
            if (line.Trim() == BeginMarker) { inBlock = true; continue; }
            if (line.Trim() == EndMarker) { inBlock = false; continue; }
            if (!inBlock)
                output.Add(line);
        }
        while (output.Count > 0 && output[^1].Trim().Length == 0)
            output.RemoveAt(output.Count - 1);

        var unique = hosts.Select(h => h.ToLowerInvariant()).Distinct().Order().ToList();
        if (unique.Count > 0)
        {
            output.Add("");
            output.Add(BeginMarker);
            foreach (var h in unique)
            {
                output.Add($"127.0.0.1\t{h}");
                output.Add($"::1\t{h}");
            }
            output.Add(EndMarker);
        }

        return string.Join(newline, output) + newline;
    }

    public static IReadOnlyList<string> ReadManagedHosts(string content)
    {
        var result = new List<string>();
        var inBlock = false;
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line == BeginMarker) { inBlock = true; continue; }
            if (line == EndMarker) { inBlock = false; continue; }
            if (inBlock && line.StartsWith("127.0.0.1"))
                result.Add(line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Last());
        }
        return result;
    }

    /// <summary>
    /// Script (rodado como admin) que copia o conteúdo para o hosts. `cat >` preserva dono, permissões e o
    /// symlink /etc/hosts → /private/etc/hosts do macOS. Falha na cópia precisa virar código de saída ≠ 0;
    /// a limpeza do cache de DNS é opcional.
    /// </summary>
    public static string BuildElevatedCopyScript(OsKind os, string source, string hostsPath) => os switch
    {
        OsKind.Windows => $"Copy-Item -Force -LiteralPath {Elevation.Ps(source)} -Destination {Elevation.Ps(hostsPath)}; ipconfig /flushdns | Out-Null",
        OsKind.MacOS => $"cat {Elevation.Sh(source)} > {Elevation.Sh(hostsPath)} || exit 1; dscacheutil -flushcache 2>/dev/null; killall -HUP mDNSResponder 2>/dev/null; exit 0",
        _ => $"cat {Elevation.Sh(source)} > {Elevation.Sh(hostsPath)}",
    };

    /// <returns>null quando já estava atualizado; senão o resultado da escrita elevada.</returns>
    public async Task<ProcessResult?> SyncAsync(IEnumerable<string> hosts)
    {
        await _lock.WaitAsync();
        try
        {
            var existing = File.Exists(HostsPath) ? await File.ReadAllTextAsync(HostsPath) : "";
            var updated = BuildContent(existing, hosts);
            if (updated == existing)
                return null;
            return await BackupAndWriteAsync(existing, updated);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Volta o hosts para o backup principal ou o último. A restauração também gera um novo "último".</summary>
    /// <returns>null quando o hosts já era igual ao backup.</returns>
    public async Task<ProcessResult?> RestoreAsync(bool principal)
    {
        await _lock.WaitAsync();
        try
        {
            var backup = principal ? PrincipalBackupPath : LastBackupPath;
            if (!File.Exists(backup))
                return new ProcessResult(-1, "", $"backup não encontrado: {backup}");
            var content = await File.ReadAllTextAsync(backup);
            var existing = File.Exists(HostsPath) ? await File.ReadAllTextAsync(HostsPath) : "";
            if (content == existing)
                return null;
            return await BackupAndWriteAsync(existing, content);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Guarda o principal (só na primeira vez) e troca o último pelo conteúdo atual, antes de alterar.</summary>
    private async Task BackupAsync(string existing)
    {
        if (!BackupEnabled || !File.Exists(HostsPath))
            return;
        if (!Directory.Exists(BackupDirectory))
        {
            Directory.CreateDirectory(BackupDirectory);
            PlatformInfo.GiveBackToUser(BackupDirectory);
        }
        if (!File.Exists(PrincipalBackupPath))
        {
            await File.WriteAllTextAsync(PrincipalBackupPath, existing, new UTF8Encoding(false));
            PlatformInfo.GiveBackToUser(PrincipalBackupPath);
        }
        if (File.Exists(LastBackupPath))
            File.Delete(LastBackupPath);
        await File.WriteAllTextAsync(LastBackupPath, existing, new UTF8Encoding(false));
        PlatformInfo.GiveBackToUser(LastBackupPath);
    }

    private async Task<ProcessResult> BackupAndWriteAsync(string existing, string updated)
    {
        try
        {
            await BackupAsync(existing);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Sem backup, não altera o hosts.
            return new ProcessResult(-1, "", $"não foi possível criar o backup em {BackupDirectory}: {e.Message}");
        }

        {
            // Se o usuário já tem permissão de escrita (ex.: app rodando como admin), não pede elevação.
            try
            {
                await File.WriteAllTextAsync(HostsPath, updated, new UTF8Encoding(false));
                return new ProcessResult(0, "", "");
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
            }

            var tmp = Path.Combine(Path.GetTempPath(), $"dotvalet-hosts-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(tmp, updated, new UTF8Encoding(false));
            try
            {
                var result = await Elevation.RunAsync(BuildElevatedCopyScript(PlatformInfo.Os, tmp, HostsPath), $"atualizar {HostsPath}");
                // Confere o resultado de fato: o código de saída do runas/osascript nem sempre é confiável.
                if (result.Success && await File.ReadAllTextAsync(HostsPath) != updated)
                    return new ProcessResult(1, result.StdOut, $"o conteúdo de {HostsPath} não foi alterado. {result.StdErr}".Trim());
                return result;
            }
            finally
            {
                File.Delete(tmp);
            }
        }
    }
}
