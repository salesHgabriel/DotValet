using System.Security.Cryptography.X509Certificates;
using DotValet.Models;
using DotValet.Services;

namespace DotValet.Tests;

/// <summary>
/// Fluxos que exigem administrador. Rodam com DOTVALET_ELEVATION=simulate (sem diálogo de senha).
/// Os marcados com <see cref="RealAdminFact"/> só rodam como root com DOTVALET_REAL_ADMIN_TESTS=1
/// (ex.: dentro de um container) porque alteram o sistema de verdade.
/// </summary>
public class AdminTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotvalet-admin").FullName;

    public AdminTests() => Environment.SetEnvironmentVariable("DOTVALET_ELEVATION", "simulate");

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        if (!OperatingSystem.IsWindows())
            foreach (var d in Directory.EnumerateDirectories(_dir, "*", SearchOption.AllDirectories).Append(_dir))
                File.SetUnixFileMode(d, (UnixFileMode)0b111_000_000);
        Directory.Delete(_dir, recursive: true);
    }

    // ---------------- hosts ----------------

    [UnixFact]
    public async Task Elevated_copy_script_copies_and_reports_failure()
    {
        var src = Path.Combine(_dir, "src");
        await File.WriteAllTextAsync(src, "127.0.0.1\tnovo.test\n");
        foreach (var os in new[] { OsKind.MacOS, OsKind.Linux })
        {
            var target = Path.Combine(_dir, $"hosts-{os}");
            await File.WriteAllTextAsync(target, "antigo\n");
            var ok = await ProcessRunner.RunAsync("/bin/sh", ["-c", HostsFileService.BuildElevatedCopyScript(os, src, target)]);
            Assert.True(ok.Success, ok.Output);
            Assert.Equal("127.0.0.1\tnovo.test\n", await File.ReadAllTextAsync(target));

            // Antes, o "; true" do script do macOS escondia a falha do cat e o app dizia "hosts atualizado".
            var bad = Path.Combine(_dir, "nao-existe", "hosts");
            var fail = await ProcessRunner.RunAsync("/bin/sh", ["-c", HostsFileService.BuildElevatedCopyScript(os, src, bad)]);
            Assert.False(fail.Success, $"{os}: falha na cópia deveria dar código ≠ 0");
        }
    }

    [UnixFact(skipWhenRoot: true)]
    public async Task Sync_reports_failure_when_hosts_cannot_be_written()
    {
        if (OperatingSystem.IsWindows())
            return;
        var hosts = Path.Combine(_dir, "hosts");
        await File.WriteAllTextAsync(hosts, "127.0.0.1\tlocalhost\n");
        File.SetUnixFileMode(hosts, UnixFileMode.UserRead);

        // Escrita direta falha → cai no caminho "elevado" (simulado = mesmo usuário) → também falha.
        var result = await new HostsFileService { HostsPath = hosts, BackupDirectory = Path.Combine(_dir, "bkp") }.SyncAsync(["app.test"]);

        Assert.NotNull(result);
        Assert.False(result.Success, "não pode reportar sucesso sem ter escrito o hosts");
        Assert.Equal("127.0.0.1\tlocalhost\n", await File.ReadAllTextAsync(hosts));
    }

    [Fact]
    public async Task Sync_writes_block_and_is_noop_when_unchanged()
    {
        var hosts = Path.Combine(_dir, "hosts");
        await File.WriteAllTextAsync(hosts, "127.0.0.1\tlocalhost\n");
        var svc = new HostsFileService { HostsPath = hosts, BackupDirectory = Path.Combine(_dir, "bkp") };

        var first = await svc.SyncAsync(["app.test"]);
        Assert.True(first!.Success, first.Output);
        Assert.Equal(["app.test"], HostsFileService.ReadManagedHosts(await File.ReadAllTextAsync(hosts)));
        Assert.Null(await svc.SyncAsync(["app.test"]));

        var removed = await svc.SyncAsync([]);
        Assert.True(removed!.Success);
        Assert.Equal("127.0.0.1\tlocalhost\n", await File.ReadAllTextAsync(hosts));
    }

    [Fact]
    public async Task Backup_keeps_principal_and_rotates_last_on_each_change()
    {
        const string original = "127.0.0.1\tlocalhost\n";
        var hosts = Path.Combine(_dir, "hosts");
        await File.WriteAllTextAsync(hosts, original);
        var svc = new HostsFileService { HostsPath = hosts, BackupDirectory = Path.Combine(_dir, "bkp") };

        await svc.SyncAsync(["a.test"]);
        var afterA = await File.ReadAllTextAsync(hosts);
        Assert.Equal(original, await File.ReadAllTextAsync(svc.PrincipalBackupPath));
        Assert.Equal(original, await File.ReadAllTextAsync(svc.LastBackupPath));

        await svc.SyncAsync(["a.test", "b.test"]);
        var afterB = await File.ReadAllTextAsync(hosts);
        // O principal continua sendo o original; o último é o estado anterior a esta alteração.
        Assert.Equal(original, await File.ReadAllTextAsync(svc.PrincipalBackupPath));
        Assert.Equal(afterA, await File.ReadAllTextAsync(svc.LastBackupPath));
        Assert.Equal(["principal", "ultimo"], Directory.GetFiles(svc.BackupDirectory).Select(f => Path.GetFileName(f).Split('.')[1]).Order());

        // Sem mudança, sem backup novo.
        Assert.Null(await svc.SyncAsync(["a.test", "b.test"]));
        Assert.Equal(afterA, await File.ReadAllTextAsync(svc.LastBackupPath));

        // Restaurar o último volta um passo; o estado anterior vira o novo "último".
        Assert.True((await svc.RestoreAsync(principal: false))!.Success);
        Assert.Equal(afterA, await File.ReadAllTextAsync(hosts));
        Assert.Equal(afterB, await File.ReadAllTextAsync(svc.LastBackupPath));

        // Restaurar o principal volta ao original sem perder o próprio principal.
        Assert.True((await svc.RestoreAsync(principal: true))!.Success);
        Assert.Equal(original, await File.ReadAllTextAsync(hosts));
        Assert.Equal(original, await File.ReadAllTextAsync(svc.PrincipalBackupPath));
        Assert.Null(await svc.RestoreAsync(principal: true));
    }

    [Fact]
    public async Task Backup_disabled_or_missing_is_handled()
    {
        var hosts = Path.Combine(_dir, "hosts");
        await File.WriteAllTextAsync(hosts, "127.0.0.1\tlocalhost\n");
        var svc = new HostsFileService { HostsPath = hosts, BackupDirectory = Path.Combine(_dir, "bkp"), BackupEnabled = false };

        var restore = await svc.RestoreAsync(principal: false);
        Assert.False(restore!.Success);
        Assert.Contains("backup não encontrado", restore.StdErr);

        Assert.True((await svc.SyncAsync(["a.test"]))!.Success);
        Assert.False(Directory.Exists(svc.BackupDirectory));
    }

    [UnixFact(skipWhenRoot: true)]
    public async Task Hosts_is_not_changed_when_backup_fails()
    {
        if (OperatingSystem.IsWindows())
            return;
        var hosts = Path.Combine(_dir, "hosts");
        await File.WriteAllTextAsync(hosts, "127.0.0.1\tlocalhost\n");
        var bkp = Path.Combine(_dir, "bkp");
        Directory.CreateDirectory(bkp);
        File.SetUnixFileMode(bkp, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var result = await new HostsFileService { HostsPath = hosts, BackupDirectory = bkp }.SyncAsync(["a.test"]);

        Assert.False(result!.Success);
        Assert.Contains("backup", result.StdErr);
        Assert.Equal("127.0.0.1\tlocalhost\n", await File.ReadAllTextAsync(hosts));
    }

    // ---------------- elevação ----------------

    [Fact]
    public void AppleScript_escapes_quotes_and_backslashes()
    {
        var script = Elevation.BuildAppleScript("printf '%s\\n' \"a b\"", "teste \"x\"", admin: true);
        Assert.Equal(
            "do shell script \"printf '%s\\\\n' \\\"a b\\\"\" with prompt \"Dot Valet: teste \\\"x\\\"\" with administrator privileges",
            script);
    }

    [UnixFact]
    public async Task Elevation_runs_script_with_special_characters()
    {
        // Passa pelo osascript no macOS e pelo /bin/sh no Linux: aspas, $ e espaços precisam chegar intactos.
        var file = Path.Combine(_dir, "pasta com 'aspas' e $dolar", "saida.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var result = await Elevation.RunAsync($"printf '%s\\n' {Elevation.Sh("olá \"mundo\" $HOME")} > {Elevation.Sh(file)}", "teste");
        Assert.True(result.Success, result.Output);
        Assert.Equal("olá \"mundo\" $HOME\n", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public void Sudo_user_is_read_from_environment_and_passwd()
    {
        var passwd = Path.Combine(_dir, "passwd");
        File.WriteAllText(passwd, "root:x:0:0:root:/root:/bin/bash\ndev:x:1000:1000:Dev:/srv/dev:/bin/bash\n");
        var env = new Dictionary<string, string> { ["SUDO_USER"] = "dev", ["SUDO_UID"] = "1000", ["SUDO_GID"] = "1000" };

        var user = SudoUser.FromEnvironment(k => env.GetValueOrDefault(k), passwd);
        Assert.Equal(new SudoUser("dev", 1000, 1000, "/srv/dev"), user);

        env["SUDO_USER"] = "root";
        Assert.Null(SudoUser.FromEnvironment(k => env.GetValueOrDefault(k), passwd));
        Assert.Null(SudoUser.FromEnvironment(_ => null, passwd));
    }

    // ---------------- dnsmasq ----------------

    [MacFact]
    public async Task Mac_dnsmasq_install_switch_tld_and_uninstall()
    {
        var resolver = Path.Combine(_dir, "resolver");
        var brew = Path.Combine(_dir, "brew");
        var dns = new DnsmasqService(resolverDir: resolver, brewPrefix: brew, restartCommand: "true");

        // Resolver de outra ferramenta (Laravel Valet) nunca é tocado.
        Directory.CreateDirectory(resolver);
        await File.WriteAllTextAsync(Path.Combine(resolver, "valet"), "nameserver 127.0.0.1\n");
        Assert.Equal(DnsResolverState.External, dns.GetState("valet"));
        var external = await dns.InstallAsync("valet", _ => { });
        Assert.True(external.Success);
        Assert.Equal("nameserver 127.0.0.1\n", await File.ReadAllTextAsync(Path.Combine(resolver, "valet")));

        var install = await dns.InstallAsync("dvtest", _ => { });
        Assert.True(install.Success, install.Output);
        Assert.Equal(DnsResolverState.Managed, dns.GetState("dvtest"));
        Assert.Contains("nameserver 127.0.0.1", await File.ReadAllTextAsync(Path.Combine(resolver, "dvtest")));
        Assert.Contains("address=/.dvtest/127.0.0.1", await File.ReadAllTextAsync(Path.Combine(brew, "etc", "dnsmasq.d", "dotvalet.conf")));
        Assert.Contains("conf-dir=", await File.ReadAllTextAsync(Path.Combine(brew, "etc", "dnsmasq.conf")));

        // Trocar o TLD não deixa /etc/resolver/dvtest órfão.
        Assert.True((await dns.InstallAsync("dvnovo", _ => { })).Success);
        Assert.Equal(DnsResolverState.None, dns.GetState("dvtest"));
        Assert.Equal(DnsResolverState.Managed, dns.GetState("dvnovo"));

        // Desinstalar mesmo com o TLD "errado" remove o que é do Dot Valet e mantém o resto.
        var uninstall = await dns.UninstallAsync("outro");
        Assert.True(uninstall.Success, uninstall.Output);
        Assert.False(File.Exists(Path.Combine(resolver, "dvnovo")));
        Assert.False(File.Exists(Path.Combine(brew, "etc", "dnsmasq.d", "dotvalet.conf")));
        Assert.True(File.Exists(Path.Combine(resolver, "valet")));
    }

    [LinuxFact]
    public async Task Linux_dnsmasq_state_follows_tld_and_uninstall_after_tld_change()
    {
        var nm = Path.Combine(_dir, "NetworkManager");
        Directory.CreateDirectory(Path.Combine(nm, "dnsmasq.d"));
        await File.WriteAllTextAsync(Path.Combine(nm, "dnsmasq.d", "valet"), "address=/.valet/127.0.0.1\n");
        var dns = new DnsmasqService(networkManagerDir: nm, restartCommand: "true");

        Assert.Equal(DnsResolverState.External, dns.GetState("valet"));
        var install = await dns.InstallAsync("test", _ => { });
        Assert.True(install.Success, install.Output);
        Assert.Equal(DnsResolverState.Managed, dns.GetState("test"));
        // Antes, qualquer TLD aparecia como "configurado" só porque o arquivo existia.
        Assert.Equal(DnsResolverState.None, dns.GetState("local"));
        Assert.Contains("dns=dnsmasq", await File.ReadAllTextAsync(Path.Combine(nm, "conf.d", "dotvalet.conf")));

        var uninstall = await dns.UninstallAsync("local");
        Assert.True(uninstall.Success, uninstall.Output);
        Assert.False(File.Exists(Path.Combine(nm, "dnsmasq.d", "dotvalet.conf")));
        Assert.False(File.Exists(Path.Combine(nm, "conf.d", "dotvalet.conf")));
        Assert.True(File.Exists(Path.Combine(nm, "dnsmasq.d", "valet")));
    }

    // ---------------- certificados ----------------

    [UnixFact]
    public async Task Linux_trust_script_fails_clearly_without_ca_tools()
    {
        var result = await ProcessRunner.RunAsync("/usr/bin/env",
            ["-i", "PATH=/nonexistent", "/bin/sh", "-c", CertificateService.BuildLinuxTrustScript("/tmp/x.crt")]);
        Assert.False(result.Success);
        Assert.Contains("Nenhuma ferramenta de CA", result.StdErr);
    }

    // ---------------- root de verdade (container) ----------------

    [RealAdminFact]
    public async Task Root_trusts_ca_in_system_store_and_chain_validates()
    {
        var certs = new CertificateService(Path.Combine(_dir, "certs"));
        certs.EnsureCa();
        Assert.False(certs.IsCaTrusted());
        var result = await certs.TrustCaAsync();
        Assert.True(result.Success, result.Output);
        // O app confere a confiança logo após instalar: precisa enxergar a CA nova no mesmo processo.
        Assert.True(certs.IsCaTrusted(), "IsCaTrusted não viu a CA recém-instalada");

        var leaf = certs.GetSiteCertificate("seguro.test")!;
        // Lê o bundle do sistema na hora: o X509Chain padrão usa um cache do repositório carregado no início
        // do processo (outro teste pode tê-lo carregado antes da instalação). Navegadores e curl leem o bundle.
        var bundle = new[] { "/etc/ssl/certs/ca-certificates.crt", "/etc/pki/tls/certs/ca-bundle.crt" }.First(File.Exists);
        var system = new X509Certificate2Collection();
        system.ImportFromPemFile(bundle);
        Assert.Contains(system.Cast<X509Certificate2>(), c => c.Thumbprint == certs.EnsureCa().Thumbprint);

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(system);
        Assert.True(chain.Build(leaf), string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation)));
    }

    [RealAdminFact]
    public async Task Root_writes_real_hosts_file_without_prompt()
    {
        Environment.SetEnvironmentVariable("DOTVALET_ELEVATION", null);
        var svc = new HostsFileService { HostsPath = "/etc/hosts", BackupDirectory = Path.Combine(_dir, "bkp") };
        try
        {
            var result = await svc.SyncAsync(["root-admin.test"]);
            Assert.True(result!.Success, result.Output);
            Assert.Contains("root-admin.test", HostsFileService.ReadManagedHosts(await File.ReadAllTextAsync("/etc/hosts")));
            var resolved = await System.Net.Dns.GetHostAddressesAsync("root-admin.test");
            Assert.Contains(resolved, a => System.Net.IPAddress.IsLoopback(a));
        }
        finally
        {
            await svc.SyncAsync([]);
        }
        Assert.DoesNotContain("root-admin.test", await File.ReadAllTextAsync("/etc/hosts"));
    }

    [SudoFact]
    public async Task Sudo_keeps_files_owned_by_real_user_and_runs_apps_as_user()
    {
        var user = PlatformInfo.SudoInvoker!;
        Assert.StartsWith(user.Home, PlatformInfo.DataDirectory);

        var uid = await ProcessRunner.RunAsync("id", ["-u"], asUser: true);
        Assert.Equal(user.Uid.ToString(), uid.StdOut.Trim());

        // config.json e certificados na home do usuário real, com dono correto.
        ConfigStore.Save(new AppConfig());
        var certs = new CertificateService();
        certs.EnsureCa();
        certs.GetSiteCertificate("dono.test");

        // launchSettings.json de um projeto do usuário.
        var project = Path.Combine(user.Home, "proj", "App.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        PlatformInfo.GiveBackToUser(Path.GetDirectoryName(project)!);
        var site = new Site { Name = "Dono", ProjectPath = project, Domain = "dono.test", UpdateLaunchSettings = true };
        site.AppPort = 5100;
        LaunchSettingsService.Apply(site, SiteEndpoints.TryResolve(site, new AppConfig(), out _)!);

        var paths = new[]
        {
            PlatformInfo.DataDirectory, ConfigStore.ConfigPath, certs.Directory, certs.CaPfxPath, certs.CaCrtPath,
            Path.Combine(certs.Directory, "dono.test.pfx"),
            Path.Combine(user.Home, "proj", "Properties"), Path.Combine(user.Home, "proj", "Properties", "launchSettings.json"),
        };
        foreach (var p in paths)
        {
            var owner = await ProcessRunner.RunAsync("stat", ["-c", "%u", p]);
            Assert.True(owner.StdOut.Trim() == user.Uid.ToString(), $"{p} pertence a {owner.Output}");
        }
    }
}

public sealed class UnixFact : FactAttribute
{
    public UnixFact(bool skipWhenRoot = false)
    {
        if (OperatingSystem.IsWindows())
            Skip = "somente macOS/Linux";
        else if (skipWhenRoot && Environment.IsPrivilegedProcess)
            Skip = "root ignora permissões de arquivo";
    }
}

public sealed class MacFact : FactAttribute
{
    public MacFact() { if (!OperatingSystem.IsMacOS()) Skip = "somente macOS"; }
}

public sealed class LinuxFact : FactAttribute
{
    public LinuxFact() { if (!OperatingSystem.IsLinux()) Skip = "somente Linux"; }
}

public sealed class RealAdminFact : FactAttribute
{
    public RealAdminFact()
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess || PlatformInfo.SudoInvoker is not null
            || Environment.GetEnvironmentVariable("DOTVALET_REAL_ADMIN_TESTS") != "1")
            Skip = "altera o sistema: só como root em container (DOTVALET_REAL_ADMIN_TESTS=1)";
    }
}

public sealed class SudoFact : FactAttribute
{
    public SudoFact()
    {
        if (!OperatingSystem.IsLinux() || PlatformInfo.SudoInvoker is null
            || Environment.GetEnvironmentVariable("DOTVALET_REAL_ADMIN_TESTS") != "1")
            Skip = "só via sudo em container (DOTVALET_REAL_ADMIN_TESTS=1)";
    }
}
