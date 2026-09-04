using System;
using System.IO;
using System.Linq;

namespace StarFix.Services;

/// <summary>
/// Writes a timestamped session log to %AppData%\StarFix\logs\.
/// Keeps the five most recent sessions; older files are deleted at startup.
/// All writes are best-effort — exceptions are silently swallowed.
/// Ported from VariLab/TransitLab's SessionLogService so diagnostics work the same way
/// across all three apps.
/// </summary>
public static class SessionLogService
{
    private static readonly string LogDir = Path.Combine(ConfigService.AppDataDir, "logs");

    private static string _logPath = "";

    public static string CurrentLogPath => _logPath;

    public static void Initialize(string appVersion)
    {
        try
        {
            Directory.CreateDirectory(LogDir);

            var old = Directory.GetFiles(LogDir, "StarFix_diagnostics_*.log")
                               .OrderByDescending(f => f)
                               .Skip(5)
                               .ToArray();
            foreach (var f in old)
                try { File.Delete(f); } catch { }

            _logPath = Path.Combine(LogDir, $"StarFix_diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        }
        catch { return; }

        Write($"=== StarFix {appVersion} — Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Write("");
    }

    /// <summary>Fired on the calling thread whenever a new line is written.
    /// Subscribers are responsible for dispatching to the UI thread if needed.</summary>
    public static event Action<string>? LineWritten;

    public static void Write(string message)
    {
        if (string.IsNullOrEmpty(_logPath)) return;
        var line = string.IsNullOrEmpty(message)
            ? ""
            : $"[{DateTime.Now:HH:mm:ss}]  {message}";
        try { File.AppendAllText(_logPath, line + Environment.NewLine); }
        catch { }
        LineWritten?.Invoke(line);
    }

    public static string ReadAll()
    {
        if (string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath)) return "";
        try { return File.ReadAllText(_logPath); }
        catch { return ""; }
    }
}
