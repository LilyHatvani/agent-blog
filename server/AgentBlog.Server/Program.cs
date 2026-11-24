using System.Reflection;
using AgentBlog.Announce;
using AgentBlog.Auth;
using AgentBlog.Blogs;
using AgentBlog.Connections;
using AgentBlog.Database;
using AgentBlog.Feedback;
using AgentBlog.Mcp;
using AgentBlog.Media;
using AgentBlog.Notify;
using AgentBlog.Posts;
using AgentBlog.Public;
using AgentBlog.Secrets;
using AgentBlog.Storage;
using AgentKit.Fetch;
using AgentKit.McpOAuth;
using AgentKit.Secrets;
using AgentKit.Social;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// A platform that sets PORT (Railway) decides the port; otherwise ASPNETCORE_HTTP_PORTS (8080 in the image) does.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// The database is required: the schema is migrated before the first request is served.
var connections = ConnectionSettings.Resolve(builder.Configuration);
builder.Services.AddSingleton(connections);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connections.App));
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddBlogSignIn(builder.Configuration, connections);
builder.Services.AddBlogOAuth(builder.Configuration, connections);
builder.Services.AddAuthorization();
builder.Services.AddSingleton<PostService>();
builder.Services.AddSingleton<Undo>();
builder.Services.AddSingleton<AgentBlog.Blogs.BlogRequests>();
builder.Services.AddSingleton<Taxonomy>();
builder.Services.AddSingleton<PostChecker>();
builder.Services.AddSingleton<Bylines>();
builder.Services.AddSingleton<OwnerFeedback>();
builder.Services.AddSingleton<Scheduler>();
builder.Services.AddHostedService<Scheduler.Worker>();
builder.Services.AddSingleton<Theme>();
builder.Services.AddSingleton<AgentBlog.Public.Cards.LinkCards>();
// Telling search engines (IndexNow) and feed readers (WebSub) when a post changes (docs/specs/social.md § Found).
builder.Services.AddSingleton<SearchPing>();
builder.Services.AddHostedService<SearchPing.Worker>();
builder.Services.AddHttpClient(SearchPing.HttpClientName);
// Media: the bucket (off until Blog__Bucket__* is set) and the guarded client that fetches pictures from URLs.
builder.Services.AddSingleton(BucketSettings.From(builder.Configuration));
builder.Services.AddSingleton<Bucket>();
builder.Services.AddHttpClient(Bucket.HttpClientName);
builder.Services.AddHttpClient(SafeFetch.HttpClientName).ConfigurePrimaryHttpMessageHandler(SafeFetch.CreateHandler);
builder.Services.AddSingleton<MediaService>();
// Bringing a blog over from Echo of Dreams (docs/specs/import.md): jobs the owner starts, run in the background.
builder.Services.AddSingleton<AgentBlog.Import.ImportJobs>();
builder.Services.AddSingleton<AgentBlog.Import.EodImporter>();
builder.Services.AddSingleton<AgentBlog.Import.ImportWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentBlog.Import.ImportWorker>());
// The import's source is an address the owner names, so its client reaches only public addresses on 443 and follows
// no redirects: a source that answers with a redirect is a failure, not a way into the network the server sits on.
builder.Services.AddHttpClient(AgentBlog.Import.ImportJobs.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => GuardedHttp.CreateHandler(new GuardedHttpOptions { Ports = [443], FollowRedirects = false }));
// Pictures through Cloudflare, when the owner connects a zone (decision 0009): the link and the purge drain.
builder.Services.AddSingleton<CdnLinks>();
builder.Services.AddSingleton<AgentBlog.Export.BlogExport>();
builder.Services.AddHttpClient(CdnLinks.HttpClientName);
// Secrets at rest (off until Blog__Secrets__MasterKey is set) and notifications: the owner's channels, the worker that
// sends what the event log says, and its client, which reaches only public addresses (Blog__Notify__AllowPrivate for a
// self-hoster's own network, such as a local ntfy) and follows no redirects.
builder.Services.AddSingleton(new Sealer(MasterKeys.From(builder.Configuration, BlogSecrets.Options)));
builder.Services.AddSingleton<ChannelStore>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddHostedService<Notifier.Worker>();
builder.Services.AddHttpClient(Notifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(() =>
    builder.Configuration.GetValue("Blog:Notify:AllowPrivate", false)
        ? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }
        : GuardedHttp.CreateHandler(new GuardedHttpOptions { FollowRedirects = false }));
// Announcing new posts (docs/specs/announce.md): the owner's accounts, the worker that makes and sends announcements,
// and AgentKit.Social's channels, whose clients reach only public addresses on 443.
builder.Services.AddSocialChannels();
builder.Services.AddSingleton<SocialAccounts>();
builder.Services.AddSingleton<Announcer>();
builder.Services.AddHostedService<Announcer.Worker>();
// Subscribing by email (docs/specs/subscribe.md): readers, the outgoing queue, its worker, and the client for mail APIs.
builder.Services.AddSingleton<AgentBlog.Mail.Subscribers>();
builder.Services.AddSingleton<AgentBlog.Mail.SubscribeLimit>();
builder.Services.AddSingleton<AgentBlog.Mail.MailQueue>();
builder.Services.AddHostedService<AgentBlog.Mail.MailQueue.Worker>();
builder.Services.AddHttpClient(AgentBlog.Mail.MailQueue.HttpClientName).ConfigurePrimaryHttpMessageHandler(() =>
    GuardedHttp.CreateHandler(new GuardedHttpOptions { Ports = [443], FollowRedirects = false }));
builder.Services.AddHttpClient(SocialAccounts.ProbeHttpClientName).ConfigurePrimaryHttpMessageHandler(() =>
    GuardedHttp.CreateHandler(new GuardedHttpOptions { Ports = [443], FollowRedirects = false }));
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "Agent Blog", Version = BuildInfo.Version };
        options.ServerInstructions = BlogTools.Instructions;
    })
    // Stateless: any replica serves any request, and who is calling comes from the bearer on every request.
    .WithHttpTransport(t => t.Stateless = true)
    .WithTools<BlogTools>();

var app = builder.Build();

await new Migrator(connections.Migrations, connections.AppRole, connections.AppRolePassword).RunAsync();

app.UseMovedAddresses(); // an old address (Blog__PreviousUrls) answers a page with a 301 to the new one (docs/specs/address-move.md)
app.UseMcpOAuth();      // issuer pinning and client metadata documents, before anything reads the request
app.UseAuthentication(); // OpenIddict's token, discovery and validation handlers
app.UseRefusalReasons(); // a 401 at /mcp says why: revoked, expired, or unknown
app.UseMcpOAuthGate();  // /mcp: bearer → the connection as HttpContext.User, or 401 with resource_metadata
app.UseRouting();
app.UseRateLimiter();
app.UseAuthorization();
app.MapBlogSignIn();
app.MapConnectionsApi();
app.MapPostsApi();
app.MapMediaApi();
app.MapBlogSettingsApi();
AgentBlog.Blogs.Embeds.MapEmbedsApi(app);
app.MapFeedbackApi();
app.MapNotifyApi();
app.MapAnnounceApi();
app.MapMembersApi();
AgentBlog.Import.ImportApi.MapImportApi(app);
AgentBlog.Export.BlogExportApi.MapBlogExport(app);
AgentBlog.Blogs.BlogRequestsApi.MapBlogRequestsApi(app);
app.MapOwnerPages();
app.MapAdminApp();
app.MapMcpOAuth();      // discovery and the consent page
app.MapMcp("/mcp");
app.MapPublicSite(); // last: its fallback answers only what nothing above claimed

app.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok")));
app.MapGet("/version", () => Results.Ok(new VersionResponse(BuildInfo.Version)));

app.Run();

internal sealed record HealthResponse(string Status);

internal sealed record VersionResponse(string Version);

internal static class BuildInfo
{
    /// <summary>The informational version Directory.Build.props stamps (0.1.N, or 0.1.0-dev locally, plus +sha).</summary>
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}

/// <summary>Visible to WebApplicationFactory in the tests.</summary>
public partial class Program;
