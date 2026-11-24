using System.Text.Json;
using AgentBlog.Database;

namespace AgentBlog.Auth;

/// <summary>
/// Moving the blog to a new address (docs/specs/address-move.md). <c>Blog__PublicUrl</c> stays the operator's setting:
/// the MCP issuer, the provider redirect addresses and the passkeys' relying party are all built from it when the server
/// starts. What the code adds around a move:
/// <list type="bullet">
/// <item>The old addresses, <c>Blog__PreviousUrls</c>, answer a page request with a 301 to the same path on the new one.
/// Only the listed hosts, and the target is always built from <c>Blog__PublicUrl</c>, never from the request.</item>
/// <item>Which address each passkey was made at (WebAuthn's client data records its origin), so the app can ask anyone
/// whose passkeys belong to an old address to make one here.</item>
/// </list>
/// </summary>
public static class AddressMove
{
    public const string PreviousUrlsKey = "Blog:PreviousUrls";

    /// <summary>Paths an old address keeps serving itself: API calls (a cross-site redirect breaks them), the MCP door and
    /// its OAuth discovery (their issuer is the new address; old connections reconnect).</summary>
    private static readonly string[] Kept = ["/api", "/mcp", "/oauth", "/.well-known"];

    /// <summary>The hosts (with a port when it isn't the default) of <c>Blog__PreviousUrls</c>: https or http addresses,
    /// separated by commas, semicolons or spaces. Anything else in the list is ignored, and so is the current address.</summary>
    public static IReadOnlySet<string> PreviousHosts(IConfiguration config)
    {
        var current = config["Blog:PublicUrl"] is { Length: > 0 } p && Uri.TryCreate(p, UriKind.Absolute, out var now) ? Authority(now) : null;
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (config[PreviousUrlsKey] ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Uri.TryCreate(part, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" && u.Host.Length > 0 && Authority(u) != current)
                hosts.Add(Authority(u));
        return hosts;
    }

    private static string Authority(Uri u) => u.IsDefaultPort ? u.Host.ToLowerInvariant() : $"{u.Host.ToLowerInvariant()}:{u.Port}";

    /// <summary>
    /// GET and HEAD for a page on an old address: 301 to the same path and query on the new one. Everything else on an
    /// old address is served as before: a one-click unsubscribe from an email sent before the move is a POST, and a 301
    /// would turn it into a GET that unsubscribes nobody.
    /// </summary>
    public static void UseMovedAddresses(this WebApplication app)
    {
        var hosts = PreviousHosts(app.Configuration);
        if (hosts.Count == 0 || app.Configuration["Blog:PublicUrl"] is not { Length: > 0 } publicUrl) return;
        var target = publicUrl.TrimEnd('/');
        app.Use(async (ctx, next) =>
        {
            var request = ctx.Request;
            if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
                && hosts.Contains(request.Host.Port is null or 80 or 443 ? request.Host.Host : request.Host.Value ?? "")
                && !Kept.Any(k => request.Path.StartsWithSegments(k)))
            {
                ctx.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                ctx.Response.Headers.Location = target + request.PathBase + request.Path + request.QueryString;
                ctx.Response.Headers.CacheControl = "public, max-age=86400";
                return;
            }
            await next(ctx);
        });
    }

    /// <summary>The relying party passkeys are made for now: <c>Blog__PublicUrl</c>'s host.</summary>
    public static string? CurrentPasskeyHost(IConfiguration config) =>
        config["Blog:PublicUrl"] is { Length: > 0 } p && Uri.TryCreate(p, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;

    /// <summary>
    /// The host each of the account's passkeys was made at, by its identity key: from the origin its client data recorded
    /// when it was created. Null for one whose origin can't be read.
    /// </summary>
    public static Task<IReadOnlyDictionary<string, string?>> PasskeyHostsAsync(Db db, Guid accountId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyDictionary<string, string?>>(async tx =>
            (await tx.Query<(string Key, byte[] ClientData)>(
                "SELECT provider_account_id, client_data_json FROM account_passkey WHERE account_id = @accountId", new { accountId }))
            .ToDictionary(r => r.Key, r => HostOf(r.ClientData)), ct);

    /// <summary>The host of the origin in a WebAuthn client data JSON, or null.</summary>
    internal static string? HostOf(byte[] clientDataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(clientDataJson);
            return doc.RootElement.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.String
                && Uri.TryCreate(o.GetString(), UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
