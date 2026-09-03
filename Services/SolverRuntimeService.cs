using System;
using System.IO;

namespace StarFix.Services;

/// <summary>Locates the bundled, PyInstaller-frozen solve.exe next to the app's own
/// executable. No Python/venv resolution needed at all — that's the entire point of
/// shipping a frozen exe instead of TransitLab/EXOTIC's runtime-provisioning approach.</summary>
public static class SolverRuntimeService
{
    public static string ExePath =>
        Path.Combine(AppContext.BaseDirectory, "PySolver", "solve", "solve.exe");

    public static bool IsAvailable => File.Exists(ExePath);
}
