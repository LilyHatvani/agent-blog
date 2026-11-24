# Embeds: a comments widget on post pages

Status: **built** (2026-11-24). Code: `Blogs/Embeds.cs`, the `_shared/comments-embed.liquid` partial, the post pages of
Paper and Midnight (every other theme uses Paper's), `PublicSite.cs` (the post route and the page CSP). Owner how-to:
[guides/comments-widget.md](../guides/comments-widget.md). Tests: `EmbedsTests`.

Agent Blog has no comments of its own (a moderation problem, [VISION](../VISION.md)). A blog that has a comments service
elsewhere puts that service's widget on its post pages through this slot. Echo of Dreams' blog is the first user: its
comments and reactions live in EoD, keyed by each post's uuid ([import.md](import.md) keeps the uuids).

## The setting

The owner, in the app (**Account → Sign-in and comments**), or `PUT /api/blogs/{blog}/embeds/comments`:

| Field | |
| --- | --- |
| `script_url` | the widget's script: one https address (`https://comments.example/v1/widget.js`). Its **origin** is what the page allows to run. |
| `connect_origins` | the origins the widget may call (`fetch`, `XMLHttpRequest`, `WebSocket`, `EventSource`): https origins only, at most 10. May be empty. |

`GET` reads it (`set`, `script_url`, `script_origin`, `connect_origins`), `DELETE` clears it. Owner only: an editor or a
writer finds nothing there (404), and no agent tool or approval request reaches it, because it puts another site's
script on every post. Each change is a `blog_settings_changed` event naming the slot's old and new values.

**The brief asked for "the script's origin"; the setting takes the script's address.** A `<script src>` needs a path,
and an origin alone would mean guessing one. The CSP still allows only the **origin** (`script-src https://host`), so a
widget that loads more of its own files from its own origin works.

## What is checked at save

- **`script_url`**: an absolute `https://` address with a host, no credentials (`user:pass@`), no fragment, no quotes,
  angle brackets or whitespace, at most 500 characters. `http://`, `javascript:`, a protocol-relative `//host` and
  anything unparsable are refused with the reason.
- **Each origin**: exactly `https://host` or `https://host:port`, an optional trailing slash: no path, query, fragment,
  credentials, wildcard (`*`), quote, comma, semicolon or whitespace. An `http://` origin is refused.
- What reaches the page and the header is **re-written from the parsed address** (scheme, host in its ASCII form, port),
  never the text the owner typed, so nothing typed into the setting can add a CSP directive or an attribute. Duplicates
  are dropped; a trailing slash is tidied away.
- What is stored is checked again when a page is rendered: a stored value that doesn't pass (written by hand into the
  database) puts nothing on the page.

## On the page

On a **post page**, below the post (after the subscribe box, before the link back), the theme renders:

```html
<div data-embed="comments" data-post="{post uuid}"></div>
<script src="{script_url}" defer></script>
```

**The post's uuid becomes public on the page.** It is the id the widget keys its comments by; before the slot, a reader
never saw it (only the slug). Nothing about a uuid lets a reader do anything here: every owner and agent route checks the
caller first, and a draft's uuid is never on a public page, because drafts have no public page.

The page's CSP, otherwise `default-src 'none'` with no script at all, becomes:

```
default-src 'none'; img-src https: 'self'; style-src 'self'; font-src 'self'; script-src https://<script origin>;
connect-src <origin> <origin>…; base-uri 'none'; form-action 'self'; frame-ancestors 'none'
```

with `connect-src` left out when no origins are listed. Nothing else changes: home, tag, category, author, theme and
not-found pages keep today's header and carry no script, and the post page's other directives stay as they were.

**With no setting, a post page and its CSP are byte-identical to what they were before the slot existed** (checked by
diffing every theme's rendered post page before and after the change, and by `EmbedsTests`: setting the widget and
clearing it again gives back the same bytes).

## What a widget can't do under this CSP

The page widens only `script-src` and `connect-src`, as asked. A widget therefore:

- **can** run its script, call the listed origins, build DOM and set styles through the CSSOM (`element.style.x = …`),
  and show `https:` images;
- **can't** inject `<style>` elements or `style="…"` attributes (`style-src 'self'`), load a stylesheet or font from its
  own origin, open an `<iframe>` (`frame-src` falls back to `'none'`), or run inline script. A widget that needs one of
  these needs a wider setting first; it is not widened here.

## The widget's side (a contract for whoever writes one)

- Find every `[data-embed="comments"]` element and read `data-post`: the post's uuid, stable for the post's life (an
  imported post keeps its source's uuid).
- Reach only the origins the owner listed; the browser refuses the rest.
- Reading the post never needs the widget: the page is complete without JavaScript, and the widget adds comments below it.
- The widget's own sign-in (cookies on its origin, its own session) is its business. The blog's session cookie is
  HttpOnly and scoped to the blog's host; the widget can't read it.

## Acceptance criteria

- [x] With no widget set, a post page and its CSP are byte-identical to before (`EmbedsTests`, and the per-theme render diff).
- [x] With one set, the post page carries `data-post` = the post's uuid and the script; the CSP allows exactly the script's
      origin and exactly the listed origins, on post pages only (`EmbedsTests`).
- [x] A non-https origin, a wildcard, a path, credentials or a `;` is refused at save, and nothing reaches the page.
- [x] Only the owner sets it; a write needs the CSRF header.
- [ ] As an owner, I set the widget from the app and see it under my posts (the live pass, with EoD's widget).
