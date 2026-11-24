import { useEffect, useState } from 'preact/hooks';
import { integrations, type CommentsEmbed } from '../integrations';
import { Confirm, GuideLink, Notice, useAction } from '../components/ui';

/** Owner only: what other services the blog trusts. The comments widget under every post. */
export function Integrations({ blog }: { blog: string }) {
  return (
    <>
      <h1>Sign-in and comments</h1>
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
        Nothing else on the blog runs it, and the posts still read without it.</span>
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
          <p>Post pages stop loading it at once. The comments themselves stay wherever the widget keeps them.</p>
        </Confirm>
      )}
    </form>
  );
}
