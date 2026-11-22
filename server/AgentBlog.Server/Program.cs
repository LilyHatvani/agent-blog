using System.Reflection;
using AgentBlog.Database;
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

var app = builder.Build();

await new Migrator(connections.Migrations, connections.AppRole, connections.AppRolePassword).RunAsync();

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
