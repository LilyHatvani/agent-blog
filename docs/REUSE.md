# Reuse map

Every piece lifted from a sibling project names its source commit, so drift can be checked. Packages from
[agent-kit](https://github.com/LilyHatvani/agent-kit) are consumed, not copied.

EchoOfDreams is private: its paths don't resolve for readers outside the project. They're kept as provenance.

| Here | From | Commit | Notes |
| --- | --- | --- | --- |
| `.gitattributes`, `.editorconfig`, `.gitignore` | Relay | `332ef97` | as is |
| `server/Dockerfile`, `Directory.Build.props` version stamping | Relay `server/` | `332ef97` | without the PWA stage (M1 adds it) |
| `Database/ConnectionSettings.cs`, `Migrator.cs` (no agent seeding), `Db.cs` (+ event append) | Relay `server/Relay.Server/Database/` | `332ef97` | copies until `AgentKit.Postgres` is published (0007) |
| `BlogApp` test fixture (throwaway database and roles) | Relay `Relay.Server.Tests/RelayApp.cs` | `332ef97` | trimmed |
| `Auth/Sessions.cs`, `Auth/SignIn.cs` (setup, completion) | Relay `Api/OwnerSessions.cs`, `Api/OwnerSignIn.cs` | `332ef97` | keyed by account; recovery binds to the same owner instead of unbinding |
| `StubProviders.cs` (tests) | agent-kit's `FakeProviders.cs` via Relay | `332ef97` | as is |
| autonomy levels' "public changes wait for a click" | EoD `Assistant.Tooling/Packs/BlogAuthoringToolPack.cs` | `8c3312ca` | becomes the Propose level (0001) |
| schedules judged at fire time | EoD `database/v1/30g_named_page_scheduled_by.sql` | `8c3312ca` | M2 |
| RSS, Atom, sitemap | EoD `ServerAPI/Api/Endpoints/BlogSeoEndpoints.cs` | `8c3312ca` | M1 |
