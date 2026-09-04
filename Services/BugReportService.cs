using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StarFix.Services;

/// <summary>Posts a feedback submission directly to the StarFix GitHub repo as an Issue, via
/// a fine-grained Personal Access Token scoped to Issues: Read &amp; Write on this one repo
/// only — the same in-app one-click UX TransitLab uses, chosen deliberately over a
/// no-credential browser-based alternative (open a pre-filled github.com/issues/new page)
/// after discussing the tradeoff directly. The token below is a real, live secret embedded
/// in shipped source, same as TransitLab's own BugReportService.cs — narrowly scoped so a
/// worst-case leak only allows spamming/tampering with this repo's Issues, not anything
/// broader. Unlike TransitLab's version, this one is NOT split/obfuscated to evade GitHub's
/// secret-scanning — accepting the risk knowingly is a different thing from deliberately
/// defeating the safeguard that exists to catch it.</summary>
public static class BugReportService
{
    private const string RepoOwner = "ArtTrail";
    private const string RepoName  = "StarFix";

    // Fine-grained PAT "StarFix Bug Report (Issues only)" — Issues: Read & Write on the
    // StarFix repo only.
    private const string Token = "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";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task SubmitAsync(
        string type, string summary, string description,
        string email, string version, string os,
        CancellationToken ct = default)
    {
        var isBug  = type == "Bug Report";
        var prefix = isBug ? "[Bug]" : "[Feature]";
        var label  = isBug ? "bug"   : "enhancement";
        var title  = string.IsNullOrWhiteSpace(summary)
                        ? $"{prefix} User Report"
                        : $"{prefix} {summary.Trim()}";

        var body = new StringBuilder();
        body.AppendLine($"**Type:** {type}");
        body.AppendLine($"**Version:** {version}");
        body.AppendLine($"**OS:** {os}");
        if (!string.IsNullOrWhiteSpace(email))
            body.AppendLine($"**Contact:** {email.Trim()}");
        body.AppendLine();
        body.AppendLine("**Description:**");
        body.AppendLine(description.Trim());

        var payload = JsonSerializer.Serialize(new
        {
            title,
            body = body.ToString(),
            labels = new[] { label },
        });

        var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/issues")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        req.Headers.UserAgent.ParseAdd("StarFix-BugReport");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        var response = await Http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
    }
}
