using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StarFix.Services;

/// <summary>Posts a feedback submission to a shared Cloudflare Worker
/// (app-feedback.mobs-sync-trigger.workers.dev), which creates the actual GitHub Issue on
/// StarFix's behalf. The real GitHub token lives only in that Worker's Cloudflare secret
/// store — never in this source, never in the shipped app.
///
/// This replaces an earlier version that embedded the GitHub token directly here, matching
/// TransitLab's own BugReportService.cs. That approach doesn't actually work: GitHub's
/// secret scanning detected the token the moment it was pushed to this public repo and
/// auto-revoked it within minutes — confirmed directly, twice, with two different fresh
/// tokens. TransitLab's version only still works because its token is deliberately split
/// across two string literals specifically to evade that scanner, which is a safeguard-
/// defeating pattern, not a legitimate fix, and isn't repeated here.
///
/// ClientToken below is NOT a real secret — it's shipped in this binary like any other
/// string and a determined actor can extract it. Its only job is filtering out casual
/// discovery of the Worker's public URL. Worst case if it leaks: someone spams Issues on
/// an allowlisted repo (the Worker only accepts pre-approved repo names) — annoying, but
/// the real GitHub token never leaves the Worker either way.</summary>
public static class BugReportService
{
    private const string WorkerUrl = "https://app-feedback.mobs-sync-trigger.workers.dev";
    private const string RepoName = "StarFix";
    private const string ClientToken = "vX0lTWqUQeQRkAgGna3Ux9ufCXtN816nKuvxPOlGPIM";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task SubmitAsync(
        string type, string summary, string description,
        string email, string version, string os,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            repo = RepoName,
            type,
            summary,
            description,
            email,
            version,
            os,
        });

        var req = new HttpRequestMessage(HttpMethod.Post, WorkerUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Client-Token", ClientToken);

        var response = await Http.SendAsync(req, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            string reason;
            try
            {
                using var doc = JsonDocument.Parse(body);
                reason = doc.RootElement.TryGetProperty("error", out var errProp)
                    ? errProp.GetString() ?? body
                    : body;
            }
            catch (JsonException)
            {
                reason = body;
            }
            throw new HttpRequestException(reason);
        }
    }
}
