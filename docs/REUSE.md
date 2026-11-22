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
| `Connections/BlogOAuth.cs` (consent owner, connection store) | Relay `OAuth/RelayOAuth.cs`, `OAuth/RelayConsentOwner.cs` | `332ef97` | the consent page asks for a blog, not an agent and projects |
| `ConnectionTests` OAuth helpers | Relay `Relay.Server.Tests/OAuthTests.cs` | `332ef97` | trimmed |
| `Storage/Bucket.cs`, `Storage/SigV4QueryPresigner.cs` (+ its tests) | Relay `server/Relay.Server/Storage/` (the presigner from EoD `Core/Services/SigV4QueryPresigner.cs`) | `925b9d1` | Relay's session quotas and download links left out; `OpenAsync` streams to readers |
| `FakeBucket` (tests) | Relay `Relay.Server.Tests/RelayApp.cs` | `925b9d1` | renamed only |
| media fetch guard (`Media/SafeFetch.cs`) | agent-kit `AgentKit.Fetch` 0.1.7 | package | the copy and its address table are gone; one table with Relay |
| notification senders (`Notify/Notifier.cs`) | agent-kit `AgentKit.Channels` 0.1.7 | package | the blog keeps its tables, cursor, judgement and screens |
| sealed secrets (`Secrets/BlogSecrets.cs`), Web Push | agent-kit `AgentKit.Secrets`, `AgentKit.Push` 0.1.7 (from Relay's credential store and `Push/WebPush.cs`) | package | the blog keeps only its label and associated data |
| `StubProviders.cs` (tests) | agent-kit's `FakeProviders.cs` via Relay | `332ef97` | as is |
| autonomy levels' "public changes wait for a click" | EoD `Assistant.Tooling/Packs/BlogAuthoringToolPack.cs` | `8c3312ca` | becomes the Propose level (0001) |
| schedules judged at fire time | EoD `database/v1/30g_named_page_scheduled_by.sql` | `8c3312ca` | M1 (built) |
| RSS, Atom, sitemap | EoD `ServerAPI/Api/Endpoints/BlogSeoEndpoints.cs` | `8c3312ca` | M1 |
