# Announce

Status: **M2, slice 7, built** (2026-11-22). A new post is announced on the owner's accounts: Bluesky, Mastodon, a Discord,
Slack or Telegram channel, X. A short wait comes first, the decision to send is made at the moment of sending, and a
post an agent published on its own waits for the owner's tap. Code: `Announce/` here; the networks are agent-kit's
`AgentKit.Social` (one contract, six adapters, a ledger so nothing posts twice), credentials sealed with
`AgentKit.Secrets` as notification channels are.

## Accounts

The owner adds accounts under **Announce** in the app. Each is one place posts go:

| Network | What the owner pastes | What it posts |
| --- | --- | --- |
| Bluesky | handle and an **app password** (Settings, Privacy and security, App passwords) | a post with a link card (title, excerpt, the post's picture) |
| Mastodon | the instance address and an access token with `write:statuses` and `write:media` | a status; the instance builds the card. Its limits are read from the instance when it is added |
| Discord | a channel's webhook URL | a message with an embed: title, excerpt, picture, the blog's name, the time |
| Slack | an incoming webhook URL | a message; Slack previews the link |
| Telegram | a bot token (from @BotFather) and `@channel` or a chat id; the bot posts in the channel | a message with the link's preview |
| X | the four keys of an app with Read and write (OAuth 1.0a) | a post; X builds the card. **X charges per post with a link**: the screen says so |

- Credentials are sealed under `Blog__Secrets__MasterKey`, one row per value, associated data naming the account, and
  never returned. Without the master key the screen says what to set and nothing can be added.
- Each account has **Announce new posts** (on by default) and can be turned off or removed. Removing deletes its
  credentials and cancels what was waiting for it.
- An account hears about posts published **after it was added**. An older post is announced by hand from its page.

## When a post goes live

The **event log** is the source, as for notifications: a cursor (`notify_cursor` row `announce`) reads
`post_published`. Only a post's **first** publication announces it; publishing it again after taking it down doesn't.
For each account that announces new posts, one **announcement** row is made (one per post per account, ever):

- **The owner vouched for the post** (they published it, approved the request, or scheduled it themselves): the
  announcement is **scheduled**, due after the blog's wait (**Announce after**, 10 minutes by default, 0 to 24 hours),
  so a typo found in the first minutes can be fixed, or the announcement cancelled.
- **An agent published it on its own** (at Publish or Full, or a schedule an agent set): the announcement **waits** for
  the owner. The owner is told (`announcement_waiting`) and taps **Announce** on the post. Nothing goes out under the
  owner's accounts that the owner hasn't seen.

### Sections

An account can be **kept to sections** ([taxonomy.md § Sections](taxonomy.md#sections)): `social_account.section_ids`,
the sections whose new posts it announces, ticked per account on the Announce screen (`PATCH …/social-accounts/{id}`
with `section_ids`, each a section of this blog, else refused). None ticked is every post, as before. Kept to sections,
it announces a post only when the post is in one of them: a post in no section, or only in other sections, goes to the
accounts kept to none. The rule lives in the Announcer and is applied twice: when the announcement is made, and again
when it is due ([Judged when sent](#judged-when-sent)), so a section changed in between counts. It **fails closed**: an
account kept to sections that are all unmarked announces nothing on its own (`scope_lost` on the account, said on the
screen with a one-tap way back to every post). The sections hold back only what goes out on its own: the owner's own
**Send** or **Send again** (`announcement.by_owner`), and words someone wrote for that account, go out regardless.

### Patreon

Patreon's API reads posts but can't make one, so Patreon is a **manual** account: nothing to paste but, optionally, the
owner's Patreon page (an https address on patreon.com), one per blog. Its announcement on every new post is made as for
any account (sections included) but always **waits** (`why_waiting` `manual`), whoever published: the post's page shows
the words with the link under them, **Copy this**, **Open Patreon** and **It's there**. Nothing is ever sent:
**Announce** on it is refused with how, and **It's there** marks it posted (making the row if there was none), so the
Posts list shows Patreon beside the post. The owner is told it waits, as for any waiting announcement.

## What it says

- By default: the post's **title**, then its **excerpt**, then the link, then the post's **tags** as hashtags where the
  network has them. When that doesn't fit the network's count, the tags go, then the excerpt.
- The **link card** is the post's title, excerpt, and the picture shared links show (the post's own first picture, else
  its card, [social.md](social.md)). The link is the post's address, with nothing added to it: no tracking parameters.
- The owner can write the text per account on the post's page, with the network's own count shown as they type. A
  text that doesn't fit is refused, never cut.
- **An agent can write it too**, with `write_announcement` (any level: it sends nothing). Text an agent wrote always
  waits for the owner's tap, even on a post the owner published: words under the owner's name are the owner's to send.
  Without text, the tool shows each account's announcement and how its network counts the current text.

## Judged when sent

A worker (`Blog__Announce__IntervalSeconds`, 15 s) takes what is due and checks, at that moment:

- the post is **still live** (taken down, trashed or back to draft: *skipped*, with the reason);
- the account is **still there and on** (removed: *cancelled*; turned off: *skipped*);
- the post is **in one of the account's sections**, when it is kept to some and the announcement is going out on its own
  (not: *skipped*, "The post isn't in a section this account announces.");
- the credentials open (*failed*: set the account up again).

Then it posts through `SocialPublisher` with the announcement's id as the key, so a retry never posts twice where the
network can be asked. What comes back:

| Result | The announcement | The owner |
| --- | --- | --- |
| Posted | **posted**, with the post's address on the network | the address on the post's page |
| The network asked to wait, or couldn't be reached | tried again after its wait, or after 1 min, 5 min, 30 min, 2 h, 12 h; then **failed** | — |
| Credentials refused | **failed**; the account shows the error | told (`announcement_failed`) |
| The network refused the post | **failed**, with the network's words | told |
| The answer was lost where the network can't be asked (a Discord or Slack webhook, Telegram, X; Mastodon after 50 min) | **unconfirmed** | told (`announcement_unconfirmed`): the owner looks, then taps **Send again** or **It's there** |

A failed announcement can be sent again by the owner. Every change writes an event.

## Acceptance criteria

**User use cases:**
- [ ] As an owner, I add my Bluesky account from the app, and my next published post appears there with its card,
      after the wait, without my doing anything else.
- [ ] As an owner, when an agent publishes on its own, I'm told, I read what will be posted, change it if I want, and
      tap Announce.
- [ ] As an owner, I see on a post's page where it was announced, with a link to each, and what failed and why.
- [ ] As an agent, I write the announcement for each network, see its count, and know the owner sends it.
- [ ] As an owner, I keep my Discord to my Echo of Dreams section from the Announce screen, and a Personal post doesn't
      go there while my Bluesky (kept to none) still gets it.
- [ ] As an owner, I see on the Posts list which networks each post went out on.
- [ ] As an owner, I add Patreon, and each new post waits on its page with the words to copy; after I post them on
      Patreon and tap It's there, the Posts list shows Patreon on it.

Behaviour, invariants and edges are checked by `AnnounceTests` (15), each rule turned off once to see a test fail; the
use cases wait for the live pass.

**Behaviour:**
- [x] A post the owner published is announced once per account after the wait; a second publication isn't.
- [x] A post an agent published on its own, or a schedule an agent set, waits for the owner's tap; nothing is sent until then.
- [x] Text an agent wrote waits for the owner's tap on any post.
- [x] A post taken down before its announcement is due is skipped, and nothing is sent.
- [x] A removed account's announcements are cancelled and its credentials deleted.
- [x] A lost answer from a network that can't be asked is unconfirmed, and neither retried nor sent again without the owner.
- [x] A rate limit waits the network's time; refused credentials fail and say so on the account.
- [x] The default text drops tags, then the excerpt, to fit; the owner's text that doesn't fit is refused when saved.
- [ ] An account kept to a section announces only new posts in it; one kept to none announces every post; a foreign id is refused.
- [ ] A section set after an announcement was scheduled skips it when due; the owner's Send still goes out.
- [ ] An account kept to sections that are all unmarked announces nothing on its own and says so (`scope_lost`).
- [ ] The post list names only posted announcements, with each network and address.
- [ ] Patreon needs no credentials, takes only a patreon.com page, one per blog; its announcement always waits with
      the link in its words; Announce is refused; It's there marks it posted.

**Invariants:**
- [x] No agent path sends, schedules, cancels or marks an announcement, or adds or changes an account.
- [x] Credentials are never returned by any API and are sealed at rest.
- [x] An account never announces a post published before it was added, unless the owner sends it by hand.
- [x] The link has no tracking parameters.

**Edges:**
- [x] Without the master key the screen says what to set; nothing can be added; the rest of the blog works.
- [x] A blog with no public address (no `Blog__PublicUrl` and no hosting domain) fails an announcement with that reason.
