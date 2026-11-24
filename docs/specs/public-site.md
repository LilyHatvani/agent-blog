# Public site

Status: **M1, slice 5** (2026-11-22). Themes: [decision 0006](../decisions/0006-themes-in-liquid.md).

## What readers get

| Path | |
| --- | --- |
| `/` | live posts, newest first, ten a page (`?page=2`), with newer/older links |
| `/posts/{slug}` | one live post |
| anything else | an old address ([old-addresses.md](old-addresses.md)) answers 301 to its page; otherwise the theme's not-found page |
| `/author/{slug}` | an author's live posts, ten a page, while bylines are on ([authors.md](authors.md)) |
| `/category/{slug}` for a section | the section's landing page: its intro, then its live posts paginated ([taxonomy.md § Sections](taxonomy.md#sections)) |
| `/category/{slug}/feed.xml`, `/category/{slug}/atom.xml` | a section's own feeds (its live posts), advertised with `<link rel="alternate">` on its page |
| `/feed.xml` | RSS 2.0, the last 30 posts, full HTML in `content:encoded` |
| `/atom.xml` | Atom, the same posts |
| `/sitemap.xml` | the home page, every live post, term pages with live posts, and every section page |
| `/robots.txt` | the sitemap; `/api/`, `/admin`, `/oauth/`, `/mcp` disallowed |
| `/api/public/posts/{uuid}` | one live post's public facts, as JSON, for a machine ([below](#a-post-by-its-id)) |
| anything else (GET) | the theme's not-found page, `noindex` |

**Only live posts exist here.** A draft, a post in review, a scheduled post or one in the trash answers 404 exactly
like a slug that never existed. A blog hidden from the public ([0005](../decisions/0005-hosted-and-self-hosted.md))
answers 404 everywhere.

**Feed entry ids.** A post written here uses its own address (`/posts/{slug}`) as its RSS guid (a permalink) and its
Atom entry id. An imported post keeps the source's feed id instead ([import.md](import.md)) when the source gave a
usable one, so a feed reader doesn't show it again after a move; its RSS guid is then marked not a permalink. With no
usable source id it keeps its own absolute address, like a post written here.

**The post's uuid in the feeds.** Every RSS item and Atom entry also carries the post's own uuid (an imported post's is
its source's), as Dublin Core's identifier, in this form:

```xml
<dc:identifier>urn:uuid:0f4b1c3e-1111-4a9b-8c7d-000000000001</dc:identifier>
```

with `xmlns:dc="http://purl.org/dc/elements/1.1/"` declared on the feed's root, lower-case hex, hyphenated. It sits
beside the guid and the Atom id, never in their place: those stay as above, because a feed reader keys on them and a
changed id would show every post to its readers again. A comments service keyed by post uuid (Echo of Dreams') reads
it to match an item to its post.

## A post by its id

`GET /api/public/posts/{uuid}` answers one **live** post's public facts, the ones its page shows anyone, as JSON. It is
for a machine that holds a post's uuid (a comments service checking that the post it was asked about is out) and
shouldn't depend on the feed's last-30 window.

```json
{ "id": "0f4b1c3e-1111-4a9b-8c7d-000000000001", "title": "Rain in Lisbon",
  "url": "https://blog.example/posts/rain-in-lisbon", "published_at": "2025-03-01T09:00:00Z" }
```

- `url` is the post's canonical address; `published_at` is UTC, to the second. `Cache-Control: public, max-age=60`,
  as the pages.
- **Every other answer is the same 404**, byte for byte, with the same headers: a draft, a post in review, a scheduled
  post, one in the trash, another blog's post, an unknown uuid, a path segment that isn't a uuid, and any id on a blog
  hidden from the public. (An address with no id or more than one segment after `/posts/` isn't this lookup at all: it
  gets the API's bare 404, whatever posts exist.) The body is `{"code":"not_found","detail":"No such post."}` (`application/problem+json`,
  `Cache-Control: no-store`, so a post that goes live is found at once). The lookup never tells anyone that a post
  they can't read exists.
- No sign-in, no cookie; `Access-Control-Allow-Origin: *`, since it says nothing the post page doesn't. The same blog
  the host names answers it ([Which blog](#which-blog)). `/api/` is outside the sitemap and disallowed for crawlers.

## Sections

A category the owner marks as a section ([taxonomy.md § Sections](taxonomy.md#sections)) appears in the **site nav** in
every theme, through the shared nav partial, in nav order. Its landing page shows its intro then its posts; a non-section
category page is unchanged. The **home page** lists posts from sections with `show_on_home`, plus posts in no section.
With no section at all the home page is byte-identical to before (a test pins that), and the nav renders nothing.

## Which blog

Self-hosted: the instance's blog, at `Blog__PublicUrl`. Hosted (`Blog__Hosting__Domain` set, e.g. `blogs.example`): the
subdomain names the blog (`name.blogs.example`); a host outside the domain, a deeper subdomain or an unknown name has
none. Custom domains come with M5 (hosted).

## Markdown

Markdig, with tables, task lists, footnotes and autolinks. Posts are written by agents, so the output is made safe:
raw HTML in the Markdown is escaped, and links and images keep only `http`, `https`, `mailto` or relative addresses;
`javascript:`, `data:` and anything else become `#`. The feeds carry the same HTML.

## Rendering

Fluid 3 with limits set through its options builder: 100,000 steps, recursion 20, 4 MB of output, collections of
10,000. Templates see plain dictionaries and lists the server builds, never a .NET object. Everything is HTML-escaped
except two values: the page's own content in the layout, and the sanitized post body. A render that fails serves a
bare fallback page and is logged.

The default theme (`Public/Theme/`: layout, home, post, not-found, theme.css) is embedded in the server. Per-blog
themes, stored with revisions and editable at Full, are a later slice.

## Headers

`Content-Security-Policy: default-src 'none'; img-src https: 'self'; style-src 'self'; base-uri 'none'; form-action
'none'; frame-ancestors 'none'`: no script at all (the code adds `font-src 'self'` and `form-action 'self'`). The one
exception is a post page on a blog whose owner set a comments widget: it may frame that widget's origin and run the
blog's own resize script, nothing more ([embeds.md](embeds.md)). `Cache-Control: public, max-age=60` on pages; canonical, Open Graph
and `article:published_time` tags; RSS and Atom linked from every page. The by-id lookup sends `X-Robots-Tag: noindex`
and `X-Content-Type-Options: nosniff`.

## Themes

The pages render in the blog's theme, one of four built in; readers may try the others when the owner allows it.
See [themes.md](themes.md).
