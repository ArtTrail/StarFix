using System.Globalization;
using System.Linq;

namespace StarFix.Services;

/// <summary>Ported verbatim from TransitLab/VariLab: parses user-typed and data-sourced
/// numeric strings without trusting any particular OS locale, treating any "," or "." as a
/// decimal mark rather than guessing based on the OS locale (avoids a decimal comma being
/// misread as a thousands-grouping separator and inflating the value ~1000x).</summary>
public static class NumericParseService
{
    public static bool TryParse(string? s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return double.TryParse(Normalize(s), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static string Normalize(string s)
    {
        var t = s.Trim();
        t = t.Replace(" ", "").Replace(" ", "").Replace("'", "");

        int commaCount  = t.Count(c => c == ',');
        int periodCount = t.Count(c => c == '.');

        if (commaCount > 0 && periodCount > 0)
        {
            bool commaIsDecimal = t.LastIndexOf(',') > t.LastIndexOf('.');
            char groupChar = commaIsDecimal ? '.' : ',';
            char decimalChar = commaIsDecimal ? ',' : '.';
            t = t.Replace(groupChar.ToString(), "");
            t = ReplaceLast(t, decimalChar, '.');
        }
        else if (commaCount == 1)
        {
            t = t.Replace(',', '.');
        }
        else if (commaCount > 1)
        {
            t = t.Replace(",", "");
        }

        return t;
    }

    private static string ReplaceLast(string s, char oldChar, char newChar)
    {
        int idx = s.LastIndexOf(oldChar);
        return idx < 0 ? s : s[..idx] + newChar + s[(idx + 1)..];
    }
}
