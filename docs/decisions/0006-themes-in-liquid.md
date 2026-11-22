# 0006 — Themes are Liquid templates, rendered with Fluid

Status: **accepted** (owner, 2026-11-22: "we could use Liquid for templates")

## Decision

Public pages are rendered from **Liquid** templates with **Fluid** (NuGet `Fluid.Core`, 3.0.0, MIT, by Sébastien
Ros). A theme is a set of Liquid files plus CSS; the default theme ships in the repository, and a blog's own theme is
stored per blog with revisions, like posts.

## Why Liquid

- **A hosted theme is code someone else wrote, running on our server.** Liquid can't execute code: no file access, no
  reflection, no network, only the values the server hands it. Razor compiles to C# and could do anything. This is why
  Shopify uses Liquid for merchants' themes.
- **So an agent may edit the theme too.** At Full ([0001](0001-agent-autonomy-levels.md)) an agent can change the
  blog's look, and nothing it writes can reach past the template.
- **The same engine renders mail and other templates** for the sibling support product, so the two share it
  ([0007](0007-shared-kernel.md)).
- Theme authors already know Liquid from Shopify and Jekyll.

## Rules

- **Templates see a model, never the database.** The server builds a read-only model (the post, the blog, navigation,
  the list) and passes it in. Only allowed members are exposed (Fluid's member access is opt-in per type).
- **Bounded rendering.** Every render has a step and recursion limit and a time limit, so a theme with a runaway loop
  fails that render with a clear error instead of tying up the server. (Verify Fluid 3.0's option names for these
  before M1.)
- **Output is escaped by default.** A post body is HTML that already went through the server's sanitizer; it is the
  one value marked raw.
- **A broken theme never takes a blog down.** If a blog's theme fails to parse or render, the page renders with the
  default theme and the owner sees why.

## Alternatives considered

- **Razor** — fast and familiar, and unsafe for anyone's theme but ours.
- **Scriban** — capable, but its language is a superset with features (functions, object creation) a sandbox has to
  switch off one by one. Liquid's smaller language is the sandbox.
- **DotLiquid** — older and slower than Fluid.
