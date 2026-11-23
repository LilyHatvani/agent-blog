using System.Net;
using AgentKit.McpOAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// The demo (docs/specs/demo.md): <c>/demo</c> opens the owner's app as a guest on a hidden sample blog. Everything
/// shows; every change is refused with the reason; the real blog, its owner and setup are out of its reach.
/// </summary>
public sealed class DemoTests
{
    private static readonly StubAccount Lenka = new("500000000000000001", "Lenka", null);
    private const string Refusal = "This is a demo";

    private static Task<SignInHost> WithDemoAsync() => SignInHost.StartAsync(configure: b => b.UseSetting("Blog:Demo:Enabled", "true"));

    private static async Task<SignInBrowser> GuestAsync(SignInHost host)
    {
        var guest = host.Browser();
        var r = await guest.Http.GetAsync("/demo");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/admin", r.Headers.Location!.OriginalString);
        return guest;
    }

    [Fact]
    public async Task Off_by_default_there_is_no_demo()
    {
        await using var host = await SignInHost.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await host.Browser().Http.GetAsync("/demo")).StatusCode);
        Assert.DoesNotContain("/demo", await host.Browser().Http.GetStringAsync("/sign-in"));
    }

    [Fact]
    public async Task A_visitor_looks_around_a_sample_blog_as_its_owner()
    {
        await using var host = await WithDemoAsync();
        Assert.Contains("Try the demo", await host.Browser().Http.GetStringAsync("/sign-in"));
        var guest = await GuestAsync(host);

        var me = (await guest.GetJsonAsync("/api/me")).Json!;
        Assert.True(me["demo"]!.GetValue<bool>());
        var blog = Assert.Single(me["blogs"]!.AsArray())!;
        Assert.Equal("owner", blog["role"]!.GetValue<string>());
        var id = blog["id"]!.GetValue<string>();

        Assert.Equal(5, (await guest.GetJsonAsync($"/api/blogs/{id}/posts")).Json!["posts"]!.AsArray().Count);
        Assert.Equal(2, (await guest.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray().Count);
        Assert.Equal(2, (await guest.GetJsonAsync("/api/connections")).Json!["connections"]!.AsArray().Count);
        Assert.Contains("Anna", (await guest.GetJsonAsync($"/api/blogs/{id}/people")).Json!.ToJsonString());
        Assert.NotEmpty((await guest.GetJsonAsync($"/api/blogs/{id}/history")).Json!["events"]!.AsArray());

        // A second visitor shares the same sample, made once.
        var other = await GuestAsync(host);
        Assert.Equal(id, (await other.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Every_change_is_refused_with_the_reason_and_signing_out_still_works()
    {
        await using var host = await WithDemoAsync();
        var guest = await GuestAsync(host);
        var blog = (await guest.GetJsonAsync("/api/me")).Json!["blogs"]![0]!["id"]!.GetValue<string>();
        var request = (await guest.GetJsonAsync("/api/requests")).Json!["requests"]![0]!["id"]!.GetValue<string>();

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, $"/api/blogs/{blog}/posts", new { title = "Mine", body_md = "x" }),
            (HttpMethod.Post, $"/api/requests/{request}/approve", null),
            (HttpMethod.Patch, $"/api/blogs/{blog}", new { title = "Renamed" }),
            (HttpMethod.Post, $"/api/blogs/{blog}/invitations", new { role = "writer" }),
            (HttpMethod.Post, "/api/auth/passkeys/begin", new { }),
            (HttpMethod.Post, "/api/auth/identities/discord", null),
            (HttpMethod.Post, "/api/auth/sign-out-everywhere", null),
        })
        {
            var r = await guest.SendAsync(method, path, csrf: true, body: body);
            Assert.True(r.Status == HttpStatusCode.Forbidden && r.Json!["code"]!.GetValue<string>() == "demo", $"{method} {path}: {r.Status}");
            Assert.StartsWith(Refusal, r.Json["detail"]!.GetValue<string>());
        }
        Assert.Equal("Notes from the Workshop", (await guest.GetJsonAsync($"/api/blogs/{blog}")).Json!["title"]!.GetValue<string>());
        Assert.Equal(2, (await guest.GetJsonAsync("/api/requests")).Json!["requests"]!.AsArray().Count);

        // Connecting an AI is a change too.
        using var scope = host.App.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpConnectionStore>();
        var consent = new McpConsentRequest("c", "Claude", new Uri("https://claude.ai/cb"), false, null, "https://blog.test/x", DateTimeOffset.UtcNow);
        var refused = await Assert.ThrowsAsync<McpConsentRejectedException>(async () =>
            await store.CreateAsync(consent, new McpOAuthGrant(Guid.NewGuid(), "a", "c"), AgentBlog.Demo.DemoBlog.AccountId.ToString(),
                new FormCollection(new() { ["blog"] = blog }), default));
        Assert.StartsWith(Refusal, refused.Message);

        Assert.Equal(HttpStatusCode.NoContent, (await guest.PostJsonAsync("/api/auth/sign-out", null, csrf: true)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetJsonAsync("/api/me")).Status);
    }

    [Fact]
    public async Task The_real_blog_its_owner_and_setup_stay_out_of_the_demo_s_reach()
    {
        await using var host = await WithDemoAsync();
        // A visitor tries the demo before the owner has even set up: the instance still has no owner.
        var guest = await GuestAsync(host);
        Assert.Contains("no owner yet", await host.Browser().Http.GetStringAsync("/sign-in"));

        var owner = host.Browser();
        await owner.SetupAsync(host.SetupToken, "discord", Lenka);
        var me = (await owner.GetJsonAsync("/api/me")).Json!;
        Assert.Equal("Lenka", me["account"]!["display_name"]!.GetValue<string>());
        Assert.NotEqual(AgentBlog.Demo.DemoBlog.AccountId.ToString(), me["account"]!["id"]!.GetValue<string>());
        var real = Assert.Single(me["blogs"]!.AsArray())!["id"]!.GetValue<string>();
        Assert.False(me["demo"]!.GetValue<bool>());

        // The guest can't see the real blog, and the public site is the real blog's, never the demo's.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetJsonAsync($"/api/blogs/{real}")).Status);
        Assert.Single((await guest.GetJsonAsync("/api/me")).Json!["blogs"]!.AsArray());
        var home = await host.Browser().Http.GetStringAsync("/");
        Assert.DoesNotContain("Notes from the Workshop", home);

        // Recovery finds the real owner, never the guest.
        await using var recovered = host.Restart("setup-" + new string('3', 40), b => b.UseSetting("Blog:Demo:Enabled", "true"));
        var back = recovered.Browser();
        await back.SetupAsync(recovered.SetupToken, "google", new StubAccount("500000000000000002", "Lenka again", null));
        Assert.Equal(me["account"]!["id"]!.GetValue<string>(), (await back.GetJsonAsync("/api/me")).Json!["account"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Turning_the_demo_off_ends_its_sessions()
    {
        await using var host = await WithDemoAsync();
        var guest = await GuestAsync(host);
        await using var off = host.Restart(host.SetupToken, b => b.UseSetting("Blog:Demo:Enabled", "false"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await off.Browser(guest).GetJsonAsync("/api/me")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await off.Browser().Http.GetAsync("/demo")).StatusCode);
    }
}
