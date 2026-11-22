-- 0002 — owner sign-in (docs/specs/auth.md): sessions behind a cookie, the setup token's burn record, and identity hints.
-- The package's own table, provider_signin_state, is created from AgentKit.ProviderSignIn's script by the Migrator, so
-- this repository never keeps a copy of it (decision 0007).

-- Display hints refreshed at each sign-in; never used to match (the key stays (provider, provider_account_id)).
ALTER TABLE account_identity
    ADD COLUMN display_name text CHECK (length(display_name) <= 200),
    ADD COLUMN last_used_at timestamptz;

-- A signed-in browser: the SHA-256 of its cookie, never the value. 30 days, sliding. Which identity began it is kept,
-- so unbinding that identity ends its sessions.
CREATE TABLE account_session (
    id_hash             bytea PRIMARY KEY CHECK (length(id_hash) = 32),
    account_id          uuid NOT NULL REFERENCES account (id),
    provider            text NOT NULL,
    provider_account_id text NOT NULL,
    created_at          timestamptz NOT NULL,
    last_seen_at        timestamptz NOT NULL,
    expires_at          timestamptz NOT NULL,
    FOREIGN KEY (provider, provider_account_id) REFERENCES account_identity (provider, provider_account_id) ON DELETE CASCADE
);

CREATE INDEX account_session_account ON account_session (account_id);
CREATE INDEX account_session_expires_at ON account_session (expires_at);

-- The setup token that last completed setup: its SHA-256 only. Setup is open while no row exists, or while the
-- configured Blog__SetupToken hashes to something else (a new token re-opens it, for recovery). One row at most.
CREATE TABLE instance_setup (
    singleton         boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    burned_token_hash bytea NOT NULL CHECK (length(burned_token_hash) = 32),
    burned_at         timestamptz NOT NULL
);
