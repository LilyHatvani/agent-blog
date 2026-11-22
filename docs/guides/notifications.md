# Getting notified

The blog can tell you when something needs you: an agent asking you to decide, a draft sent for review, a schedule that
didn't go live, a new agent connected. It can tell you on your phone through this app, in Discord or Slack, through
ntfy, or by calling a webhook of your own.

## Before you start

The server needs `Blog__Secrets__MasterKey` set. Without it the Notifications screen says "Secrets can't be stored yet:
set Blog__Secrets__MasterKey on the server (32 random bytes: openssl rand -hex 32) and redeploy." and the add buttons
are off. See [Secrets and storage](self-host-secrets-and-storage.md).

All of this is on **Account** → **Notifications** → "Where the blog tells you what needs you".

## On your phone or computer

1. Open the app on the device you want notified, and go to **Account** → **Notifications**.
2. Under **This device**, tap **Turn on for this device**.
3. Allow notifications when the browser asks.

The device appears under **This app, on your phones**. Tapping a notification opens the app at the right screen; an
approval request opens the Inbox. **Turn off on this device** stops it for that device only. You can have up to 20
phones and browsers.

Where it works:

- Chrome on Android, and Chrome on a desktop.
- Firefox, on Android and on a desktop.
- iPhone and iPad: only once the app is added to the Home Screen (Share, then **Add to Home Screen**) and opened from
  there, on iOS 16.4 or later. We haven't tested this on an iPhone yet. In Safari without that step, the screen says
  "This browser can't show notifications from the app."

The blog has to be served over https for any browser to allow this.

## Discord, Slack, ntfy or a webhook

1. Under **Other places**, tap **Add Discord**, **Add Slack**, **Add ntfy** or **Add webhook**.
2. Give it a **Name** and fill in where it sends:
   - **Discord**: in the Discord channel's settings, Integrations, Webhooks, New Webhook, Copy Webhook URL. Paste it as
     the **Webhook address**.
   - **Slack**: an app with Incoming Webhooks turned on, Add New Webhook to Workspace, pick the channel, copy the URL.
   - **ntfy**: the **Server** (`https://ntfy.sh` by default), a **Topic**, and an optional **Access token**. Subscribe
     to the topic in the ntfy app. On ntfy.sh anyone who knows the topic can read it: keep the long random topic the
     form suggests, or use a protected topic with a token.
   - **Webhook**: an https address that takes a POST.
3. Tap **Add**.

For a webhook, the next screen shows its signing secret **once**. Copy it into whatever receives the webhook. Each
request carries `webhook-id`, `webhook-timestamp` and `webhook-signature` headers ([Standard
Webhooks](https://www.standardwebhooks.com/)), so the receiver can check it came from your blog.

Addresses and tokens are kept encrypted and never shown again. Where a channel sends can't be changed: remove it and
add a new one.

## Choose what each channel says

On a channel's card, tap **Settings**:

- **Tell me when**: the events it sends. New channels start with all of these except the last:
  - An agent asks you to decide (publish, schedule, edit, trash)
  - An agent submits a draft for review
  - A scheduled post didn't go live
  - A new agent is connected
  - Another notification channel stopped working
  - Cloudflare stopped taking purges of trashed pictures
  - An agent's post went live (by itself or on schedule)

  The events about announcing posts are in [Announcing new posts](announce.md).
- **How much it says**: **The post's title and who did it**, or **Only what happened (for a channel others can read)**.
  ntfy without a token starts at the second.

Tap **Save**. What you do yourself is never sent back to you, except a new agent connection.

## Send a test

Tap **Send test** on the channel's card. It goes at once and is never retried. The card says "Test sent." or why it
didn't arrive. For your phones, turn on a device first.

**Last deliveries** under **Settings** shows the last 20: sent, will retry, not delivered, or not sent, each with why.
A request you decided before it went out shows as not sent, with "The request was approved before this went out."

## If something goes wrong

- **"Notifications to … aren't getting through. See why"** shows on every screen while a channel is stopped or keeps
  failing.
- **"Failing since …"** with the error: the service didn't answer or was busy. The blog retries after 1 minute, 5
  minutes, 30 minutes, 2 hours and 12 hours, then gives up on that one notification.
- **"Stopped: Discord answered 404: the webhook was deleted or no longer takes messages."** The address is gone (401,
  403, 404 or 410). What was waiting is skipped, and your other channels are told. Fix it on the service's side, then
  tap **Turn back on**, or remove the channel and add a new address.
- A phone whose subscription ended is let go on its own; your other phones keep getting notifications.
- "Notifications are blocked for this site: allow them in the browser settings, then try again."
- "A Discord webhook URL starts with https://discord.com/api/webhooks/." (or the Slack one, "…https://hooks.slack.com/services/."):
  you pasted another address.
- "A webhook address is an https URL without a user name or password."
- An address on your own network or `localhost` is refused. A self-hoster can allow it with
  `Blog__Notify__AllowPrivate=true`.
- "Its secret doesn't open (the master key changed?): remove the channel and add it again."
- "A blog has at most 20 channels: remove one first." / "At most 20 phones get notifications: remove one first."
