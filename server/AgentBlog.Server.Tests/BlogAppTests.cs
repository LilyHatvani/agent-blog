using System.Diagnostics;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// The test host leaves nothing behind on the shared Postgres server: a host that fails to start drops its database and
/// roles before the failure reaches the test, and a run that was killed has its leftovers dropped by the next run, while
/// a run still going keeps its own.
/// </summary>
public sealed class BlogAppTests
{
    private static async Task<bool> ExistsAsync(string sql, string name)
    {
        await using var admin = new NpgsqlConnection(BlogApp.AdminConnection);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        cmd.Parameters.AddWithValue("n", name);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    private static Task<bool> DatabaseExistsAsync(string name) => ExistsAsync("SELECT count(*) FROM pg_database WHERE datname = @n", name);
    private static Task<bool> RoleExistsAsync(string name) => ExistsAsync("SELECT count(*) FROM pg_roles WHERE rolname = @n", name);

    [Fact]
    public async Task A_host_that_fails_to_start_drops_its_database_and_roles()
    {
        // The host's settings throw while it starts, after its database and roles were made (as a migration that
        // doesn't apply would).
        var app = new BlogApp(_ => throw new InvalidOperationException("the host can't start"));
        await Assert.ThrowsAnyAsync<Exception>(async () => await app.InitializeAsync());

        Assert.False(await DatabaseExistsAsync(app.Database));
        Assert.False(await RoleExistsAsync(app.AppRole));
        Assert.False(await RoleExistsAsync(app.Database + "_owner"));
    }

    [Fact]
    public async Task A_killed_runs_leftovers_are_dropped_and_a_live_runs_are_kept()
    {
        // A process that has ended (no such id), and one still running (this one: a database named after another live
        // process would outlive a run killed mid-test).
        var dead = Enumerable.Range(0, 1000).Select(i => 4_000_000 - i * 4).First(pid => !Running(pid));
        var live = Environment.ProcessId;
        // Random suffixes, so two clones running this at once don't collide.
        var deadDb = $"{BlogApp.Prefix}{dead}_{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4))}";
        var liveDb = $"{BlogApp.Prefix}{live}_{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4))}";
        await using (var admin = new NpgsqlConnection(BlogApp.AdminConnection))
        {
            await admin.OpenAsync();
            foreach (var db in new[] { deadDb, liveDb })
            {
                await BlogApp.Exec(admin, $"CREATE ROLE {db}_owner");
                await BlogApp.Exec(admin, $"CREATE ROLE {db}_app");
                await BlogApp.Exec(admin, $"CREATE DATABASE {db} OWNER {db}_owner");
            }
        }
        try
        {
            await BlogApp.DropDeadRunsAsync();

            Assert.False(await DatabaseExistsAsync(deadDb));
            Assert.False(await RoleExistsAsync(deadDb + "_owner"));
            Assert.False(await RoleExistsAsync(deadDb + "_app"));
            Assert.True(await DatabaseExistsAsync(liveDb));
            Assert.True(await RoleExistsAsync(liveDb + "_owner"));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(BlogApp.AdminConnection);
            await admin.OpenAsync();
            foreach (var db in new[] { deadDb, liveDb })
            {
                await BlogApp.Exec(admin, $"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
                await BlogApp.Exec(admin, $"DROP ROLE IF EXISTS {db}_app");
                await BlogApp.Exec(admin, $"DROP ROLE IF EXISTS {db}_owner");
            }
        }
    }

    private static bool Running(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid); // throws when no process has the id
            return true; // it exists (asking whether it exited is refused for a protected process)
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Hex(int bytes) => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes));

    [Fact]
    public async Task The_migrator_tests_databases_are_swept_like_the_hosts_and_names_that_only_resemble_them_survive()
    {
        var dead = Enumerable.Range(0, 1000).Select(i => 4_000_000 - i * 4).First(pid => !Running(pid));
        var live = Environment.ProcessId;
        var hex8 = Hex(4);
        // A dead run's databases in both migrator families, and the names those tests used before they carried a process
        // id (nothing makes those now): all dropped.
        string[] dropped =
        [
            $"{BlogApp.MadePrefix}{dead}_{Hex(4)}", $"{BlogApp.UpPrefix}{dead}_{Hex(4)}",
            $"blog_made_{Hex(6)}", $"blog_up_{Hex(6)}",
        ];
        // A live run's, kept; and decoys that look like a swept name but aren't one, kept whatever their process id.
        string[] kept =
        [
            $"{BlogApp.MadePrefix}{live}_{Hex(4)}", $"{BlogApp.UpPrefix}{live}_{Hex(4)}",
            $"{BlogApp.Prefix}{dead}_{hex8}x",          // a longer suffix
            $"{BlogApp.Prefix}{dead}_{hex8}0",          // nine hex digits
            $"x{BlogApp.Prefix}{dead}_{Hex(4)}",        // something in front
            $"my_{BlogApp.MadePrefix}{dead}_{Hex(4)}",
            $"{BlogApp.MadePrefix}{dead}_{Hex(4)}_copy",
            $"{BlogApp.UpPrefix}{dead}{Hex(4)}",        // no separator before the hex
            $"blog_made_p_{Hex(4)}",                    // no process id
            $"blog_madex_p{dead}_{Hex(4)}",             // another family
            $"blog_made_{Hex(5)}g",                     // ten hex digits, then a letter that isn't one
            $"blog_up_{Hex(6)}_keep",
            $"blog_made_{Hex(7)}",                      // fourteen hex digits
        ];
        // Roles: the host's two shapes are swept; a role that only resembles them is not.
        string[] keptRoles = [$"{BlogApp.Prefix}{dead}_{Hex(4)}_owners", $"{BlogApp.Prefix}{dead}_{Hex(4)}_admin", $"{BlogApp.Prefix}{dead}_{Hex(4)}"];
        await using (var admin = new NpgsqlConnection(BlogApp.AdminConnection))
        {
            await admin.OpenAsync();
            foreach (var db in dropped.Concat(kept)) await BlogApp.Exec(admin, $"CREATE DATABASE {db}");
            foreach (var role in keptRoles) await BlogApp.Exec(admin, $"CREATE ROLE {role}");
        }
        try
        {
            await BlogApp.DropDeadRunsAsync();

            foreach (var db in dropped) Assert.False(await DatabaseExistsAsync(db), $"{db} should have been dropped");
            foreach (var db in kept) Assert.True(await DatabaseExistsAsync(db), $"{db} should have been kept");
            foreach (var role in keptRoles) Assert.True(await RoleExistsAsync(role), $"{role} should have been kept");
        }
        finally
        {
            await using var admin = new NpgsqlConnection(BlogApp.AdminConnection);
            await admin.OpenAsync();
            foreach (var db in dropped.Concat(kept)) await BlogApp.Exec(admin, $"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
            foreach (var role in keptRoles) await BlogApp.Exec(admin, $"DROP ROLE IF EXISTS {role}");
        }
    }
}
