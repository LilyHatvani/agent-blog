using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace AgentBlog.Database;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

/// <summary>One row of the append-only <c>event</c> table.</summary>
/// <param name="BlogId">The blog it belongs to; null only for events about accounts.</param>
/// <param name="Actor">An actor value (docs/specs/data-model.md § Actors), see <see cref="Actors"/>.</param>
public sealed record EventRow(Guid? BlogId, string TargetType, Guid TargetId, string Kind, string Actor, object? Payload);

/// <summary>The three actor forms, written one way everywhere.</summary>
public static class Actors
{
    public const string System = "system";
    public static string Account(Guid id) => $"account:{id:D}";
    public static string Connection(Guid id) => $"connection:{id:D}";
}

/// <summary>
/// The app's connection pool, plus the two shapes a write takes: a transaction that holds the event lock
/// (<see cref="WriteAsync{T}"/>, for anything that logs an event), or a consistent read (<see cref="ReadAsync{T}"/>).
/// Lifted from Relay's <c>Database/Db.cs</c> (docs/REUSE.md).
/// </summary>
public sealed class Db(NpgsqlDataSource dataSource)
{
    static Db()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction holding the event lock. Every transaction that writes an event
    /// takes it first, so event ids commit in order and a reader's cursor never skips one (Relay's guarantee).
    /// </summary>
    public async Task<T> WriteAsync<T>(Func<Tx, Task<T>> work, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var cmd = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({EventLockKey})", conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);
        var result = await work(new Tx(conn, tx, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>A consistent read: one REPEATABLE READ snapshot for the whole composition.</summary>
    public async Task<T> ReadAsync<T>(Func<Tx, Task<T>> work, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var result = await work(new Tx(conn, tx, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    private const long EventLockKey = 0x41474E54_45565453; // "AGNTEVTS"
}

public sealed record Tx(NpgsqlConnection Conn, NpgsqlTransaction Transaction, CancellationToken Ct)
{
    public Task<IEnumerable<T>> Query<T>(string sql, object? args = null) =>
        Conn.QueryAsync<T>(new CommandDefinition(sql, args, Transaction, cancellationToken: Ct));

    public Task<T?> One<T>(string sql, object? args = null) =>
        Conn.QuerySingleOrDefaultAsync<T>(new CommandDefinition(sql, args, Transaction, cancellationToken: Ct));

    public Task<int> Exec(string sql, object? args = null) =>
        Conn.ExecuteAsync(new CommandDefinition(sql, args, Transaction, cancellationToken: Ct));

    /// <summary>Appends one event. Only valid inside <see cref="Db.WriteAsync{T}"/>, which holds the event lock.</summary>
    public async Task<long> Event(EventRow e, DateTimeOffset at)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO event (blog_id, target_type, target_id, kind, actor, payload, at)
            VALUES (@blog, @tt, @tid, @kind, @actor, @payload, @at)
            RETURNING id
            """, Conn, Transaction);
        cmd.Parameters.AddWithValue("blog", (object?)e.BlogId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tt", e.TargetType);
        cmd.Parameters.AddWithValue("tid", e.TargetId);
        cmd.Parameters.AddWithValue("kind", e.Kind);
        cmd.Parameters.AddWithValue("actor", e.Actor);
        cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(e.Payload ?? new { }, Json.Options),
        });
        cmd.Parameters.AddWithValue("at", at);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
