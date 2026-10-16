using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotValet;
using DotValet.Models;
using DotValet.Services;
using DotValet.ViewModels;
using DotValet.Views;

// Gera as imagens do README em docs/screenshots/:
//   - a janela do Dot Valet (Avalonia headless, sem abrir janela de verdade);
//   - os apps de samples/ rodando pelo Dot Valet nos domínios .test, capturados com o Chrome headless.
//
// Nada do sistema é alterado: config, CA e hosts ficam num diretório temporário (DOTVALET_HOME/DOTVALET_HOSTS_FILE)
// e o Chrome resolve *.test para 127.0.0.1 com --host-resolver-rules. O proxy precisa das portas 80/443 livres
// (macOS e Windows; no Linux rode com permissão para portas baixas).
//
//   dotnet run --project tools/DotValet.Screenshots [-- --out docs/screenshots]

var repo = FindRepoRoot();
var outDir = Path.GetFullPath(ArgValue("--out") ?? Path.Combine(repo, "docs", "screenshots"));
var work = OperatingSystem.IsWindows() ? Path.Combine(Path.GetTempPath(), "dotvalet-demo") : "/tmp/dotvalet-demo";
var chrome = FindChrome() ?? throw new InvalidOperationException("Chrome/Chromium não encontrado; defina CHROME_PATH.");

if (Directory.Exists(work))
    Directory.Delete(work, recursive: true);
Directory.CreateDirectory(outDir);

var home = Path.Combine(work, ".dotvalet");
var hostsFile = Path.Combine(work, "hosts");
Directory.CreateDirectory(home);
File.WriteAllText(hostsFile, "127.0.0.1\tlocalhost\n::1\tlocalhost\n");
Environment.SetEnvironmentVariable("DOTVALET_HOME", home);
Environment.SetEnvironmentVariable("DOTVALET_HOSTS_FILE", hostsFile);
Environment.SetEnvironmentVariable("DOTVALET_ELEVATION", "simulate");

// Os exemplos são copiados para fora do repositório: bin/obj e launchSettings.json não sujam samples/.
Site Sample(string name, string domain, bool secure = false, int appPort = 0)
{
    var dir = Path.Combine(work, "Projetos", name);
    CopyDirectory(Path.Combine(repo, "samples", name), dir);
    return new Site
    {
        Name = name,
        ProjectPath = Path.Combine(dir, $"{name}.csproj"),
        Domain = domain,
        AppPort = appPort,
        Secure = secure,
    };
}

ConfigStore.Save(new AppConfig
{
    HttpPort = 80,
    HttpsPort = 443,
    EnableHttps = true,
    CaTrusted = true,
    Sites =
    [
        Sample("Loja", "loja", appPort: 5101),
        Sample("Painel", "painel.test:5172"),
        Sample("Blog", "blog.test", secure: true, appPort: 5102),
    ],
});
new CertificateService().EnsureCa();

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var done = new CancellationTokenSource();
Exception? failure = null;
Dispatcher.UIThread.Post(async () =>
{
    try
    {
        await RunAsync();
    }
    catch (Exception e)
    {
        failure = e;
    }
    finally
    {
        done.Cancel();
    }
});
Dispatcher.UIThread.MainLoop(done.Token);

if (failure is not null)
{
    Console.Error.WriteLine(failure);
    return 1;
}
Console.WriteLine($"Imagens em {outDir}");
return 0;

async Task RunAsync()
{
    var vm = new MainViewModel();
    var window = new MainWindow { DataContext = vm, Width = 1180, Height = 800 };
    vm.Ui = window;
    window.Show();

    try
    {
        await vm.InitializeAsync();
        if (!vm.ProxyRunning)
            throw new InvalidOperationException($"proxy não iniciou: {vm.ProxyStatus}");

        foreach (var site in vm.Sites)
        {
            vm.SelectedSite = site;
            vm.StartSiteCommand.Execute(null);
        }
        foreach (var site in vm.Sites)
        {
            await WaitForAppAsync(site.Endpoints!.ListenPort);
            Console.WriteLine($"{site.Name} no ar em {site.PrimaryUrl}");
        }

        foreach (var site in vm.Sites)
            await BrowserScreenshotAsync(site.PrimaryUrl, Path.Combine(outDir, $"app-{site.Name.ToLowerInvariant()}.png"));

        var tabs = window.GetLogicalDescendants().OfType<TabControl>().First();
        vm.StatusMessage = "3 sites rodando · proxy em http://localhost:80 e https://localhost:443";
        vm.SelectedSite = vm.Sites.First(s => s.Name == "Painel");
        Capture(window, "dotvalet-sites.png");
        vm.SelectedSite = vm.Sites.First(s => s.Name == "Blog");
        Capture(window, "dotvalet-https.png");
        tabs.SelectedIndex = 1;
        Capture(window, "dotvalet-ambiente.png");
    }
    finally
    {
        await vm.ShutdownAsync();
        window.Close();
    }
}

void Capture(Window window, string file)
{
    // Deixa o layout/binding assentar antes de renderizar o quadro.
    void Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
    Settle();
    // O log acompanha o fim do texto e arrasta a rolagem dos detalhes: volta tudo para o topo.
    window.FindControl<TextBox>("LogBox")!.CaretIndex = 0;
    Settle();
    foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>())
        scroll.Offset = default;
    Settle();
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("nenhum quadro renderizado");
    frame.Save(Path.Combine(outDir, file));
    Console.WriteLine($"  {file}");
}

static async Task WaitForAppAsync(int port)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var deadline = DateTime.UtcNow.AddMinutes(3);
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            if ((await http.GetAsync($"http://127.0.0.1:{port}/")).IsSuccessStatusCode)
                return;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
        }
        await Task.Delay(500);
    }
    throw new TimeoutException($"o app da porta {port} não respondeu");
}

async Task BrowserScreenshotAsync(string url, string file)
{
    var psi = new ProcessStartInfo(chrome) { RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var arg in new[]
    {
        "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--no-default-browser-check",
        $"--user-data-dir={Path.Combine(work, "chrome")}",
        // Sem mexer no hosts/DNS do sistema: só este Chrome resolve *.test para a máquina local.
        "--host-resolver-rules=MAP *.test 127.0.0.1",
        // A CA do Dot Valet foi criada no diretório temporário e não é confiável no sistema.
        "--ignore-certificate-errors",
        "--disable-features=HttpsUpgrades",
        "--window-size=1100,620", "--force-device-scale-factor=2",
        $"--screenshot={file}", url,
    })
        psi.ArgumentList.Add(arg);

    using var process = Process.Start(psi)!;
    _ = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    await process.WaitForExitAsync(cts.Token);
    if (!File.Exists(file))
        throw new InvalidOperationException($"Chrome não gerou {file}: {await stderr}");
    Console.WriteLine($"  {Path.GetFileName(file)} ← {url}");
}

static string? FindChrome()
{
    if (Environment.GetEnvironmentVariable("CHROME_PATH") is { Length: > 0 } custom)
        return custom;
    string[] candidates = OperatingSystem.IsMacOS()
        ? ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Chromium.app/Contents/MacOS/Chromium"]
        : OperatingSystem.IsWindows()
        ? [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
        ]
        : ["/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser"];
    return candidates.FirstOrDefault(File.Exists);
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "DotValet.sln")))
            return dir.FullName;
    }
    throw new InvalidOperationException("DotValet.sln não encontrado");
}

static void CopyDirectory(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    foreach (var dir in Directory.GetDirectories(source))
    {
        var name = Path.GetFileName(dir);
        if (name is not ("bin" or "obj"))
            CopyDirectory(dir, Path.Combine(target, name));
    }
}

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
