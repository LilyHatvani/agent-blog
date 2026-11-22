# Readers who subscribe by email

Readers can leave their email address under any post and get each new post in their inbox. The emails go out through
your own mail service, so they come from your address and your domain. The rules: [specs/subscribe.md](../specs/subscribe.md).

## Before you start

- The server needs `Blog__Secrets__MasterKey`: your mail service's key and every reader's address are kept encrypted
  with it. See [self-host-secrets-and-storage.md](self-host-secrets-and-storage.md).
- A mail service with your domain verified on it, so it may send from an address like `posts@yourdomain.com`:
  - **Resend**: make an API key (API Keys, Create API Key, with Sending access) after verifying the domain under
    Domains.
  - **Any other service** (Postmark, Mailgun, Amazon SES, Fastmail, your own server) over **SMTP**: its server name, port,
    user and password. **On Railway, outbound SMTP works on the Pro plan only**: on Hobby, use Resend.

## Turn it on

1. Open **Account**, then **Where new posts are announced**.
2. Tap **Add Email**, choose the **Mail service**, and fill in **From address**, **From name** (optional: the blog's title
   otherwise) and the service's key or SMTP details. Tap **Add**.
3. On the new **Email subscribers** card, type your own address under **Send a test to** and tap **Send test**. "Sent.
   Check that inbox." means it works; otherwise the card shows what the service answered (an unverified domain, a wrong
   key).

From then on, every post shows **Get new posts by email** at its end, in every theme, and `/subscribe` has the same form.

## What readers go through

1. They type their address and tap **Subscribe**. The page says **Check your inbox** whatever the address is, so nobody
   can use the form to find out who reads your blog.
2. They get an email, **Confirm your subscription to …**, with a **Confirm** button. The link opens a page where they
   press **Confirm** (a mail scanner that opens links can't subscribe anyone). The link works for 48 hours.
3. Each new post arrives as an email: the title, the excerpt (or what you wrote for it), your byline if bylines are on,
   and **Read it**, which goes to the post's own address. No tracking: nobody knows who opened it.
4. Every email ends with **Unsubscribe**, and mail apps show their own unsubscribe button too. One click, and the address
   is erased.

## When a post goes out

The email is an announcement, like the social ones, so it follows the same rules
([announce.md](announce.md)):

- A post you published goes to every confirmed subscriber after the wait (10 minutes unless you changed it).
- A post an agent published on its own, or an email an agent wrote, **waits for your tap**: open the post and tap
  **Announce** on Email subscribers.
- Taken down before or while it goes out: whatever hasn't gone yet stops.
- On the post, the Email line says how far it got: "Sent to 40 of 42, 2 on the way".

## Who subscribed

On the **Email subscribers** card: how many are confirmed and how many haven't confirmed yet, and **Show who** lists each
address with **Remove** (the same as their own unsubscribe: the address is erased). Agents never see any of it.

## If something goes wrong

- **The card shows "Resend refused the API key"** (or the SMTP server refused the user): fix it at the service, remove
  the Email account and add it again. Emails waiting meanwhile hold for an hour at a time, then go out.
- **"… asked to wait"**: the service's sending limit. Each email waits as long as the service says, then goes.
- **A test says the domain isn't verified**: verify it at the service; until then it won't send from that address.
- **Nobody sees the form**: the Email account is off or removed, or the master key isn't set.
