using System.Reflection;
using AgentBlog.Auth;
using AgentBlog.Blogs;
using AgentBlog.Connections;
using AgentBlog.Database;
using AgentBlog.Mcp;
using AgentBlog.Media;
using AgentBlog.Posts;
using AgentBlog.Public;
using AgentBlog.Storage;
using AgentKit.McpOAuth;
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
builder.Services.AddSingleton<Taxonomy>();
builder.Services.AddSingleton<Scheduler>();
builder.Services.AddHostedService<Scheduler.Worker>();
builder.Services.AddSingleton<Theme>();
// Media: the bucket (off until Blog__Bucket__* is set) and the guarded client that fetches pictures from URLs.
builder.Services.AddSingleton(BucketSettings.From(builder.Configuration));
builder.Services.AddSingleton<Bucket>();
builder.Services.AddHttpClient(Bucket.HttpClientName);
builder.Services.AddHttpClient(SafeFetch.HttpClientName).ConfigurePrimaryHttpMessageHandler(SafeFetch.CreateHandler);
builder.Services.AddSingleton<MediaService>();
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
