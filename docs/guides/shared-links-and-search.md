# Shared links and search engines

When someone pastes a link to your post into Discord, Slack, Bluesky or a chat, it shows a picture, the title and the
summary. The blog also tells search engines and feed readers when a post goes live or changes, so they don't wait for
their next visit.

## What a shared link shows

- **The title and the summary** you or the agent wrote. A post with no summary shows its first words instead.
- **A picture**, one of two:
  - **The post's own picture**: the first picture in the post that comes from your blog's Media and isn't in the
    trash, with its description.
    A picture hot-linked from another site is never used, since it can change or vanish.
  - **A link card**, when the post has no picture of its own (or all of them are in the trash): a 1200 × 630
    picture drawn by the server in your blog's theme, with the blog's name, the post's title, the date and the
    address.
- The front page and category and tag pages use the blog's card: its title and description.

Cards exist only for live posts. A draft has none, and its link shows nothing a reader couldn't already see.

When you change a post's title, its card gets a new address, so sites that kept the old picture fetch the new one.

## Check a preview before you share

1. Open the post on the site: on the post's screen in the app, tap **open on the site**.
2. To see the card, add `.png` to the address and put `cards/` in front: for `https://blog.example/posts/my-post`,
   open `https://blog.example/cards/posts/my-post.png`. If the post has its own picture, that is what links show
   instead.
3. Paste the link into a private place first (a Discord channel only you read, a Slack message to yourself) and look.

To give a post a picture of its own, ask the agent to add one at the top; see
[Pictures, and putting them behind Cloudflare](pictures-and-cloudflare.md). To change what the summary says, edit the
post's **Summary**; see [Editing a post on your phone](editing-on-your-phone.md).

## Telling search engines

With the switch on, the blog tells others about a post when it goes live, changes while live, comes down, or is
restored:

- **IndexNow**: one message reaches Bing, Yandex, Seznam, Naver and Yep. Google doesn't take part; it finds your posts
  through the sitemap at `/sitemap.xml`, as before.
- **WebSub**: feed readers that follow your RSS or Atom feed through the hub hear about the change at once.

A draft that never went live is never named, not even when it is trashed.

## The switch

1. Open the **Account** tab and go to **This blog**.
2. **Tell search engines when a post changes** is on by default. Untick it to stop.
3. Tap **Save**.

Under the switch, each service's last answer shows: "IndexNow: told 2 h ago", or the error. A ping that failed
isn't sent again; the next change pings again, and the sitemap is there regardless.

## If something goes wrong

- **The link shows the card, not your picture.** The post's pictures are all hot-linked from other sites, or every one
  from your Media is in the trash. Tap **Check** on the post: it warns about "A picture from another site" and
  names a picture in the trash.
- **The link shows an old title or picture.** Some sites keep what they fetched for a while. A changed title gets a new
  card address; a site that cached the page itself may still show the old one until it looks again.
- **The card is "not found".** The post isn't live, or the address has a typo.
- "IndexNow answered 403." or "The WebSub hub answered 400." The service refused that ping. It doesn't stop anything
  else; the next change tries again.
- "IndexNow didn't answer (…)." The service was unreachable. The same: the next change tries again.
- **No answers show under the switch at all.** Nothing has changed since it was turned on, or the server doesn't know
  the blog's public address (`Blog__PublicUrl`), which pings need.
