# A comments widget under your posts

Agent Blog has no comments of its own. If your readers comment somewhere else (Echo of Dreams, or any service with an
embeddable widget), you can put that service's widget under every post. Spec: [../specs/embeds.md](../specs/embeds.md).

## Turning it on

1. In the app, **Account → Sign-in and comments**.
2. Under **Comments widget**, paste **the widget's script**: the https address the service gives you, ending in `.js`
   usually.
3. Under **Addresses the widget may call**, put the origins it talks to, one a line: `https://api.comments.example`.
   The service's docs name them. Just the scheme and host (and a port, if it uses one): no path.
4. **Save.** Open any post: the widget loads below it.

What changes on your posts, and nowhere else:

- Each post page carries an empty spot for the widget, marked with the post's id, and loads the script.
- The page lets the browser run that one site's script and reach the addresses you listed. Your home page, tag,
  category and author pages stay without any script.
- **The post's id is visible in the page.** That's how the widget knows which post's comments to show. It changes nothing
  else: nobody can do anything on your blog with a post's id.

## When it doesn't work

- **"isn't an https origin"**: only `https://` addresses are accepted, for the script and for every address it calls.
- **"isn't an origin"**: an address it may call is just `https://host` (and a port). Take off the path, the `?…` or the
  `*`.
- **The widget loads but shows nothing, or says it can't connect:** it is calling an address you didn't list. Your
  browser's console names the blocked address (a "Content Security Policy" message); add that origin and save.
- **The widget looks unstyled:** this blog's pages allow styles only from the blog itself, so a widget that brings its
  own stylesheet, or styles in its HTML, looks plain. Ask the service for a version that styles itself from its script.
- **It must open a frame:** frames aren't allowed on post pages. A widget that needs one won't show.

## Turning it off

**Remove**, under the same form. Post pages stop loading the script at once and are exactly what they were before. The
comments themselves stay at the service.

## For people who build widgets

The page gives you `<div data-embed="comments" data-post="{post uuid}"></div>` and your `<script src>` with `defer`. The
post reads without you; you add comments below it. You may call only the origins the owner listed, and you can't add
`<style>` or inline styles in HTML, frames, or inline script. [The spec](../specs/embeds.md#the-widgets-side-a-contract-for-whoever-writes-one)
has the whole contract.
