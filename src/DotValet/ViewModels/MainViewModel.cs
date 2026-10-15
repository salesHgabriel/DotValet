using System.Collections.ObjectModel;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotValet.Models;
using DotValet.Services;

namespace DotValet.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly DotnetEnvironment _dotnet = new();
    private readonly HostsFileService _hosts = new();
    private readonly DnsmasqService _dnsmasq = new();
    private readonly CertificateService _certs = new();
    private readonly ProxyServer _proxy = new();
    private readonly SiteRunner _runner = new();
    private readonly ValetService _valet = new();
    private AppConfig _config;

    public IUiService? Ui { get; set; }

    public MainViewModel()
    {
        _config = ConfigStore.Load();
        foreach (var site in _config.Sites)
            Sites.Add(CreateVm(site));
        SelectedSite = Sites.FirstOrDefault();
        ParkedPaths = new ObservableCollection<string>(_config.ParkedPaths);
        LoadSettingsIntoForm();
    }

    public ObservableCollection<SiteViewModel> Sites { get; } = [];
    public ObservableCollection<string> ParkedPaths { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))]
    private SiteViewModel? selectedSite;

    public bool HasSelection => SelectedSite is not null;
    public bool HasNoSites => Sites.Count == 0;

    [ObservableProperty] private string statusMessage = "Pronto.";
    [ObservableProperty] private bool isBusy;

    // ---- Ambiente ----
    public string PlatformName => PlatformInfo.DisplayName;
    public string PlatformStrategy => PlatformInfo.Strategy;
    public string ElevationInfo => PlatformInfo.ElevationDescription;
    public string HostsPath => PlatformInfo.HostsPath;
    public string ConfigPath => ConfigStore.ConfigPath;
    public string CaPath => _certs.CaCrtPath;
    [ObservableProperty] private string dotnetPath = "procurando...";
    [ObservableProperty] private string dotnetSdks = "";
    [ObservableProperty] private string aspNetRuntimes = "";

    // ---- Proxy ----
    [ObservableProperty] private bool proxyRunning;
    [ObservableProperty] private string proxyStatus = "parado";

    // ---- Configurações (formulário) ----
    [ObservableProperty] private string tld = "test";
    [ObservableProperty] private int httpPort;
    [ObservableProperty] private int httpsPort;
    [ObservableProperty] private bool enableHttps;
    [ObservableProperty] private bool autoStartProxy;
    [ObservableProperty] private bool useHostsFile;
    [ObservableProperty] private bool useDnsmasq;
    [ObservableProperty] private string dnsmasqStatus = "";
    [ObservableProperty] private string caStatus = "";
    [ObservableProperty] private string? selectedParkedPath;
    [ObservableProperty] private bool backupHostsFile;
    [ObservableProperty] private string hostsBackupStatus = "";

    // ---- Laravel Valet ----
    public bool ValetInstalled => _valet.IsInstalled;
    [ObservableProperty] private bool valetIntegration;
    [ObservableProperty] private string valetStatus = "";

    public string ValetRecommendation =>
        $"O Laravel Valet está instalado: o nginx dele ocupa as portas 80/443 e o dnsmasq dele responde por *.{_valet.Tld}. " +
        $"Para não gerar conflito, recomendamos parar o Valet enquanto usa o Dot Valet ({ValetService.StopCommand}; para voltar, {ValetService.StartCommand}) " +
        $"ou desinstalá-lo se não usa mais PHP ({ValetService.UninstallCommand}). " +
        "Se precisa dos dois ao mesmo tempo, marque a integração abaixo: o nginx do Valet passa a encaminhar os sites .NET.";

    public bool DnsmasqSupported => _dnsmasq.IsSupported;
    public string DnsmasqHint => DnsmasqSupported
        ? $"Resolve qualquer *.{_config.Tld} sem editar o hosts a cada site (igual ao Laravel Valet)."
        : DnsmasqService.UnsupportedReason;

    private SiteViewModel CreateVm(Site site, bool isNew = false) => new(site, () => _config, _dotnet, isNew);

    private void LoadSettingsIntoForm()
    {
        Tld = _config.Tld;
        HttpPort = _config.HttpPort;
        HttpsPort = _config.HttpsPort;
        EnableHttps = _config.EnableHttps;
        AutoStartProxy = _config.AutoStartProxy;
        UseDnsmasq = _config.DnsMode == DnsMode.Dnsmasq;
        UseHostsFile = !UseDnsmasq;
        BackupHostsFile = _config.BackupHostsFile;
        ValetIntegration = _config.ValetIntegration;
        RefreshDnsStatus();
        RefreshCaStatus();
    }

    public async Task InitializeAsync()
    {
        await _dotnet.RefreshAsync();
        DotnetPath = _dotnet.DotnetPath ?? "não encontrado — instale o .NET SDK (https://dot.net)";
        DotnetSdks = _dotnet.Sdks.Count == 0 ? "nenhum" : string.Join("\n", _dotnet.Sdks);
        AspNetRuntimes = _dotnet.AspNetRuntimes.Any() ? string.Join("\n", _dotnet.AspNetRuntimes) : "nenhum";
        foreach (var s in Sites)
            s.Refresh();

        var parkedAdded = ScanParkedFolders();
        if (parkedAdded > 0)
            Persist();

        // Sites salvos por versões anteriores (ex.: modo direto escutando no domínio): regrava o launchSettings.json.
        foreach (var s in Sites)
        {
            if (s.IsNew || !s.Model.UpdateLaunchSettings || s.Endpoints is not { } ep
                || s.Model.AppliedBindingUrl is null || s.Model.AppliedBindingUrl == ep.BindingUrl)
                continue;
            try
            {
                LaunchSettingsService.Apply(s.Model, ep);
                Persist();
            }
            catch (Exception e)
            {
                StatusMessage = $"{s.Name}: não foi possível atualizar o launchSettings.json: {e.Message}";
            }
        }

        if (_config.AutoStartProxy)
            await StartProxyAsync();
        await SyncDnsAsync(quietWhenUnchanged: true);
        await RefreshValetStatusAsync();
        if (_valet.IsInstalled && !_config.ValetIntegration && !ProxyRunning)
            StatusMessage = $"Laravel Valet detectado: pare-o ({ValetService.StopCommand}) ou desinstale-o para não gerar conflito — veja a aba Ambiente.";
    }

    // ================= Sites =================

    [RelayCommand]
    private async Task LinkProjectAsync()
    {
        if (Ui is null)
            return;
        var folder = await Ui.PickFolderAsync("Selecione a pasta do projeto ASP.NET Core");
        if (folder is null)
            return;
        AddDraft(folder);
    }

    [RelayCommand]
    private async Task LinkProjectFileAsync()
    {
        if (Ui is null)
            return;
        var file = await Ui.PickProjectFileAsync("Selecione o .csproj do projeto ASP.NET Core");
        if (file is null)
            return;
        AddDraft(file);
    }

    private void AddDraft(string path)
    {
        var projectFile = ProjectInspector.FindProjectFile(path);
        if (projectFile is null)
        {
            StatusMessage = $"Nenhum .csproj encontrado em {path}.";
            return;
        }

        var existing = Sites.FirstOrDefault(s => SamePath(s.Model.ProjectPath, projectFile));
        if (existing is not null)
        {
            SelectedSite = existing;
            StatusMessage = $"{existing.Name} já está vinculado.";
            return;
        }

        var info = ProjectInspector.TryInspect(projectFile);
        var folderName = Path.GetFileName(Path.GetDirectoryName(projectFile)!);
        var site = new Site
        {
            Name = info?.Name ?? folderName,
            ProjectPath = projectFile,
            Domain = UniqueSlug(DomainParser.Slugify(folderName)),
            AppPort = PortAllocator.Next(ReservedPorts()),
            UpdateLaunchSettings = true,
        };
        var vm = CreateVm(site, isNew: true);
        vm.IsDirty = true;
        Sites.Add(vm);
        OnPropertyChanged(nameof(HasNoSites));
        SelectedSite = vm;
        StatusMessage = $"Revise o domínio e as opções de {site.Name} e clique em \"Salvar e aplicar\".";
    }

    [RelayCommand]
    private async Task ChangeProjectAsync()
    {
        if (Ui is null || SelectedSite is null)
            return;
        var file = await Ui.PickProjectFileAsync("Selecione o arquivo de projeto");
        if (file is null)
            return;
        SelectedSite.ProjectPath = Path.GetFullPath(file);
    }

    [RelayCommand]
    private async Task SaveSiteAsync()
    {
        if (SelectedSite is not { } vm)
            return;
        await RunBusyAsync(async () =>
        {
            var site = vm.Model;
            if (vm.Endpoints is not { } ep)
            {
                StatusMessage = vm.DomainError ?? "Domínio inválido.";
                return;
            }
            if (string.IsNullOrWhiteSpace(site.LaunchProfileName))
                vm.LaunchProfileName = "DotValet";

            if (ValidateConflicts(vm, ep) is { } conflict)
            {
                StatusMessage = conflict;
                return;
            }

            if (ep.ExplicitPort is null && site.AppPort == 0)
            {
                site.AppPort = PortAllocator.Next(ReservedPorts(except: site));
                vm.Refresh();
                ep = vm.Endpoints!;
            }

            var messages = new List<string>();
            try
            {
                if (site.UpdateLaunchSettings)
                {
                    LaunchSettingsService.Apply(site, ep);
                    messages.Add($"launchSettings.json atualizado (perfil \"{site.LaunchProfileName}\")");
                }
                else if (site.AppliedProfileName is not null || site.AppliedBindingUrl is not null)
                {
                    LaunchSettingsService.Remove(site);
                    messages.Add("entradas do Dot Valet removidas do launchSettings.json");
                }
            }
            catch (Exception e)
            {
                messages.Add($"falha no launchSettings.json: {e.Message}");
            }

            if (vm.IsNew)
                _config.Sites.Add(site);
            vm.MarkSaved();
            Persist();

            if (site.Secure && _config.EnableHttps)
                messages.Add(await EnsureHttpsReadyAsync());

            UpdateProxyRoutes();
            var dns = await SyncDnsAsync(quietWhenUnchanged: true);
            if (dns is not null)
                messages.Add(dns);

            StatusMessage = $"{site.Name} salvo → {ep.PrimaryUrl}" + (messages.Count > 0 ? " · " + string.Join(" · ", messages.Where(m => m.Length > 0)) : "");
        });
    }

    [RelayCommand]
    private async Task RemoveSiteAsync()
    {
        if (SelectedSite is not { } vm)
            return;
        await RunBusyAsync(async () =>
        {
            _runner.Stop(vm.Model.Id);
            try
            {
                LaunchSettingsService.Remove(vm.Model);
            }
            catch (Exception e)
            {
                StatusMessage = $"Não foi possível limpar o launchSettings.json: {e.Message}";
            }
            if (vm.Endpoints is { } ep)
                _certs.Forget(ep.Host);

            _config.Sites.Remove(vm.Model);
            Sites.Remove(vm);
            OnPropertyChanged(nameof(HasNoSites));
            SelectedSite = Sites.FirstOrDefault();

            // Pastas estacionadas recriariam o site; mantém-no fora até o usuário reestacionar.
            if (vm.Model.Parked)
                StatusMessage = $"{vm.Name} removido (ele volta se a pasta estacionada for re-escaneada).";
            else
                StatusMessage = $"{vm.Name} removido.";

            Persist();
            UpdateProxyRoutes();
            await SyncDnsAsync(quietWhenUnchanged: true);
        });
    }

    [RelayCommand]
    private void RevertSite()
    {
        if (SelectedSite is not { IsDirty: true } vm)
            return;
        var fresh = ConfigStore.Load().Sites.FirstOrDefault(s => s.Id == vm.Model.Id);
        var index = Sites.IndexOf(vm);
        Sites.RemoveAt(index);
        if (fresh is null)
        {
            SelectedSite = Sites.FirstOrDefault();
            OnPropertyChanged(nameof(HasNoSites));
            return;
        }
        _config.Sites[_config.Sites.IndexOf(vm.Model)] = fresh;
        var restored = CreateVm(fresh);
        restored.IsRunning = _runner.IsRunning(fresh.Id);
        Sites.Insert(index, restored);
        SelectedSite = restored;
    }

    [RelayCommand]
    private void StartSite()
    {
        if (SelectedSite is not { } vm)
            return;
        if (vm.IsNew || vm.IsDirty)
        {
            StatusMessage = "Salve o site antes de iniciá-lo.";
            return;
        }
        if (_dotnet.DotnetPath is null)
        {
            StatusMessage = "dotnet não encontrado.";
            return;
        }
        if (vm.Endpoints is not { } ep)
            return;
        if (!PortAllocator.IsFree(ep.ListenPort))
            vm.AppendLog($"[Dot Valet] Aviso: a porta {ep.ListenPort} já está em uso (o app talvez já esteja rodando na IDE).");

        try
        {
            _runner.Start(vm.Model, ep, _dotnet.DotnetPath,
                line => Dispatcher.UIThread.Post(() => vm.AppendLog(line)),
                code => Dispatcher.UIThread.Post(() =>
                {
                    vm.IsRunning = false;
                    vm.AppendLog($"[Dot Valet] processo encerrado (código {code}).");
                }));
            vm.IsRunning = true;
            StatusMessage = $"Iniciando {vm.Name} em {ep.BindingUrl} → {ep.PrimaryUrl}";
        }
        catch (Exception e)
        {
            StatusMessage = $"Falha ao iniciar: {e.Message}";
        }
    }

    [RelayCommand]
    private void StopSite()
    {
        if (SelectedSite is not { } vm)
            return;
        _runner.Stop(vm.Model.Id);
        vm.IsRunning = false;
    }

    [RelayCommand]
    private void ClearLog() => SelectedSite?.ClearLog();

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
            PlatformInfo.OpenUrl(url);
    }

    [RelayCommand]
    private async Task CopyUrlAsync(string? url)
    {
        if (Ui is not null && !string.IsNullOrWhiteSpace(url))
        {
            await Ui.CopyToClipboardAsync(url);
            StatusMessage = $"Copiado: {url}";
        }
    }

    [RelayCommand]
    private void OpenProjectFolder()
    {
        if (SelectedSite is { } vm && Path.GetDirectoryName(vm.ProjectPath) is { } dir && Directory.Exists(dir))
            PlatformInfo.OpenUrl(dir);
    }

    private string? ValidateConflicts(SiteViewModel vm, SiteEndpoints ep)
    {
        foreach (var other in Sites.Where(s => s != vm && !s.IsNew && s.Endpoints is not null))
        {
            if (other.Endpoints!.Host == ep.Host)
                return $"O domínio {ep.Host} já é usado por {other.Name}.";
            if (ep.ExplicitPort is int p && other.Endpoints.ListenPort == p)
                return $"A porta {p} já é usada por {other.Name}.";
        }
        if (ep.ExplicitPort is int port && (port == _config.HttpPort || (_config.EnableHttps && port == _config.HttpsPort)))
            return $"A porta {port} é usada pelo proxy do Dot Valet; escolha outra.";
        return null;
    }

    private IEnumerable<int> ReservedPorts(Site? except = null)
    {
        foreach (var s in Sites.Where(s => s.Model != except))
        {
            if (s.Model.AppPort > 0)
                yield return s.Model.AppPort;
            if (s.Endpoints?.ExplicitPort is int p)
                yield return p;
        }
        yield return _config.HttpPort;
        yield return _config.HttpsPort;
    }

    private string UniqueSlug(string slug)
    {
        var hosts = Sites.Select(s => s.Endpoints?.Host).ToHashSet();
        var candidate = slug;
        for (var i = 2; hosts.Contains($"{candidate}.{_config.Tld}"); i++)
            candidate = $"{slug}-{i}";
        return candidate;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            PlatformInfo.Os == OsKind.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    // ================= Park (pastas estacionadas) =================

    [RelayCommand]
    private async Task ParkFolderAsync()
    {
        if (Ui is null)
            return;
        var folder = await Ui.PickFolderAsync("Pasta com vários projetos (cada subpasta vira um site)");
        if (folder is null)
            return;
        if (!_config.ParkedPaths.Any(p => SamePath(p, folder)))
        {
            _config.ParkedPaths.Add(folder);
            ParkedPaths.Add(folder);
        }
        await RescanParkedAsync();
    }

    [RelayCommand]
    private async Task RescanParkedAsync()
    {
        await RunBusyAsync(async () =>
        {
            var added = ScanParkedFolders();
            Persist();
            UpdateProxyRoutes();
            await SyncDnsAsync(quietWhenUnchanged: true);
            StatusMessage = added == 0 ? "Nenhum projeto novo nas pastas estacionadas." : $"{added} site(s) adicionados das pastas estacionadas.";
        });
    }

    [RelayCommand]
    private async Task UnparkFolderAsync()
    {
        if (SelectedParkedPath is not { } path)
            return;
        await RunBusyAsync(async () =>
        {
            _config.ParkedPaths.RemoveAll(p => SamePath(p, path));
            ParkedPaths.Remove(path);
            var root = Path.GetFullPath(path) + Path.DirectorySeparatorChar;
            foreach (var vm in Sites.Where(s => s.Model.Parked && Path.GetFullPath(s.Model.ProjectPath).StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _runner.Stop(vm.Model.Id);
                try { LaunchSettingsService.Remove(vm.Model); } catch { /* best effort */ }
                _config.Sites.Remove(vm.Model);
                Sites.Remove(vm);
            }
            OnPropertyChanged(nameof(HasNoSites));
            SelectedSite ??= Sites.FirstOrDefault();
            Persist();
            UpdateProxyRoutes();
            await SyncDnsAsync(quietWhenUnchanged: true);
            StatusMessage = $"Pasta {path} removida.";
        });
    }

    /// <summary>Cada subpasta com um projeto web vira um site "pasta.tld", como o `valet park`.</summary>
    private int ScanParkedFolders()
    {
        var added = 0;
        foreach (var parked in _config.ParkedPaths.Where(Directory.Exists))
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.GetDirectories(parked).Order(); }
            catch { continue; }

            foreach (var dir in dirs)
            {
                if (Path.GetFileName(dir).StartsWith('.'))
                    continue;
                var project = ProjectInspector.FindProjectFile(dir);
                if (project is null || ProjectInspector.TryInspect(project) is not { IsWeb: true } info)
                    continue;
                if (Sites.Any(s => SamePath(s.Model.ProjectPath, project)))
                    continue;

                var site = new Site
                {
                    Name = info.Name,
                    ProjectPath = project,
                    Domain = UniqueSlug(DomainParser.Slugify(Path.GetFileName(dir))),
                    AppPort = PortAllocator.Next(ReservedPorts()),
                    Parked = true,
                    UpdateLaunchSettings = false,
                };
                _config.Sites.Add(site);
                Sites.Add(CreateVm(site));
                added++;
            }
        }
        if (added > 0)
        {
            OnPropertyChanged(nameof(HasNoSites));
            SelectedSite ??= Sites.FirstOrDefault();
        }
        return added;
    }

    // ================= Proxy =================

    [RelayCommand]
    private async Task ToggleProxyAsync()
    {
        if (_proxy.IsRunning)
        {
            await _proxy.StopAsync();
            ProxyRunning = false;
            ProxyStatus = "parado";
        }
        else
        {
            await StartProxyAsync();
        }
    }

    private async Task StartProxyAsync()
    {
        var https = _config.EnableHttps && _certs.CaExists ? _config.HttpsPort : (int?)null;
        try
        {
            await _proxy.StartAsync(_config.HttpPort, https, _certs, BuildRoutes());
            ProxyRunning = true;
            ProxyStatus = $"rodando em http://localhost:{_config.HttpPort}" + (https is int p ? $" e https://localhost:{p}" : "") +
                          (_proxy.ListensOnAllInterfaces ? " (escuta em todas as interfaces por exigência do macOS, mas só aceita conexões desta máquina)" : "");
        }
        catch (Exception e)
        {
            ProxyRunning = false;
            var inner = e is IOException { InnerException: { } ie } and not PortInUseException ? ie : e;
            var hint = inner switch
            {
                PortInUseException { Owner: "nginx" or null } when _valet.IsInstalled =>
                    _config.ValetIntegration
                        ? " É o nginx do Laravel Valet — com a integração ligada, os sites são servidos por ele (sem porta)."
                        : $" É o nginx do Laravel Valet: pare-o ({ValetService.StopCommand}) ou desinstale-o ({ValetService.UninstallCommand}) para não gerar conflito, ligue a integração com o Valet ou mude as portas do proxy na aba Ambiente.",
                PortInUseException =>
                    " Encerre o outro programa ou mude as portas do proxy na aba Ambiente.",
                _ when inner is SocketException { SocketErrorCode: SocketError.AccessDenied } || inner.Message.Contains("ermission") =>
                    PlatformInfo.Os == OsKind.Linux
                        ? " Portas < 1024 exigem root no Linux: use 8080/8443 ou rode `sudo sysctl -w net.ipv4.ip_unprivileged_port_start=80`."
                        : " Sem permissão para usar a porta.",
                _ => " A porta pode estar em uso (Laravel Valet, nginx, Apache, IIS, Docker?). Altere as portas na aba Ambiente.",
            };
            ProxyStatus = $"erro: {inner.Message}.{hint}";
            StatusMessage = $"Proxy não iniciou: {inner.Message}";
        }
    }

    private IReadOnlyList<ProxyRoute> BuildRoutes() => Sites
        .Where(s => !s.IsNew && s.Endpoints is not null)
        .Select(s => new ProxyRoute(s.Model.Id, s.Name, s.Endpoints!.Host, s.Endpoints.ListenPort, s.Model.Secure && _config.EnableHttps))
        .ToList();

    private void UpdateProxyRoutes() => _proxy.UpdateRoutes(BuildRoutes());

    private async Task<string> EnsureHttpsReadyAsync()
    {
        var created = !_certs.CaExists;
        _certs.EnsureCa();
        RefreshCaStatus();
        if (created || (_proxy.IsRunning && _proxy.HttpsPort is null))
            await StartProxyAsync();
        if (_certs.IsCaTrusted())
            return "";
        // Sem a CA confiável o navegador mostra "Não seguro": oferece instalar na hora, em vez de só avisar.
        if (Ui is not null && await Ui.ConfirmAsync("Confiar na CA local",
                "Para o navegador aceitar o HTTPS dos sites .test sem o aviso \"Não seguro\", a CA local do Dot Valet precisa " +
                "ser instalada como confiável (o sistema vai pedir sua senha). Instalar agora?"))
        {
            var result = await TrustCaCoreAsync();
            return result.Success ? "CA local instalada como confiável — recarregue a página no navegador" : $"CA NÃO instalada: {result.Output}";
        }
        return "HTTPS ativo, mas o navegador vai mostrar \"Não seguro\" até você clicar em \"Criar e confiar na CA local\" na aba Ambiente";
    }

    // ================= DNS =================

    private IEnumerable<string> HostsNeedingEntries()
    {
        var hosts = Sites.Where(s => !s.IsNew && s.Endpoints is not null).Select(s => s.Endpoints!.Host);
        // O dnsmasq de outra ferramenta só é reaproveitado se não for o do Valet: ele some com `valet stop`.
        var dnsState = _dnsmasq.GetState(_config.Tld);
        if (_config.DnsMode == DnsMode.Dnsmasq && (dnsState == DnsResolverState.Managed || (dnsState == DnsResolverState.External && !_valet.IsInstalled)))
            hosts = hosts.Where(h => !h.EndsWith($".{_config.Tld}", StringComparison.OrdinalIgnoreCase));
        return hosts;
    }

    /// <returns>Mensagem para o status, ou null se nada mudou.</returns>
    private async Task<string?> SyncDnsAsync(bool quietWhenUnchanged)
    {
        try
        {
            var result = await _hosts.SyncAsync(HostsNeedingEntries());
            var hosts = result is null
                ? quietWhenUnchanged ? null : "arquivo hosts já estava atualizado"
                : result.Success ? "arquivo hosts atualizado" : $"hosts NÃO atualizado: {result.Output}";
            var valet = await SyncValetAsync();
            return hosts is null ? valet : valet is null ? hosts : $"{hosts} · {valet}";
        }
        catch (Exception e)
        {
            return $"hosts NÃO atualizado: {e.Message}";
        }
    }

    // ================= Laravel Valet =================

    private List<ValetProxy> DesiredValetProxies()
    {
        if (!_config.ValetIntegration || !_valet.IsInstalled)
            return [];
        var tld = _valet.Tld;
        return Sites
            .Where(s => !s.IsNew && s.Endpoints is not null && s.Endpoints.Host.EndsWith($".{tld}", StringComparison.OrdinalIgnoreCase))
            .Select(s => new ValetProxy(s.Endpoints!.Host, $"http://localhost:{s.Endpoints.ListenPort}", s.Model.Secure && _config.EnableHttps))
            .DistinctBy(p => p.Host)
            .OrderBy(p => p.Host)
            .ToList();
    }

    /// <returns>Mensagem para o status, ou null se nada mudou.</returns>
    private async Task<string?> SyncValetAsync()
    {
        var desired = DesiredValetProxies();
        var result = await _valet.SyncProxiesAsync(desired, _config.ValetProxies);
        if (result is null)
            return null;
        if (!result.Success)
            return $"Laravel Valet NÃO atualizado: {result.Output}";
        _config.ValetProxies = desired;
        Persist();
        await RefreshValetStatusAsync();
        return desired.Count == 0 ? "sites removidos do Laravel Valet" : $"{desired.Count} site(s) publicados pelo Laravel Valet";
    }

    partial void OnValetIntegrationChanged(bool value)
    {
        if (_config.ValetIntegration == value)
            return;
        _config.ValetIntegration = value;
        Persist();
        _ = RunBusyAsync(async () => StatusMessage = await SyncValetAsync() ?? "Integração com o Laravel Valet atualizada.");
    }

    private async Task RefreshValetStatusAsync()
    {
        if (!_valet.IsInstalled)
        {
            ValetStatus = "não instalado";
            return;
        }
        var running = await _valet.IsUsingPortAsync(80);
        ValetStatus = (running ? "rodando (nginx em 127.0.0.1:80)" : "parado") +
                      (_config.ValetProxies.Count > 0 ? $" · publicando {string.Join(", ", _config.ValetProxies.Select(p => p.Host))}" : "");
    }

    [RelayCommand]
    private async Task StopValetAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _valet.StopAsync();
            await RefreshValetStatusAsync();
            if (!result.Success)
            {
                StatusMessage = $"Não foi possível parar o Laravel Valet: {result.Output}";
                return;
            }
            await StartProxyAsync();
            StatusMessage = $"Laravel Valet parado ({ValetService.StartCommand} para voltar). Proxy: {ProxyStatus}";
        });
    }

    [RelayCommand]
    private async Task CopyValetCommandAsync(string? command)
    {
        if (Ui is null || string.IsNullOrEmpty(command))
            return;
        await Ui.CopyToClipboardAsync(command);
        StatusMessage = $"Copiado: {command}";
    }

    [RelayCommand]
    private async Task SyncHostsAsync()
    {
        await RunBusyAsync(async () => StatusMessage = await SyncDnsAsync(quietWhenUnchanged: false) ?? "");
    }

    [RelayCommand]
    private async Task InstallDnsmasqAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _dnsmasq.InstallAsync(_config.Tld, msg => Dispatcher.UIThread.Post(() => StatusMessage = msg));
            RefreshDnsStatus();
            if (!result.Success)
            {
                StatusMessage = $"Falha ao configurar dnsmasq: {result.Output}";
                return;
            }
            _config.DnsMode = DnsMode.Dnsmasq;
            UseDnsmasq = true;
            UseHostsFile = false;
            Persist();
            await SyncDnsAsync(quietWhenUnchanged: true);
            StatusMessage = $"dnsmasq configurado: todo *.{_config.Tld} resolve para 127.0.0.1.";
        });
    }

    [RelayCommand]
    private async Task UninstallDnsmasqAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _dnsmasq.UninstallAsync(_config.Tld);
            _config.DnsMode = DnsMode.HostsFile;
            UseHostsFile = true;
            UseDnsmasq = false;
            Persist();
            RefreshDnsStatus();
            var dns = await SyncDnsAsync(quietWhenUnchanged: true);
            StatusMessage = result.Success ? $"dnsmasq removido. {dns}" : $"Falha: {result.Output}";
        });
    }

    partial void OnBackupHostsFileChanged(bool value)
    {
        _hosts.BackupEnabled = value;
        RefreshHostsBackupStatus();
        if (_config.BackupHostsFile == value)
            return;
        _config.BackupHostsFile = value;
        Persist();
    }

    [RelayCommand]
    private Task RestoreHostsLastAsync() => RestoreHostsAsync(principal: false);

    [RelayCommand]
    private Task RestoreHostsPrincipalAsync() => RestoreHostsAsync(principal: true);

    private async Task RestoreHostsAsync(bool principal)
    {
        var name = principal ? "principal" : "último";
        if (Ui is not null && !await Ui.ConfirmAsync("Restaurar hosts",
                $"Substituir {_hosts.HostsPath} pelo backup {name}? O conteúdo atual vira o novo backup \"último\"."))
            return;
        await RunBusyAsync(async () =>
        {
            var result = await _hosts.RestoreAsync(principal);
            StatusMessage = result is null ? $"O hosts já está igual ao backup {name}."
                : result.Success ? $"hosts restaurado a partir do backup {name}."
                : $"hosts NÃO restaurado: {result.Output}";
            RefreshHostsBackupStatus();
        });
    }

    private void RefreshHostsBackupStatus()
    {
        static string Describe(string path) => File.Exists(path) ? $"{File.GetLastWriteTime(path):dd/MM/yyyy HH:mm} — {path}" : "não existe";
        HostsBackupStatus = $"Principal: {Describe(_hosts.PrincipalBackupPath)}\nÚltimo: {Describe(_hosts.LastBackupPath)}";
    }

    private void RefreshDnsStatus()
    {
        RefreshHostsBackupStatus();
        DnsmasqStatus = !_dnsmasq.IsSupported
            ? "indisponível nesta plataforma"
            : _dnsmasq.IsInstalled(_config.Tld) ? $"configurado para *.{_config.Tld}" : "não configurado";
        OnPropertyChanged(nameof(DnsmasqHint));
    }

    // ================= Certificados =================

    [RelayCommand]
    private async Task TrustCaAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await TrustCaCoreAsync();
            StatusMessage = result.Success
                ? "CA local instalada como confiável. Recarregue a página (ou reinicie o navegador se ele continuar com o aviso)."
                : $"Não foi possível confiar na CA: {result.Output}";
        });
    }

    private async Task<ProcessResult> TrustCaCoreAsync()
    {
        var result = await _certs.TrustCaAsync();
        if (result.Success && !_certs.IsCaTrusted())
            result = new ProcessResult(1, result.StdOut, "o comando terminou, mas o sistema ainda não confia na CA. " + result.StdErr);
        if (result.Success)
        {
            _config.CaTrusted = true;
            Persist();
            if (_proxy.IsRunning && _proxy.HttpsPort is null && _config.EnableHttps)
                await StartProxyAsync();
        }
        RefreshCaStatus();
        return result;
    }

    private void RefreshCaStatus()
    {
        var trusted = _certs.IsCaTrusted();
        if (_config.CaTrusted != trusted && _certs.CaExists)
        {
            _config.CaTrusted = trusted;
            Persist();
        }
        CaStatus = !_certs.CaExists ? "ainda não criada (é criada ao salvar o primeiro site HTTPS)"
            : trusted ? "criada e confiável no sistema ✔"
            : "criada, mas NÃO confiável — o navegador mostra \"Não seguro\". Clique em \"Criar e confiar na CA local\".";
    }

    // ================= Configurações =================

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var tld = Tld.Trim().Trim('.').ToLowerInvariant();
        if (!DomainParser.TryParse($"x.{tld}", tld, out _, out _) || tld.Contains('.'))
        {
            StatusMessage = "TLD inválido (ex.: test).";
            return;
        }
        if (HttpPort is < 1 or > 65535 || HttpsPort is < 1 or > 65535 || HttpPort == HttpsPort)
        {
            StatusMessage = "Portas do proxy inválidas.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var proxyChanged = HttpPort != _config.HttpPort || HttpsPort != _config.HttpsPort || EnableHttps != _config.EnableHttps;
            var tldChanged = tld != _config.Tld;
            var oldTld = _config.Tld;

            _config.Tld = tld;
            Tld = tld;
            _config.HttpPort = HttpPort;
            _config.HttpsPort = HttpsPort;
            _config.EnableHttps = EnableHttps;
            _config.AutoStartProxy = AutoStartProxy;
            _config.DnsMode = UseDnsmasq && _dnsmasq.IsSupported ? DnsMode.Dnsmasq : DnsMode.HostsFile;

            // URLs mudaram: recalcula e reaplica o launchSettings de quem usa.
            var errors = new List<string>();
            foreach (var s in Sites)
            {
                s.Refresh();
                if (s.IsNew || !s.Model.UpdateLaunchSettings || s.Endpoints is not { } ep)
                    continue;
                if (!tldChanged && !proxyChanged && s.Model.AppliedBindingUrl == ep.BindingUrl)
                    continue;
                try { LaunchSettingsService.Apply(s.Model, ep); }
                catch (Exception e) { errors.Add($"{s.Name}: {e.Message}"); }
            }
            Persist();

            if (proxyChanged && (_proxy.IsRunning || _config.AutoStartProxy))
                await StartProxyAsync();
            else
                UpdateProxyRoutes();

            RefreshDnsStatus();
            var dns = await SyncDnsAsync(quietWhenUnchanged: true);
            StatusMessage = "Configurações salvas." +
                            (tldChanged && _dnsmasq.IsInstalled(oldTld) ? $" O dnsmasq ainda aponta *.{oldTld}: reconfigure-o." : "") +
                            (dns is null ? "" : $" {dns}.") +
                            (errors.Count > 0 ? " Erros: " + string.Join("; ", errors) : "");
        });
    }

    // ================= Infra =================

    private void Persist()
    {
        _config.ParkedPaths = ParkedPaths.ToList();
        try
        {
            ConfigStore.Save(_config);
        }
        catch (Exception e)
        {
            StatusMessage = $"Não foi possível salvar {ConfigStore.ConfigPath}: {e.Message}";
        }
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception e)
        {
            StatusMessage = $"Erro: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ShutdownAsync()
    {
        _runner.StopAll();
        await _proxy.StopAsync();
    }
}
