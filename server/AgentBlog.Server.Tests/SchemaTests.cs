using AgentBlog.Database;
using Dapper;
using Npgsql;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// The schema's own guarantees (docs/specs/data-model.md), exercised as the app role the server runs as: what it may
/// write, what it may never rewrite, and that one blog's rows can't point at another's.
/// </summary>
public sealed class SchemaTests(BlogApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrating_again_changes_nothing()
    {
        await app.NewMigrator().RunAsync(Ct);

        await using var conn = await app.OpenAsOwnerAsync();
        var applied = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM schema_migrations");
        Assert.Equal(Migrator.LoadMigrations().Count(), applied);
    }

    [Fact]
    public async Task The_app_writes_an_event_through_Db()
    {
        var blog = await NewBlogAsync();
        var db = new Db(NpgsqlDataSource.Create(app.AppConnectionString));

        var id = await db.WriteAsync(tx => tx.Event(
            new EventRow(blog, "blog", blog, "blog_created", Actors.System, new { slug = "x" }), DateTimeOffset.UtcNow), Ct);

        await using var conn = await app.OpenAsAppAsync();
        Assert.Equal("blog_created", await conn.ExecuteScalarAsync<string>("SELECT kind FROM event WHERE id = @id", new { id }));
    }

    [Theory]
    [InlineData("UPDATE event SET kind = 'rewritten'")]
    [InlineData("DELETE FROM event")]
    [InlineData("UPDATE post_revision SET title = 'rewritten'")]
    [InlineData("DELETE FROM post_revision")]
    public async Task The_app_role_cannot_rewrite_history(string sql)
    {
        await using var conn = await app.OpenAsAppAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Theory]
    [InlineData("UPDATE event SET kind = 'rewritten'")]
    [InlineData("DELETE FROM event")]
    [InlineData("UPDATE post_revision SET title = 'rewritten'")]
    [InlineData("DELETE FROM post_revision")]
    public async Task Even_the_schema_owner_cannot_rewrite_history(string sql)
    {
        await using var conn = await app.OpenAsOwnerAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task A_revision_cannot_point_at_another_blogs_post()
    {
        var blogA = await NewBlogAsync();
        var blogB = await NewBlogAsync();
        var postA = await NewPostAsync(blogA);

        await using var conn = await app.OpenAsAppAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("""
            INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, actor, created_at)
            VALUES (@id, @blogB, @postA, 2, 't', 'b', 'system', now())
            """, new { id = Guid.CreateVersion7(), blogB, postA }));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task A_post_cannot_take_another_blogs_revision_as_current()
    {
        var blogA = await NewBlogAsync();
        var blogB = await NewBlogAsync();
        var postA = await NewPostAsync(blogA);
        var postB = await NewPostAsync(blogB);
        await using var conn = await app.OpenAsAppAsync();
        var revisionB = await conn.ExecuteScalarAsync<Guid>("SELECT current_revision_id FROM post WHERE id = @postB", new { postB });

        // The reference is checked at commit (deferred), so the failure surfaces there.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "UPDATE post SET current_revision_id = @revisionB WHERE id = @postA", new { revisionB, postA }));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task A_new_connection_is_at_Propose_and_no_other_level_exists()
    {
        var blog = await NewBlogAsync();
        await using var conn = await app.OpenAsAppAsync();
        var owner = await conn.ExecuteScalarAsync<Guid>("SELECT account_id FROM blog_member WHERE blog_id = @blog", new { blog });

        var level = await conn.ExecuteScalarAsync<string>("""
            INSERT INTO connection (id, blog_id, label, created_by, created_at)
            VALUES (@id, @blog, 'Claude', @owner, now()) RETURNING level
            """, new { id = Guid.CreateVersion7(), blog, owner });
        Assert.Equal("propose", level);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("""
            INSERT INTO connection (id, blog_id, label, level, created_by, created_at)
            VALUES (@id, @blog, 'Claude', 'admin', @owner, now())
            """, new { id = Guid.CreateVersion7(), blog, owner }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Theory]
    [InlineData("UPDATE post SET status = 'scheduled' WHERE id = @post")]
    [InlineData("UPDATE post SET status = 'trash' WHERE id = @post")]
    [InlineData("UPDATE post SET status = 'live' WHERE id = @post")]
    public async Task A_state_without_its_record_is_refused(string sql)
    {
        // A schedule names when and who; the trash names when, who and what to restore; live names when it went live.
        var post = await NewPostAsync(await NewBlogAsync());
        await using var conn = await app.OpenAsAppAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, new { post }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Actors_have_one_form()
    {
        var post = await NewPostAsync(await NewBlogAsync());
        await using var conn = await app.OpenAsAppAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("""
            UPDATE post SET status = 'scheduled', publish_at = now() + interval '1 hour', scheduled_by = 'claude'
            WHERE id = @post
            """, new { post }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    /// <summary>A blog with an owner, written as the app role.</summary>
    private async Task<Guid> NewBlogAsync()
    {
        await using var conn = await app.OpenAsAppAsync();
        var account = Guid.CreateVersion7();
        var blog = Guid.CreateVersion7();
        await conn.ExecuteAsync("""
            INSERT INTO account (id, display_name, created_at) VALUES (@account, 'Owner', now());
            INSERT INTO blog (id, slug, title, created_at) VALUES (@blog, @slug, 'A blog', now());
            INSERT INTO blog_member (blog_id, account_id, created_at) VALUES (@blog, @account, now());
            """, new { account, blog, slug = "b-" + blog.ToString("N")[..12] });
        return blog;
    }

    /// <summary>A draft with its first revision, written in one transaction as the app role.</summary>
    private async Task<Guid> NewPostAsync(Guid blog)
    {
        await using var conn = await app.OpenAsAppAsync();
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var post = Guid.CreateVersion7();
        var revision = Guid.CreateVersion7();
        await conn.ExecuteAsync("""
            INSERT INTO post (id, blog_id, slug, current_revision_id, created_at, updated_at)
            VALUES (@post, @blog, @slug, @revision, now(), now());
            INSERT INTO post_revision (id, blog_id, post_id, number, title, body_md, actor, created_at)
            VALUES (@revision, @blog, @post, 1, 'Hello', 'Body', 'system', now());
            """, new { post, blog, revision, slug = "p-" + post.ToString("N")[..12] }, tx);
        await tx.CommitAsync(Ct);
        return post;
    }
}
