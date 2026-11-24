using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBlog.Database;
using AgentBlog.Secrets;
using AgentKit.ProviderSignIn;
using AgentKit.Secrets;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AgentBlog.Auth;

/// <summary>
/// Sign-in through a provider the blog's owner trusts (docs/specs/auth.md § A trusted provider): any OpenID Connect
/// issuer, set in the app, not the environment. The authorization code flow with PKCE S256, discovery from the issuer's
/// <c>/.well-known/openid-configuration</c>, and the ID token checked for its signature (the issuer's JWKS, asymmetric
/// algorithms only), issuer, audience, expiry and this sign-in's nonce.
/// <para>
/// <c>AgentKit.ProviderSignIn</c> runs Google and Discord from configuration and can't take a provider the owner adds at
/// run time, so the round trip lives here (decision 0007's ledger lists it as a copy waiting for the package). It reuses
/// the package's public pieces: its state store (one row per sign-in in flight, taken once), and
/// <see cref="IProviderSignInCompletion"/>, so setup, invitations, binding and sign-in decide exactly as they do for
/// Google and Discord.
/// </para>
/// <para>
/// The identity is provider <c>oidc</c> keyed by the SHA-256 of the issuer and the token's <c>sub</c>: never an email,
/// and the same <c>sub</c> at another issuer is someone else.
/// </para>
/// </summary>
public sealed class OidcSignIn(Db db, Sealer sealer, IProviderSignInStateStore store, IProviderSignInCompletion completion,
    IHttpClientFactory http, IConfiguration config, TimeProvider clock, ILoggerFactory logs)
{
    public const string Provider = "oidc";
    public const string HttpClientName = "AgentBlog.Oidc";
    public const string SettingKey = "oidc";
    public const string SecretName = "oidc_client_secret";
    public const string CallbackPath = "/api/auth/oidc/callback";
    public const int MaxDisplayName = 40;
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinRefresh = TimeSpan.FromMinutes(1);

    /// <summary>The algorithms an ID token may be signed with: public-key ones only. Never <c>none</c>, never an HMAC
    /// keyed by the client secret (anyone holding the secret could mint one).</summary>
    private static readonly string[] Algorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    private readonly ILogger _log = logs.CreateLogger("AgentBlog.Oidc");
    private readonly JsonWebTokenHandler _jwt = new() { MapInboundClaims = false };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (Discovery Doc, DateTimeOffset At)> _discovery = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IList<SecurityKey> Keys, DateTimeOffset At)> _keys = new(StringComparer.Ordinal);

    /// <summary>The provider as the owner set it (the secret apart).</summary>
    public sealed record Settings(Guid BlogId, string Issuer, string ClientId, string DisplayName);

    /// <summary>What discovery says, the endpoints checked to be https.</summary>
    public sealed record Discovery(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string JwksUri, bool PostSecret);

    /// <summary>Why a sign-in through the provider was refused: a code for the page's sentence, never the details.</summary>
    public static class Codes
    {
        public const string Off = "oidc_off";
        public const string Signature = "oidc_signature";
        public const string Issuer = "oidc_issuer";
        public const string Audience = "oidc_audience";
        public const string Expired = "oidc_expired";
        public const string Nonce = "oidc_nonce";
        public const string Rejected = "oidc_rejected";
    }

    /// <summary>
    /// The redirect URI the owner registers at the provider: built from <c>Blog__PublicUrl</c>, never from a request.
    /// Null when the server has no public address.
    /// </summary>
    public string? RedirectUri => config["Blog:PublicUrl"] is { Length: > 0 } url ? url.TrimEnd('/') + CallbackPath : null;

    /// <summary>Why the provider can't be set here, or null. Hosted (one sign-in page for every blog): the platform's
    /// providers only, so one blog's owner never decides who signs in on another's page.</summary>
    public string? UnavailableReason => config["Blog:Hosting:Domain"] is { Length: > 0 }
        ? "On a hosted instance the sign-in page is shared by every blog, so a blog can't add its own provider there."
        : RedirectUri is null ? "Set Blog__PublicUrl first: the provider sends people back to it."
        : !sealer.On ? BlogSecrets.OffReason
        : null;

    /// <summary>The provider offered on this instance's sign-in page: its blog's (self-hosted), when set. Null otherwise.</summary>
    public async Task<Settings?> CurrentAsync(CancellationToken ct)
    {
        if (UnavailableReason is not null) return null;
        var blogId = await db.ReadAsync(tx => tx.One<Guid?>("SELECT id FROM blog WHERE hidden_at IS NULL ORDER BY created_at LIMIT 1"), ct);
        return blogId is { } id ? await ForBlogAsync(id, ct) : null;
    }

    /// <summary>The blog's provider, or null when none is set.</summary>
    public async Task<Settings?> ForBlogAsync(Guid blogId, CancellationToken ct)
    {
        var json = await db.ReadAsync(tx => tx.One<string?>($"SELECT settings->'{SettingKey}' #>> '{{}}' FROM blog WHERE id = @blogId", new { blogId }), ct);
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<Stored>(json) is { Issuer: { } i, ClientId: { } c, DisplayName: { } n } ? new Settings(blogId, i, c, n) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Stored(
        [property: JsonPropertyName("issuer")] string? Issuer,
        [property: JsonPropertyName("client_id")] string? ClientId,
        [property: JsonPropertyName("display_name")] string? DisplayName);

    // ------------------------------------------------------------------------------------------------- the owner's setting

    /// <summary>
    /// Sets the blog's provider. Checked before it is kept: an https issuer whose discovery answers with that very issuer
    /// and https endpoints. The secret is sealed (<see cref="BlogSecrets.ForBlog"/>); left out, the stored one stays.
    /// Returns why not, or null.
    /// </summary>
    public async Task<string?> SaveAsync(Guid blogId, Guid ownerId, string? issuer, string? clientId, string? clientSecret, string? displayName,
        CancellationToken ct)
    {
        if (UnavailableReason is { } off) return off;
        issuer = issuer?.Trim() ?? "";
        clientId = clientId?.Trim() ?? "";
        clientSecret = clientSecret?.Trim();
        displayName = string.Join(' ', (displayName ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (CleanIssuer(issuer) is { } why) return why;
        if (clientId.Length is 0 or > 300 || clientId.Any(char.IsControl)) return "The client id is the one the provider showed for this app, on one line.";
        if (displayName.Length is 0 or > MaxDisplayName) return $"The name on the button is 1–{MaxDisplayName} characters: \"Sign in with …\".";
        if (clientSecret is { Length: > 500 }) return "That client secret is too long.";

        var before = await ForBlogAsync(blogId, ct);
        var hasSecret = await db.ReadAsync(tx => tx.One<bool>(
            "SELECT EXISTS (SELECT 1 FROM blog_secret WHERE blog_id = @blogId AND name = @SecretName)", new { blogId, SecretName }), ct);
        if (string.IsNullOrEmpty(clientSecret) && !hasSecret) return "Paste the client secret the provider showed for this app.";
        // The stored secret never follows a new issuer: it would be sent to that issuer's token endpoint at the next
        // sign-in, so pointing the issuer at another host would hand it over.
        if (string.IsNullOrEmpty(clientSecret) && before is not null && before.Issuer != issuer)
            return "A new issuer needs its own client secret: paste the one it gave you for this app.";

        var (doc, error) = await DiscoverAsync(issuer, ct);
        if (doc is null) return error;

        var sealedSecret = string.IsNullOrEmpty(clientSecret) ? null : sealer.Seal(clientSecret, BlogSecrets.ForBlog(blogId, SecretName));
        var now = clock.GetUtcNow();
        await db.WriteAsync(async tx =>
        {
            await tx.Exec($"UPDATE blog SET settings = jsonb_set(settings, '{{{SettingKey}}}', @stored::jsonb) WHERE id = @blogId",
                new { blogId, stored = JsonSerializer.Serialize(new Stored(issuer, clientId, displayName)) });
            if (sealedSecret is { } s)
                await tx.Exec("""
                    INSERT INTO blog_secret (blog_id, name, ciphertext, nonce, key_id, created_at)
                    VALUES (@blogId, @SecretName, @Ciphertext, @Nonce, @KeyId, @now)
                    ON CONFLICT (blog_id, name) DO UPDATE SET ciphertext = EXCLUDED.ciphertext, nonce = EXCLUDED.nonce,
                        key_id = EXCLUDED.key_id, created_at = EXCLUDED.created_at
                    """, new { blogId, SecretName, s.Ciphertext, s.Nonce, s.KeyId, now });
            // What changed, never the secret: the event log is append-only and readable in History.
            await tx.Event(new EventRow(blogId, "blog", blogId, "blog_settings_changed", Actors.Account(ownerId), new Dictionary<string, object?>
            {
                [SettingKey] = new { from = Describe(before), to = Describe(new Settings(blogId, issuer, clientId, displayName)), secret_changed = sealedSecret is not null },
            }), now);
            return 0;
        }, ct);
        return null;
    }

    /// <summary>Removes the provider. Accounts that signed in through it keep the identity (unusable until it's set again
    /// with the same issuer), so nobody loses their other ways in.</summary>
    public Task<bool> RemoveAsync(Guid blogId, Guid ownerId, CancellationToken ct) => db.WriteAsync(async tx =>
    {
        var json = await tx.One<string?>($"SELECT settings->'{SettingKey}' #>> '{{}}' FROM blog WHERE id = @blogId FOR UPDATE", new { blogId });
        if (json is null) return false;
        await tx.Exec($"UPDATE blog SET settings = settings - '{SettingKey}' WHERE id = @blogId", new { blogId });
        await tx.Exec("DELETE FROM blog_secret WHERE blog_id = @blogId AND name = @SecretName", new { blogId, SecretName });
        var before = JsonSerializer.Deserialize<Stored>(json);
        await tx.Event(new EventRow(blogId, "blog", blogId, "blog_settings_changed", Actors.Account(ownerId), new Dictionary<string, object?>
        {
            [SettingKey] = new { from = new { issuer = before?.Issuer, client_id = before?.ClientId, display_name = before?.DisplayName }, to = (object?)null },
        }), clock.GetUtcNow());
        return true;
    }, ct);

    private static object? Describe(Settings? s) => s is null ? null : new { issuer = s.Issuer, client_id = s.ClientId, display_name = s.DisplayName };

    /// <summary>An issuer is an https address with a host and no query, fragment or credentials (OpenID Connect Discovery §3).</summary>
    internal static string? CleanIssuer(string issuer)
    {
        if (issuer.Length is 0 or > 500 || issuer.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return "The issuer is one https address, as the provider documents it.";
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "The issuer must be an https address.";
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.HostNameType == UriHostNameType.Unknown)
            return "The issuer is an https address with no ?query, #fragment or credentials.";
        return null;
    }

    // ------------------------------------------------------------------------------------------------- the round trip

    /// <summary>
    /// Begins a sign-in through the provider for <paramref name="signIn"/>'s purpose: stores its state, binds it to this
    /// browser with a cookie, and returns the provider's authorize address. A refusal comes back as a page code.
    /// </summary>
    public async Task<(Uri? Url, string? Error)> BeginAsync(HttpContext ctx, ProviderSignInContext signIn, CancellationToken ct)
    {
        if (await CurrentAsync(ct) is not { } settings) return (null, Codes.Off);
        var (doc, _) = await DiscoverAsync(settings.Issuer, ct);
        if (doc is null) return (null, "provider_unavailable");

        var state = Random();
        var verifier = Random();
        var nonce = Random();
        var now = clock.GetUtcNow();
        await store.SaveAsync(new ProviderSignInState(Hash(state), Fingerprint(settings), signIn.Purpose, signIn.Data, Pages.LocalPath(signIn.ReturnTo),
            verifier, nonce, now, now + StateLifetime), now, ct);
        ctx.Response.Cookies.Append(CookieName, state, CookieOptions(StateLifetime));

        return (new Uri(QueryHelpers.AddQueryString(doc.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = settings.ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = "openid profile",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
        })), null);
    }

    /// <summary>
    /// The callback: the browser binding, the state (once), the code exchange with the PKCE verifier, and the ID token.
    /// A verified identity goes to <see cref="IProviderSignInCompletion"/>, which decides who it is; a refusal goes to the
    /// page the sign-in began from, with its reason. Never touches a session that already exists.
    /// </summary>
    public async Task<IResult> CallbackAsync(HttpContext ctx, CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer"; // the address carries the code
        var bound = ReadAndDeleteCookie(ctx);
        var query = ctx.Request.Query;
        var state = query["state"].ToString();
        if (state.Length is 0 or > 200 || bound is null || !FixedTimeEquals(state, bound))
        {
            _log.LogInformation("OIDC sign-in refused: the state is missing or not this browser's.");
            return Refuse(null, "state_invalid");
        }

        var settings = await CurrentAsync(ct);
        var stored = await store.TakeAsync(Hash(state), ct);
        if (stored is null || settings is null || stored.Provider != Fingerprint(settings))
        {
            // Unknown, used already, begun for Google or Discord, or begun before the owner changed the provider.
            _log.LogInformation("OIDC sign-in refused: the state is unknown, used, or for another provider.");
            return Refuse(null, "state_invalid");
        }
        var signIn = new ProviderSignInContext(stored.Purpose, stored.Data, stored.ReturnTo);
        if (stored.ExpiresAt <= clock.GetUtcNow()) return Refuse(signIn, "state_expired");
        if (query["error"].ToString() is { Length: > 0 }) return Refuse(signIn, "denied");
        var code = query["code"].ToString();
        if (code.Length is 0 or > 4096) return Refuse(signIn, "exchange_failed");

        try
        {
            var (doc, _) = await DiscoverAsync(settings.Issuer, ct);
            if (doc is null) return Refuse(signIn, "provider_unavailable");
            var secret = await SecretAsync(settings.BlogId, ct);
            if (secret is null) return Refuse(signIn, Codes.Off);

            var (exchangeError, idToken) = await ExchangeAsync(doc, settings, secret, code, stored.CodeVerifier, ct);
            if (exchangeError is not null) return Refuse(signIn, exchangeError);

            var (tokenError, identity) = await CheckIdTokenAsync(doc, settings, idToken!, stored.Nonce, ct);
            if (tokenError is not null) return Refuse(signIn, tokenError);
            return await completion.SignedInAsync(new ProviderSignInSuccess(identity!, signIn), ctx, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("OIDC sign-in failed: the provider did not answer ({Reason}).", e.GetType().Name);
            return Refuse(signIn, "provider_unavailable");
        }
    }

    /// <summary>Back to the page the sign-in began from, with the reason's code. Nothing else: no cookie is cleared.</summary>
    private static IResult Refuse(ProviderSignInContext? signIn, string code) => Results.Redirect(SignIn.FailurePage(signIn?.Purpose) + code);

    private async Task<(string? Error, string? IdToken)> ExchangeAsync(Discovery doc, Settings settings, string secret, string code, string verifier,
        CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri!,
            ["code_verifier"] = verifier,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(doc.TokenEndpoint));
        if (doc.PostSecret)
        {
            form["client_id"] = settings.ClientId;
            form["client_secret"] = secret;
        }
        else
        {
            // client_secret_basic (RFC 6749 §2.3.1): each part form-encoded, then Base64.
            var pair = Uri.EscapeDataString(settings.ClientId) + ":" + Uri.EscapeDataString(secret);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
        }
        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var client = http.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("OIDC sign-in failed: the token endpoint answered {Status}.", (int)response.StatusCode);
            return (InTrouble(response.StatusCode) ? "provider_unavailable" : "exchange_failed", null);
        }
        var tokens = await ReadJsonAsync(response, ct);
        if (String(tokens, "id_token") is not { Length: > 0 } idToken)
        {
            _log.LogWarning("OIDC sign-in refused: the token response carried no ID token.");
            return (Codes.Rejected, null);
        }
        return (null, idToken);
    }

    private async Task<(string? Error, ProviderIdentity? Identity)> CheckIdTokenAsync(Discovery doc, Settings settings, string idToken, string nonce,
        CancellationToken ct)
    {
        var keys = await KeysAsync(doc.JwksUri, refresh: false, ct);
        if (keys is null) return ("provider_unavailable", null);
        var result = await ValidateAsync(idToken, settings, keys);
        if (result is { IsValid: false, Exception: SecurityTokenSignatureKeyNotFoundException })
        {
            keys = await KeysAsync(doc.JwksUri, refresh: true, ct);
            if (keys is null) return ("provider_unavailable", null);
            result = await ValidateAsync(idToken, settings, keys);
        }
        if (!result.IsValid)
        {
            // The exception's type only: token library messages can echo token content.
            _log.LogWarning("OIDC sign-in refused: the ID token failed validation ({Reason}).", result.Exception?.GetType().Name ?? "unknown");
            return (result.Exception switch
            {
                SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException or SecurityTokenInvalidAlgorithmException => Codes.Signature,
                SecurityTokenInvalidIssuerException => Codes.Issuer,
                SecurityTokenInvalidAudienceException => Codes.Audience,
                SecurityTokenExpiredException or SecurityTokenNoExpirationException or SecurityTokenNotYetValidException => Codes.Expired,
                _ => Codes.Rejected,
            }, null);
        }
        // Several audiences: the authorized party must be this client (OpenID Connect Core §3.1.3.7).
        if (Claim(result, "azp") is { } azp && azp != settings.ClientId) return (Codes.Audience, null);
        if (!FixedTimeEquals(Claim(result, "nonce"), nonce))
        {
            _log.LogWarning("OIDC sign-in refused: the ID token's nonce is not this sign-in's.");
            return (Codes.Nonce, null);
        }
        if (Claim(result, "sub") is not { Length: > 0 and <= 255 } subject) return (Codes.Rejected, null);

        var name = Claim(result, "name") is { Length: > 0 } n ? n : Claim(result, "preferred_username");
        var verified = result.Claims.TryGetValue("email_verified", out var v) && v is true or "true";
        return (null, new ProviderIdentity(Provider, AccountKey(settings.Issuer, subject), name is { Length: > 200 } ? name[..200] : name,
            Claim(result, "email"), verified));
    }

    private Task<TokenValidationResult> ValidateAsync(string idToken, Settings settings, IList<SecurityKey> keys) =>
        _jwt.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = settings.Issuer,
            ValidateIssuer = true,
            ValidAudience = settings.ClientId,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidAlgorithms = Algorithms,
            ClockSkew = TimeSpan.FromMinutes(2),
        });

    /// <summary>The account key: the SHA-256 of the issuer and the subject, joined by a byte no URL carries.</summary>
    public static string AccountKey(string issuer, string subject) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "\0" + subject)));

    // ------------------------------------------------------------------------------------------------- discovery and keys

    /// <summary>
    /// The issuer's discovery document, cached an hour. Its <c>issuer</c> must be exactly the configured one (so a document
    /// can't speak for another issuer), and every endpoint https. Fetched through the guarded client: public addresses
    /// only, no redirects (<c>Blog__Auth__Oidc__AllowPrivate</c> for a provider on the self-hoster's own network).
    /// </summary>
    internal async Task<(Discovery? Doc, string? Error)> DiscoverAsync(string issuer, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (_discovery.TryGetValue(issuer, out var cached) && now - cached.At < CacheFor) return (cached.Doc, null);
            JsonElement json;
            try
            {
                using var client = http.CreateClient(HttpClientName);
                using var response = await client.GetAsync(new Uri(issuer.TrimEnd('/') + "/.well-known/openid-configuration"), ct);
                if (!response.IsSuccessStatusCode)
                    return (null, $"The issuer's discovery document answered {(int)response.StatusCode}: is {issuer} the issuer the provider documents?");
                json = await ReadJsonAsync(response, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _log.LogWarning("OIDC discovery for an issuer failed ({Reason}).", e.GetType().Name);
                return (null, AgentKit.Fetch.GuardedHttp.Blocked(e) is { } blocked
                    ? $"The issuer isn't reachable from here: {blocked.Message}"
                    : "The issuer didn't answer. Check the address, or try again in a moment.");
            }
            if (String(json, "issuer") != issuer)
                return (null, "The provider's discovery document names another issuer. Use the issuer exactly as it gives it (a trailing slash counts).");
            var endpoints = new[] { "authorization_endpoint", "token_endpoint", "jwks_uri" }.Select(k => String(json, k)).ToArray();
            if (endpoints.Any(e => !Uri.TryCreate(e, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
                return (null, "The provider's discovery document lacks an https authorization, token or keys address.");
            var methods = json.TryGetProperty("token_endpoint_auth_methods_supported", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(x => x.GetString()).ToList()
                : null;
            // client_secret_basic is the default (Discovery §3); post only when the provider says basic isn't offered.
            var post = methods is not null && !methods.Contains("client_secret_basic") && methods.Contains("client_secret_post");
            var doc = new Discovery(issuer, endpoints[0]!, endpoints[1]!, endpoints[2]!, post);
            _discovery[issuer] = (doc, now);
            return (doc, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The issuer's signing keys, cached an hour, fetched again for an unknown key id at most once a minute.</summary>
    private async Task<IList<SecurityKey>?> KeysAsync(string jwksUri, bool refresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            var known = _keys.TryGetValue(jwksUri, out var cached);
            if (known && now - cached.At < (refresh ? MinRefresh : CacheFor)) return cached.Keys;
            try
            {
                using var client = http.CreateClient(HttpClientName);
                using var response = await client.GetAsync(new Uri(jwksUri), ct);
                if (!response.IsSuccessStatusCode) return known ? cached.Keys : null;
                var keys = new JsonWebKeySet(await response.Content.ReadAsStringAsync(ct)).GetSigningKeys();
                _keys[jwksUri] = (keys, now);
                return keys;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ArgumentException && !ct.IsCancellationRequested)
            {
                _log.LogWarning("The OIDC provider's signing keys could not be fetched ({Reason}); its sign-in fails closed.", e.GetType().Name);
                return known ? cached.Keys : null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> SecretAsync(Guid blogId, CancellationToken ct)
    {
        var row = await db.ReadAsync(tx => tx.One<SecretRow>(
            "SELECT ciphertext, nonce, key_id FROM blog_secret WHERE blog_id = @blogId AND name = @SecretName", new { blogId, SecretName }), ct);
        return row is null ? null : sealer.OpenText(new SealedValue(row.Ciphertext, row.Nonce, row.KeyId), BlogSecrets.ForBlog(blogId, SecretName));
    }

    private sealed record SecretRow(byte[] Ciphertext, byte[] Nonce, string KeyId);

    // ------------------------------------------------------------------------------------------------- the browser binding

    /// <summary>
    /// The state the provider echoes back must equal this cookie, so another browser's code and state (login CSRF) are
    /// refused. On https it carries the <c>__Host-</c> prefix; it is read by its exact name, one copy only, and always
    /// deleted. The same rule as the package's own cookie (internal there), under a name of its own.
    /// </summary>
    private bool Secure => !(Uri.TryCreate(config["Blog:PublicUrl"], UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp);

    private string CookieName => Secure ? "__Host-blog_oidc" : "blog_oidc";

    private CookieOptions CookieOptions(TimeSpan? lifetime) => new()
    {
        HttpOnly = true, Secure = Secure, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = lifetime, IsEssential = true,
    };

    private string? ReadAndDeleteCookie(HttpContext ctx)
    {
        string? found = null;
        var copies = 0;
        foreach (var header in ctx.Request.Headers.Cookie)
        {
            if (header is null) continue;
            foreach (var part in header.Split(';'))
            {
                var pair = part.Trim();
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0 || !string.Equals(pair[..eq], CookieName, StringComparison.Ordinal)) continue;
                found = pair[(eq + 1)..];
                copies++;
            }
        }
        ctx.Response.Cookies.Delete(CookieName, CookieOptions(null));
        return copies == 1 && found is { Length: > 0 } ? found : null;
    }

    // ------------------------------------------------------------------------------------------------- small things

    /// <summary>What the state row records as its provider: the provider as configured when the sign-in began. A sign-in
    /// begun before the owner changed the issuer or client id finds a different fingerprint and is refused.</summary>
    private static string Fingerprint(Settings s) =>
        "oidc:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{s.BlogId:D}\0{s.Issuer}\0{s.ClientId}")))[..32];

    private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string state) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool InTrouble(HttpStatusCode status) => (int)status >= 500 || status == HttpStatusCode.TooManyRequests;

    private static bool FixedTimeEquals(string? actual, string expected)
    {
        if (actual is null) return false;
        var a = Encoding.UTF8.GetBytes(actual);
        var e = Encoding.UTF8.GetBytes(expected);
        return a.Length == e.Length && CryptographicOperations.FixedTimeEquals(a, e);
    }

    private static string? Claim(TokenValidationResult result, string name) =>
        result.Claims.TryGetValue(name, out var value) ? value as string : null;

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ------------------------------------------------------------------------------------------------- endpoints

    public sealed record SettingsBody(
        [property: JsonPropertyName("issuer")] string? Issuer,
        [property: JsonPropertyName("client_id")] string? ClientId,
        [property: JsonPropertyName("client_secret")] string? ClientSecret,
        [property: JsonPropertyName("display_name")] string? DisplayName);

    public static void MapOidc(WebApplication app)
    {
        // Literal paths: they win over the package's /api/auth/{provider}/… routes, which answer 404 for "oidc".
        app.MapGet("/api/auth/oidc/start", async (HttpContext ctx, OidcSignIn oidc, string? returnTo) =>
        {
            var (url, error) = await oidc.BeginAsync(ctx, new ProviderSignInContext(ProviderSignInPurposes.SignIn, null, returnTo), ctx.RequestAborted);
            return url is not null ? Results.Redirect(url.AbsoluteUri) : Results.Redirect("/sign-in?error=" + error);
        }).WithMetadata(SignIn.AuthRateLimited.Instance);
        app.MapGet(CallbackPath, (HttpContext ctx, OidcSignIn oidc) => oidc.CallbackAsync(ctx, ctx.RequestAborted))
            .WithMetadata(SignIn.AuthRateLimited.Instance);

        // The owner's setting. Owner only; no agent tool or approval request reaches it.
        var api = app.MapOwnerApi();
        api.MapGet("/blogs/{blogId:guid}/sign-in/oidc", async (HttpContext ctx, Guid blogId, OidcSignIn oidc, Db db) =>
            await OwnsAsync(db, ctx, blogId) ? Results.Json(await oidc.JsonAsync(blogId, ctx.RequestAborted)) : NotFound());

        api.MapPut("/blogs/{blogId:guid}/sign-in/oidc", async (HttpContext ctx, Guid blogId, SettingsBody body, OidcSignIn oidc, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            if (oidc.UnavailableReason is { } off) return SignIn.Problem(409, "unavailable", off);
            return await oidc.SaveAsync(blogId, SignIn.Current(ctx).AccountId, body.Issuer, body.ClientId, body.ClientSecret, body.DisplayName,
                    ctx.RequestAborted) is { } error
                ? SignIn.Problem(400, "invalid_argument", error)
                : Results.Json(await oidc.JsonAsync(blogId, ctx.RequestAborted));
        });

        api.MapDelete("/blogs/{blogId:guid}/sign-in/oidc", async (HttpContext ctx, Guid blogId, OidcSignIn oidc, Db db) =>
        {
            if (!await OwnsAsync(db, ctx, blogId)) return NotFound();
            await oidc.RemoveAsync(blogId, SignIn.Current(ctx).AccountId, ctx.RequestAborted);
            return Results.Json(await oidc.JsonAsync(blogId, ctx.RequestAborted));
        });
    }

    private async Task<object> JsonAsync(Guid blogId, CancellationToken ct)
    {
        var s = await ForBlogAsync(blogId, ct);
        var secretSet = s is not null && await db.ReadAsync(tx => tx.One<bool>(
            "SELECT EXISTS (SELECT 1 FROM blog_secret WHERE blog_id = @blogId AND name = @SecretName)", new { blogId, SecretName }), ct);
        return new
        {
            set = s is not null,
            issuer = s?.Issuer,
            client_id = s?.ClientId,
            display_name = s?.DisplayName,
            secret_set = secretSet, // the secret itself is never sent back
            redirect_uri = RedirectUri,
            available = UnavailableReason is null,
            reason = UnavailableReason,
            max_display_name = MaxDisplayName,
        };
    }

    private static IResult NotFound() => SignIn.Problem(404, "not_found", "No such blog of yours.");

    private static Task<bool> OwnsAsync(Db db, HttpContext ctx, Guid blogId) => db.ReadAsync(tx => tx.One<bool>("""
        SELECT EXISTS (SELECT 1 FROM blog_member WHERE blog_id = @blogId AND account_id = @accountId AND role = 'owner')
        """, new { blogId, accountId = SignIn.Current(ctx).AccountId }), ctx.RequestAborted);
}
