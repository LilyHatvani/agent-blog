# 0007 — The shared kernel: reuse the agent-kit NuGet packages, extract what blog and support both need

Status: **accepted** for the published packages · **accepted** for extracting the copies in the ledger below (owner, 2026-11-22) · **proposed** for the new ones (owner, 2026-11-22: "whether we can
reuse nuggets again")

## What exists (checked on nuget.org, 2026-11-22)

| Package | Version | Reused here | Fit |
| --- | --- | --- | --- |
| `AgentKit.McpOAuth` | 0.1.5 | **as is** | OAuth for claude.ai, ChatGPT and Claude Code: PKCE S256, hashed reference tokens, audience pinned to `<issuer>/mcp`, per-call revocation, a POST-only consent page. The host implements `IMcpConnectionStore` (the consent page's choices, creating and resolving a connection) and `IMcpConsentOwner` (who may consent). For hosted ([0005](0005-hosted-and-self-hosted.md)): one issuer for the service, the blog chosen on the consent page, so no change. |
| `AgentKit.ProviderSignIn` | 0.1.5 | **as is** | Google and Discord sign-in, matched by provider account id, never email. Hosted accounts and the self-hosted owner both use it. |
| `AgentKit.Postgres` | not published | wanted | named in Relay's decision 0009 (append-only guard, worker-cursor leases, run-once migrator); still a copy in Relay |
| `AgentKit.Storage` | not published | wanted | named in 0009 (the SigV4 presigner that signs Content-Length); still a copy in Relay |

**Consequence:** M1's riskiest parts (the MCP door and sign-in) are package references, not work. Publishing
`AgentKit.Postgres` and `AgentKit.Storage` from Relay's copies, as 0009 planned, removes the next two copies before
they're made.

## What the blog and the support product would both need (rule of two)

The sibling support product ([family/SUPPORT.md](../family/SUPPORT.md)) needs the same pieces as the blog. A second
consumer in scope is the moment to extract, so these become agent-kit packages when the second product's code starts,
not copies:

| Candidate | What | First written in |
| --- | --- | --- |
| `AgentKit.Autonomy` | levels per connection (Propose, Publish, Full), the approval request (pin the exact change and the revision it was made against, approve or decline, applies only if nothing moved), the "judged when it fires" rule for anything scheduled | the blog, M1 and M3 |
| `AgentKit.Liquid` | Fluid with the sandbox defaults of [0006](0006-themes-in-liquid.md): allowed members, step, recursion and time limits, fallback template on failure | the blog, M1 |
| `AgentKit.Secrets` | Relay's encrypted credential store (Relay 0024): a master key, sealed values, rotation | Relay |
| `AgentKit.Export` | "export everything" and import, as a versioned archive format both products write | **built** (0.1.12, 2026-11-23): Agent Blog's export uses it; Relay's `Export.cs`, which it generalises, switches to it next |

**Not a package:** tenancy. It's a column and a rule (every query scoped), and each product's schema owns it.

## Copies waiting for a package (ledger, 2026-11-22)

The owner asked to keep this from getting lost ("what about our nugets?"). Agent Blog is now the **second consumer** of
everything below, so by the rule of two each one is due for agent-kit; until then each copy is recorded here and in
[REUSE.md](../REUSE.md), with its source, so the extraction is mechanical.

| Package | Copied into Agent Blog | From | Tests that go with it |
| --- | --- | --- | --- |
| `AgentKit.Postgres` | `Database/Db.cs` (event lock, `Tx`), `Database/Migrator.cs` (run-once, embedded scripts, app-role grants), `ConnectionSettings.cs` | Relay `Database/` | `SchemaTests`, the `BlogApp` fixture (throwaway database and roles) |
| `AgentKit.Storage` | `Storage/Bucket.cs`, `Storage/SigV4QueryPresigner.cs` | Relay `Storage/` (presigner from EoD) | `SigV4QueryPresignerTests`, `FakeBucket` |
| `AgentKit.Secrets` | **published 0.1.6 (2026-11-22)**; Relay and Agent Blog switched, copies deleted | Relay `Secrets/` (Relay 0024) | in agent-kit, plus Relay's `CredentialStoreTests` |
| `AgentKit.Push` | **published 0.1.6 (2026-11-22)**, with the receiver (decrypt, VAPID verify) for tests; both switched | Relay `Push/WebPush.cs` | in agent-kit, pinned to RFC 8291's bytes |
| `AgentKit.Fetch` | **published 0.1.6 (2026-11-22)**: one address table merged from Relay's guard and Agent Blog's; both switched | Agent Blog `Media/SafeFetch.cs`, Relay `Net/PublicAddress.cs` | in agent-kit; `MediaTests` keeps the real-client checks |
| `AgentKit.Channels` | **published 0.1.7 (2026-11-22)**, written for slice 3: Discord, Slack, ntfy, Standard Webhooks, `SendOutcome`, `Retries` | Agent Blog (first written in agent-kit) | in agent-kit, pinned to the Standard Webhooks vector |
| social posting (Bluesky, Mastodon) | — | the EoD director is preparing it (owner, 2026-11-22); built on Channels' `SendOutcome` and `Retries` (Relay `eod-404`) | — |
| `AgentKit.Testing` (new) | `StubProviders`, `FakeBucket`, the cookie-jar browser | agent-kit's `FakeProviders`, Relay's test fixtures | — |
| `AgentKit.ProviderSignIn`: an OpenID Connect provider set at run time | `Auth/Oidc.cs` (discovery, JWKS cache, ID-token checks, the state cookie), 2026-11-24 | the package's own Google round trip (`ProviderSignInFlow`, `GoogleIdTokens`, `StateCookie`, internal there), widened to any issuer | `OidcTests`, `OidcStub`. Wanted in the package: a provider added after start-up, so the blog keeps only its settings screen |

**Extraction order:** Secrets, Push and Fetch went first (2026-11-22), because slice 3 needed them; Storage and Postgres
next. Fetch had two consumers already: Relay's push and link guards kept their own table. Each extraction: move the clean copy into `LilyHatvani/agent-kit` with its tests,
publish 0.1.x to nuget.org, switch Relay and Agent Blog to the package in the same change, delete both copies.
**How:** a push to agent-kit's `develop` publishes `0.1.<run>` through Trusted Publishing (no stored key); a session
with the repository attached does the extraction end to end.

## Rules

- **Extract from the clean copy** (Relay's rule): the first product writes it inside its own code with no product names
  in the API; the second product's start lifts it into agent-kit. No "lift later" comment ships.
- **EchoOfDreams adopts a package the next time it touches that area**, never as a big-bang refactor.
- Every package stays MIT, in `LilyHatvani/agent-kit`, published to nuget.org.
