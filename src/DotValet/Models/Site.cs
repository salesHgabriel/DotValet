namespace DotValet.Models;

/// <summary>
/// Um projeto ASP.NET Core servido pelo Dot Valet (equivalente a um "link" do Laravel Valet).
/// </summary>
public sealed class Site
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Caminho absoluto do .csproj/.fsproj/.vbproj.</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>
    /// Domínio como o usuário digitou: "app", "app.test" ou "app2.test:5172".
    /// Sem porta o site é servido pelo proxy do Dot Valet; com porta o próprio Kestrel escuta nela.
    /// </summary>
    public string Domain { get; set; } = "";

    /// <summary>Porta interna (localhost) que o app escuta quando o domínio não define porta.</summary>
    public int AppPort { get; set; }

    public bool Secure { get; set; }

    public bool UpdateLaunchSettings { get; set; } = true;
    public string LaunchProfileName { get; set; } = "DotValet";
    public bool AddToExistingProfiles { get; set; }

    public bool UseWatch { get; set; }

    /// <summary>Criado automaticamente a partir de uma pasta estacionada ("park").</summary>
    public bool Parked { get; set; }

    /// <summary>O que foi escrito por último no launchSettings.json, para conseguir desfazer.</summary>
    public string? AppliedBindingUrl { get; set; }
    public string? AppliedProfileName { get; set; }
}
