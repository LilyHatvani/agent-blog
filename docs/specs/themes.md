# Themes

Status: **M1, slice 9** (2026-11-22). Code: `Public/Theme.cs`, `Public/PublicSite.cs`, `Public/Themes/`.

## Built-in themes

Five, each in Liquid ([0006](../decisions/0006-themes-in-liquid.md)), each its own layout and home page, not a
recolour:

| Theme | What it's like |
| --- | --- |
| **Paper** (default) | quiet and bookish: a narrow column, a serif you can read for an hour |
| **Ledger** | a broadsheet: a masthead, the newest story as the lead, the rest in columns, a drop cap |
| **Mono** | a terminal: monospace, dark first, posts as a log, `#` headings |
| **Studio** | bold and bright: a colour band, big type, posts as cards with their tags |
| **Midnight** | Echo of Dreams' look (2026-11-23): a night sky, literary display headings, a reading column with a drop cap and pull quotes, a creator card for each author under the post |

Paper, Ledger, Mono and Studio follow the reader's light or dark setting and use system fonts. **Midnight** is dark by
design, as Echo of Dreams is, and carries its own fonts: Quicksand, Cormorant Garamond and Raleway (SIL Open Font
License 1.1, the licences beside them in `fonts/`), latin and latin-ext only, served from this site at
`/assets/themes/midnight/fonts/` so no reader's address goes to a font host. The page's policy allows `font-src 'self'`
and nothing else. All five need no JavaScript, load nothing from elsewhere, and render every page: home, post, category
and tag pages, author pages, not found, and the themes page.

**Midnight's creator card**, under each post: one card per person who wrote it (their agents named with them, "With
Claude, their agent"), their author page, and their own link when they set one ([authors.md](authors.md#the-authors-link)).
The look comes from Echo of Dreams' editorial primitives and Midnight palette (`shared/styles/editorial.css`,
`theme-variables.css`, the blog's `reader.css`), rewritten as one stylesheet: no EoD code runs here.

A theme is a folder under `Public/Themes/`: `theme.json` (label, description), `theme.css`, `fonts/*.woff2` if it brings
its own, and any of `layout`, `home`, `post`, `term`, `author`, `not-found`. A page a theme doesn't have comes from Paper. A theme that fails to render a page
is served in Paper instead, with Paper's own styles; Paper failing serves a bare page with the blog's name and a way
back. Both are logged.

Per-blog uploaded themes are later; these are the shipped set.

## Choosing

The owner picks the blog's theme under Account → This blog in the app (`PATCH /api/blogs/{id}` `{ "theme" }`), with a
Preview link beside each. Readers see the blog's theme.

## Trying themes on the site

**Let readers try the themes** (Account → This blog, `theme_picker`, off by default) puts a bar at the top of every
public page: the four themes, *All themes* (`/themes`, a page describing each with links to the home page and a post in
it), and while previewing, *Back to this blog's theme*.

- `?theme=name` shows that page in the theme and remembers it in a session cookie (`blog_theme`), so the reader stays
  in it from page to page. `?theme=own`, or the blog's own name, forgets it. A name that isn't a theme is the blog's
  own.
- A preview is the reader's own view: its pages say `noindex` and `Cache-Control: private, no-store`; every page with
  the bar says `Vary: Cookie`. The canonical address never carries the parameter.
- Without the switch, the parameter and the cookie are ignored, nothing is set, and `/themes` is not found.
- **The owner, signed in, can always preview**, switch or not; the bar then says only they see it, and their pages are
  never cached for anyone else.

## Acceptance criteria

**Use cases (driven in Chromium against a running server, 2026-11-22):**
- [x] As a visitor, without signing in, I switch the blog between the four themes from the bar and keep the one I picked as I move between pages.
- [x] As a visitor, I open All themes, read what each is like and see the home page or a post in it.
- [x] As an owner, I pick the blog's theme on my phone, previewing each first, and turn the readers' bar on or off.

**Behaviour (tests, `ThemeTests`, 4):**
- [x] Every theme renders home, post, category, tag, themes and not-found in itself (its own layout and CSS, the bar), with posts, terms and tables.
- [x] With the switch off, `?theme` and even a forged cookie change nothing, no cookie is set, pages stay publicly cacheable, `/themes` is 404.
- [x] A reader's pick follows them, is noindex and private, never reaches another browser, and back forgets it; a bogus name is the blog's own.
- [x] The owner sets the theme (an event; an unknown theme is refused), readers get it, and the owner previews with the switch off.

**Invariants:** each of these, turned off, fails the tests (mutation runs, 2026-11-22): the owner's switch; the owner
check; noindex on previews; private caching of previews; Paper filling in a page a theme doesn't have (which also
exposed that a fallback served Paper's markup with the failed theme's styles, fixed).
