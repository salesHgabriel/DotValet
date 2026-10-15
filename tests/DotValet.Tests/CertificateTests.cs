using System.Security.Cryptography.X509Certificates;
using DotValet.Services;

namespace DotValet.Tests;

public class CertificateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotvalet-certs").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Issues_site_certificate_chained_to_local_ca()
    {
        var certs = new CertificateService(_dir);
        var ca = certs.EnsureCa();
        var leaf = certs.GetSiteCertificate("app.test")!;

        Assert.True(leaf.HasPrivateKey);
        Assert.Contains("app.test", leaf.GetNameInfo(X509NameType.DnsName, false));
        Assert.Equal(ca.Subject, leaf.Issuer);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(leaf), string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation)));

        // segunda chamada vem do cache/disco
        Assert.Equal(leaf.Thumbprint, certs.GetSiteCertificate("APP.test")!.Thumbprint);
    }

    [Fact]
    public void New_ca_is_not_trusted_until_installed()
    {
        var certs = new CertificateService(_dir);
        Assert.False(certs.IsCaTrusted()); // ainda não existe
        certs.EnsureCa();
        Assert.False(certs.IsCaTrusted()); // existe, mas não está no repositório do sistema
    }
}
