# Undoing what an agent did

Everything an agent does is in **History** (the clock tab), newest first, under the agent's name. Each thing an agent
did to a post or a picture has an **Undo** button right there. The rules: [specs/history.md](../specs/history.md).

| The agent | Undo |
| --- | --- |
| wrote a draft | moves it to the trash |
| changed a post's words | puts the words from before back (as a new version, so nothing is lost) |
| published or scheduled a post | takes it back to draft |
| took a post down | publishes it again |
| moved a post to the trash | restores it |
| added a picture | moves the picture to the trash |

**When Undo is off**, the reason is beside it. Usually: the post changed since (someone wrote more after the agent).
Undoing would throw those words away, so open the post, look at its **History** of versions, and put back the one you
want.

An agent can also ask to **trash a picture** or **change the blog's title or description**. Below Full that waits in
your Inbox under **The blog**, with the picture or the before and after. At Full it happens at once, and History has the
Undo. If what it was about changed before you decide, the Inbox says so, and approving changes nothing.

An undone action shows **Undone**. Only you see Undo buttons; editors see History without them.
