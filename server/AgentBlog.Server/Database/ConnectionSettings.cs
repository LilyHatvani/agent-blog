using Npgsql;

namespace AgentBlog.Database;

/// <summary>
/// Where the blog's two database connections come from. Explicit connection strings win; otherwise a
/// <c>DATABASE_URL</c> (the URI form Railway and most hosts hand out) is the schema owner's connection, and
/// the app connects with the same host as <c>Blog:AppRole</c> / <c>Blog:AppRolePassword</c>, a role the
/// migrator creates if it is missing. With neither, the app falls back to the owner's connection (one role
/// for both: events are then protected by the trigger alone).
/// </summary>
public sealed record ConnectionSettings(string App, string Migrations, string? AppRole, string? AppRolePassword)
{
    public static ConnectionSettings Resolve(IConfiguration config)
    {
        var appRole = Blank(config["Blog:AppRole"]);
        var appRolePassword = Blank(config["Blog:AppRolePassword"]);
        var explicitApp = Blank(config.GetConnectionString("Blog"));
        var migrations = Blank(config.GetConnectionString("BlogMigrations"))
                         ?? FromUrl(Blank(config["DATABASE_URL"]))
                         ?? explicitApp
                         ?? throw new InvalidOperationException(
                             "No database configured: set ConnectionStrings__Blog, or DATABASE_URL (see .env.example).");

        var app = explicitApp
                  ?? (appRole is not null && appRolePassword is not null
                      ? new NpgsqlConnectionStringBuilder(migrations) { Username = appRole, Password = appRolePassword }.ConnectionString
                      : migrations);
        return new ConnectionSettings(app, migrations, appRole, appRolePassword);
    }

    /// <summary><c>postgres://user:pass@host:port/db?sslmode=require</c> → an Npgsql connection string.</summary>
    public static string? FromUrl(string? url)
    {
        if (url is null) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("postgres" or "postgresql"))
            throw new InvalidOperationException("DATABASE_URL must look like postgres://user:password@host:port/database.");
        var userInfo = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase) && kv.Length == 2
                && Enum.TryParse<SslMode>(Uri.UnescapeDataString(kv[1]).Replace("-", ""), ignoreCase: true, out var mode))
                b.SslMode = mode;
        }
        return b.ConnectionString;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
