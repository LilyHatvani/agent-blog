# Self-hosting: the master key and the bucket

Two settings on your server turn on most of what the app can do beyond posts. The master key lets the blog keep
webhook addresses and tokens encrypted. The bucket is where pictures live.

## Before you start

- Access to your host's variables: the **Variables** tab of the blog's service on Railway, or the `environment:` of
  the `blog` service in `compose.yaml` for Docker Compose.
- For pictures: an S3-compatible bucket. Cloudflare R2 is the recommendation, because it charges nothing for pictures
  leaving the bucket.

With Docker Compose, put the values in `.env` next to `compose.yaml` under these names, which `compose.yaml` passes on:
`BLOG_SECRETS_MASTER_KEY`, `BLOG_SECRETS_PREVIOUS_KEYS`, `BUCKET_ENDPOINT`, `BUCKET_NAME`, `BUCKET_ACCESS_KEY`,
`BUCKET_SECRET_KEY`, `BUCKET_REGION` (default `auto`) and `BUCKET_URL_STYLE` (default `path`).

## Set the master key

Without it, these don't work and say so: notifications (Discord, Slack, ntfy, webhooks and your phones), connecting
Cloudflare, and announcing posts on your social accounts. Everything else runs.

1. On your own computer, run `openssl rand -hex 32`. It prints 64 hex digits. (A 32-byte key in base64 works too.)
2. Set it as `Blog__Secrets__MasterKey` in your host's variables. (A deploy from the Railway template generates one
   for you; on a service you set up by hand, paste the one you made.)
3. Redeploy.
4. Keep a copy somewhere safe, such as a password manager. Everything sealed under the key needs it, and it isn't in
   any database backup.

Never paste the key into a chat, an issue or an agent conversation, and never commit it. Whoever has it and a copy of
the database can open every stored token.

To check it worked: **Account** → **Notifications** shows **Turn on for this device** and the **Add …** buttons, with no
note about secrets.

## Change the master key

Only when you must, for example when the old one leaked.

1. Make a new key with `openssl rand -hex 32`.
2. Set `Blog__Secrets__MasterKey` to the new key, and `Blog__Secrets__PreviousKeys` to the old one. Several old keys
   go comma-separated.
3. Redeploy. New values are sealed under the new key; the old key still opens what it sealed.

This version of the blog doesn't re-seal stored values under the new key. Keep the old key in
`Blog__Secrets__PreviousKeys` for as long as you use what was set up under it: your channels, the Cloudflare connection,
and the key that phone notifications are signed with. If you remove it, those stop opening. Channels can be removed and
added again, and Cloudflare connected again; phone notifications can't be repaired from the app.

## Wire a bucket: Cloudflare R2

1. In Cloudflare, open **R2** and create a bucket, for example `blog-media`. Leave public access off: the blog serves
   pictures itself.
2. In R2, create an API token with **Object Read & Write**, for that one bucket only. Copy the Access Key ID and the
   Secret Access Key. Cloudflare shows the secret once.
3. Note your account's S3 endpoint, `https://<account id>.r2.cloudflarestorage.com`.
4. Set these variables and redeploy:

   ```
   Blog__Bucket__endpoint=https://<account id>.r2.cloudflarestorage.com
   Blog__Bucket__name=blog-media
   Blog__Bucket__access_key=<Access Key ID>
   Blog__Bucket__secret_key=<Secret Access Key>
   Blog__Bucket__region=auto
   Blog__Bucket__url_style=path
   ```

The first four turn pictures on. The endpoint is the store's address without the bucket name.

Optional limits: `Blog__Bucket__max_bytes` per picture (default `10485760`, 10 MiB) and `Blog__Bucket__max_blog_bytes`
per blog, trash included (default `1073741824`, 1 GiB).

To check it worked: **Posts** → **Media** no longer says the server has no bucket. Ask your agent to add a picture to a
draft.

## Other buckets

- **A Railway bucket**: reference its variables, `${{bucket.ENDPOINT}}`, `${{bucket.REGION}}`,
  `${{bucket.ACCESS_KEY_ID}}` and `${{bucket.SECRET_ACCESS_KEY}}`, and set `Blog__Bucket__url_style=virtual`. Railway
  bills for traffic leaving the bucket, so put Cloudflare in front of the blog
  ([Pictures, and putting them behind Cloudflare](pictures-and-cloudflare.md)).
- **MinIO, AWS S3 and others**: the store's address, its region (`us-east-1` is the default), keys for that bucket, and
  usually `url_style=path`. AWS uses `virtual`.

## If something goes wrong

The blog refuses to start on a setting it can't use, and says which:

- "Blog__Secrets__MasterKey must be 32 random bytes, as base64 or 64 hex digits (openssl rand -hex 32 prints one)." The
  value isn't 64 hex digits or 44 characters of base64; a copy that lost a character is the usual cause.
- "Blog__Secrets__PreviousKeys is set without Blog__Secrets__MasterKey: set the current key too."
- "Blog:Bucket:endpoint must be an absolute http(s) URL."
- "Blog:Bucket:url_style must be path or virtual."
- "Blog:Bucket:url_style virtual needs a bucket name that can be a host name: lower-case letters, digits, '-' and '.'."

While running:

- "Secrets can't be stored yet: set Blog__Secrets__MasterKey on the server (32 random bytes: openssl rand -hex 32) and
  redeploy." The key isn't reaching the server. With Compose, check `compose.yaml` passes it.
- "This blog has no bucket configured, so it can't keep pictures yet." One of the first four bucket variables is
  missing.
- "The bucket answered PUT with 403." or "…GET with 403.": the keys are wrong, or the token isn't for this bucket.
- "Its secret doesn't open (the master key changed?)" on a channel, or "The token doesn't open (the master key
  changed?)" for Cloudflare: the key that sealed it is no longer set. Put it back in `Blog__Secrets__PreviousKeys`.
- If the old key is lost for good: notifications to your phones start over with a new key (each phone says to turn
  notifications on again, under Notifications), and channels, the Cloudflare connection and social accounts each say
  their secret doesn't open: remove and add them again.
