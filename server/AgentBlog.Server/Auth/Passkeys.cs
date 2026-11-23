using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBlog.Database;
using AgentKit.ProviderSignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentBlog.Auth;

/// <summary>
/// Passkey sign-in (docs/specs/auth.md § Passkeys): a way in that needs no third party, so a fresh deploy is claimed with
/// the setup token and a passkey, nothing else filled in. WebAuthn itself is ASP.NET Core's own
/// <see cref="PasskeyHandler{TUser}"/> (in the shared framework, no package); this class keeps its state and decides what
/// a verified passkey means, the same way <see cref="SignIn.Completion"/> does for Google and Discord:
/// <list type="bullet">
/// <item>A passkey is one more identity, provider <c>passkey</c>, its key the SHA-256 of the credential id. So setup,
/// recovery (a new setup token), invitations, binding, unbinding and sessions are the existing paths, unchanged.</item>
/// <item>A ceremony (what the browser is asked to sign) is kept server-side for one answer within five minutes; the
/// browser holds only its id. What a valid answer does is fixed when it begins, and re-checked when it ends (the setup
/// token still configured, the invitation still open, the session still live).</item>
/// <item>The authenticator knows the account by a random handle, never its id.</item>
/// </list>
/// </summary>
public sealed class Passkeys
{
    public const string Provider = "passkey";
    public const string PurposeSetup = "setup";
    public const string PurposeInvite = "invite";
    public const string PurposeSignIn = "sign-in";
    public const string PurposeAdd = "add";
    public const int MaxName = 100;
    private static readonly TimeSpan CeremonyLife = TimeSpan.FromMinutes(5);

    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly PasskeyHandler<PasskeyUser> _handler;

    public Passkeys(Db db, TimeProvider clock, IConfiguration config, IServiceProvider services)
    {
        (_db, _clock) = (db, clock);
        var publicUrl = config["Blog:PublicUrl"] is { Length: > 0 } url ? new Uri(url) : null;
        var options = new IdentityPasskeyOptions
        {
            // The relying party is the blog's own address, never whatever Host a request claims.
            ServerDomain = publicUrl?.Host,
            // Discoverable: the phone offers the passkey without a username first.
            ResidentKeyRequirement = "required",
            UserVerificationRequirement = "preferred",
            ValidateOrigin = ctx => ValueTask.FromResult(!ctx.CrossOrigin && publicUrl is not null
                && Uri.TryCreate(ctx.Origin, UriKind.Absolute, out var origin)
                && string.Equals(origin.GetLeftPart(UriPartial.Authority), publicUrl.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)),
        };
        var users = new UserManager<PasskeyUser>(new PasskeyStore(db), Options.Create(new IdentityOptions()), null!, [], [], null!,
            new IdentityErrorDescriber(), services, NullLogger<UserManager<PasskeyUser>>.Instance);
        _handler = new PasskeyHandler<PasskeyUser>(users, Options.Create(options));
    }

    /// <summary>The identity a passkey signs in as: its key is the SHA-256 of the credential id, fixed in length.</summary>
    public static ProviderIdentity IdentityOf(byte[] credentialId, string? displayName) =>
        new(Provider, Base64Url.EncodeToString(SHA256.HashData(credentialId)), displayName, null, false);

    public sealed record Begun(string Ceremony, JsonNode Options);

    /// <summary>
    /// Begins creating a passkey: for setup (data = the setup token's hash), an invitation (its hash) or adding one to a
    /// signed-in account (data = account and session). <paramref name="accountId"/> is the account it will belong to when
    /// one exists already (the owner on a re-opened setup, an imported author, the signed-in person), so its passkeys are
    /// offered as already registered and it keeps one handle.
    /// </summary>
    public async Task<Begun> BeginCreateAsync(HttpContext ctx, string purpose, string data, string name, Guid? accountId, CancellationToken ct)
    {
        var handle = accountId is { } id
            ? await _db.ReadAsync(tx => tx.One<string>("SELECT passkey_handle FROM account WHERE id = @id", new { id }), ct)
            : null;
        handle ??= Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18));
        var created = await _handler.MakeCreationOptionsAsync(new PasskeyUserEntity { Id = handle, Name = name, DisplayName = name }, ctx);
        var ceremony = await StoreAsync(purpose, data, name, handle, created.AttestationState ?? throw new InvalidOperationException("No attestation state."), ct);
        return new Begun(ceremony, JsonNode.Parse(created.CreationOptionsJson)!);
    }

    /// <summary>
    /// Begins signing in: any passkey of this blog's, the phone chooses (no username). <paramref name="returnTo"/> is a
    /// path on this site to go back to afterwards (the consent page of a connecting AI).
    /// </summary>
    public async Task<Begun> BeginSignInAsync(HttpContext ctx, string? returnTo, CancellationToken ct)
    {
        var request = await _handler.MakeRequestOptionsAsync(null, ctx);
        var ceremony = await StoreAsync(PurposeSignIn, returnTo, null, null, request.AssertionState ?? throw new InvalidOperationException("No assertion state."), ct);
        return new Begun(ceremony, JsonNode.Parse(request.RequestOptionsJson)!);
    }

    private async Task<string> StoreAsync(string purpose, string? data, string? name, string? handle, string state, CancellationToken ct)
    {
        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await _db.WriteAsync(async tx =>
        {
            var now = _clock.GetUtcNow();
            await tx.Exec("DELETE FROM passkey_ceremony WHERE expires_at < @now", new { now });
            await tx.Exec("""
                INSERT INTO passkey_ceremony (id_hash, purpose, data, display_name, user_handle, state, expires_at)
                VALUES (@hash, @purpose, @data, @name, @handle, @state, @expires)
                """, new { hash = Sessions.Hash(id), purpose, data, name, handle, state, expires = now + CeremonyLife });
            return 0;
        }, ct);
        return id;
    }

    public sealed record Ceremony(string Purpose, string? Data, string? DisplayName, string? UserHandle, string State);

    /// <summary>Takes a ceremony for its one answer: gone afterwards whatever the answer, and null when unknown or expired.</summary>
    public Task<Ceremony?> TakeAsync(string ceremony, CancellationToken ct) => ceremony is not { Length: > 0 and <= 100 }
        ? Task.FromResult<Ceremony?>(null)
        : _db.WriteAsync(tx => tx.One<Ceremony>("""
            DELETE FROM passkey_ceremony WHERE id_hash = @hash AND expires_at >= @now
            RETURNING purpose, data, display_name, user_handle, state
            """, new { hash = Sessions.Hash(ceremony), now = _clock.GetUtcNow() }), ct);

    public sealed record Created(byte[] CredentialId, UserPasskeyInfo Passkey, ProviderIdentity Identity);

    /// <summary>Verifies the browser's new passkey against its ceremony; null with the reason when it doesn't hold.</summary>
    public async Task<(Created? Created, string? Why)> VerifyCreatedAsync(HttpContext ctx, Ceremony ceremony, string credentialJson)
    {
        var result = await _handler.PerformAttestationAsync(new PasskeyAttestationContext
        {
            HttpContext = ctx, CredentialJson = credentialJson, AttestationState = ceremony.State,
        });
        if (!result.Succeeded) return (null, result.Failure?.Message ?? "The passkey wasn't accepted.");
        var id = result.Passkey.CredentialId;
        return (new Created(id, result.Passkey, IdentityOf(id, ceremony.DisplayName)), null);
    }

    /// <summary>
    /// Stores a new passkey once its identity is bound to <paramref name="accountId"/> (the identity row first, so the
    /// passkey goes with it on unbinding), and gives the account its handle if it had none.
    /// </summary>
    public Task SaveAsync(Guid accountId, Created created, string handle, CancellationToken ct) => _db.WriteAsync(async tx =>
    {
        var p = created.Passkey;
        await tx.Exec("UPDATE account SET passkey_handle = @handle WHERE id = @accountId AND passkey_handle IS NULL", new { accountId, handle });
        await tx.Exec("""
            INSERT INTO account_passkey (credential_id, account_id, provider_account_id, public_key, sign_count, transports,
                is_user_verified, is_backup_eligible, is_backed_up, attestation_object, client_data_json, created_at)
            VALUES (@CredentialId, @accountId, @key, @PublicKey, @signCount, @Transports, @IsUserVerified, @IsBackupEligible,
                @IsBackedUp, @AttestationObject, @ClientDataJson, @now)
            """, new
        {
            p.CredentialId, accountId, key = created.Identity.AccountId, p.PublicKey, signCount = (long)p.SignCount, p.Transports,
            p.IsUserVerified, p.IsBackupEligible, p.IsBackedUp, p.AttestationObject, p.ClientDataJson, now = _clock.GetUtcNow(),
        });
        return 0;
    }, ct);

    /// <summary>
    /// Verifies a sign-in: the passkey is this blog's and signed this ceremony. Returns its identity (to sign in through
    /// <see cref="Accounts.SignInAsync"/>, which still requires a blog), its counters updated.
    /// </summary>
    public async Task<(ProviderIdentity? Identity, string? Why)> VerifySignInAsync(HttpContext ctx, Ceremony ceremony, string credentialJson, CancellationToken ct)
    {
        var result = await _handler.PerformAssertionAsync(new PasskeyAssertionContext
        {
            HttpContext = ctx, CredentialJson = credentialJson, AssertionState = ceremony.State,
        });
        if (!result.Succeeded) return (null, result.Failure?.Message ?? "The passkey wasn't accepted.");
        var p = result.Passkey;
        await _db.WriteAsync(tx => tx.Exec("""
            UPDATE account_passkey SET sign_count = @signCount, is_user_verified = @IsUserVerified, is_backed_up = @IsBackedUp,
                last_used_at = @now
            WHERE credential_id = @CredentialId
            """, new { signCount = (long)p.SignCount, p.IsUserVerified, p.IsBackedUp, p.CredentialId, now = _clock.GetUtcNow() }), ct);
        return (IdentityOf(p.CredentialId, null), null);
    }

    /// <summary>Who a passkey belongs to, as WebAuthn needs to know it: the account and its handle.</summary>
    public sealed class PasskeyUser
    {
        public required Guid AccountId { get; init; }
        public required string Handle { get; init; }
    }

    /// <summary>A stored passkey. Properties, not a positional record: Dapper sees <c>text[]</c> as <c>System.Array</c>.</summary>
    private sealed class PasskeyRow
    {
        public byte[] CredentialId { get; init; } = [];
        public byte[] PublicKey { get; init; } = [];
        public DateTime CreatedAt { get; init; }
        public long SignCount { get; init; }
        public string[]? Transports { get; init; }
        public bool IsUserVerified { get; init; }
        public bool IsBackupEligible { get; init; }
        public bool IsBackedUp { get; init; }
        public byte[] AttestationObject { get; init; } = [];
        public byte[] ClientDataJson { get; init; } = [];

        public UserPasskeyInfo ToInfo() => new(CredentialId, PublicKey, new DateTimeOffset(CreatedAt, TimeSpan.Zero), (uint)SignCount,
            Transports, IsUserVerified, IsBackupEligible, IsBackedUp, AttestationObject, ClientDataJson);
    }

    /// <summary>
    /// The store <see cref="PasskeyHandler{TUser}"/> reads through: finding an account by handle or by credential, and
    /// its passkeys. Writes happen in <see cref="Passkeys"/>, inside the blog's own transactions; Identity's user
    /// management (names, passwords) isn't used, so those members refuse.
    /// </summary>
    private sealed class PasskeyStore(Db db) : IUserPasskeyStore<PasskeyUser>
    {
        private const string Columns = """
            credential_id, public_key, created_at, sign_count, transports, is_user_verified, is_backup_eligible, is_backed_up,
            attestation_object, client_data_json
            """;

        public Task<PasskeyUser?> FindByIdAsync(string userId, CancellationToken ct) =>
            db.ReadAsync(tx => tx.One<PasskeyUser>("SELECT id AS account_id, passkey_handle AS handle FROM account WHERE passkey_handle = @userId",
                new { userId }), ct);

        public Task<PasskeyUser?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken ct) =>
            db.ReadAsync(tx => tx.One<PasskeyUser>("""
                SELECT a.id AS account_id, a.passkey_handle AS handle FROM account_passkey p JOIN account a ON a.id = p.account_id
                WHERE p.credential_id = @credentialId AND a.passkey_handle IS NOT NULL
                """, new { credentialId }), ct);

        public async Task<IList<UserPasskeyInfo>> GetPasskeysAsync(PasskeyUser user, CancellationToken ct) =>
            [.. (await db.ReadAsync(tx => tx.Query<PasskeyRow>($"SELECT {Columns} FROM account_passkey WHERE account_id = @AccountId", user), ct))
                .Select(r => r.ToInfo())];

        public async Task<UserPasskeyInfo?> FindPasskeyAsync(PasskeyUser user, byte[] credentialId, CancellationToken ct) =>
            (await db.ReadAsync(tx => tx.One<PasskeyRow>($"SELECT {Columns} FROM account_passkey WHERE account_id = @AccountId AND credential_id = @credentialId",
                new { user.AccountId, credentialId }), ct))?.ToInfo();

        public Task<string> GetUserIdAsync(PasskeyUser user, CancellationToken ct) => Task.FromResult(user.Handle);
        public Task<string?> GetUserNameAsync(PasskeyUser user, CancellationToken ct) => Task.FromResult<string?>(user.Handle);

        public Task AddOrUpdatePasskeyAsync(PasskeyUser user, UserPasskeyInfo passkey, CancellationToken ct) => throw NotUsed();
        public Task RemovePasskeyAsync(PasskeyUser user, byte[] credentialId, CancellationToken ct) => throw NotUsed();
        public Task SetUserNameAsync(PasskeyUser user, string? userName, CancellationToken ct) => throw NotUsed();
        public Task<string?> GetNormalizedUserNameAsync(PasskeyUser user, CancellationToken ct) => throw NotUsed();
        public Task SetNormalizedUserNameAsync(PasskeyUser user, string? normalizedName, CancellationToken ct) => throw NotUsed();
        public Task<IdentityResult> CreateAsync(PasskeyUser user, CancellationToken ct) => throw NotUsed();
        public Task<IdentityResult> UpdateAsync(PasskeyUser user, CancellationToken ct) => throw NotUsed();
        public Task<IdentityResult> DeleteAsync(PasskeyUser user, CancellationToken ct) => throw NotUsed();
        public Task<PasskeyUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => throw NotUsed();
        public void Dispose() { }

        private static NotSupportedException NotUsed() => new("Agent Blog writes passkeys itself (Auth/Passkeys.cs).");
    }
}
