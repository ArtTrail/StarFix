using System;
using System.Text.Json.Serialization;

namespace StarFix.Models;

/// <summary>Mirrors the JSON envelope printed by `solve.exe --json` (one line to stdout):
/// {"summary": {...}, "text": "...", "num_detected", "num_catalog", "num_matched", "rms_pixels"}.</summary>
public class SolveResultJson
{
    [JsonPropertyName("summary")]      public SolveSummary Summary { get; set; } = new();
    [JsonPropertyName("text")]         public string Text { get; set; } = "";
    [JsonPropertyName("num_detected")] public int NumDetected { get; set; }
    [JsonPropertyName("num_catalog")]  public int NumCatalog { get; set; }
    [JsonPropertyName("num_matched")]  public int NumMatched { get; set; }
    [JsonPropertyName("rms_pixels")]   public double RmsPixels { get; set; }
    [JsonPropertyName("fwhm_used")]       public double? FwhmUsed { get; set; }
    [JsonPropertyName("match_cap_used")]  public int? MatchCapUsed { get; set; }
}

/// <summary>Outcome of a single solve attempt (success or failure), as consumed by the UI.</summary>
public class SolveOutcome
{
    public required string SourcePath { get; init; }
    public required string SolvedPath { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public SolveResultJson? Result { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.Now;
}
