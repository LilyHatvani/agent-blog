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

    /// <summary>
    /// One strictly increasing sequence: two files sharing a number leave the apply order to the alphabet, and the
    /// next migration written against either branch collides. The Migrator applies files in ordinal name order, so
    /// the numbers themselves must carry the order.
    /// </summary>
    [Fact]
    public void Migration_numbers_are_strictly_increasing()
    {
        var names = Migrator.LoadMigrations().Select(m => m.Name).ToList();
        var numbers = names.Select(n => int.Parse(n[..4])).ToList();
        for (var i = 1; i < numbers.Count; i++)
            Assert.True(numbers[i] > numbers[i - 1], $"{names[i - 1]} then {names[i]}: migration numbers must strictly increase.");
    }

    [Fact]
    public async Task The_migrator_makes_its_database_when_the_server_has_none_by_that_name()
    {
        var name = BlogApp.RunDatabaseName(BlogApp.MadePrefix); // a killed run's is dropped by the next (BlogApp.DropDeadRunsAsync)
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

    /// <summary>
    /// A blog already running at e233657 (migrations 0001–0024) with data in it upgrades to now: 0029 removes an old
    /// address that would redirect off the blog ('/\x' reads as '//x') before tightening the CHECK, and 0031's CHECK on
    /// a section mark is added; everything else the blog had is still there.
    /// </summary>
    [Fact]
    public async Task A_blog_with_data_from_0024_upgrades_to_head()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = BlogApp.RunDatabaseName(BlogApp.UpPrefix);
        var target = new NpgsqlConnectionStringBuilder(Admin) { Database = name, Pooling = false }.ConnectionString;
        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Admin) { Pooling = false }.ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync(ct);
        }
        try
        {
            var blog = Guid.NewGuid();
            await using (var conn = new NpgsqlConnection(target))
            {
                await conn.OpenAsync(ct);
                // What the Migrator had applied by e233657, recorded as it records them.
                await Exec(conn, "CREATE TABLE schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", ct);
                var old = Migrator.LoadMigrations().Where(m => string.CompareOrdinal(m.Name, "0025") < 0).ToList();
                Assert.Equal(24, old.Count);
                Assert.Equal("0024_event_imported.sql", old[^1].Name);
                foreach (var (migration, sql) in old)
                {
                    await Exec(conn, sql, ct);
                    await using var mark = new NpgsqlCommand("INSERT INTO schema_migrations (name) VALUES (@n)", conn);
                    mark.Parameters.AddWithValue("n", migration);
                    await mark.ExecuteNonQueryAsync(ct);
                }

                // Its data: a blog, a category, a tag, and old addresses, one of them '/\x', which 0018's CHECK let in.
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO blog (id, slug, title, created_at) VALUES (@blog, 'my-blog', 'My blog', now());
                    INSERT INTO term (id, blog_id, kind, slug, name, created_by, created_at) VALUES
                        (gen_random_uuid(), @blog, 'category', 'guides', 'Guides', 'system', now()),
                        (gen_random_uuid(), @blog, 'tag', 'travel', 'Travel', 'system', now());
                    INSERT INTO old_address (blog_id, path, target) VALUES
                        (@blog, '/2024/rain', '/posts/rain'),
                        (@blog, '/away', '/\x');
                    """, conn);
                seed.Parameters.AddWithValue("blog", blog);
                await seed.ExecuteNonQueryAsync(ct);
            }

            await new Migrator(target, appRole: null).RunAsync(ct);

            await using var after = new NpgsqlConnection(target);
            await after.OpenAsync(ct);
            Assert.Equal((long)Migrator.LoadMigrations().Count(), await Scalar<long>(after, "SELECT count(*) FROM schema_migrations", ct));
            // The address that would leave the blog is gone; the good one, the blog and its terms are as they were.
            Assert.Equal("/2024/rain>/posts/rain", await Scalar<string>(after, "SELECT string_agg(path || '>' || target, ',') FROM old_address", ct));
            Assert.Equal("my-blog", await Scalar<string>(after, "SELECT slug FROM blog", ct));
            Assert.Equal("category:guides,tag:travel",
                await Scalar<string>(after, "SELECT string_agg(kind || ':' || slug, ',' ORDER BY kind) FROM term", ct));
            Assert.Null(await Scalar<string?>(after, "SELECT section::text FROM term WHERE slug = 'guides'", ct));

            // Both CHECKs are in place and refuse what they exist to refuse.
            Assert.Equal(1L, await Scalar<long>(after, "SELECT count(*) FROM pg_constraint WHERE conname = 'term_section_object_check'", ct));
            var scalar = await Assert.ThrowsAsync<PostgresException>(() => Exec(after, """UPDATE term SET section = '"x"' WHERE slug = 'guides'""", ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, scalar.SqlState);
            Assert.Equal("term_section_object_check", scalar.ConstraintName);
            var backslash = await Assert.ThrowsAsync<PostgresException>(() => Exec(after,
                $"INSERT INTO old_address (blog_id, path, target) VALUES ('{blog}', '/again', '/\\x')", ct));
            Assert.Equal("old_address_target_check", backslash.ConstraintName);
            // A section mark that is an object still goes in.
            await Exec(after, """UPDATE term SET section = '{"title": "Guides"}' WHERE slug = 'guides'""", ct);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Admin) { Pooling = false }.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is DBNull or null ? default! : (T)value;
    }
}
