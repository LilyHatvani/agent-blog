# Posts

Status: **M1, slice 4** (2026-11-22). Tool names: [tools.md](tools.md). Levels: [0001](../decisions/0001-agent-autonomy-levels.md).

## One set of rules

`PostService` holds every rule. The MCP tools call it as the agent's connection, at its level; the owner's API calls it
as the owner, with no level limit. Neither face has a rule of its own.

## States

`draft` → `review` (submit) → `live` (publish) or `scheduled` (schedule) → back to `draft` (unpublish). Anything but
`trash` → `trash`; `trash` → the state it had (restore). A scheduled post that's trashed comes back as a draft: a
schedule doesn't survive the trash.

## Revisions

Every save is a new revision, numbered per post, naming its actor (`connection:…` or `account:…`). Revisions are never
edited or deleted (the database refuses it, even for the schema owner). A write names the revision it read
(`base_revision`): if the post moved on, it's refused with `stale_revision` and the current revision id, never
silently overwritten. The owner puts an old revision back as a **new** revision with its content, so history keeps
both.

## What each level does at once

| Action | Lowest level that runs it at once |
| --- | --- |
| create a draft, edit a draft or a post in review, submit for review | Propose |
| edit a live or scheduled post, publish, schedule, unpublish | Publish |
| trash, restore | Full |

Below that, the call becomes an **approval request**: `status: waiting_for_owner`, the request id, and a sentence
saying which level it needs. It isn't an error; the agent carries on and checks it with `get_request`.

## Approval requests

A request pins the action, its arguments (an edit's new title, body and excerpt; a schedule's time), and the revision
it was asked against. The owner approves or declines (`/api/requests`, the admin page).

- **Approve** applies exactly what was asked, and only if the post is still at the pinned revision and the action
  still makes sense for its state. Otherwise the request turns **stale** and nothing changes.
- An approved edit's revision names the **agent** as its author (the words are its); the event names the owner as who
  approved.
- An approved publish or schedule is the owner's act, made at the agent's request: an approved schedule is judged as
  the owner's when it fires.

## Schedules

`schedule_post` records who set the schedule. When it comes due (`Scheduler`, every 30 s; `Blog__Scheduler__IntervalSeconds`,
0 turns the loop off), it goes live only if that actor could still publish it then: a connection not revoked and at
Publish or Full, or an account that still owns the blog. Otherwise the post goes back to draft with a
`schedule_dropped` event saying why. Passes on several replicas skip each other's rows.

## Isolation

Every query names the connection's blog. Another blog's post, by id or slug, reads as `not_found` to an agent and to
the owner's API alike.

## Events

`post_created`, `revision_saved`, `revision_restored`, `post_submitted`, `post_published`, `post_scheduled`,
`post_unpublished`, `post_trashed`, `post_restored`, `schedule_dropped`, `approval_requested`, `approval_approved`,
`approval_declined`, `approval_stale`.
