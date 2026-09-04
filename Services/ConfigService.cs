using System;
using System.IO;
using System.Text.Json;
using StarFix.Models;

namespace StarFix.Services;

/// <summary>Loads and saves config.json in %AppData%\StarFix\.</summary>
public static class ConfigService
{
    public static readonly string AppDataDir = ResolveAppDataDir();

    /// <summary>Environment.SpecialFolder.ApplicationData confirmed returning an empty
    /// string on a real Linux runtime test — not a container/root-user artifact (reproduced
    /// identically as a normal non-root user with HOME correctly set, and independent of
    /// InvariantGlobalization). Falls back to $HOME/.config (the same convention .NET's own
    /// Unix implementation is documented to use) rather than silently failing to save
    /// config/logs anywhere at all, which is what happened before this fix — Save() has
    /// always caught and logged the exception, but a relative/empty path meant nothing
    /// useful ever got written or found on the next run.</summary>
    private static string ResolveAppDataDir()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(path))
            return Path.Combine(path, "StarFix");

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
            return Path.Combine(home, ".config", "StarFix");

        return Path.Combine(AppContext.BaseDirectory, "StarFixData");
    }

    private static readonly string ConfigPath = Path.Combine(AppDataDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Config] Load failed: {ex.Message}");
        }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Config] Save failed: {ex.Message}");
        }
    }
}
