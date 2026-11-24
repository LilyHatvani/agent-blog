# Embeds: a comments widget on post pages

Status: **built** (2026-11-24). Code: `Blogs/Embeds.cs`, the `_shared/comments-embed.liquid` partial and the blog's own
`_shared/embed-resize.js`, the post pages of Paper and Midnight (every other theme uses Paper's), `PublicSite.cs` (the
post route and the page CSP). Owner how-to: [guides/comments-widget.md](../guides/comments-widget.md). Tests: `EmbedsTests`.

Agent Blog has no comments of its own (a moderation problem, [VISION](../VISION.md)). A blog whose readers comment
somewhere else shows that service's comments under each post through this slot. Echo of Dreams' blog is the first user:
its comments and reactions live in EoD, keyed by each post's uuid ([import.md](import.md) keeps the uuids).

## Trust: the widget's site sees the post id and what its own users do; it cannot act on this blog

The widget is **another site's page, in a sandboxed iframe**. Because the frame is cross-origin, the browser keeps it
on its own side: it can't read or change the post page, can't send requests with this blog's cookies as if it were
the page, and can't reach `/admin` or `/api` as the reader. It learns the post's uuid (in its address) and whatever its
own users do inside it. No script of the widget's ever runs on the blog's origin.

The only way the frame talks to the page is a `postMessage` with its height, which the blog's own script reads under
the strict rules below.

(This replaced a first version that loaded the widget's script on the page itself. That gave the widget's site whatever
a signed-in reader, the owner included, could do on the blog; it is gone.)

## The setting

The owner, in the app (**Account → Sign-in and comments**), or `PUT /api/blogs/{blog}/embeds/comments`
`{ "url_template": "https://comments.example.com/posts/{post}" }`. `GET` reads it (`set`, `url_template`,
`frame_origin`), `DELETE` clears it. Owner only: an editor or a writer finds nothing there (404), and no agent tool or
approval request reaches it. Each change is a `blog_settings_changed` event with the old and new template.

### What is checked at save

The template must be:

- one absolute **`https://`** address with a host, at most 500 characters;
- with **`{post}` exactly once, in the path** (not the host, not a query);
- with **no credentials** (`user:pass@`), **no query**, **no fragment**;
- with no wildcard (`*`), quote, angle bracket, semicolon, comma or whitespace.

`http://`, `javascript:`, a protocol-relative `//host` and anything unparsable are refused with the reason. The origin
that reaches the CSP is written from the parsed address (scheme, host in its ASCII form, port), never from the text
typed. A stored value is checked again whenever a page is rendered: one that no longer passes (written by hand, or by
the first version's shape) puts nothing on the page.

## On the page

On a **post page**, below the post (after the subscribe box, before the link back), the theme renders:

```html
<iframe data-embed="comments" data-post="{post uuid}" data-origin="https://comments.example.com"
  src="https://comments.example.com/posts/{post uuid}" sandbox="allow-scripts allow-same-origin allow-popups allow-forms"
  loading="lazy" title="Comments" width="100%" height="600"></iframe>
<script src="/assets/embed-resize.js" defer></script>
```

- The frame's address is the template with **the post's uuid, percent-encoded**, in place of `{post}`.
- **The sandbox:** `allow-scripts` and `allow-forms` let the widget work; `allow-popups` lets it open its own sign-in
  window; `allow-same-origin` means the frame keeps **its own** origin (the widget's site, with its own cookies), never
  this blog's, because the frame is cross-origin. Without it the frame would be an opaque origin and couldn't keep its
  users signed in. Nothing in the list lets the frame navigate this page (`allow-top-navigation` is absent).
- **The post's uuid becomes public on the page** (and in the frame's address). Before the slot, a reader saw only the
  slug. Nothing about a uuid lets a reader do anything here: every owner and agent route checks the caller first, and a
  draft's uuid is never on a public page, because drafts have no public page.

### The CSP

The page's CSP, otherwise `default-src 'none'` with no script at all, becomes:

```
default-src 'none'; img-src https: 'self'; style-src 'self'; font-src 'self';
script-src https://<this blog>/assets/embed-resize.js; frame-src https://<the widget's origin>;
base-uri 'none'; form-action 'self'; frame-ancestors 'none'
```

- **`frame-src`**: exactly the widget's origin.
- **`script-src`**: exactly one file, **the blog's own resize script, by its full address** (a path-specific source, so
  no other script of this origin matches). The brief asked for no `script-src` change at all, but a page with no script
  can't hear the frame's height, and the brief also asks for the resize. This is the narrowest source that does both.
  Without resizing, the frame is a fixed 600 pixels; that would need no `script-src`, and is a one-line change if
  preferred.
- **No `connect-src`**, and nothing else changes. Home, tag, category, author, theme and not-found pages keep today's
  header and carry no frame or script.

**With no setting, a post page and its CSP are byte-identical to what they were before the slot existed**
(`EmbedsTests`: setting the widget and clearing it again gives back the same bytes, against today's CSP written out).

### Height

`/assets/embed-resize.js` (the same bytes for every blog, cached an hour):

- sets each comments frame's width to 100% and its border to none, through the CSSOM (the page's CSP allows no style
  attributes in markup);
- listens for `message` events, and resizes a frame only when the event's **source is that frame's window**, its
  **origin is exactly** the frame's `data-origin` (an opaque `null` origin never matches), and its data is **exactly**
  `{ type: 'resize', height: <finite number> }`, with no other property;
- clamps the height to whole pixels between 100 and 5000.

Every other message is ignored. Tested by running the served script under Node with each case.

## The widget's side (a contract for whoever writes one)

- Serve a page per post at the address the owner registers, with `{post}` where the post's uuid goes (stable for the
  post's life; an imported post keeps its source's uuid).
- Allow being framed by the blog (your `frame-ancestors`, or no `X-Frame-Options: DENY`).
- To fit your content, post `{ type: 'resize', height: <pixels> }` to `window.parent`, with the blog's origin as the target
  origin, whenever your height changes. Anything else you post is ignored.
- Your sign-in, cookies and session are your own origin's business. In browsers that partition third-party cookies,
  a frame's cookies are partitioned per blog; a widget that needs its users signed in across sites uses its own popup
  (`allow-popups`) or the Storage Access API.
- Reading the post never needs you: the page is complete without the frame.

## Acceptance criteria

- [x] With no widget set, a post page and its CSP are byte-identical to before (`EmbedsTests`).
- [x] With one set, the post page frames the widget's page for the post's uuid, sandboxed as above; the CSP adds exactly
      `frame-src` for the widget's origin and `script-src` for the blog's own resize script, on post pages only.
- [x] A template that isn't https, has credentials, a query, a fragment, a wildcard, markup, or `{post}` other than once
      in the path, is refused at save, and nothing reaches the page.
- [x] The resize script ignores a message from another origin, and one carrying anything but a numeric height; it clamps.
- [x] Only the owner sets it; a write needs the CSRF header.
- [ ] As an owner, I set EoD's widget page from the app and see comments under my posts (the live pass).
