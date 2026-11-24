using System.Collections.Concurrent;
using System.Diagnostics;
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
/// <para>
/// Every database is dropped, whatever happens: on dispose; when the host fails to start (a migration that doesn't
/// apply), before the failure is thrown; and, for a host whose test never disposed it (a helper that threw after
/// starting it), when the run ends (the assembly fixture drops every host still alive). A run that was killed can't drop
/// anything, so its names carry its process id and the next run drops the databases of runs no longer running.
/// </para>
/// </summary>
public sealed class BlogApp : IAsyncLifetime
{
    /// <summary>The shared host (the assembly fixture).</summary>
    public BlogApp() => _shared = true;

    /// <summary>A host of its own, on its own database, with extra settings (the sign-in tests: setup binds one owner for good).</summary>
    internal BlogApp(Action<IWebHostBuilder> configure) => _configure = configure;

    private readonly Action<IWebHostBuilder>? _configure;
    private readonly bool _shared;
    private int _dropped;

    /// <summary>Every host started in this run and not yet dropped.</summary>
    private static readonly ConcurrentDictionary<BlogApp, byte> Live = new();

    /// <summary>This run's names: <c>agentblog_test_p{pid}_{8 hex}</c>, so a later run can tell a dead run's leftovers.</summary>
    internal const string Prefix = "agentblog_test_p";
    private static readonly string RunPrefix = $"{Prefix}{Environment.ProcessId}_";

    private readonly string _suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    internal string Database => $"{RunPrefix}{_suffix}";
    private string OwnerRole => $"{Database}_owner";
    public string AppRole => $"{Database}_app";
    private readonly string _ownerPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private readonly string _appPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    internal static string AdminConnection =>
        Environment.GetEnvironmentVariable("BLOG_TEST_ADMIN_CONNECTION")
        ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres";

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public string AppConnectionString => Cs(AppRole, _appPassword);
    public string OwnerConnectionString => Cs(OwnerRole, _ownerPassword);

    private string Cs(string user, string password) =>
        new NpgsqlConnectionStringBuilder(AdminConnection) { Database = Database, Username = user, Password = password }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        if (_shared)
        {
            // Tidying after earlier runs never fails this one.
            try { await DropDeadRunsAsync(); }
            catch (Exception) { /* the server is checked again by this host's own start */ }
        }
        Live[this] = 0;
        try
        {
            await StartAsync();
        }
        catch
        {
            try { await DropAsync(); }
            catch (Exception) { /* the start's own failure is the one to report */ }
            throw;
        }
    }

    private async Task StartAsync()
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
            b.UseSetting("Blog:Mail:IntervalSeconds", "0"); // and each mail pass (MailQueue)
            _configure?.Invoke(b);
        });
        _ = Factory.Server; // starts the host: the migrator runs here
    }

    public async ValueTask DisposeAsync()
    {
        await DropAsync();
        // The run is over: a host a test never disposed is dropped now, not left behind.
        if (_shared)
            foreach (var orphan in Live.Keys) await orphan.DropAsync();
    }

    /// <summary>Stops the host and drops its database and roles, once.</summary>
    private async Task DropAsync()
    {
        if (Interlocked.Exchange(ref _dropped, 1) == 1) return;
        try
        {
            if (Factory is not null) await Factory.DisposeAsync();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(AdminConnection);
            await admin.OpenAsync();
            await Exec(admin, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
            await Exec(admin, $"DROP ROLE IF EXISTS {AppRole}");
            await Exec(admin, $"DROP ROLE IF EXISTS {OwnerRole}");
            Live.TryRemove(this, out _);
        }
    }

    /// <summary>
    /// The migrator's own test databases (MigratorDatabaseTests: one it makes, one it upgrades) carry the run's process id
    /// too, <c>blog_made_p{pid}_{8 hex}</c> and <c>blog_up_p{pid}_{8 hex}</c>, so the same sweep drops a dead run's.
    /// </summary>
    internal const string MadePrefix = "blog_made_p";
    internal const string UpPrefix = "blog_up_p";

    /// <summary>A database name of this run's own in one of the swept families: <c>{prefix}{pid}_{8 hex}</c>.</summary>
    internal static string RunDatabaseName(string prefix) =>
        $"{prefix}{Environment.ProcessId}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";

    /// <summary>
    /// Every database name the sweep may drop, the process id that made it in <c>pid</c>: the host's, the migrator tests'
    /// two, and the migrator tests' names from before they carried one (<c>blog_made_</c> or <c>blog_up_</c> and 12 hex,
    /// no process id). Nothing makes those any more, so one still there is a leftover; the drop never forces, so the one
    /// a run of older code is connected to at that moment stays. Anchored at both ends: a name that merely resembles one
    /// of these (a longer suffix, a prefix in front, another family) is never touched.
    /// </summary>
    private const string SweptDatabase = $"^(?:(?:{Prefix}|{MadePrefix}|{UpPrefix})(?<pid>[0-9]+)_[0-9a-f]{{8}}|blog_(?:made|up)_[0-9a-f]{{12}})$";

    /// <summary>The host's two roles per database: <c>agentblog_test_p{pid}_{8 hex}_owner</c> and <c>_app</c>.</summary>
    private const string SweptRole = $"^{Prefix}(?<pid>[0-9]+)_[0-9a-f]{{8}}_(?:owner|app)$";

    /// <summary>
    /// The databases and roles of earlier runs that ended without dropping them (killed, or out of memory): their process
    /// is gone. A run still going (another clone's, at the same time) keeps its own.
    /// </summary>
    internal static async Task DropDeadRunsAsync()
    {
        await using var admin = new NpgsqlConnection(AdminConnection);
        await admin.OpenAsync();
        var names = new List<(string Name, bool IsRole)>();
        // Postgres and .NET read these patterns alike (anchors, classes, counts); the named group is .NET's alone, so the
        // query is handed the pattern without it.
        await using (var cmd = new NpgsqlCommand("""
            SELECT datname, false FROM pg_database WHERE datname ~ @database
            UNION ALL SELECT rolname, true FROM pg_roles WHERE rolname ~ @role
            """, admin))
        {
            cmd.Parameters.AddWithValue("database", SweptDatabase.Replace("?<pid>", "", StringComparison.Ordinal));
            cmd.Parameters.AddWithValue("role", SweptRole.Replace("?<pid>", "", StringComparison.Ordinal));
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add((reader.GetString(0), reader.GetBoolean(1)));
        }
        // Databases first: a role can't be dropped while it owns one.
        foreach (var (name, isRole) in names.OrderBy(n => n.IsRole))
        {
            // Checked again here, in .NET: only a whole-name match is dropped, whatever the server's regex made of it.
            var match = System.Text.RegularExpressions.Regex.Match(name, isRole ? SweptRole : SweptDatabase);
            if (!match.Success) continue;
            if (match.Groups["pid"].Success && (!int.TryParse(match.Groups["pid"].ValueSpan, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var pid) || IsRunning(pid))) continue; // this run included
            // Best effort, never FORCE: a database something is still connected to (a run on another machine that
            // shares this server, whose process id means nothing here) refuses the drop and is left alone.
            try { await Exec(admin, isRole ? $"DROP ROLE IF EXISTS {name}" : $"DROP DATABASE IF EXISTS {name}"); }
            catch (PostgresException) { /* in use or still owning something: the next run tries again */ }
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid); // throws when no process has the id
            return true; // it exists (asking whether it exited is refused for a protected process)
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
