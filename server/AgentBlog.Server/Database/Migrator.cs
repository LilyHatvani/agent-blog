using AgentKit.McpOAuth;
using AgentKit.ProviderSignIn;
using Npgsql;

namespace AgentBlog.Database;

/// <summary>
/// Runs the numbered SQL files in <c>Database/Migrations</c> once each, in order, each in its own transaction, and
/// records them in <c>schema_migrations</c>. A session-level advisory lock keeps two replicas from migrating at once.
/// Then it grants the app role what it may do: no UPDATE, DELETE or TRUNCATE on <c>event</c> or <c>post_revision</c>
/// (docs/specs/data-model.md § Roles). Lifted from Relay's <c>Database/Migrator.cs</c> (docs/REUSE.md) until
/// <c>AgentKit.Postgres</c> is published (decision 0007).
/// </summary>
public sealed class Migrator(string connectionString, string? appRole, string? appRolePassword = null)
{
    private const long MigrationLockKey = 0x41474E54_424C4F47; // "AGNTBLOG"

    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenCreatingAsync(connectionString, ct);
        await Exec(conn, $"SELECT pg_advisory_lock({MigrationLockKey})", ct);
        try
        {
            await Exec(conn, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    name       text PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now())
                """, ct);

            var applied = new HashSet<string>();
            await using (var cmd = new NpgsqlCommand("SELECT name FROM schema_migrations", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) applied.Add(reader.GetString(0));

            foreach (var (name, sql) in LoadMigrations())
            {
                if (applied.Contains(name)) continue;
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(sql, conn, tx)) await cmd.ExecuteNonQueryAsync(ct);
                await using (var cmd = new NpgsqlCommand("INSERT INTO schema_migrations (name) VALUES (@n)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("n", name);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }

            // The packages own their tables: their scripts run here on every start, every statement IF NOT EXISTS.
            await Exec(conn, ProviderSignInSchema.ReadScript(), ct);
            await Exec(conn, McpOAuthSchema.ReadScript(), ct);

            if (!string.IsNullOrWhiteSpace(appRole))
            {
                if (!string.IsNullOrWhiteSpace(appRolePassword)) await EnsureRoleAsync(conn, appRole, appRolePassword, ct);
                await GrantAsync(conn, appRole, ct);
            }
        }
        finally
        {
            await Exec(conn, $"SELECT pg_advisory_unlock({MigrationLockKey})", ct);
        }
    }

    /// <summary>
    /// Opens the blog's database, making it first when the server has none by that name: a blog can share a Postgres
    /// server with others (another blog, other apps) in a database of its own, named in its connection, with nothing
    /// to create by hand. The database is made from the server's maintenance database (<c>postgres</c>, else
    /// <c>template1</c>) by the connection's own user, who then owns it. Two instances racing to make it both succeed.
    /// </summary>
    internal static async Task<NpgsqlConnection> OpenCreatingAsync(string connectionString, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            await conn.DisposeAsync();
        }

        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var name = target.Database ?? throw new InvalidOperationException("The connection names no database.");
        foreach (var maintenance in new[] { "postgres", "template1" })
        {
            var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = maintenance, Pooling = false };
            try
            {
                await using var c = new NpgsqlConnection(admin.ConnectionString);
                await c.OpenAsync(ct);
                try { await Exec(c, $"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(name)}", ct); }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.DuplicateDatabase) { }
                break;
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidCatalogName) { }
        }

        conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    internal static IEnumerable<(string Name, string Sql)> LoadMigrations()
    {
        var asm = typeof(Migrator).Assembly;
        foreach (var res in asm.GetManifestResourceNames()
                     .Where(n => n.StartsWith("migrations/", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(stream);
            yield return (res["migrations/".Length..], reader.ReadToEnd());
        }
    }

    /// <summary>
    /// Hosts that hand out one superuser (Railway) get the app role created here, so the app still serves as a role
    /// without UPDATE or DELETE on the append-only tables. An existing role is left as it is: rotating its password is
    /// the owner's step, not something a deploy does behind their back.
    /// </summary>
    private static async Task EnsureRoleAsync(NpgsqlConnection conn, string role, string password, CancellationToken ct)
    {
        await using (var check = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname = @r", conn))
        {
            check.Parameters.AddWithValue("r", role);
            if (await check.ExecuteScalarAsync(ct) is not null) return;
        }
        var r = new NpgsqlCommandBuilder().QuoteIdentifier(role);
        // CREATE ROLE takes no bind parameters; the password is a literal with quotes doubled.
        var literal = "'" + password.Replace("'", "''") + "'";
        await Exec(conn, $"CREATE ROLE {r} LOGIN PASSWORD {literal}", ct);
    }

    private static async Task GrantAsync(NpgsqlConnection conn, string role, CancellationToken ct)
    {
        var r = new NpgsqlCommandBuilder().QuoteIdentifier(role);
        await Exec(conn, $"""
            GRANT USAGE ON SCHEMA public TO {r};
            GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO {r};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {r};
            REVOKE UPDATE, DELETE, TRUNCATE ON event, post_revision FROM {r};
            REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON schema_migrations FROM {r};
            GRANT DELETE ON provider_signin_state, account_session, account_identity, channel_secret, blog_secret, media_purge, social_secret, blog_member, passkey_ceremony, account_passkey TO {r};
            """, ct);
        // AgentKit.McpOAuth needs full rights on its own tables (its README § Schema).
        foreach (var table in McpOAuthSchema.Tables)
            await Exec(conn, $"GRANT SELECT, INSERT, UPDATE, DELETE ON {table} TO {r}", ct); // the package lists them already quoted
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
