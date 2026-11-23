using System.Text.Encodings.Web;
using AgentKit.ProviderSignIn;

namespace AgentBlog.Auth;

/// <summary>
/// The two pages that come before the app: setup and sign-in, server-rendered. The owner's app itself is the PWA at
/// /admin (Auth/AdminApp.cs). No inline script or style: the CSP allows only this server's own /assets.
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
                : $"""
                   <p>Claim this blog: paste the setup token from the server's variables, then make a passkey{(flow.EnabledProviders.Count > 0 ? " or sign in" : "")}. You become the owner.</p>
                   <label for="token">Setup token</label>
                   <input id="token" type="password" autocomplete="off" spellcheck="false">
                   <label for="name">Your name, as readers see it on posts</label>
                   <input id="name" autocomplete="name" maxlength="100">
                   <div class="row"><button type="button" data-action="passkey-setup">Create a passkey</button></div>
                   <p class="quiet">A passkey lives on your phone or computer and signs you in with your face, fingerprint or screen lock. No password.</p>
                   {(flow.EnabledProviders.Count > 0 ? $"<p class=\"quiet\">Or sign in with (your name comes from there):</p><div class=\"row\">{buttons}</div>" : "")}
                   """;
            return Page(ctx, "Set up your blog", ErrorLine(error) + body);
        });

        app.MapGet("/sign-in", async (HttpContext ctx, ProviderSignInFlow flow, AgentBlog.Database.Db db, IConfiguration config,
            string? error, string? returnTo) =>
        {
            // A fresh instance has nobody to sign in as: say so, and where setup is, instead of a dead end.
            var hasOwner = await db.ReadAsync(tx => tx.One<bool>("SELECT EXISTS (SELECT 1 FROM blog_member WHERE role = 'owner')"),
                ctx.RequestAborted);
            if (!hasOwner && config["Blog:Hosting:Domain"] is not { Length: > 0 })
                return Page(ctx, "Sign in", ErrorLine(error) + (SignIn.SetupToken(config) is null
                    ? "<p>This blog has no owner yet. To claim it, set <code>Blog__SetupToken</code> (32+ characters) on the server, then open <a href=\"/setup\">setup</a>.</p>"
                    : "<p>This blog has no owner yet. <a class=\"button\" href=\"/setup\">Set it up</a></p><p class=\"quiet\">Then come back and connect your AI.</p>"));

            // Back to where sign-in was asked for (the consent page), as a path on this site only; the package keeps it so.
            var back = LocalPath(returnTo) is { } local ? "?returnTo=" + Uri.EscapeDataString(local) : "";
            var links = string.Concat(flow.EnabledProviders.Select(p =>
                $"""<a class="button" href="/api/auth/{Html.Encode(p)}/start{Html.Encode(back)}">Sign in with {Html.Encode(Label(p))}</a>"""));
            var body = $"""
                <div class="row"><button type="button" data-action="passkey-sign-in" data-return="{Html.Encode(LocalPath(returnTo) ?? "")}">Sign in with a passkey</button>{links}</div>
                <p class="quiet">New instance? <a href="/setup">Set it up</a>.</p>
                """;
            return Page(ctx, "Sign in", ErrorLine(error) + body);
        });


        // An invitation (docs/specs/members.md): who invites whom to which blog, as what, and the sign-in that accepts it.
        app.MapGet("/invite/{token}", async (HttpContext ctx, string token, ProviderSignInFlow flow, Members members) =>
        {
            if (token.Length is < 16 or > 100 || await members.LookUpAsync(token, ctx.RequestAborted) is not { } i)
                return Page(ctx, "Invitation", ErrorLine("invitation_gone") + "<p><a href=\"/sign-in\">Sign in</a> if you're on the blog already.</p>");
            var buttons = string.Concat(flow.EnabledProviders.Select(p =>
                $"""<button type="button" data-action="invite" data-token="{Html.Encode(token)}" data-provider="{Html.Encode(p)}">Sign in with {Html.Encode(Label(p))}</button>"""));
            var what = i.Role == Roles.Editor
                ? "As an editor you write, edit any post and publish."
                : "As a writer you write drafts and send them for review; an editor or the owner publishes them.";
            var signIns = $"""
                <label for="name">Your name, as readers see it on posts</label>
                <input id="name" autocomplete="name" maxlength="100">
                <div class="row"><button type="button" data-action="passkey-invite" data-token="{Html.Encode(token)}">Create a passkey</button></div>
                {(flow.EnabledProviders.Count > 0 ? $"<p class=\"quiet\">Or sign in with (your name comes from there):</p><div class=\"row\">{buttons}</div>" : "")}
                """;
            return Page(ctx, $"Write for {i.BlogTitle}", $"""
                <p><strong>{Html.Encode(i.InvitedBy)}</strong> invites you to write for <strong>{Html.Encode(i.BlogTitle)}</strong> as {(i.Role == Roles.Editor ? "an editor" : "a writer")}.</p>
                <p class="quiet">{Html.Encode(what)}</p>
                {signIns}
                <p class="quiet">Making a passkey or signing in accepts it, and that account joins the blog. The link works once, until {i.ExpiresAt:d MMMM}.</p>
                """);
        });
    }

    /// <summary>A path on this site, or null: no scheme, no host, no protocol-relative <c>//</c>, no backslash.</summary>
    internal static string? LocalPath(string? returnTo) =>
        returnTo is { Length: > 1 and <= 2000 } r && r[0] == '/' && r[1] != '/' && !r.Contains('\\') && !r.Contains("://") ? r : null;

    private static string Label(string provider) => provider switch
    {
        "google" => "Google",
        "discord" => "Discord",
        _ => provider,
    };

    /// <summary>What a coded sign-in outcome means, in words. Unknown codes are shown as a plain fallback, never echoed.</summary>
    private static string ErrorLine(string? code) => code is null ? "" : $"<p class=\"error\" role=\"alert\">{Html.Encode(code switch
    {
        "not_owner" => "That account isn't on this blog. Sign in with the account you joined with, or ask the owner for an invitation.",
        "invitation_gone" => "That invitation has run out, was used, or was withdrawn. Ask the owner for a new one.",
        "setup_closed" => "Setup closed while you were signing in. If this blog has an owner, sign in instead.",
        "session_ended" => "Your session ended before that finished. Sign in and try again.",
        "identity_taken" => "That account already belongs to someone else here.",
        "denied" => "Sign-in was cancelled at the provider.",
        "passkey_unsupported" => "This browser can't use passkeys. Try another browser, or your phone.",
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
        // Setup's one write: JSON with the X-Blog-Request header (the CSRF guard), then off to the provider.
        async function call(method, url, body) {
          const r = await fetch(url, { method, headers: { 'Content-Type': 'application/json', 'X-Blog-Request': '1' }, body: body ? JSON.stringify(body) : undefined, credentials: 'same-origin' });
          const data = r.status === 204 ? null : await r.json().catch(() => null);
          if (!r.ok) throw new Error((data && data.detail) || 'Something went wrong.');
          return data;
        }
        // Passkeys (WebAuthn): the server's options are JSON with base64url bytes; the browser wants ArrayBuffers, and the
        // answer goes back as JSON the same way.
        const b64 = (buf) => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
        const bytes = (s) => Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((s.length + 3) % 4)), (c) => c.charCodeAt(0));
        const ids = (list) => (list || []).map((c) => ({ ...c, id: bytes(c.id) }));
        function toJson(c) {
          if (typeof c.toJSON === 'function') return c.toJSON();
          const r = c.response, out = { id: c.id, rawId: b64(c.rawId), type: c.type, authenticatorAttachment: c.authenticatorAttachment || null,
            clientExtensionResults: c.getClientExtensionResults ? c.getClientExtensionResults() : {}, response: { clientDataJSON: b64(r.clientDataJSON) } };
          if (r.attestationObject) { out.response.attestationObject = b64(r.attestationObject); out.response.transports = r.getTransports ? r.getTransports() : []; }
          if (r.authenticatorData) { out.response.authenticatorData = b64(r.authenticatorData); out.response.signature = b64(r.signature); out.response.userHandle = r.userHandle ? b64(r.userHandle) : null; }
          return out;
        }
        async function passkey(begin) {
          if (!window.PublicKeyCredential) throw new Error("This browser can't use passkeys. Try another browser, or your phone.");
          const { ceremony, options: o } = await call('POST', '/api/auth/passkey/begin', begin);
          const credential = o.rp
            ? await navigator.credentials.create({ publicKey: { ...o, challenge: bytes(o.challenge), user: { ...o.user, id: bytes(o.user.id) }, excludeCredentials: ids(o.excludeCredentials) } })
            : await navigator.credentials.get({ publicKey: { ...o, challenge: bytes(o.challenge), allowCredentials: ids(o.allowCredentials) } });
          location.href = (await call('POST', '/api/auth/passkey/finish', { ceremony, credential: toJson(credential) })).redirect;
        }
        const value = (id) => (document.getElementById(id) || {}).value || '';
        document.addEventListener('click', async (e) => {
          const b = e.target.closest('[data-action]');
          if (!b) return;
          b.disabled = true;
          try {
            const p = b.dataset.provider;
            switch (b.dataset.action) {
              case 'setup': location.href = (await call('POST', '/api/auth/setup', { token: value('token'), provider: p })).url; return;
              case 'invite': location.href = (await call('POST', '/api/auth/invite', { token: b.dataset.token, provider: p })).url; return;
              case 'passkey-setup': await passkey({ purpose: 'setup', token: value('token'), name: value('name') }); return;
              case 'passkey-invite': await passkey({ purpose: 'invite', token: b.dataset.token, name: value('name') }); return;
              case 'passkey-sign-in': await passkey({ purpose: 'sign-in', return_to: b.dataset.return || null }); return;
            }
          } catch (err) {
            // Cancelling the phone's prompt is not an error worth a dialog.
            if (err && err.name !== 'NotAllowedError') alert(err.message);
            b.disabled = false;
          }
        });
        """;
}
