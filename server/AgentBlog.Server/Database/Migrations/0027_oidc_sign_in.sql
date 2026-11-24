-- 0027 — a trusted sign-in provider (docs/specs/auth.md § A trusted provider): the owner names an OpenID Connect issuer
-- in the app, and an account signs in through it as one more identity, provider 'oidc', keyed by the SHA-256 of the
-- issuer and the token's subject (base64url, so any issuer and subject fit provider_account_id). The provider's
-- settings live in blog.settings ('oidc') and its client secret in blog_secret, sealed; sign-ins in flight use the
-- package's provider_signin_state. So the one schema change is the provider's name.
ALTER TABLE account_identity DROP CONSTRAINT account_identity_provider_check;
ALTER TABLE account_identity ADD CONSTRAINT account_identity_provider_check
    CHECK (provider IN ('google', 'discord', 'passkey', 'demo', 'oidc'));
