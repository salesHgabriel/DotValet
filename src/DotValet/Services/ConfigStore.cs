using System.Text.Json;
using System.Text.Json.Serialization;
using DotValet.Models;

namespace DotValet.Services;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ConfigPath => Path.Combine(PlatformInfo.DataDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Options) ?? new AppConfig();
        }
        catch (JsonException)
        {
            File.Copy(ConfigPath, ConfigPath + ".corrupt", overwrite: true);
        }
        return new AppConfig
        {
            DnsMode = DnsMode.HostsFile,
            // Linux não permite portas < 1024 sem root por padrão.
            HttpPort = PlatformInfo.Os == OsKind.Linux ? 8080 : 80,
            HttpsPort = PlatformInfo.Os == OsKind.Linux ? 8443 : 443,
        };
    }

    public static void Save(AppConfig config) => Save(config, ConfigPath);

    internal static void Save(AppConfig config, string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        // Guarda o authtoken do ngrok: só o dono lê.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
        PlatformInfo.GiveBackToUser(path);
    }
}
