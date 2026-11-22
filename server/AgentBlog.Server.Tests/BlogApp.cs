using System.Security.Cryptography;
using AgentBlog.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

[assembly: AssemblyFixture(typeof(AgentBlog.Server.Tests.BlogApp))]

namespace AgentBlog.Server.Tests;

/// <summary>
/// The running server against a real Postgres: a throwaway database and two throwaway roles, created through the
/// local superuser (BLOG_TEST_ADMIN_CONNECTION). The schema is migrated as the owner role and the app serves as a
/// separate role, so the grants are what is tested, not bypassed (Relay's RelayApp pattern).
/// </summary>
public sealed class BlogApp : IAsyncLifetime
{
    /// <summary>The shared host (the assembly fixture).</summary>
    public BlogApp() { }

    /// <summary>A host of its own, on its own database, with extra settings (the sign-in tests: setup binds one owner for good).</summary>
    internal BlogApp(Action<IWebHostBuilder> configure) => _configure = configure;

    private readonly Action<IWebHostBuilder>? _configure;

    private readonly string _suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    private string Database => $"agentblog_test_{_suffix}";
    private string OwnerRole => $"agentblog_test_{_suffix}_owner";
    public string AppRole => $"agentblog_test_{_suffix}_app";
    private readonly string _ownerPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private readonly string _appPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string AdminConnection =>
        Environment.GetEnvironmentVariable("BLOG_TEST_ADMIN_CONNECTION")
        ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres";

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public string AppConnectionString => Cs(AppRole, _appPassword);
    public string OwnerConnectionString => Cs(OwnerRole, _ownerPassword);

    private string Cs(string user, string password) =>
        new NpgsqlConnectionStringBuilder(AdminConnection) { Database = Database, Username = user, Password = password }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(AdminConnection))
        {
            await admin.OpenAsync();
            await Exec(admin, $"CREATE ROLE {OwnerRole} LOGIN PASSWORD '{_ownerPassword}'");
            await Exec(admin, $"CREATE ROLE {AppRole} LOGIN PASSWORD '{_appPassword}'");
            await Exec(admin, $"CREATE DATABASE {Database} OWNER {OwnerRole}");
        }
        await using (var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(AdminConnection) { Database = Database }.ConnectionString))
        {
            await db.OpenAsync();
            await Exec(db, $"ALTER SCHEMA public OWNER TO {OwnerRole}");
        }

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:BlogMigrations", OwnerConnectionString);
            b.UseSetting("ConnectionStrings:Blog", AppConnectionString);
            b.UseSetting("Blog:AppRole", AppRole);
            b.UseSetting("Blog:Scheduler:IntervalSeconds", "0"); // the tests run each pass themselves (Scheduler.RunOnceAsync)
            b.UseSetting("Blog:SearchPing:IntervalSeconds", "0"); // and each search ping (SearchPing.RunOnceAsync)
            b.UseSetting("Blog:Announce:IntervalSeconds", "0"); // and each announce pass (Announcer)
            _configure?.Invoke(b);
        });
        _ = Factory.Server; // starts the host: the migrator runs here
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(AdminConnection);
        await admin.OpenAsync();
        await Exec(admin, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
        await Exec(admin, $"DROP ROLE IF EXISTS {AppRole}");
        await Exec(admin, $"DROP ROLE IF EXISTS {OwnerRole}");
    }

    /// <summary>A connection as the app role: what the server's own queries can and can't do.</summary>
    public async Task<NpgsqlConnection> OpenAsAppAsync()
    {
        var c = new NpgsqlConnection(AppConnectionString);
        await c.OpenAsync(TestContext.Current.CancellationToken);
        return c;
    }

    public async Task<NpgsqlConnection> OpenAsOwnerAsync()
    {
        var c = new NpgsqlConnection(OwnerConnectionString);
        await c.OpenAsync(TestContext.Current.CancellationToken);
        return c;
    }

    public Migrator NewMigrator() => new(OwnerConnectionString, AppRole);

    internal static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
