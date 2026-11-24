# A comments widget under your posts

Agent Blog has no comments of its own. If your readers comment somewhere else (Echo of Dreams, or any service that
offers a comments page per post), you can show that service's comments under every post. Spec:
[../specs/embeds.md](../specs/embeds.md).

## Turning it on

1. In the app, **Account → Sign-in and comments**.
2. Under **Comments widget**, paste **the widget's page for a post**: the https address the service gives you, with
   `{post}` where the post's id goes, for example `https://comments.example.com/posts/{post}`.
3. **Save.** Open any post: the service's page shows in a frame below it, and grows to fit its comments.

What changes on your posts, and nowhere else:

- Each post page shows the service's page for that post in a frame, below the post.
- **The service sees the post's id and what its own users do in the frame. It can't act on your blog**: the frame is
  the service's own page, kept apart by the browser, so it can't read the post page, use your sign-in, or reach your app.
- **The post's id is visible** in the page and in the frame's address. That's how the service knows which post's
  comments to show; nobody can do anything on your blog with it.
- Your home page, tag, category and author pages are unchanged.

## When it doesn't work

- **"must be https"**: only `https://` addresses are accepted.
- **"carries {post} exactly once"** or **"goes in the address's path"**: put `{post}` once, in the path:
  `…/posts/{post}`, not `?post={post}`.
- **"can't carry credentials, a ?query or a #fragment"**: take off the `?…`, the `#…` or the `user:password@`.
- **The frame stays empty or says it refused to connect:** the service doesn't allow being framed by your blog. Ask it
  to allow your blog's address.
- **The frame doesn't grow to fit:** the service doesn't send its height. It shows at 600 pixels and scrolls inside.

## Turning it off

**Remove**, under the same form. Post pages stop showing it within a minute (a page may be cached for up to 60 seconds)
and are exactly what they were before. The comments themselves stay at the service.

## For people who build widgets

Serve a page per post at the owner's address with the post's uuid in place of `{post}`, allow framing by the blog, and
post `{ type: 'resize', height: <pixels> }` to `window.parent` when your height changes; nothing else you post is read.
To check that a uuid is a post that is out, ask `GET /api/public/posts/{uuid}` on the blog: a live post answers its
title, address and date, anything else the same 404. The feeds carry each post's uuid too, as `dc:identifier`.
[The spec](../specs/embeds.md#the-widgets-side-a-contract-for-whoever-writes-one) has the whole contract.
