namespace StarFix.Models;

public class AppConfig
{
    // Req #3: overwrite the source FITS in place, or write into a new auto-numbered copy.
    // Default false (non-destructive) — matches VariLab's own unconditionally-additive philosophy.
    public bool OverwriteExisting { get; set; } = false;

    // Solve defaults (mirror solve.py's own CLI defaults).
    public double DefaultSearchRadiusDeg { get; set; } = 0.5;
    public int    DefaultMaxStars        { get; set; } = 2000;
    public double DefaultThresholdSigma  { get; set; } = 6.0;

    // Last-used paths (UX convenience).
    public string LastInputDirectory  { get; set; } = "";
    public string LastBatchResultsDir { get; set; } = "";
    public string LastImportFileDir   { get; set; } = "";

    // Req #7: Gaia catalog install location/status.
    public string GaiaCatalogPath      { get; set; } = "";
    public bool   GaiaCatalogInstalled { get; set; } = false;
    public long   GaiaCatalogBytesOnDisk { get; set; } = 0;
}
