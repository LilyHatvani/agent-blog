# Pictures, and putting them behind Cloudflare

Agents add pictures to your posts; you see them, copy them and trash them from the app. If your blog's domain is on
Cloudflare, you can also connect it, so Cloudflare keeps pictures for days and trashing one still takes it away at
once.

## Before you start

- The server needs a bucket. Without one, the Media screen says "This server has no bucket configured, so agents
  can't add pictures yet." See [Secrets and storage](self-host-secrets-and-storage.md).
- Cloudflare is optional. To connect it you need the blog's domain proxied by Cloudflare, and the server's
  `Blog__Secrets__MasterKey` set.

## How pictures get in

You don't upload pictures in the app: your agent does. Ask it to add a picture to a post. It has two ways:

- `upload_media`, when the agent has the file. The server checks the bytes after the upload.
- `upload_media_from_url`, when the picture is on the web. The server fetches it, from the public internet only.

PNG, JPEG, GIF and WebP are kept, up to 10 MiB each by default. SVG isn't, because it can carry script. The agent gets
back Markdown to put in the post, such as `![A red bicycle](/media/…/bicycle.jpg)`.

## See and manage pictures

1. Open the **Posts** tab and tap **Media**.
2. **In use** shows the pictures with their description, size, who added them and when.
   - **Markdown** copies the text to embed the picture in a post.
   - **Trash** takes it off the site. Readers stop getting it at once; browsers may keep a copy for up to an hour.
3. **Trash** (the tab) shows trashed pictures. **Restore** puts one back at the same address.

A trashed picture keeps its bytes in the bucket. Nothing is deleted for good from the app.

## Connect Cloudflare

Without Cloudflare, pictures are cached for an hour. Connected, Cloudflare keeps each one for days, so readers don't
cost bucket traffic after the first view, and trashing a picture empties it from Cloudflare's cache at once.

1. In Cloudflare, open your blog's domain. Its **Overview** page shows the **Zone ID**. Copy it.
2. Go to My Profile, **API Tokens**, **Create Token**, and make a custom token with one permission:
   **Zone → Cache Purge → Purge**, for this zone only. Nothing else: the token can only empty caches.
3. Copy the token. Cloudflare shows it once.
4. In the app: **Posts** → **Media**, scroll to **Cloudflare**, tap **Connect Cloudflare**.
5. Paste the **Zone ID** and the **API token**.
6. Under **How long Cloudflare keeps a picture**, pick **1 day**, **7 days** or **30 days**.
7. Tap **Connect**.

The blog tries the token first, with a purge of a tag no picture carries. A token that can't purge this zone is
refused there, with Cloudflare's reason. The token is kept encrypted and never shown again.

Once connected, the card says "Connected", the zone and how long the edge keeps pictures, how many trashed pictures
are waiting to leave the edge, and when the last purge was.

## Disconnect

Tap **Disconnect** on the Cloudflare card and confirm. Every picture is purged from Cloudflare first, then the token is
deleted. Pictures go back to an hour at the edge.

## If something goes wrong

- "The zone id is 32 letters and digits: Cloudflare shows it on the domain's Overview page, under API."
- "Paste the API token Cloudflare showed once when you made it." The token is too short or too long.
- "Cloudflare refused the token (403): it needs Zone → Cache Purge on this zone." The token has another permission, or
  is for another zone. Make a new one as in step 2.
- "Cloudflare doesn't know that zone (404): check the zone id."
- **"Purging is failing since …"**: Cloudflare was busy or didn't answer. The blog tries again every 30 seconds.
- **A refused token after connecting** (you revoked it, or it lost its permission): the card shows Cloudflare's reason
  and "Trashed pictures can stay at Cloudflare for up to …", and you get one notification, "Cloudflare stopped taking
  purges". New copies get an hour until it works. Disconnect, make a new token and connect again.
- "Cloudflare didn't take the last purge (…): pictures it already holds can stay at its edge for up to …" after
  Disconnect: it disconnected anyway. Purge the cache from Cloudflare's dashboard if a picture must go now.
- "The token doesn't open (the master key changed?): connect again."
- "Secrets can't be stored yet: set Blog__Secrets__MasterKey on the server (32 random bytes: openssl rand -hex 32) and
  redeploy."
- An agent may report "The blog's pictures take … of its … bytes, and this one is …". The blog is full (1 GiB by
  default, trash included, so trashing a picture doesn't make room). Whoever runs the server can raise
  `Blog__Bucket__max_blog_bytes`.
