# Reuse map

Every piece lifted from a sibling project names its source commit, so drift can be checked. Packages from
[agent-kit](https://github.com/LilyHatvani/agent-kit) are consumed, not copied.

EchoOfDreams is private: its paths don't resolve for readers outside the project. They're kept as provenance.

| Here | From | Commit | Notes |
| --- | --- | --- | --- |
| `.gitattributes`, `.editorconfig`, `.gitignore` | Relay | `332ef97` | as is |
| `server/Dockerfile`, `Directory.Build.props` version stamping | Relay `server/` | `332ef97` | without the PWA stage (M1 adds it) |
| autonomy levels' "public changes wait for a click" | EoD `Assistant.Tooling/Packs/BlogAuthoringToolPack.cs` | `8c3312ca` | becomes the Propose level (0001) |
| schedules judged at fire time | EoD `database/v1/30g_named_page_scheduled_by.sql` | `8c3312ca` | M2 |
| RSS, Atom, sitemap | EoD `ServerAPI/Api/Endpoints/BlogSeoEndpoints.cs` | `8c3312ca` | M1 |
