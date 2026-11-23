-- 0023 — the demo (docs/specs/demo.md): a visitor opens /demo and looks around the owner's app as a guest on a hidden
-- sample blog, where every change is refused. The guest signs in through an identity of its own kind, 'demo', which no
-- sign-in page offers and no other path binds.
ALTER TABLE account_identity DROP CONSTRAINT account_identity_provider_check;
ALTER TABLE account_identity ADD CONSTRAINT account_identity_provider_check
    CHECK (provider IN ('google', 'discord', 'passkey', 'demo'));
