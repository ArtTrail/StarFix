using System.Text.RegularExpressions;

namespace StarFix.Services;

/// <summary>Turns known raw solver error text (a Python traceback, in the CLI path) into a
/// short, friendly message for the UI — the full raw text is still written to the session
/// log by whichever caller builds it, so nothing is actually lost, just not shown by default
/// to a user who doesn't need to see a stack trace to understand what to do next.</summary>
public static class SolveErrorMessageService
{
    private static readonly Regex ZeroStarsPattern =
        new(@"Only \d+ Gaia stars found in the search region", RegexOptions.Compiled);

    public static string Humanize(string raw)
    {
        if (ZeroStarsPattern.IsMatch(raw))
        {
            return "No Gaia catalog stars found for this position. Make sure the Gaia catalog is " +
                   "installed (Tools → Download Gaia Catalog) and covers this part of the sky.";
        }

        return raw;
    }
}
