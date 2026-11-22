# 0004 — Public from the start: MIT, DCO, history kept

Status: **accepted** (owner, 2026-11-22: both repositories "planned for public"), following Relay's 0010

- **Licence: MIT**, the same as Relay and agent-kit, so code moves between them without relicensing.
- **Contributions:** issues and discussions open; pull requests welcome for fixes, docs and self-host improvements;
  anything that changes behaviour or a principle starts as an issue. DCO (`Signed-off-by:`), no CLA.
- **History is public**, so commit author addresses are public. Commits use a GitHub `noreply` address, never a
  personal one.
- **Releases:** a container image on GHCR once M1 ships; the Railway template deploys that image, so a stranger never
  gets a half-finished commit.
- **The flip to public** waits for M1 (the first useful version). Until then the repository can be private or public;
  nothing in it is secret.
