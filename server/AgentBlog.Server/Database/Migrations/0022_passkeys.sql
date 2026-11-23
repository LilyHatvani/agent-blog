-- 0022 — passkeys (docs/specs/auth.md § Passkeys): a sign-in that needs no third party, so a fresh deploy can be claimed
-- with the setup token and a passkey alone. A passkey is one more identity, provider 'passkey', keyed by its credential
-- id (base64url), so setup, invitations, binding, unbinding and sessions treat it like Google or Discord.

ALTER TABLE account_identity DROP CONSTRAINT account_identity_provider_check;
ALTER TABLE account_identity ADD CONSTRAINT account_identity_provider_check
    CHECK (provider IN ('google', 'discord', 'passkey'));

-- What an authenticator knows the account by (WebAuthn's user handle): random, one per account, never the account id.
ALTER TABLE account ADD COLUMN passkey_handle text UNIQUE CHECK (length(passkey_handle) BETWEEN 16 AND 64);

-- A passkey's public half and its counters. It goes when its identity goes (unbinding it, or the account's last).
CREATE TABLE account_passkey (
    credential_id       bytea PRIMARY KEY CHECK (length(credential_id) BETWEEN 16 AND 1023),
    account_id          uuid NOT NULL REFERENCES account (id),
    provider            text NOT NULL DEFAULT 'passkey' CHECK (provider = 'passkey'),
    provider_account_id text NOT NULL UNIQUE,
    public_key          bytea NOT NULL,
    sign_count          bigint NOT NULL CHECK (sign_count >= 0),
    transports          text[],
    is_user_verified    boolean NOT NULL,
    is_backup_eligible  boolean NOT NULL,
    is_backed_up        boolean NOT NULL,
    attestation_object  bytea NOT NULL,
    client_data_json    bytea NOT NULL,
    created_at          timestamptz NOT NULL,
    last_used_at        timestamptz,
    FOREIGN KEY (provider, provider_account_id) REFERENCES account_identity (provider, provider_account_id) ON DELETE CASCADE
);

CREATE INDEX account_passkey_account ON account_passkey (account_id);

-- A ceremony in progress: what the browser was asked to sign, for one answer within five minutes. The browser holds
-- the id; only its SHA-256 is kept. Purpose and data say what a valid answer does (claim the blog with this setup
-- token, accept this invitation, sign in, or add a passkey to this session's account).
CREATE TABLE passkey_ceremony (
    id_hash      bytea PRIMARY KEY CHECK (length(id_hash) = 32),
    purpose      text NOT NULL CHECK (purpose IN ('setup', 'invite', 'sign-in', 'add')),
    data         text,
    display_name text CHECK (length(display_name) <= 100),
    user_handle  text,
    state        text NOT NULL,
    expires_at   timestamptz NOT NULL
);

CREATE INDEX passkey_ceremony_expires_at ON passkey_ceremony (expires_at);
