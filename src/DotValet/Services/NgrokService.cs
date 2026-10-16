using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotValet.Services;

/// <summary>Uma linha do log do ngrok interpretada: a URL pública quando o túnel sobe, ou um erro.</summary>
public sealed record NgrokLogEvent(string? PublicUrl, string? Error);

/// <summary>
/// Compartilha um site pela internet com o agente do ngrok (equivalente ao `valet share`):
/// `ngrok http http://localhost:{porta do app} [--url meu-app.ngrok-free.app]`.
/// O túnel aponta direto para o Kestrel do app, então funciona igual nos modos proxy e direto.
/// </summary>
public sealed partial class NgrokService
{
    public const string DownloadUrl = "https://ngrok.com/download";
    public const string AuthTokenUrl = "https://dashboard.ngrok.com/get-started/your-authtoken";
    public const string DomainsUrl = "https://dashboard.ngrok.com/domains";

    private readonly ConcurrentDictionary<string, Process> _processes = new();

    public static string? FindExecutable() => PlatformInfo.FindExecutable("ngrok",
        PlatformInfo.Os == OsKind.Windows
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ngrok.exe")]
            : ["/opt/homebrew/bin/ngrok", "/usr/local/bin/ngrok", "/snap/bin/ngrok", "/usr/bin/ngrok"]);

    public bool IsSharing(string siteId) => _processes.TryGetValue(siteId, out var p) && !p.HasExited;

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex HostRegex();

    /// <summary>
    /// Aceita "meu-app.ngrok-free.app" ou a URL copiada do painel ("https://meu-app.ngrok-free.app/").
    /// Vazio é válido: o ngrok gera uma URL aleatória.
    /// </summary>
    public static bool TryNormalizeDomain(string? input, out string? domain, out string? error)
    {
        domain = null;
        error = null;
        var value = input?.Trim().ToLowerInvariant() ?? "";
        if (value.Length == 0)
            return true;
        if (value.Contains("://"))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                error = "URL do ngrok inválida.";
                return false;
            }
            value = uri.Host;
        }
        value = value.TrimEnd('/');
        if (!HostRegex().IsMatch(value))
        {
            error = "Domínio do ngrok inválido (ex.: meu-app.ngrok-free.app).";
            return false;
        }
        domain = value;
        return true;
    }

    public static string PublicUrlFor(string domain) => $"https://{domain}";

    /// <summary>
    /// Endereço do app para o túnel. Não usa "localhost": o ngrok pode resolvê-lo para [::1] enquanto o app só escuta em
    /// 127.0.0.1 (ou o contrário) e aí todo acesso dá ERR_NGROK_8012. Testa os dois loopbacks e usa o que responde.
    /// </summary>
    /// <returns>A URL e se havia algo escutando (false = o app não está rodando nessa porta).</returns>
    public static async Task<(string Url, bool Listening)> ResolveTargetAsync(int port)
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            using var client = new TcpClient(address.AddressFamily);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                await client.ConnectAsync(address, port, cts.Token);
                return (TargetUrl(address, port), true);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
            }
        }
        return (TargetUrl(IPAddress.Loopback, port), false);
    }

    public static string TargetUrl(IPAddress address, int port) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"http://[{address}]:{port}" : $"http://{address}:{port}";

    /// <param name="legacyDomainFlag">Agentes v3 antigos só conhecem --domain (o --url veio depois).</param>
    public static List<string> BuildArguments(string targetUrl, string? domain, bool legacyDomainFlag = false)
    {
        var args = new List<string> { "http", targetUrl, "--log", "stdout", "--log-format", "json" };
        if (domain is not null)
            args.Add(legacyDomainFlag ? $"--domain={domain}" : $"--url={PublicUrlFor(domain)}");
        return args;
    }

    /// <summary>Interpreta uma linha do log JSON (`--log-format json`) ou uma mensagem "ERROR:" do stderr.</summary>
    public static NgrokLogEvent? ParseLogLine(string line)
    {
        line = line.Trim();
        if (line.StartsWith("ERROR:", StringComparison.Ordinal))
        {
            var text = line["ERROR:".Length..].Trim();
            return text.Length == 0 ? null : new NgrokLogEvent(null, text);
        }
        if (!line.StartsWith('{'))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            if (Get("msg") == "started tunnel" && Get("url") is { Length: > 0 } url)
                return new NgrokLogEvent(url, null);
            if (Get("lvl") is "eror" or "crit" && (Get("err") ?? Get("msg")) is { Length: > 0 } err)
                return new NgrokLogEvent(null, err);
            // Requisição que chegou pelo túnel mas o app não atendeu (o visitante vê ERR_NGROK_8012).
            if (Get("lvl") == "warn" && Get("err") is { } upstream && upstream.Contains("dial tcp", StringComparison.Ordinal))
                return new NgrokLogEvent(null, upstream);
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>Explicação para os erros mais comuns do ngrok (códigos ERR_NGROK_*).</summary>
    public static string? Hint(string error) => error switch
    {
        _ when error.Contains("ERR_NGROK_4018") || error.Contains("authtoken", StringComparison.OrdinalIgnoreCase) && error.Contains("required", StringComparison.OrdinalIgnoreCase) =>
            "Salve o authtoken do ngrok na aba Ambiente.",
        _ when error.Contains("ERR_NGROK_105") || error.Contains("ERR_NGROK_107") =>
            "O authtoken foi recusado: confira o token salvo na aba Ambiente.",
        _ when error.Contains("ERR_NGROK_108") =>
            "O plano gratuito permite um agente do ngrok por vez: pare o outro compartilhamento (ou o ngrok aberto no terminal).",
        _ when error.Contains("ERR_NGROK_320") || error.Contains("ERR_NGROK_319") || error.Contains("ERR_NGROK_313") =>
            "Esse domínio não está reservado na sua conta: crie-o em dashboard.ngrok.com/domains ou deixe o campo vazio.",
        _ when error.Contains("ERR_NGROK_8012") || error.Contains("connection refused", StringComparison.OrdinalIgnoreCase) =>
            "O app não respondeu na porta do site: inicie-o (▶ Iniciar) ou, se roda pela IDE, use o perfil do Dot Valet (mesma porta).",
        _ when error.Contains("ERR_NGROK_334") =>
            "Esse domínio já está em uso por outro túnel.",
        _ => null,
    };

    /// <summary>Sobe o agente do ngrok para o site. onUrl é chamado quando o túnel fica disponível.</summary>
    public void Start(string siteId, string ngrokPath, string targetUrl, string? domain, string? authToken,
        Action<string> onOutput, Action<string> onUrl, Action<string?> onExit, Action<string>? onError = null, bool legacyDomainFlag = false)
    {
        if (IsSharing(siteId))
            return;

        var psi = new ProcessStartInfo(ngrokPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildArguments(targetUrl, domain, legacyDomainFlag))
            psi.ArgumentList.Add(arg);
        // Pela variável de ambiente o token não aparece na lista de processos (como apareceria com --authtoken).
        if (!string.IsNullOrWhiteSpace(authToken))
            psi.Environment["NGROK_AUTHTOKEN"] = authToken.Trim();
        PlatformInfo.EnrichPath(psi);
        PlatformInfo.RunAsUser(psi);

        string? publicUrl = null;
        string? lastError = null;
        var unknownFlag = false;
        void Handle(string? line)
        {
            if (line is null)
                return;
            if (line.Contains("unknown flag: --url", StringComparison.Ordinal))
                unknownFlag = true;
            if (ParseLogLine(line) is not { } ev)
                return;
            if (ev.PublicUrl is { } url)
            {
                publicUrl = url;
                onOutput($"[ngrok] túnel aberto: {url} → {targetUrl}");
                onUrl(url);
            }
            else if (ev.Error is { } err)
            {
                lastError = err;
                onOutput($"[ngrok] erro: {err}");
                onError?.Invoke(err);
            }
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);
        process.Exited += (_, _) =>
        {
            // Garante que todo o stderr foi lido antes de decidir o motivo da saída.
            try { process.WaitForExit(); } catch { /* já encerrado */ }
            var wasTracked = _processes.TryRemove(new KeyValuePair<string, Process>(siteId, process));
            process.Dispose();
            if (wasTracked && unknownFlag && !legacyDomainFlag && publicUrl is null)
            {
                onOutput("[ngrok] versão antiga do agente: tentando de novo com --domain");
                Start(siteId, ngrokPath, targetUrl, domain, authToken, onOutput, onUrl, onExit, onError, legacyDomainFlag: true);
                return;
            }
            // Parado pelo usuário (Stop já o removeu): não é erro.
            onExit(!wasTracked ? null : publicUrl is null ? lastError ?? "o ngrok encerrou sem abrir o túnel" : lastError);
        };

        onOutput($"$ ngrok {string.Join(' ', psi.ArgumentList)}");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes[siteId] = process;
    }

    public void Stop(string siteId)
    {
        if (_processes.TryRemove(siteId, out var p))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* já encerrado */ }
        }
    }

    public void StopAll()
    {
        foreach (var id in _processes.Keys.ToList())
            Stop(id);
    }
}
