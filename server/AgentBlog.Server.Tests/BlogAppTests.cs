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
}
