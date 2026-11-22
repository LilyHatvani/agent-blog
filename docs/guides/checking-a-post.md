# Checking a post before it goes out

The blog checks a post for what readers would trip over: a missing picture, a broken link, a "TODO" left in. Agents run
the check before they ask you for anything, and you see the same check on every request in the Inbox. It is rules and
counts on the server, never an AI model.

## What you see

**On an Inbox card.** When an agent asks to publish, schedule or change a post, the card carries the check of exactly
what it asks: an edit's new words, or the post as it stands for a publish or schedule. It is one line:

- **Ready**: nothing to fix.
- **Ready, with things worth a look**: no problems, some warnings.
- **Readers would hit something**: at least one problem.

Next to it: how many problems and warnings, the word count, the reading time, and how many outside links were checked.
Tap the line (**Details**) to see each finding; **Hide** folds it again. The Inbox never fetches links itself: it shows
what each link answered the last time it was checked, within a day.

**On a post.** On the post's screen, tap **Check** next to **Edit**. This one also fetches the outside links, up to 25
at a time, so it takes a few seconds ("Checking…"). The findings open at once.

## Problem, warning, note

Each finding is labelled.

**Problem**: readers would hit it. An agent is asked to fix every one before it asks you.

- The post has no text.
- A picture with no description.
- A picture that isn't in this blog's media, is in the trash, or isn't ready.
- A link to a post on this blog that doesn't exist.
- A link the blog won't keep (`javascript:` and the like): readers get a dead link.
- An outside link that answers 404 or 410, or points inside the server's own network.
- `TODO`, `FIXME`, `TBD`, `XXX` or lorem ipsum.

**Worth a look**: a warning. The agent should fix it or say why not.

- A title over 70 characters, no summary, or a summary over 200.
- A `#` heading in the body (the title is already the page's heading), a skipped heading level, an empty heading.
- A picture description that says nothing ("image", a filename) or runs over 250 characters.
- Link text like "here" or "click here", or no link text.
- An `http://` link, a picture hot-linked from another site, HTML in the text (readers see it as text).
- A link to one of your posts that isn't live yet.
- An outside link that answered something else, or nothing within 8 seconds ("Couldn't confirm this link works").
- Another post with the same title, or much of the same text.

**Note**: for your information. A post under 100 words, no category or tag, outside links not checked yet.

**Similar** lists up to three posts this one may repeat, with how alike they are. Tap one to open it.

## If something goes wrong

- "Couldn't confirm this link works (the site answers 403)." Many sites refuse robots or sit behind a login. Open the
  link yourself; if it works, leave it.
- "3 outside links not checked yet." On an Inbox card: those links haven't been fetched in the last day. Open the post
  and tap **Check**.
- "30 outside links not checked: at most 25 per check. Run it again for the rest."
- A check that names a picture "in the trash": restore it from **Posts** → **Media**, or ask the agent to remove it
  from the post.
- No check on a card: requests to unpublish, trash or restore carry none, and neither does a request that no longer
  applies.

## For whoever writes the agent's instructions

The tool is `check_post`, open to every agent at every level. It reads nothing outside the blog except the outside
links, and writes nothing.

- Call it with `post` (the post's id or slug) to check the post as saved.
- Pass `title`, `excerpt` or `body_md` to check new text before saving it. Nothing is written.
- `check_links` (default true) fetches outside links. At most 25 per check; answers are kept a day.
- The answer has `ready` (false while any problem is left), a `summary`, and `findings`, each with `check` (such as
  `alt_missing`), `level` (`problem`, `warning` or `note`), `message` and `where`. `similar_posts` lists likely
  repeats.

The server's instructions already tell agents to run it before asking to publish, schedule, submit, or change a live
post, to fix every problem, and to say why for any warning they leave. If you write your own instructions, keep that
rule: you see the same check on the request, so a problem the agent left is the first thing you'll notice.
