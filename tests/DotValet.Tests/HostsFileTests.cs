using DotValet.Services;

namespace DotValet.Tests;

public class HostsFileTests
{
    private const string Original = "127.0.0.1\tlocalhost\n::1\tlocalhost\n";

    [Fact]
    public void Adds_block_and_is_idempotent()
    {
        var once = HostsFileService.BuildContent(Original, ["b.test", "a.test", "a.test"]);
        Assert.StartsWith(Original.TrimEnd(), once);
        Assert.Equal(["a.test", "b.test"], HostsFileService.ReadManagedHosts(once));
        Assert.Equal(once, HostsFileService.BuildContent(once, ["a.test", "b.test"]));
    }

    [Fact]
    public void Removing_all_sites_restores_original()
    {
        var withBlock = HostsFileService.BuildContent(Original, ["a.test"]);
        Assert.Equal(Original, HostsFileService.BuildContent(withBlock, []));
    }

    [Fact]
    public void Preserves_windows_line_endings()
    {
        var crlf = Original.Replace("\n", "\r\n");
        var result = HostsFileService.BuildContent(crlf, ["a.test"]);
        Assert.DoesNotContain("\n", result.Replace("\r\n", ""));
    }
}
