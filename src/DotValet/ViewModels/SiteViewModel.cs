using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DotValet.Models;
using DotValet.Services;

namespace DotValet.ViewModels;

public partial class SiteViewModel : ViewModelBase
{
    private const int MaxLogChars = 200_000;
    private readonly StringBuilder _log = new();
    private readonly Func<AppConfig> _config;
    private readonly DotnetEnvironment _dotnet;
    private bool _loading;

    public Site Model { get; }

    public SiteViewModel(Site model, Func<AppConfig> config, DotnetEnvironment dotnet, bool isNew = false)
    {
        Model = model;
        _config = config;
        _dotnet = dotnet;
        _loading = true;
        name = model.Name;
        projectPath = model.ProjectPath;
        domain = model.Domain;
        secure = model.Secure;
        updateLaunchSettings = model.UpdateLaunchSettings;
        launchProfileName = model.LaunchProfileName;
        addToExistingProfiles = model.AddToExistingProfiles;
        useWatch = model.UseWatch;
        this.isNew = isNew;
        _loading = false;
        Refresh();
    }

    [ObservableProperty] private string name;
    [ObservableProperty] private string projectPath;
    [ObservableProperty] private string domain;
    [ObservableProperty] private bool secure;
    [ObservableProperty] private bool updateLaunchSettings;
    [ObservableProperty] private string launchProfileName;
    [ObservableProperty] private bool addToExistingProfiles;
    [ObservableProperty] private bool useWatch;

    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isNew;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private bool isRunning;

    [ObservableProperty] private SiteEndpoints? endpoints;
    [ObservableProperty] private string? domainError;
    [ObservableProperty] private string frameworkLabel = "";
    [ObservableProperty] private string? projectWarning;
    [ObservableProperty] private string log = "";

    public bool Parked => Model.Parked;

    public string PrimaryUrl => Endpoints?.PrimaryUrl ?? "";
    public string ProxyUrl => Endpoints?.ProxyUrl ?? "";
    public string BindingUrl => Endpoints?.BindingUrl ?? "";
    public bool HasEndpoints => Endpoints is not null;
    public bool ShowProxyUrl => Endpoints is { IsDirect: true };

    public string StatusText => IsRunning ? "rodando" : "parado";

    public string ModeDescription => Endpoints switch
    {
        null => "",
        { IsDirect: true } ep =>
            $"Modo direto: abra {ep.PrimaryUrl} — o Kestrel escuta em {ep.BindingUrl} (só nesta máquina) e o arquivo hosts aponta {ep.Host} para 127.0.0.1. " +
            $"O proxy do Dot Valet também atende {ep.ProxyUrl}.",
        var ep =>
            $"Modo proxy: o app escuta em {ep.BindingUrl} e o proxy do Dot Valet publica {ep.ProxyUrl}.",
    };

    public string LaunchSettingsPath => string.IsNullOrEmpty(ProjectPath) ? "" : LaunchSettingsService.GetPath(ProjectPath);

    public string LaunchSettingsPreview => Endpoints is null
        ? ""
        : LaunchSettingsService.PreviewProfile(string.IsNullOrWhiteSpace(LaunchProfileName) ? "DotValet" : LaunchProfileName, Endpoints);

    partial void OnNameChanged(string value) => Changed(() => Model.Name = value);
    partial void OnDomainChanged(string value) => Changed(() => Model.Domain = value);
    partial void OnSecureChanged(bool value) => Changed(() => Model.Secure = value);
    partial void OnUpdateLaunchSettingsChanged(bool value) => Changed(() => Model.UpdateLaunchSettings = value);
    partial void OnLaunchProfileNameChanged(string value) => Changed(() => Model.LaunchProfileName = value);
    partial void OnAddToExistingProfilesChanged(bool value) => Changed(() => Model.AddToExistingProfiles = value);
    partial void OnUseWatchChanged(bool value) => Changed(() => Model.UseWatch = value);
    partial void OnProjectPathChanged(string value) => Changed(() => Model.ProjectPath = value);

    partial void OnEndpointsChanged(SiteEndpoints? value)
    {
        OnPropertyChanged(nameof(PrimaryUrl));
        OnPropertyChanged(nameof(ProxyUrl));
        OnPropertyChanged(nameof(BindingUrl));
        OnPropertyChanged(nameof(HasEndpoints));
        OnPropertyChanged(nameof(ShowProxyUrl));
        OnPropertyChanged(nameof(ModeDescription));
        OnPropertyChanged(nameof(LaunchSettingsPreview));
    }

    private void Changed(Action apply)
    {
        if (_loading)
            return;
        apply();
        IsDirty = true;
        Refresh();
    }

    /// <summary>Recalcula URLs e informações do projeto (após mudança de domínio, TLD, portas...).</summary>
    public void Refresh()
    {
        Endpoints = SiteEndpoints.TryResolve(Model, _config(), out var error);
        DomainError = error;
        OnPropertyChanged(nameof(LaunchSettingsPreview));
        OnPropertyChanged(nameof(LaunchSettingsPath));

        var info = File.Exists(Model.ProjectPath) ? ProjectInspector.TryInspect(Model.ProjectPath) : null;
        if (info is null)
        {
            FrameworkLabel = "?";
            ProjectWarning = File.Exists(Model.ProjectPath)
                ? "Não foi possível ler o arquivo de projeto."
                : "Arquivo de projeto não encontrado.";
            return;
        }

        FrameworkLabel = info.FrameworkLabel;
        if (!info.IsWeb)
            ProjectWarning = "Este projeto não parece ser ASP.NET Core (Microsoft.NET.Sdk.Web).";
        else if (info.TargetFrameworks.Count > 0 && info.RuntimeVersion is null)
            ProjectWarning = "Somente .NET Core 2.x / 3.x e .NET 5+ são suportados (o .NET Framework não roda no Kestrel).";
        else if (info.RuntimeVersion is { } rv && _dotnet.DotnetPath is not null && !_dotnet.HasAspNetRuntime(rv))
            ProjectWarning = $"Runtime ASP.NET Core {rv.Major}.{rv.Minor} não está instalado nesta máquina.";
        else
            ProjectWarning = null;
    }

    public void MarkSaved()
    {
        IsDirty = false;
        IsNew = false;
    }

    public void AppendLog(string line)
    {
        _log.AppendLine(line);
        if (_log.Length > MaxLogChars)
            _log.Remove(0, _log.Length - MaxLogChars);
        Log = _log.ToString();
    }

    public void ClearLog()
    {
        _log.Clear();
        Log = "";
    }
}
