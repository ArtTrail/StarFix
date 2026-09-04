// app-feedback — Cloudflare Worker, shared across multiple apps
//
// Holds the real GitHub token server-side (Wrangler secret, never shipped in any app) so
// each app's Submit Feedback form can create a real GitHub Issue without embedding a
// credential in distributed client code. Background: a token embedded directly in
// StarFix.exe was detected by GitHub's own secret scanning the moment it was pushed and
// auto-revoked within minutes — this Worker exists specifically to avoid that failure mode.
// The token this Worker holds is now scoped to "All repositories" (Issues: Read & write)
// rather than one repo, specifically so this one Worker can serve every app's feedback
// feature — deliberate, not scope creep: it never leaves Cloudflare's secret store, so the
// exposure model (Cloudflare account compromise) is unrelated to how many repos it covers.
//
// CLIENT_TOKEN (also a Wrangler secret) is NOT a real security boundary — it's shipped
// inside each app's compiled binary like any other embedded string, so a determined actor
// can extract it. Its only purpose is filtering out casual/automated discovery of this
// public endpoint. Worst case if it leaks: someone spams Issues on an allowlisted repo,
// annoying but not a credential compromise — the real GitHub token never leaves this Worker.
//
// REPO_OWNER is hardcoded and never taken from the request, so this Worker can never be
// pointed at an account other than this one. REPO_ALLOWLIST is the second guardrail: the
// client sends which repo it's reporting for, but only a name already on this list is
// accepted — a compromised/malicious caller can't route a submission to an arbitrary repo
// name, only to one of these pre-approved ones. Add a new app's repo name here when wiring
// up its own Submit Feedback feature.

const REPO_OWNER = "ArtTrail";
const REPO_ALLOWLIST = ["StarFix"];

export default {
  async fetch(request, env) {
    if (request.method !== "POST") {
      return new Response("Method not allowed", { status: 405 });
    }

    // .trim() guards against trailing-whitespace artifacts from however a secret was set
    // (confirmed directly: piping a string into `wrangler secret put` via PowerShell added
    // a trailing newline to the stored value even after routing through a temp file).
    const received = (request.headers.get("X-Client-Token") || "").trim();
    if (received !== (env.CLIENT_TOKEN || "").trim()) {
      return new Response("Forbidden", { status: 403 });
    }

    let payload;
    try {
      payload = await request.json();
    } catch {
      return jsonResponse({ ok: false, error: "Invalid JSON body" }, 400);
    }

    const { repo, type, summary, description, email, version, os } = payload;

    if (!REPO_ALLOWLIST.includes(repo)) {
      return jsonResponse({ ok: false, error: `Unknown repo "${repo}"` }, 400);
    }

    if (!description || !description.trim()) {
      return jsonResponse({ ok: false, error: "Description is required" }, 400);
    }

    const isBug = type === "Bug Report";
    const prefix = isBug ? "[Bug]" : "[Feature]";
    const label = isBug ? "bug" : "enhancement";
    const title = summary && summary.trim() ? `${prefix} ${summary.trim()}` : `${prefix} User Report`;

    let body = `**Type:** ${type}\n**Version:** ${version}\n**OS:** ${os}\n`;
    if (email && email.trim()) body += `**Contact:** ${email.trim()}\n`;
    body += `\n**Description:**\n${description.trim()}\n`;

    const ghResponse = await fetch(
      `https://api.github.com/repos/${REPO_OWNER}/${repo}/issues`,
      {
        method: "POST",
        headers: {
          Authorization: `Bearer ${(env.GITHUB_TOKEN || "").trim()}`,
          "User-Agent": "app-feedback-worker",
          Accept: "application/vnd.github+json",
          "X-GitHub-Api-Version": "2022-11-28",
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ title, body, labels: [label] }),
      }
    );

    if (!ghResponse.ok) {
      const detail = await ghResponse.text();
      return jsonResponse({ ok: false, error: `GitHub API error ${ghResponse.status}: ${detail}` }, 502);
    }

    const issue = await ghResponse.json();
    return jsonResponse({ ok: true, url: issue.html_url });
  },
};

function jsonResponse(obj, status = 200) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}
