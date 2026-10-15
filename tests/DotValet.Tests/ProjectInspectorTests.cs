using DotValet.Services;

namespace DotValet.Tests;

public class ProjectInspectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotvalet-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("netcoreapp2.1", ".NET Core 2.1", "2.1")]
    [InlineData("netcoreapp3.1", ".NET Core 3.1", "3.1")]
    [InlineData("net5.0", ".NET 5", "5.0")]
    [InlineData("net8.0", ".NET 8", "8.0")]
    [InlineData("net10.0", ".NET 10", "10.0")]
    [InlineData("net9.0-windows", ".NET 9", "9.0")]
    [InlineData("net48", ".NET Framework 48", null)]
    public void Describes_target_frameworks(string tfm, string label, string? runtime)
    {
        Assert.Equal(label, ProjectInspector.Describe(tfm));
        Assert.Equal(runtime is null ? null : Version.Parse(runtime), ProjectInspector.RuntimeVersionOf(tfm));
    }

    [Fact]
    public void Finds_web_project_in_solution_layout()
    {
        Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        Write("src/Web/Web.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");

        var found = ProjectInspector.FindProjectFile(_dir);
        Assert.EndsWith("Web.csproj", found);
        var info = ProjectInspector.Inspect(found!);
        Assert.True(info.IsWeb);
        Assert.Equal(".NET 8", info.FrameworkLabel);
    }

    [Fact]
    public void Detects_legacy_aspnetcore_2_project()
    {
        var path = Write("Old/Old.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>netcoreapp2.1</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
            </Project>
            """);
        var info = ProjectInspector.Inspect(path);
        Assert.True(info.IsWeb);
        Assert.Equal(new Version(2, 1), info.RuntimeVersion);
    }

    [Fact]
    public void Reads_target_framework_from_directory_build_props()
    {
        Write("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup></Project>");
        var path = Write("App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Assert.Equal(".NET 6", ProjectInspector.Inspect(path).FrameworkLabel);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
