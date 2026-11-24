using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// An OpenID Connect provider in the test process, behaving like a real one where it matters (docs/specs/auth.md § A
/// trusted provider): discovery at <c>/.well-known/openid-configuration</c>, an authorization code good once, for the
/// client that asked, at the same redirect URI, only with the client secret (HTTP Basic, or the form when
/// <see cref="PostSecretOnly"/>) and only with the PKCE verifier whose S256 challenge the authorize request carried; ID
/// tokens RS256-signed with a key its JWKS publishes, issued by <see cref="Issuer"/> for the client, with the authorize
/// request's nonce. <see cref="IdTokenTweak"/> bends one sign-in's token to prove each check refuses it.
/// </summary>
public sealed class OidcStub : IDisposable
{
    public const string ClientId = "agent-blog-test";
    public const string ClientSecret = "oidc-secret-value-1";
    private const string KeyId = "oidc-key-1";

    public string Issuer { get; }
    public string Host { get; }
    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _forger = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Grant> _codes = new();
    private readonly JsonWebTokenHandler _jwt = new();

    public OidcStub(string host) => (Host, Issuer) = (host, $"https://{host}");

    /// <summary>Every endpoint answers 503: the provider is down.</summary>
    public bool Down { get; set; }

    /// <summary>The discovery document names this issuer instead of its own.</summary>
    public string? DiscoveryIssuer { get; set; }

    /// <summary>Discovery offers only client_secret_post.</summary>
    public bool PostSecretOnly { get; set; }

    /// <summary>The nonces authorize requests carried, in order.</summary>
    public List<string> Nonces { get; } = [];

    /// <summary>Token requests that came without a PKCE verifier.</summary>
    public int MissingVerifier { get; private set; }

    private sealed record Grant(string ClientId, string RedirectUri, string Challenge, string Nonce, StubAccount Account, IdTokenTweak Tweak);

    /// <summary>The browser at the provider, signing in as <paramref name="account"/>: where it is sent back to.</summary>
    public Uri Approve(Uri authorizeUrl, StubAccount account, IdTokenTweak? tweak = null)
    {
        Assert.Equal(Issuer + "/authorize", authorizeUrl.GetLeftPart(UriPartial.Path));
        var q = QueryHelpers.ParseQuery(authorizeUrl.Query);
        string Required(string name) => q.TryGetValue(name, out var v) && v.ToString() is { Length: > 0 } s ? s
            : throw new InvalidOperationException($"The authorize request has no {name}.");
        Assert.Equal("code", Required("response_type"));
        Assert.Equal(ClientId, Required("client_id"));
        Assert.Equal("S256", Required("code_challenge_method"));
        Assert.Contains("openid", Required("scope").Split(' '));
        var nonce = Required("nonce");
        Nonces.Add(nonce);
        var code = "code-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _codes[code] = new Grant(Required("client_id"), Required("redirect_uri"), Required("code_challenge"), nonce, account, tweak ?? new IdTokenTweak());
        return new Uri(QueryHelpers.AddQueryString(Required("redirect_uri"), new Dictionary<string, string?> { ["code"] = code, ["state"] = Required("state") }));
    }

    internal async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
    {
        if (Down) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        return (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/.well-known/openid-configuration") => Json(HttpStatusCode.OK, new Dictionary<string, object>
            {
                ["issuer"] = DiscoveryIssuer ?? Issuer,
                ["authorization_endpoint"] = Issuer + "/authorize",
                ["token_endpoint"] = Issuer + "/token",
                ["jwks_uri"] = Issuer + "/jwks",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["code_challenge_methods_supported"] = new[] { "S256" },
                ["token_endpoint_auth_methods_supported"] = PostSecretOnly ? new[] { "client_secret_post" } : new[] { "client_secret_basic", "client_secret_post" },
            }),
            ("GET", "/jwks") => Json(HttpStatusCode.OK, Jwks()),
            ("POST", "/token") => await TokenAsync(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request)
    {
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync());
        string F(string name) => form.TryGetValue(name, out var v) ? v.ToString() : "";
        var (clientId, secret) = (F("client_id"), F("client_secret"));
        if (request.Headers.Authorization is { Scheme: "Basic", Parameter: { } basic })
        {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(basic)).Split(':', 2);
            (clientId, secret) = (Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1]));
        }
        else if (!PostSecretOnly) return Error("invalid_client"); // this provider wants Basic

        if (F("grant_type") != "authorization_code" || !_codes.TryRemove(F("code"), out var grant)) return Error("invalid_grant");
        if (clientId != grant.ClientId || secret != ClientSecret) return Error("invalid_client");
        if (F("redirect_uri") != grant.RedirectUri) return Error("invalid_grant");
        if (F("code_verifier").Length == 0) { MissingVerifier++; return Error("invalid_grant"); }
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(F("code_verifier"))));
        if (F("code_verifier").Length < 43 || challenge != grant.Challenge) return Error("invalid_grant");

        var accessToken = "at-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        return grant.Tweak.OmitIdToken
            ? Json(HttpStatusCode.OK, new { access_token = accessToken, token_type = "Bearer", expires_in = 3600 })
            : Json(HttpStatusCode.OK, new { access_token = accessToken, token_type = "Bearer", expires_in = 3600, id_token = IdToken(grant) });
    }

    private string IdToken(Grant grant)
    {
        var tweak = grant.Tweak;
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object> { ["sub"] = grant.Account.Id, ["name"] = grant.Account.Name };
        if (grant.Account.Email is { } email)
        {
            claims["email"] = email;
            claims["email_verified"] = true;
        }
        if (!tweak.OmitNonce) claims["nonce"] = tweak.Nonce ?? grant.Nonce;
        return _jwt.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = tweak.Issuer ?? Issuer,
            Audience = tweak.Audience ?? grant.ClientId,
            IssuedAt = tweak.Expired ? now.AddHours(-2) : now,
            NotBefore = tweak.Expired ? now.AddHours(-2) : now,
            Expires = tweak.Expired ? now.AddHours(-1) : now.AddHours(1),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(tweak.ForgedSignature ? _forger : _key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        });
    }

    private object Jwks()
    {
        var p = _key.ExportParameters(false);
        return new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = KeyId, n = Base64Url(p.Modulus!), e = Base64Url(p.Exponent!) } } };
    }

    private static HttpResponseMessage Error(string error) => Json(HttpStatusCode.BadRequest, new { error });

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, new MediaTypeHeaderValue("application/json")) };

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        _key.Dispose();
        _forger.Dispose();
    }
}

/// <summary>The blog's OIDC client in the tests: every request goes to the stub whose host it names.</summary>
public sealed class OidcTransport(IReadOnlyList<OidcStub> stubs) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        stubs.FirstOrDefault(s => s.Host == request.RequestUri!.Host) is { } stub
            ? stub.HandleAsync(request)
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
}
