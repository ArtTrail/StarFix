using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace StarFix.Services;

/// <summary>Reads keyword values from a FITS primary header without any external library —
/// ported/trimmed from TransitLab's FitsHeaderService (same 2880-byte-block/80-byte-card
/// parsing), used here only to pre-fill RA/Dec/NAXIS hints in the Solve panel.</summary>
public static class FitsHeaderService
{
    public class FitsHeader
    {
        private readonly Dictionary<string, string> _kv;
        public FitsHeader(Dictionary<string, string> kv) => _kv = kv;

        public string Get(string keyword)
            => _kv.TryGetValue(keyword.ToUpperInvariant().TrimEnd(), out var v) ? v : "";

        public double? GetDouble(string keyword)
            => NumericParseService.TryParse(Get(keyword), out var d) ? d : null;

        public int? GetInt(string keyword)
            => int.TryParse(Get(keyword), out var i) ? i : null;

        /// <summary>FITS boolean cards store the raw value as a single "T" or "F" character
        /// (the FITS standard's boolean convention), not the strings "true"/"false".</summary>
        public bool GetBool(string keyword) => Get(keyword).Trim() == "T";
    }

    public static Dictionary<string, string> ReadHeaderBlock(FileStream fs)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var block = new byte[2880];
        while (true)
        {
            int read = fs.Read(block, 0, 2880);
            if (read < 80) break;

            bool end = false;
            for (int i = 0; i + 79 < read; i += 80)
            {
                var record = Encoding.ASCII.GetString(block, i, 80);
                var kw     = record[..8].TrimEnd();

                if (kw == "END") { end = true; break; }

                if (record.Length > 9 && record[8] == '=')
                {
                    var rawVal = record[9..].Split('/')[0].Trim();
                    if (rawVal.StartsWith('\''))
                        rawVal = rawVal.Trim('\'').Trim();
                    kv[kw] = rawVal;
                }
            }
            if (end) break;
        }
        return kv;
    }

    public static FitsHeader Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var kv = ReadHeaderBlock(fs);
        return new FitsHeader(kv);
    }
}
