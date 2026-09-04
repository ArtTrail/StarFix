using System;
using System.IO;

namespace StarFix.Services;

/// <summary>Locates the bundled, PyInstaller-frozen solver next to the app's own
/// executable. No Python/venv resolution needed at all — that's the entire point of
/// shipping a frozen exe instead of TransitLab/EXOTIC's runtime-provisioning approach.
///
/// PyInstaller names its output "solve.exe" on Windows but plain "solve" (no extension)
/// on macOS/Linux — the frozen binary itself is platform-native either way, so the
/// filename has to match whatever that platform's PyInstaller run actually produced.</summary>
public static class SolverRuntimeService
{
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "solve.exe" : "solve";

    public static string ExePath =>
        Path.Combine(AppContext.BaseDirectory, "PySolver", "solve", ExeName);

    public static bool IsAvailable => File.Exists(ExePath);
}
