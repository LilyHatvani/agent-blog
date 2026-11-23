# Releasing the image

Status: **M4, built** (2026-11-23) · `.github/workflows/release.yml`

The Railway template and every self-hoster run the published image, `ghcr.io/lilyhatvani/agent-blog`, built for
`linux/amd64` and `linux/arm64` (a Raspberry Pi, an ARM VPS). Nobody needs the .NET SDK or Node to run the blog.

## Tags

| You do | The workflow pushes |
| --- | --- |
| Publish a GitHub release `v1.2.3` | `:1.2.3`, `:1.2`, `:1`, `:latest` |
| Publish a pre-release `v1.3.0-rc.1` | `:1.3.0-rc.1` only (`latest` stays on the last release) |
| Run *release* by hand with **push** on | `:edge`, from the branch you ran it on |
| Run *release* by hand with **push** off | nothing: both platforms build, as a check |

After every push, a second job runs the pushed image the way a template deploy does (a database, its own address and a
setup token, nothing else) and waits for `/healthz` and the app's manifest; `/version` names the commit.

The template points at `:latest`; pin a self-host to `:1` to take fixes and features but no breaking change, or to an
exact version to move only when you choose.

## How the build stays fast

`server/Dockerfile` builds the app and compiles the server on the runner's own platform, cross-compiling the .NET
output for the target (`-a $TARGETARCH`), so arm64 costs no emulated compile. Only the runtime stage's one `apt-get`
(Kerberos libraries Npgsql probes for) runs emulated. Layers are cached in GitHub Actions' cache.

Each image carries provenance and an SBOM (`docker buildx imagetools inspect ghcr.io/lilyhatvani/agent-blog:latest
--format '{{ json .Provenance }}'`).

## The first release (the owner's)

1. The package is private while the repository is. The template needs it public: on GitHub, **Packages →
   agent-blog → Package settings → Change visibility → Public** (or make the repository public, M4's third line; the
   package follows only if you set it so).
2. Publish release `v0.1.0` from `develop`. The workflow pushes and smoke-tests it.
3. Build the template from it ([railway-template.md](railway-template.md)).
