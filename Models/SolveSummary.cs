using System.Text.Json.Serialization;

namespace StarFix.Models;

/// <summary>Mirrors solve.py's compute_solution_summary() dict, field-for-field.</summary>
public class SolveSummary
{
    [JsonPropertyName("center_ra_deg")]    public double CenterRaDeg { get; set; }
    [JsonPropertyName("center_dec_deg")]   public double CenterDecDeg { get; set; }
    [JsonPropertyName("center_ra_hms")]    public string CenterRaHms { get; set; } = "";
    [JsonPropertyName("center_dec_dms")]   public string CenterDecDms { get; set; } = "";
    [JsonPropertyName("pixel_scale_arcsec")]   public double PixelScaleArcsec { get; set; }
    [JsonPropertyName("pixel_scale_x_arcsec")] public double PixelScaleXArcsec { get; set; }
    [JsonPropertyName("pixel_scale_y_arcsec")] public double PixelScaleYArcsec { get; set; }
    [JsonPropertyName("fov_width_arcmin")]  public double FovWidthArcmin { get; set; }
    [JsonPropertyName("fov_height_arcmin")] public double FovHeightArcmin { get; set; }
    [JsonPropertyName("rotation_deg")]      public double RotationDeg { get; set; }
    [JsonPropertyName("parity")]            public string Parity { get; set; } = "";
    [JsonPropertyName("focal_length_header_mm")]  public double? FocalLengthHeaderMm { get; set; }
    [JsonPropertyName("focal_length_derived_mm")] public double? FocalLengthDerivedMm { get; set; }
    [JsonPropertyName("rms_arcsec")] public double RmsArcsec { get; set; }
}
