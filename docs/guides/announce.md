# Announcing new posts on your social accounts

When a post goes live, the blog can post it to your Bluesky, Mastodon, X, a Discord or Slack channel, or a Telegram
channel. It waits a few minutes first, so you can catch a typo. A post an agent published on its own never goes out
until you tap **Announce**. The rules behind it: [specs/announce.md](../specs/announce.md).

## Before you start

- The server needs `Blog__Secrets__MasterKey`: your accounts' passwords and tokens are kept encrypted with it. Without
  it, the Announce screen says so and the **Add** buttons are off. See
  [self-host-secrets-and-storage.md](self-host-secrets-and-storage.md).
- The server needs to know its own address (`Blog__PublicUrl`, or a hosting domain), because every announcement links
  to the post.

## Add an account

1. Open **Account**, then **Where new posts are announced**.
2. Tap **Add Bluesky** (or Mastodon, Discord, Slack, Telegram, X) and fill in what that network gives you:

| Network | What to paste | Where you get it |
| --- | --- | --- |
| Bluesky | **Handle** and **App password** | Bluesky: Settings, Privacy and security, App passwords. It looks like `xxxx-xxxx-xxxx-xxxx`. Your account password is refused: never paste it here. |
| Mastodon | **Instance** (such as `mastodon.social`) and **Access token** | Your instance: Preferences, Development, New application, with `write:statuses` and `write:media`. Copy "Your access token". |
| Discord | **Webhook URL** | The channel's settings: Integrations, Webhooks, New Webhook, Copy Webhook URL. |
| Slack | **Webhook URL** | A Slack app with Incoming Webhooks turned on: Add New Webhook to Workspace, pick the channel. |
| Telegram | **Bot token** and **Channel** (`@yourchannel`) | Message @BotFather, send `/newbot`, copy the token. Then add the bot to your channel as an administrator who may post messages. |
| Email | your mail service: **Resend** (an API key) or **SMTP** | see [subscribe.md](subscribe.md): readers subscribe, and each new post goes to them by email |
| X | **API key**, **API key secret**, **Access token**, **Access token secret** | The X developer portal: your app with Read and write permission, then generate the access token and secret. |

3. Optionally give it a **Name**, then tap **Add**. What you pasted is kept encrypted and never shown again.

**X costs money.** X's API has no free posting: each post with a link uses credits you buy on the X developer portal.
The screen reminds you on the X card.

## What happens when a post goes live

- **You published it** (with **Publish now**, by approving an agent's request, or on a schedule you set): after the
  wait, it goes to every account with **Announce new posts** on. The post's page shows **goes out soon** and the time.
- **An agent published it on its own** (an agent at Publish or Full, or a schedule an agent set): it **waits for you**.
  It shows in your Inbox under **Announcements**, and your phone tells you if that notification is on for it
  ("An announcement waits for your tap (an agent published the post or wrote the text)", under Notifications). Open the post, read what will be posted, and tap **Announce**.
- Each post is announced once. Taking it down and putting it back doesn't announce it again.
- An account you add hears only about posts that go live after it was added. To announce an older post, open it and tap
  **Announce** on that account.

## The wait

Under **The wait**, **Announce after (minutes)** is 10 by default (0 to 1440). If you take the post down before then,
the announcement is skipped. To stop one and keep the post up, tap **Don't announce** on the post.

## What it says

By default: the post's title, its excerpt, the link, and its tags as hashtags where the network has them. If that is too
long for the network, the tags go first, then the excerpt. The link is the post's own address, with nothing added to it.

To write it yourself, open the post, find the account under **Announce**, and tap **Edit text**. The count under the box
is the network's own (graphemes on Bluesky, characters on Mastodon, weighted characters on X). The link is added for
you. **Save** stays off while the text is too long. **Use the default** puts the default back.

An agent can write it too, with its `write_announcement` tool. Text an agent wrote always waits for your tap, even on a
post you published yourself. The post's page names the agent ("Claude on my phone wrote what to say") and shows the words.

## If something goes wrong

On the post, each account's announcement shows what happened:

- **posted**: **See it on …** opens it. Slack doesn't say where a message is, so a Slack one has no link.
- **failed**, with the network's words. "refused the credentials" means the account needs setting up again: remove it
  and add it with a new password or token. The account's card on the Announce screen shows the same error. **Try again**
  sends it once more.
- **may not have gone out**: the network took the request but the answer was lost, and Discord and Slack webhooks,
  Telegram and X can't be asked afterwards (Mastodon can, for 50 minutes; Bluesky always can, so it checks by itself). Look at the account. If the post isn't there, tap **Send again**. If it is,
  tap **It's there**. The blog never sends it again on its own.
- **not sent**: the post wasn't live any more when it was due, or the account was off.
- "This server doesn't know its public address: set Blog__PublicUrl": set the variable and tap **Try again**.
- "The account's credentials don't open: remove it and add it again.": the master key changed or was lost. Add the
  account again.

Turning an account **On** off stops everything to it without removing it. **Remove** deletes its credentials here and
cancels what was waiting. What it already posted stays on the network.
