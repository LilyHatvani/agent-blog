using AgentBlog.Database;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// A blog shares a Postgres server in a database of its own (EoD's blog beside blog-ai's): pointed at a database the
/// server doesn't have, the migrator makes it, migrates it, and a second start changes nothing.
/// </summary>
public sealed class MigratorDatabaseTests
{
    private static string Admin => Environment.GetEnvironmentVariable("BLOG_TEST_ADMIN_CONNECTION")
        ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres";

    [Fact]
    public async Task The_migrator_makes_its_database_when_the_server_has_none_by_that_name()
    {
        var name = "blog_made_" + Guid.NewGuid().ToString("N")[..12];
        var target = new NpgsqlConnectionStringBuilder(Admin) { Database = name, Pooling = false }.ConnectionString;
        try
        {
            await new Migrator(target, appRole: null).RunAsync(TestContext.Current.CancellationToken);
            await new Migrator(target, appRole: null).RunAsync(TestContext.Current.CancellationToken);

            await using var conn = new NpgsqlConnection(target);
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM schema_migrations", conn);
            Assert.Equal((long)Migrator.LoadMigrations().Count(), await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Admin) { Pooling = false }.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
