using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DotValet.Services;

/// <summary>
/// CA local (no estilo mkcert / "valet secure") que emite certificados por domínio para o proxy HTTPS.
/// </summary>
public sealed class CertificateService
{
    private const string CaSubject = "CN=Dot Valet Local CA, O=Dot Valet, OU=Desenvolvimento local";
    private readonly ConcurrentDictionary<string, X509Certificate2> _cache = new(StringComparer.OrdinalIgnoreCase);
    private X509Certificate2? _ca;

    public string Directory { get; }
    public string CaPfxPath => Path.Combine(Directory, "dotvalet-ca.pfx");
    public string CaCrtPath => Path.Combine(Directory, "dotvalet-ca.crt");

    public CertificateService(string? directory = null)
    {
        Directory = directory ?? Path.Combine(PlatformInfo.DataDirectory, "certs");
        if (!System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.CreateDirectory(Directory);
            PlatformInfo.GiveBackToUser(Directory);
        }
    }

    public bool CaExists => File.Exists(CaPfxPath);

    public X509Certificate2 EnsureCa()
    {
        if (_ca is not null)
            return _ca;
        if (CaExists)
            return _ca = Load(File.ReadAllBytes(CaPfxPath));

        using var key = RSA.Create(3072);
        var req = new CertificateRequest(CaSubject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        using var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        WriteSecret(CaPfxPath, ca.Export(X509ContentType.Pkcs12));
        File.WriteAllText(CaCrtPath, ca.ExportCertificatePem());
        PlatformInfo.GiveBackToUser(CaCrtPath);
        return _ca = Load(File.ReadAllBytes(CaPfxPath));
    }

    /// <summary>Seletor SNI do Kestrel: devolve (e cria se preciso) o certificado do host.</summary>
    public X509Certificate2? GetSiteCertificate(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || !CaExists)
            return null;
        host = host.ToLowerInvariant();

        if (_cache.TryGetValue(host, out var cached) && cached.NotAfter > DateTime.Now.AddDays(7))
            return cached;

        lock (_cache)
        {
            var file = Path.Combine(Directory, $"{host}.pfx");
            if (File.Exists(file))
            {
                var existing = Load(File.ReadAllBytes(file));
                if (existing.NotAfter > DateTime.Now.AddDays(7))
                    return _cache[host] = existing;
            }

            var issued = Issue(host);
            WriteSecret(file, issued);
            return _cache[host] = Load(issued);
        }
    }

    private byte[] Issue(string host)
    {
        var ca = EnsureCa();
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}, O=Dot Valet", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddDnsName($"*.{host}");
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        // 397 dias: limite aceito por Safari/Chrome para certificados de servidor.
        var notAfter = DateTimeOffset.UtcNow.AddDays(397);
        using var cert = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), notAfter < ca.NotAfter ? notAfter : ca.NotAfter, serial);
        using var withKey = cert.CopyWithPrivateKey(key);
        return withKey.Export(X509ContentType.Pkcs12);
    }

    public void Forget(string host)
    {
        _cache.TryRemove(host, out _);
        var file = Path.Combine(Directory, $"{host.ToLowerInvariant()}.pfx");
        if (File.Exists(file))
            File.Delete(file);
    }

    /// <summary>
    /// A CA é de fato raiz confiável no sistema (Keychain no macOS, repositório Root no Windows, bundle no Linux)?
    /// Consulta o sistema em vez de confiar só no que o Dot Valet registrou.
    /// </summary>
    public bool IsCaTrusted()
    {
        if (!CaExists)
            return false;
        // No Linux o X509Chain usa um cache do repositório carregado no início do processo e não veria a CA
        // recém-instalada: lê o bundle do sistema direto (é o que curl, openssl e afins usam).
        if (PlatformInfo.Os == OsKind.Linux)
            return LinuxBundles.Where(File.Exists).Any(BundleContainsCa);
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(EnsureCa());
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static readonly string[] LinuxBundles =
    [
        "/etc/ssl/certs/ca-certificates.crt",   // Debian/Ubuntu/Arch
        "/etc/pki/tls/certs/ca-bundle.crt",     // Fedora/RHEL
        "/etc/ssl/ca-bundle.pem",               // openSUSE
    ];

    private bool BundleContainsCa(string bundle)
    {
        try
        {
            var all = new X509Certificate2Collection();
            all.ImportFromPemFile(bundle);
            var thumbprint = EnsureCa().Thumbprint;
            return all.Any(c => c.Thumbprint == thumbprint);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Instala a CA como raiz confiável do usuário/sistema.</summary>
    public async Task<ProcessResult> TrustCaAsync()
    {
        EnsureCa();
        switch (PlatformInfo.Os)
        {
            case OsKind.Windows:
            {
                try
                {
                    // Como administrador, o CurrentUser seria o da conta elevada (que pode ser outra, no UAC
                    // com credenciais de admin): usa o repositório da máquina, que vale para todos.
                    using var store = new X509Store(StoreName.Root, PlatformInfo.IsElevated ? StoreLocation.LocalMachine : StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadWrite);
                    // Sem elevação, o Windows mostra uma confirmação ao usuário aqui.
                    store.Add(X509Certificate2.CreateFromPem(File.ReadAllText(CaCrtPath)));
                    return new ProcessResult(0, "", "");
                }
                catch (CryptographicException e)
                {
                    return new ProcessResult(-1, "", e.Message);
                }
            }
            case OsKind.MacOS:
            {
                // Como root (sudo) a confiança no domínio do usuário seria a do root: usa o keychain do sistema (-d), como o mkcert.
                if (PlatformInfo.IsElevated)
                    return await ProcessRunner.RunAsync("/usr/bin/security",
                        ["add-trusted-cert", "-d", "-r", "trustRoot", "-k", "/Library/Keychains/System.keychain", CaCrtPath]);
                var keychain = Path.Combine(PlatformInfo.UserHome, "Library", "Keychains", "login.keychain-db");
                return await ProcessRunner.RunAsync("/usr/bin/security",
                    ["add-trusted-cert", "-r", "trustRoot", "-k", keychain, CaCrtPath]);
            }
            default:
                return await Elevation.RunAsync(BuildLinuxTrustScript(CaCrtPath), "confiar no certificado local");
        }
    }

    /// <summary>Debian/Ubuntu, Fedora/RHEL/openSUSE e Arch (p11-kit), nessa ordem.</summary>
    public static string BuildLinuxTrustScript(string crtPath)
    {
        var crt = Elevation.Sh(crtPath);
        return
            $"if command -v update-ca-certificates >/dev/null 2>&1 && [ -d /usr/local/share/ca-certificates ]; then cp {crt} /usr/local/share/ca-certificates/dotvalet-ca.crt && update-ca-certificates; " +
            $"elif command -v update-ca-trust >/dev/null 2>&1 && [ -d /etc/pki/ca-trust/source/anchors ]; then cp {crt} /etc/pki/ca-trust/source/anchors/dotvalet-ca.crt && update-ca-trust; " +
            $"elif command -v trust >/dev/null 2>&1; then trust anchor --store {crt}; " +
            "else echo 'Nenhuma ferramenta de CA do sistema encontrada (update-ca-certificates, update-ca-trust ou trust).' >&2; exit 1; fi";
    }

    private static X509Certificate2 Load(byte[] pfx) =>
#pragma warning disable SYSLIB0057
        new(pfx, (string?)null, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057

    private static void WriteSecret(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        PlatformInfo.GiveBackToUser(path);
    }
}
