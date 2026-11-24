# Shared links, structured data, and being found

Status: **M2, slice 6** (2026-11-22). What a shared link shows, what search engines read, and how they hear about a
post. Code: `Public/Social.cs`, `Public/Cards/`, `Public/SearchPing.cs`, the shared head (`Themes/_shared/head.liquid`).

## Every page's head

One partial, the same in every theme (the four themes had identical copies): description, canonical, RSS and Atom,
Open Graph (`og:title`, `og:description`, `og:type`, `og:url`, `og:image` with its alt text, and size when it's a card),
Twitter (`summary_large_image` with a picture), `article:published_time` and `article:modified_time`, one `article:tag`
per tag, `noindex` on previews, and JSON-LD. Every post page and every section page carries these, once each (no tag is
duplicated): the canonical is on the blog's `PublicUrl`, the description is the post's excerpt or the section's meta
description, and the picture is the post's own or a `/cards` image. A section page also advertises its own feeds
(`<link rel="alternate">`, [taxonomy.md § Sections](taxonomy.md#sections)). `SeoTests` pins the set on both pages.

## The picture a shared link shows

- **The post's first own picture**: the first image in the post that is this blog's media and ready (a picture in the
  trash isn't shared), as an absolute address, with its description as the alt text. A hot-linked picture from another
  site isn't used: it can change or vanish.
- **Otherwise a link card**: 1200 × 630 PNG at `/cards/posts/{slug}.png?v={fingerprint}`, drawn on the server in the
  blog's own theme (never a reader's preview): the blog's name, the title as large as it fits (wrapped, then cut with an
  ellipsis), the date and the address. The front page and term pages use `/cards/blog.png` (the blog's title and
  description).
- **Only what readers can see**: a card exists for live posts only; a draft or a missing post is 404.
- **The fingerprint** covers everything drawn, so a changed title is a new address and every platform that cached the
  old picture fetches the new one. Cards answer with an ETag (304 on a match) and a day's cache.
- **Drawn with SkiaSharp (MIT)**, with fonts embedded in the server: Inter (Paper, Studio), DejaVu Serif (Ledger),
  JetBrains Mono (Mono), and DejaVu Sans for any letter a theme's font lacks (Greek, Cyrillic, …). Their licences are in
  `Public/Cards/Fonts/README.md`. ImageSharp was the first choice and was dropped: from 3.2 its builds need a Six Labors
  licence key, which every self-hoster would have to obtain.

## Structured data

- A post: schema.org `BlogPosting` (headline, description, address, dates, the shared picture, tags as keywords, the
  first category as section). The author is the people who wrote or approved the post, agents are contributors ([bylines.md](bylines.md)); the blog when bylines are off.
- The front page: `Blog`.
- Serialized with .NET's default encoder, which escapes `<`, `>` and `&`: no title can close the script it sits in. The
  JS line separators U+2028 and U+2029 are escaped explicitly too (`Social.Json`), so the guarantee holds whatever
  encoder a later change picks; `SeoTests` pins both.

## Being found

- **IndexNow** (one ping reaches Bing, Yandex, Seznam, Naver and Yep; Google doesn't take part): a worker reads the event
  log with its own cursor (every 60 s) and, per blog, sends the addresses of posts that went live, changed while live,
  came down, or were restored, plus the front page when the blog's title or description changed. The key is made per
  blog and published at `/{key}.txt`.
- **WebSub**: both feeds name the hub (`<link rel="hub">`); when a post changes, the hub hears about `feed.xml` and
  `atom.xml`, so subscribed readers get it at once.
- **Never what isn't public**: a post is named only if it is live, or was published once and came down (a 404 the
  engines should learn). A draft that never went live is never named, not even when trashed: its address would tell
  them what isn't published yet.
- **The owner's switch**: Account → This blog → "Tell search engines when a post changes", on by default; each
  service's last answer shows under it. A failed ping isn't retried: the next change pings again, and the sitemap is
  there regardless.
- Configuration: `Blog__IndexNow__Endpoint` (default `https://api.indexnow.org/indexnow`), `Blog__WebSub__Hub` (default
  `https://pubsubhubbub.appspot.com/`; empty turns WebSub off), `Blog__SearchPing__IntervalSeconds` (60; 0 off). Needs
  the blog's public address (`Blog__PublicUrl`, or hosting's domain).

## Acceptance criteria

**Use cases:**
- [ ] As a writer, I paste a post's link into Discord, Bluesky or Slack and it shows the post's picture (or its card)
      and title (on the live blog).
- [x] As an owner, I can stop telling search engines from Account (driven 2026-11-22).

**Behaviour (tests, `SocialTests`, 2026-11-22):**
- [x] A post with its own picture shares it with its description; one without shares its card with size; a trashed picture falls back to the card; the canonical link is the post's.
- [x] A card is a 1200 × 630 PNG with an ETag that answers 304; another title is another address; drafts and missing posts have none.
- [x] All four themes draw different cards; Czech, Greek, Cyrillic and a 300-letter word all render.
- [x] JSON-LD is a BlogPosting matching the page; a title with `</script>` can't break out; the front page is a Blog with its card.
- [x] Both feeds name the hub; without one, neither does.
- [x] A published post is pinged with the blog's key, which `/{key}.txt` serves (another key is 404), and the hub hears about both feeds, once.
- [x] A draft edited and trashed is never pinged; a live post taken down is.
- [x] The owner sees a failed ping's answer, and turning it off stops pings.

**Invariants:**
- [x] No ping names a post that was never public (tested; turning the rule off fails it).
- [x] Turning off the ready check on shared pictures, live-only cards, the fingerprint, the safe JSON encoder, the
      never-public rule, the owner's switch, Atom's hub condition, the key check or the blog's own theme for cards, or
      reading IndexNow's refusal, each fails the tests (mutation runs, 2026-11-22).
