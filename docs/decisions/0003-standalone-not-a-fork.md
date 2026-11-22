# 0003 — A standalone product, not a fork of EchoOfDreams' blog

Status: **accepted** for v1 · one open point below, the owner's

## Question

EchoOfDreams has a working blog (posts with revisions, a category tree, tags, schedules, feeds, a sitemap, and an
MCP tool pack for authoring). Fork it, or build a standalone one?

## Decision

**Standalone, with EoD's blog as the source of the domain.** EoD's blog is spread across its services: `ServerAPI`
(`BlogEndpoints`, `BlogAuthorEndpoints`, `BlogSeoEndpoints`), `AdminAPI`, the `Database` repositories and SQL
(`named_pages` and its revisions, `blog_categories`, `blog_post_tags`), `users.blog_role`, AuthService sessions, and
the assistant tool-pack framework. A fork would carry all of that and EoD's users. A rewrite on Relay's one-service
skeleton keeps the parts that are the blog and drops the parts that are EoD.

**What comes over (rewritten for one owner, each lift naming its EoD commit in `docs/REUSE.md`):**

- the post model: drafts, live, scheduled; every save a revision;
- the category tree and tags, slugs, the one-default-category rule;
- schedules judged at fire time (`scheduled_by`, `fn_promote_scheduled_blog_posts`);
- RSS, Atom and the sitemap (hand-written `XmlWriter`, no syndication package);
- the authoring tool pack's shape (`BlogAuthoringToolPack`): reads, private writes, and public changes that wait for a
  click, which becomes the Propose level.

**What stays behind:** EoD accounts and blog roles, the contributor/editor review flow between people, the React
consoles, the cf-worker edge caching.

## Open point (the owner's)

Does EoD's own blog later run on this product, so there is one blog codebase instead of two drifting apart? More work
up front, one place for fixes after. **Default if no answer:** no; the two stay separate and this record is revisited
when either blog next needs a large change.
