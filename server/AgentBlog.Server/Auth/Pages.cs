using System.Text.Encodings.Web;
using AgentKit.ProviderSignIn;

namespace AgentBlog.Auth;

/// <summary>
/// The owner's first three pages, server-rendered: setup, sign-in, and a small admin page (who you are, your blog, your
/// ways in, sign out). Enough to claim and run an instance before the phone app (slice 6) replaces /admin. No inline
/// script or style: the CSP allows only this server's own /assets.
/// </summary>
public static class Pages
{
    private static readonly HtmlEncoder Html = HtmlEncoder.Default;

    public static void MapOwnerPages(this WebApplication app)
    {
        app.MapGet("/assets/admin.css", () => Results.Text(Css, "text/css; charset=utf-8"));
        app.MapGet("/assets/admin.js", () => Results.Text(Js, "text/javascript; charset=utf-8"));

        app.MapGet("/setup", (HttpContext ctx, ProviderSignInFlow flow, IConfiguration config, string? error) =>
        {
            var closed = SignIn.SetupToken(config) is null;
            var buttons = string.Concat(flow.EnabledProviders.Select(p =>
                $"""<button type="button" data-action="setup" data-provider="{Html.Encode(p)}">Continue with {Html.Encode(Label(p))}</button>"""));
            var body = closed
                ? "<p>Setup is closed: <code>Blog__SetupToken</code> isn't set on the server (at least 32 characters).</p>"
                : flow.EnabledProviders.Count == 0
                    ? "<p>No sign-in provider is configured yet. Set <code>Blog__Auth__Google__ClientId</code> and <code>__ClientSecret</code>, or the same for Discord.</p>"
                    : $"""
                       <p>Claim this blog: paste the setup token from the server's variables, then sign in. That account becomes the owner.</p>
                       <label for="token">Setup token</label>
                       <input id="token" type="password" autocomplete="off" spellcheck="false">
                       <div class="row">{buttons}</div>
                       """;
            return Page(ctx, "Set up your blog", ErrorLine(error) + body);
        });

        app.MapGet("/sign-in", (HttpContext ctx, ProviderSignInFlow flow, string? error, string? returnTo) =>
        {
            // Back to where sign-in was asked for (the consent page), as a path on this site only; the package keeps it so.
            var back = LocalPath(returnTo) is { } local ? "?returnTo=" + Uri.EscapeDataString(local) : "";
            var links = string.Concat(flow.EnabledProviders.Select(p =>
                $"""<a class="button" href="/api/auth/{Html.Encode(p)}/start{Html.Encode(back)}">Sign in with {Html.Encode(Label(p))}</a>"""));
            var body = flow.EnabledProviders.Count == 0
                ? "<p>No sign-in provider is configured on this server.</p>"
                : $"""<div class="row">{links}</div><p class="quiet">New instance? <a href="/setup">Set it up</a>.</p>""";
            return Page(ctx, "Sign in", ErrorLine(error) + body);
        });

        app.MapGet("/admin", async (HttpContext ctx, Sessions sessions, Accounts accounts, AgentBlog.Connections.ConnectionStore connections,
            ProviderSignInFlow flow, string? error, string? bound) =>
        {
            if (await sessions.AuthenticateAsync(ctx, ctx.RequestAborted) is not { } session)
                return Results.Redirect("/sign-in");
            var me = await accounts.MeAsync(session.AccountId, ctx.RequestAborted);
            if (me is null) return Results.Redirect("/sign-in");
            var identities = await accounts.IdentitiesAsync(session.AccountId, ctx.RequestAborted);

            var blogs = string.Concat(me.Blogs.Select(b =>
                $"<li><strong>{Html.Encode(b.Title)}</strong> <span class=\"quiet\">{Html.Encode(b.Slug)} · {Html.Encode(b.Role)}</span></li>"));
            var ways = string.Concat(identities.Select(i =>
                $"""
                <li>{Html.Encode(Label(i.Provider))} <span class="quiet">{Html.Encode(i.DisplayName ?? i.ProviderAccountId)}</span>
                <button type="button" class="quiet-button" data-action="unbind" data-provider="{Html.Encode(i.Provider)}" data-id="{Html.Encode(i.ProviderAccountId)}">Remove</button></li>
                """));
            var add = string.Concat(flow.EnabledProviders
                .Where(p => identities.All(i => i.Provider != p))
                .Select(p => $"""<button type="button" data-action="bind" data-provider="{Html.Encode(p)}">Add {Html.Encode(Label(p))}</button>"""));
            var agents = await connections.ListAsync(session.AccountId, ctx.RequestAborted);
            var levelOptions = (string current) => string.Concat(AgentBlog.Connections.Levels.All.Select(l =>
                $"<option value=\"{l}\"{(l == current ? " selected" : "")}>{Html.Encode(LevelLabel(l))}</option>"));
            var connectionRows = agents.Count == 0
                ? "<p class=\"quiet\">None yet. Add this blog to claude.ai, ChatGPT or Claude Code as an MCP server at <code>/mcp</code>.</p>"
                : "<ul>" + string.Concat(agents.Select(c => $"""
                    <li><strong>{Html.Encode(c.Label)}</strong> <span class="quiet">{Html.Encode(c.BlogTitle)}{(c.LastUsedAt is { } used ? " · last used " + used.ToString("yyyy-MM-dd HH:mm") : " · not used yet")}</span><br>
                    <select data-action="level" data-id="{c.Id}" data-current="{Html.Encode(c.Level)}" aria-label="Level for {Html.Encode(c.Label)}">{levelOptions(c.Level)}</select>
                    <button type="button" class="quiet-button" data-action="revoke" data-id="{c.Id}">Revoke</button>
                    <br><span class="quiet">{Html.Encode(AgentBlog.Connections.Levels.Means(c.Level))}</span></li>
                    """)) + "</ul>";
            var notice = bound is not null ? $"<p class=\"ok\">{Html.Encode(Label(bound))} added. You can sign in with it now.</p>" : "";

            return Page(ctx, "Your blog", $"""
                {ErrorLine(error)}{notice}
                <p>Signed in as <strong>{Html.Encode(me.DisplayName)}</strong>.</p>
                <h2>Blogs</h2><ul>{blogs}</ul>
                <h2>Agents</h2>{connectionRows}
                <h2>Ways to sign in</h2><ul>{ways}</ul>
                {(add.Length > 0 ? $"<div class=\"row\">{add}</div><p class=\"quiet\">A second way in is how you get back if you lose the first.</p>" : "")}
                <div class="row">
                  <button type="button" data-action="sign-out">Sign out</button>
                  <button type="button" class="quiet-button" data-action="sign-out-everywhere">Sign out everywhere</button>
                </div>
                """);
        });
    }

    /// <summary>A path on this site, or null: no scheme, no host, no protocol-relative <c>//</c>, no backslash.</summary>
    internal static string? LocalPath(string? returnTo) =>
        returnTo is { Length: > 1 and <= 2000 } r && r[0] == '/' && r[1] != '/' && !r.Contains('\\') && !r.Contains("://") ? r : null;

    private static string LevelLabel(string level) => level switch
    {
        "propose" => "Propose: drafts, you publish",
        "publish" => "Publish: publishes on its own",
        "full" => "Full: runs the content",
        _ => level,
    };

    private static string Label(string provider) => provider switch
    {
        "google" => "Google",
        "discord" => "Discord",
        _ => provider,
    };

    /// <summary>What a coded sign-in outcome means, in words. Unknown codes are shown as a plain fallback, never echoed.</summary>
    private static string ErrorLine(string? code) => code is null ? "" : $"<p class=\"error\" role=\"alert\">{Html.Encode(code switch
    {
        "not_owner" => "That account isn't the owner of this blog. Sign in with the account you set it up with.",
        "setup_closed" => "Setup closed while you were signing in. If this blog has an owner, sign in instead.",
        "session_ended" => "Your session ended before that finished. Sign in and try again.",
        "identity_taken" => "That account already belongs to someone else here.",
        "denied" => "Sign-in was cancelled at the provider.",
        "state_expired" => "That took too long. Start again.",
        "state_invalid" => "That sign-in link isn't valid any more. Start again.",
        "exchange_failed" or "identity_rejected" => "The provider didn't confirm the sign-in. Try again.",
        "provider_unavailable" => "The provider didn't answer. Try again in a moment.",
        _ => "Something went wrong. Try again.",
    })}</p>";

    private static IResult Page(HttpContext ctx, string title, string body)
    {
        var h = ctx.Response.Headers;
        h.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        h.CacheControl = "no-store";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Content-Type-Options"] = "nosniff";
        return Results.Content($"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{Html.Encode(title)}</title>
            <link rel="stylesheet" href="/assets/admin.css">
            <script src="/assets/admin.js" defer></script>
            </head>
            <body>
            <main>
            <h1>{Html.Encode(title)}</h1>
            {body}
            </main>
            </body>
            </html>
            """, "text/html; charset=utf-8");
    }

    private const string Css = """
        :root { --bg: #fbfaf7; --fg: #1d1b18; --muted: #6b665e; --line: #e4e0d8; --accent: #3d5a80; --accent-fg: #fff; --bad: #a4302a; --good: #2f6b3a; color-scheme: light; }
        @media (prefers-color-scheme: dark) { :root { --bg: #17181b; --fg: #ecebe8; --muted: #a29e96; --line: #2d2f34; --accent: #8fb0d9; --accent-fg: #101114; --bad: #ef8a83; --good: #8fd19c; color-scheme: dark; } }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
        main { max-width: 34rem; margin: 0 auto; padding: 2.5rem 1rem; }
        h1 { font-size: 1.6rem; margin: 0 0 1rem; } h2 { font-size: 1.05rem; margin: 1.6rem 0 .4rem; }
        ul { padding-left: 1.1rem; } li { margin: .3rem 0; }
        label { display: block; font-weight: 600; margin: 1rem 0 .3rem; }
        select { padding: .35rem .5rem; border: 1px solid var(--line); border-radius: 8px; background: transparent; color: inherit; font: inherit; }
        input { width: 100%; padding: .55rem .7rem; border: 1px solid var(--line); border-radius: 8px; background: transparent; color: inherit; font: inherit; }
        .row { display: flex; flex-wrap: wrap; gap: .6rem; margin: 1rem 0; }
        button, .button { padding: .5rem .9rem; border: 0; border-radius: 8px; background: var(--accent); color: var(--accent-fg); font: inherit; text-decoration: none; cursor: pointer; }
        .quiet-button { background: transparent; color: var(--muted); border: 1px solid var(--line); padding: .2rem .6rem; }
        .quiet { color: var(--muted); } .error { color: var(--bad); } .ok { color: var(--good); }
        code { font-size: .9em; }
        """;

    private const string Js = """
        // The owner pages' few writes: JSON with the X-Blog-Request header (the CSRF guard), then follow or reload.
        async function call(method, url, body) {
          const r = await fetch(url, { method, headers: { 'Content-Type': 'application/json', 'X-Blog-Request': '1' }, body: body ? JSON.stringify(body) : undefined, credentials: 'same-origin' });
          const data = r.status === 204 ? null : await r.json().catch(() => null);
          if (!r.ok) throw new Error((data && data.detail) || 'Something went wrong.');
          return data;
        }
        // A level change. Full asks once, in plain words, and says how to undo (decision 0001).
        document.addEventListener('change', async (e) => {
          const s = e.target.closest('select[data-action="level"]');
          if (!s) return;
          const level = s.value;
          let confirmFull = false;
          if (level === 'full') {
            confirmFull = confirm('Full lets this agent publish, change and trash posts without asking you. You can see and undo everything it does in History. Give it Full?');
            if (!confirmFull) { s.value = s.dataset.current; return; }
          }
          s.disabled = true;
          try { await call('PATCH', '/api/connections/' + s.dataset.id, { level, confirm_full: confirmFull }); location.reload(); }
          catch (err) { alert(err.message); s.value = s.dataset.current; s.disabled = false; }
        });
        document.addEventListener('click', async (e) => {
          const b = e.target.closest('[data-action]');
          if (!b) return;
          b.disabled = true;
          try {
            const p = b.dataset.provider;
            switch (b.dataset.action) {
              case 'setup': location.href = (await call('POST', '/api/auth/setup', { token: document.getElementById('token').value, provider: p })).url; return;
              case 'bind': location.href = (await call('POST', '/api/auth/identities/' + encodeURIComponent(p))).url; return;
              case 'unbind': await call('DELETE', '/api/auth/identities/' + encodeURIComponent(p) + '/' + encodeURIComponent(b.dataset.id)); location.reload(); return;
              case 'revoke':
                if (!confirm('Revoke this agent? Its next call is refused, and it has to be connected again to come back.')) { b.disabled = false; return; }
                await call('DELETE', '/api/connections/' + b.dataset.id); location.reload(); return;
              case 'sign-out': await call('POST', '/api/auth/sign-out'); location.href = '/sign-in'; return;
              case 'sign-out-everywhere': await call('POST', '/api/auth/sign-out-everywhere'); location.href = '/sign-in'; return;
            }
          } catch (err) {
            alert(err.message);
            b.disabled = false;
          }
        });
        """;
}
