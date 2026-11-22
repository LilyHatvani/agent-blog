using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using AgentBlog.Media;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// Cloudflare's purge API as the blog sees it: <c>POST /zones/{zone}/purge_cache</c>, a bearer that must be the zone's
/// purge token, answers scripted per call (the envelope <c>{"success": true}</c> when none).
/// </summary>
internal sealed class StubCloudflare : HttpMessageHandler
{
    public const string Zone = "0123456789abcdef0123456789abcdef";
    public const string Token = "cf-purge-only-token-0123456789";

    public sealed record Purge(string Zone, string Token, string[] Tags);

    public ConcurrentQueue<Purge> Purges { get; } = new();
    private readonly ConcurrentQueue<HttpStatusCode> _script = new();

    public void Answer(params HttpStatusCode[] statuses)
    {
        foreach (var s in statuses) _script.Enqueue(s);
    }

    public IEnumerable<string> Tags => Purges.SelectMany(p => p.Tags);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["client", "v4", "zones", path[3], "purge_cache"], path);
        Assert.Equal(HttpMethod.Post, request.Method);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        var tags = body["tags"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray();
        Assert.InRange(tags.Length, 1, 100); // Cloudflare's limit per call
        var token = request.Headers.Authorization?.Parameter ?? "";
        Purges.Enqueue(new Purge(path[3], token, tags));
        if (_script.TryDequeue(out var scripted))
            return new HttpResponseMessage(scripted) { Content = new StringContent("""{"success":false,"errors":[{"code":10000,"message":"scripted"}]}""") };
        if (token != Token || path[3] != Zone)
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"success":false,"errors":[{"code":10000,"message":"Authentication error"}]}""") };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"success":true,"errors":[],"messages":[],"result":{"id":"x"}}""") };
    }
}

/// <summary>
/// M2 slice 4 (decision 0009, docs/specs/media.md § Cloudflare): pictures carry their cache tags; a connected zone gets
/// a long edge time and trash purges it at once; refusals are told, never silent; disconnecting purges everything.
/// </summary>
public sealed class CdnTests
{
    private const string MasterKey = "0000000000000000000000000000000000000000000000000000000000000002";

    private sealed class Cdn : IAsyncDisposable
    {
        public required MediaTests.MediaWorld M { get; init; }
        public required StubCloudflare Cloudflare { get; init; }

        public static async Task<Cdn> StartAsync(bool secrets = true)
        {
            var cf = new StubCloudflare();
            var m = await MediaTests.MediaWorld.StartAsync(more: b =>
            {
                if (secrets) b.UseSetting("Blog:Secrets:MasterKey", MasterKey);
                b.UseSetting("Blog:Notify:IntervalSeconds", "0");
                b.UseSetting("Blog:Cloudflare:ApiBase", "https://api.cloudflare.test/client/v4/");
                b.ConfigureTestServices(s => s.AddHttpClient(CdnLinks.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => cf));
            });
            return new Cdn { M = m, Cloudflare = cf };
        }

        public SignInBrowser Owner => M.World.Owner;
        public string Blog => M.BlogId;

        public Task<JsonResult> ConnectAsync(string zone = StubCloudflare.Zone, string token = StubCloudflare.Token, int? edge = null) =>
            Owner.SendAsync(HttpMethod.Put, $"/api/blogs/{Blog}/cdn", csrf: true, body: new { zone_id = zone, token, edge_seconds = edge });

        public async Task<JsonNode> StateAsync() => (await Owner.GetJsonAsync($"/api/blogs/{Blog}/cdn")).Json!;

        public Task<JsonResult> TrashAsync(string id) => Owner.PostJsonAsync($"/api/media/{id}/trash", null, csrf: true);

        public Task<int> DrainAsync() => M.World.Host.App.Factory.Services.GetRequiredService<CdnLinks>().DrainAsync(default);

        public async Task<T> SqlAsync<T>(string sql)
        {
            await using var conn = new NpgsqlConnection(M.World.Host.App.OwnerConnectionString);
            return await conn.QuerySingleAsync<T>(sql);
        }

        public async Task ExecAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(M.World.Host.App.OwnerConnectionString);
            await conn.ExecuteAsync(sql);
        }

        public ValueTask DisposeAsync() => M.DisposeAsync();
    }

    private static string Id(JsonNode n) => n["id"]!.GetValue<string>();

    [Fact]
    public async Task Every_picture_carries_its_tags_and_only_a_connected_blog_gets_the_long_edge_time()
    {
        await using var c = await Cdn.StartAsync();
        var picture = await c.M.UploadAsync(MediaHost.Png);
        var plain = await c.M.ReaderGetAsync(picture["path"]!.GetValue<string>());
        Assert.Equal($"media:{Id(picture)},blog-media:{c.Blog}", string.Join(",", plain.Headers.GetValues("Cache-Tag")));
        Assert.False(plain.Headers.Contains("Cloudflare-CDN-Cache-Control"));
        Assert.Equal("public, max-age=3600", plain.Headers.CacheControl!.ToString());

        Assert.Equal(HttpStatusCode.OK, (await c.ConnectAsync(edge: 86400)).Status);
        var cached = await c.M.ReaderGetAsync(picture["path"]!.GetValue<string>());
        Assert.Equal("max-age=86400", cached.Headers.GetValues("Cloudflare-CDN-Cache-Control").Single());
        Assert.Equal("public, max-age=3600", cached.Headers.CacheControl!.ToString()); // browsers keep their hour

        // Not served is not cached: a trashed picture's 404 says no-store, so restoring it shows it again at once.
        Assert.Equal(HttpStatusCode.OK, (await c.TrashAsync(Id(picture))).Status);
        var gone = await c.M.ReaderGetAsync(picture["path"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.True(gone.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Connecting_tries_the_token_first_and_keeps_it_sealed()
    {
        await using var c = await Cdn.StartAsync();
        var wrong = await c.ConnectAsync(token: "a-token-without-purge-rights-000");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
        Assert.Contains("Zone → Cache Purge", wrong.Json!["detail"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.ConnectAsync(zone: "not-a-zone")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.ConnectAsync(edge: 60)).Status);
        Assert.False((await c.StateAsync())["connected"]!.GetValue<bool>());

        var ok = await c.ConnectAsync();
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.True(ok.Json!["connected"]!.GetValue<bool>());
        Assert.Equal(604800, ok.Json!["edge_seconds"]!.GetValue<int>()); // 7 days by default
        Assert.Equal(["agentblog-connect-check"], c.Cloudflare.Purges.Last().Tags);
        Assert.DoesNotContain(StubCloudflare.Token, (await c.StateAsync()).ToJsonString());
        Assert.DoesNotContain(StubCloudflare.Token, System.Text.Encoding.UTF8.GetString(await c.SqlAsync<byte[]>("SELECT ciphertext FROM blog_secret")));
        Assert.Contains(("cdn_connected", $"account:{await c.SqlAsync<Guid>("SELECT account_id FROM blog_member LIMIT 1")}"), await c.M.World.EventsAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await c.Owner.SendAsync(HttpMethod.Put, $"/api/blogs/{c.Blog}/cdn", csrf: false,
            body: new { zone_id = StubCloudflare.Zone, token = StubCloudflare.Token })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Owner.GetJsonAsync($"/api/blogs/{Guid.NewGuid()}/cdn")).Status);
    }

    [Fact]
    public async Task Without_a_master_key_connecting_says_what_to_set()
    {
        await using var c = await Cdn.StartAsync(secrets: false);
        var r = await c.ConnectAsync();
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Contains("Blog__Secrets__MasterKey", r.Json!["detail"]!.GetValue<string>());
        Assert.Empty(c.Cloudflare.Purges); // the token wasn't even tried
    }

    [Fact]
    public async Task Trashing_a_picture_purges_it_from_the_edge_and_restoring_does_not()
    {
        await using var c = await Cdn.StartAsync();
        var before = await c.M.UploadAsync(MediaHost.Png);
        Assert.Equal(HttpStatusCode.OK, (await c.TrashAsync(Id(before))).Status); // not connected yet: nothing to purge
        Assert.Equal(0, await c.SqlAsync<int>("SELECT count(*)::int FROM media_purge"));

        await c.ConnectAsync();
        var picture = await c.M.UploadAsync(MediaHost.Gif, "image/gif", "cat.gif");
        Assert.Equal(HttpStatusCode.OK, (await c.TrashAsync(Id(picture))).Status);
        Assert.Equal(1, (await c.StateAsync())["waiting"]!.GetValue<int>());
        Assert.Equal(1, await c.DrainAsync());
        var purge = c.Cloudflare.Purges.Last();
        Assert.Equal([$"media:{Id(picture)}"], purge.Tags);
        Assert.Equal((StubCloudflare.Zone, StubCloudflare.Token), (purge.Zone, purge.Token));
        Assert.Equal(0, (await c.StateAsync())["waiting"]!.GetValue<int>());
        Assert.NotNull((await c.StateAsync())["last_purge_at"]);

        var calls = c.Cloudflare.Purges.Count;
        Assert.Equal(HttpStatusCode.OK, (await c.Owner.PostJsonAsync($"/api/media/{Id(picture)}/restore", null, csrf: true)).Status);
        Assert.Equal(0, await c.DrainAsync());
        Assert.Equal(calls, c.Cloudflare.Purges.Count);
    }

    [Fact]
    public async Task More_than_a_hundred_go_in_calls_of_a_hundred()
    {
        await using var c = await Cdn.StartAsync();
        await c.ConnectAsync();
        // 150 trashed pictures queued at once (as rows: uploading 150 through the API would test nothing more here).
        await c.ExecAsync($"""
            WITH m AS (
                INSERT INTO media (id, blog_id, status, content_type, size_bytes, sha256, filename, created_by, created_at, ready_at, trashed_at, trashed_by)
                SELECT gen_random_uuid(), '{c.Blog}', 'trash', 'image/png', 1, sha256(g::text::bytea), 'p' || g || '.png', 'system', now(), now(), now(), 'system'
                FROM generate_series(1, 150) g RETURNING id, blog_id)
            INSERT INTO media_purge (media_id, blog_id, enqueued_at) SELECT id, blog_id, now() FROM m
            """);
        var calls = c.Cloudflare.Purges.Count;
        Assert.Equal(100, await c.DrainAsync());
        Assert.Equal(50, await c.DrainAsync());
        Assert.Equal(calls + 2, c.Cloudflare.Purges.Count);
        Assert.Equal(150, c.Cloudflare.Purges.Skip(calls).SelectMany(p => p.Tags).Distinct().Count());
    }

    [Fact]
    public async Task A_limit_or_an_outage_is_retried_and_a_refusal_is_told_once_and_shortens_the_edge_time()
    {
        await using var c = await Cdn.StartAsync();
        await c.ConnectAsync();
        var picture = await c.M.UploadAsync(MediaHost.Png);
        await c.TrashAsync(Id(picture));

        c.Cloudflare.Answer(HttpStatusCode.TooManyRequests);
        Assert.Equal(0, await c.DrainAsync());
        var limited = await c.StateAsync();
        Assert.Equal(1, limited["waiting"]!.GetValue<int>());
        Assert.Contains("429", limited["last_error"]!.GetValue<string>());
        Assert.False(limited["refused"]!.GetValue<bool>());
        // A 200 whose envelope says success: false is not a purge.
        c.Cloudflare.Answer(HttpStatusCode.OK);
        Assert.Equal(0, await c.DrainAsync());
        Assert.Equal(1, (await c.StateAsync())["waiting"]!.GetValue<int>());
        Assert.Equal(1, await c.DrainAsync()); // freed at once: the next pass sends it
        Assert.Null((await c.StateAsync())["last_error"]);

        // The token is revoked in Cloudflare: refused, told once, and new copies get only the hour.
        var second = await c.M.UploadAsync(MediaHost.Gif, "image/gif", "b.gif");
        await c.TrashAsync(Id(second));
        c.Cloudflare.Answer(HttpStatusCode.Forbidden, HttpStatusCode.Forbidden);
        await c.DrainAsync();
        await c.DrainAsync();
        var refused = await c.StateAsync();
        Assert.True(refused["refused"]!.GetValue<bool>());
        Assert.Contains("Zone → Cache Purge", refused["last_error"]!.GetValue<string>());
        Assert.Equal(1, refused["waiting"]!.GetValue<int>()); // kept: the token may work again
        Assert.Single(await c.M.World.EventsAsync(), e => e.Kind == "cdn_purge_refused");
        var third = await c.M.UploadAsync(MediaHost.Jpeg, "image/jpeg", "c.jpg");
        Assert.False((await c.M.ReaderGetAsync(third["path"]!.GetValue<string>())).Headers.Contains("Cloudflare-CDN-Cache-Control"));

        // It works again: purged, and the long edge time is back.
        Assert.Equal(1, await c.DrainAsync());
        Assert.False((await c.StateAsync())["refused"]!.GetValue<bool>());
        Assert.True((await c.M.ReaderGetAsync(third["path"]!.GetValue<string>())).Headers.Contains("Cloudflare-CDN-Cache-Control"));
    }

    [Fact]
    public async Task A_purge_older_than_the_edge_time_is_moot_and_dropped()
    {
        await using var c = await Cdn.StartAsync();
        await c.ConnectAsync(edge: 3600);
        var picture = await c.M.UploadAsync(MediaHost.Png);
        await c.TrashAsync(Id(picture));
        await c.ExecAsync("UPDATE media_purge SET enqueued_at = now() - interval '2 hours'");
        c.Cloudflare.Answer(HttpStatusCode.Forbidden);
        var calls = c.Cloudflare.Purges.Count;
        Assert.Equal(0, await c.DrainAsync());
        Assert.Equal(calls, c.Cloudflare.Purges.Count); // nothing left to purge: no call
        Assert.Equal(0, await c.SqlAsync<int>("SELECT count(*)::int FROM media_purge"));
    }

    [Fact]
    public async Task Disconnecting_purges_every_picture_deletes_the_token_and_trash_stops_queueing()
    {
        await using var c = await Cdn.StartAsync();
        await c.ConnectAsync();
        var picture = await c.M.UploadAsync(MediaHost.Png);
        var d = await c.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{c.Blog}/cdn", csrf: true);
        Assert.Equal(HttpStatusCode.OK, d.Status);
        Assert.Null(d.Json!["note"]);
        Assert.Equal([$"blog-media:{c.Blog}"], c.Cloudflare.Purges.Last().Tags);
        Assert.Equal(0, await c.SqlAsync<int>("SELECT count(*)::int FROM blog_secret"));
        Assert.False((await c.StateAsync())["connected"]!.GetValue<bool>());
        Assert.False((await c.M.ReaderGetAsync(picture["path"]!.GetValue<string>())).Headers.Contains("Cloudflare-CDN-Cache-Control"));
        await c.TrashAsync(Id(picture));
        Assert.Equal(0, await c.SqlAsync<int>("SELECT count(*)::int FROM media_purge"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{c.Blog}/cdn", csrf: true)).Status);

        // A refused last purge still disconnects, and says what may stay at the edge.
        await c.ConnectAsync();
        c.Cloudflare.Answer(HttpStatusCode.Forbidden);
        var refused = await c.Owner.SendAsync(HttpMethod.Delete, $"/api/blogs/{c.Blog}/cdn", csrf: true);
        Assert.Contains("7 days", refused.Json!["note"]!.GetValue<string>());
        Assert.Equal(0, await c.SqlAsync<int>("SELECT count(*)::int FROM blog_secret"));
    }
}
