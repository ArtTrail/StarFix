using System;
using System.IO;
using System.Text.RegularExpressions;

namespace StarFix.Services;

/// <summary>Req #3 "new file" mode: pick a fresh, auto-numbered path for the solved copy so
/// the original source FITS is never touched. Mirrors VariLab's GetNextResultsDir scan-and-
/// increment pattern, adapted to name a file instead of a folder.</summary>
public static class OutputPathService
{
    /// <summary>Returns "&lt;dir&gt;\&lt;name&gt;_solved_N.&lt;ext&gt;" for the smallest N not
    /// already present in the source file's directory.</summary>
    public static string GetNextOutputPath(string sourcePath)
    {
        var dir  = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var ext  = Path.GetExtension(sourcePath);
        var pattern = new Regex($"^{Regex.Escape(name)}_solved_(\\d+){Regex.Escape(ext)}$", RegexOptions.IgnoreCase);

        int n = 1;
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                var m = pattern.Match(Path.GetFileName(f));
                if (m.Success && int.TryParse(m.Groups[1].Value, out var existing) && existing >= n)
                    n = existing + 1;
            }
        }

        return Path.Combine(dir, $"{name}_solved_{n}{ext}");
    }

    /// <summary>True if at least one "&lt;name&gt;_solved_N.&lt;ext&gt;" copy of this source
    /// already exists alongside it — the new-file-mode equivalent of checking a source's own
    /// PLTSOLVD header. In new-file mode the source is never touched/flagged, so PLTSOLVD alone
    /// missed this entirely (confirmed against a real batch: sources kept getting re-solved into
    /// "_solved_4", "_solved_6", etc. every run, since nothing on the source itself ever recorded
    /// that a solved copy already existed).</summary>
    public static bool HasExistingSolvedCopy(string sourcePath)
    {
        var dir  = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var ext  = Path.GetExtension(sourcePath);
        var pattern = new Regex($"^{Regex.Escape(name)}_solved_(\\d+){Regex.Escape(ext)}$", RegexOptions.IgnoreCase);

        if (!Directory.Exists(dir)) return false;
        foreach (var f in Directory.GetFiles(dir))
            if (pattern.IsMatch(Path.GetFileName(f)))
                return true;
        return false;
    }
}
