import { useEffect, useState } from 'preact/hooks';
import { integrations, type CommentsEmbed, type OidcSetting } from '../integrations';
import { Confirm, GuideLink, Notice, useAction } from '../components/ui';

/** Owner only: what other services the blog trusts. A sign-in provider of the owner's choosing, and the comments widget under every post. */
export function Integrations({ blog }: { blog: string }) {
  return (
    <>
      <h1>Sign-in and comments</h1>
      <h2>Your own sign-in provider</h2>
      <p class="quiet small">Let people sign in with an account they have elsewhere (any OpenID Connect provider). <GuideLink guide="sign-in-provider" /></p>
      <OidcForm blog={blog} />
      <h2>Comments widget</h2>
      <p class="quiet small">Comments from another service, under every post. <GuideLink guide="comments-widget" /></p>
      <CommentsForm blog={blog} />
    </>
  );
}

/** One form, Cancel and Save always shown, enabled once something changed; Remove clears the slot. */
function CommentsForm({ blog }: { blog: string }) {
  const [saved, setSaved] = useState<CommentsEmbed | null>(null);
  const [script, setScript] = useState('');
  const [origins, setOrigins] = useState('');
  const [load, setLoad] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [removing, setRemoving] = useState(false);
  const { busy, error, run } = useAction();

  const reset = (e: CommentsEmbed) => { setSaved(e); setScript(e.script_url ?? ''); setOrigins(e.connect_origins.join('\n')); };
  useEffect(() => { integrations.commentsEmbed(blog).then(reset).catch((e) => setLoad(e.message)); }, [blog]);

  if (load) return <Notice kind="error">{load}</Notice>;
  if (!saved) return <p class="quiet">Loading…</p>;
  const list = origins.split(/\s+/).map((o) => o.trim()).filter(Boolean);
  const dirty = script.trim() !== (saved.script_url ?? '') || list.join('\n') !== saved.connect_origins.join('\n');
  const flash = () => { setDone(true); setTimeout(() => setDone(false), 2000); };

  return (
    <form class="card form" onSubmit={(e) => { e.preventDefault(); if (dirty && script.trim()) run(() => integrations.saveCommentsEmbed(blog, script.trim(), list).then((r) => { reset(r); flash(); })); }}>
      <label class="field">
        <span>The widget's script</span>
        <input value={script} type="url" inputMode="url" placeholder="https://comments.example.com/widget.js" spellcheck={false}
          onInput={(e) => setScript((e.target as HTMLInputElement).value)} />
      </label>
      <label class="field">
        <span>Addresses the widget may call</span>
        <textarea value={origins} rows={3} placeholder="https://api.comments.example.com" spellcheck={false}
          onInput={(e) => setOrigins((e.target as HTMLTextAreaElement).value)} />
        <span class="quiet small">One https origin a line, at most {saved.max_connect_origins}. Just the scheme and host, no path.</span>
      </label>
      <span class="quiet small">Every post page then loads this script from {saved.script_origin ?? 'its site'} and gives it the post's id.
        Nothing else on the blog runs it, and the posts still read without it. It runs on this blog's own address, so it can do
        what you or a writer can while signed in and reading a post: use only a service you'd trust with your account.</span>
      {error && <Notice kind="error">{error}</Notice>}
      {done && <Notice kind="ok">Saved.</Notice>}
      <div class="row end">
        {saved.set && <button type="button" class="ghost danger-text" disabled={busy} onClick={() => setRemoving(true)}>Remove</button>}
        <button type="button" class="ghost" disabled={busy || !dirty} onClick={() => reset(saved)}>Cancel</button>
        <button type="submit" disabled={busy || !dirty || !script.trim()}>Save</button>
      </div>
      {removing && (
        <Confirm title="Remove the comments widget?" confirm="Remove" danger onCancel={() => setRemoving(false)}
          onConfirm={() => { setRemoving(false); run(() => integrations.clearCommentsEmbed(blog).then(reset)); }}>
          <p>Post pages stop loading it within a minute. The comments themselves stay wherever the widget keeps them.</p>
        </Confirm>
      )}
    </form>
  );
}

/** The provider: one form, Cancel and Save always shown, enabled once something changed. The secret is write-only. */
function OidcForm({ blog }: { blog: string }) {
  const [saved, setSaved] = useState<OidcSetting | null>(null);
  const [issuer, setIssuer] = useState('');
  const [clientId, setClientId] = useState('');
  const [secret, setSecret] = useState('');
  const [name, setName] = useState('');
  const [load, setLoad] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [removing, setRemoving] = useState(false);
  const { busy, error, run } = useAction();

  const reset = (s: OidcSetting) => { setSaved(s); setIssuer(s.issuer ?? ''); setClientId(s.client_id ?? ''); setSecret(''); setName(s.display_name ?? ''); };
  useEffect(() => { integrations.oidc(blog).then(reset).catch((e) => setLoad(e.message)); }, [blog]);

  if (load) return <Notice kind="error">{load}</Notice>;
  if (!saved) return <p class="quiet">Loading…</p>;
  if (!saved.available) return <Notice kind="info">{saved.reason}</Notice>;
  const dirty = issuer.trim() !== (saved.issuer ?? '') || clientId.trim() !== (saved.client_id ?? '') || name.trim() !== (saved.display_name ?? '') || secret.trim() !== '';
  const ready = issuer.trim() && clientId.trim() && name.trim() && (saved.secret_set || secret.trim());
  const save = () => run(() => integrations.saveOidc(blog, {
    issuer: issuer.trim(), client_id: clientId.trim(), display_name: name.trim(),
    // Left out, the stored secret stays.
    ...(secret.trim() ? { client_secret: secret.trim() } : {}),
  }).then((s) => { reset(s); setDone(true); setTimeout(() => setDone(false), 2000); }));

  return (
    <form class="card form" onSubmit={(e) => { e.preventDefault(); if (dirty && ready) save(); }}>
      <p class="small">At the provider, register this blog with the redirect address <code>{saved.redirect_uri}</code>, then copy what it gives you here.</p>
      <label class="field">
        <span>Issuer</span>
        <input value={issuer} type="url" inputMode="url" placeholder="https://accounts.example.com" spellcheck={false}
          onInput={(e) => setIssuer((e.target as HTMLInputElement).value)} />
      </label>
      <label class="field">
        <span>Client id</span>
        <input value={clientId} spellcheck={false} autocomplete="off" onInput={(e) => setClientId((e.target as HTMLInputElement).value)} />
      </label>
      <label class="field">
        <span>Client secret</span>
        <input value={secret} type="password" autocomplete="off" placeholder={saved.secret_set ? 'Saved. Type a new one to replace it.' : ''}
          onInput={(e) => setSecret((e.target as HTMLInputElement).value)} />
      </label>
      <label class="field">
        <span>Name on the button</span>
        <input value={name} maxLength={saved.max_display_name} placeholder="Echo of Dreams" onInput={(e) => setName((e.target as HTMLInputElement).value)} />
        <span class="quiet small">The sign-in page says "Sign in with {name.trim() || '…'}".</span>
      </label>
      <span class="quiet small">Saving checks the issuer answers as itself. Afterwards, add it to your own account under Ways to sign in, and
        invitations can be accepted with it. An account is matched by the provider's id for it, never by email.</span>
      {error && <Notice kind="error">{error}</Notice>}
      {done && <Notice kind="ok">Saved.</Notice>}
      <div class="row end">
        {saved.set && <button type="button" class="ghost danger-text" disabled={busy} onClick={() => setRemoving(true)}>Remove</button>}
        <button type="button" class="ghost" disabled={busy || !dirty} onClick={() => reset(saved)}>Cancel</button>
        <button type="submit" disabled={busy || !dirty || !ready}>Save</button>
      </div>
      {removing && (
        <Confirm title={`Remove ${saved.display_name}?`} confirm="Remove" danger onCancel={() => setRemoving(false)}
          onConfirm={() => { setRemoving(false); run(() => integrations.removeOidc(blog).then(reset)); }}>
          <p>Its button leaves the sign-in page. Whoever signs in only with it needs another way in; their link comes back if you set
            the same issuer again.</p>
        </Confirm>
      )}
    </form>
  );
}
