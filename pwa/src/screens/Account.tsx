import { useEffect, useState } from 'preact/hooks';
import { api, type BlogSettings, type Me } from '../api';
import { readDisplay, saveDisplay, type Display, type TextChoice, type ThemeChoice } from '../display';
import { Confirm, Notice, useAction } from '../components/ui';

const providerName = (p: string) => (p === 'google' ? 'Google' : p === 'discord' ? 'Discord' : p);

/** The blog's title and description, display settings, your ways in (a second one is how you get back if you lose the first), sign out. */
export function Account({ me, blogId, onRenamed }: { me: Me; blogId: string; onRenamed: (title: string) => void }) {
  const { busy, error, run } = useAction();
  const [toRemove, setToRemove] = useState<Me['identities'][number] | null>(null);
  const [identities, setIdentities] = useState(me.identities);
  const missing = me.providers.filter((p) => !identities.some((i) => i.provider === p));

  return (
    <>
      <h1>Account</h1>
      <p>Signed in as <strong>{me.account.display_name}</strong>.</p>

      <h2>This blog</h2>
      <BlogForm blogId={blogId} onRenamed={onRenamed} />

      <h2>Display</h2>
      <DisplayForm />

      <h2>Ways to sign in</h2>
      {identities.map((i) => (
        <div class="card" key={i.provider + i.account_id}>
          <div class="card-head">
            <span><strong>{providerName(i.provider)}</strong> <span class="quiet">{i.display_name ?? i.account_id}</span></span>
            {i.this_session
              ? <span class="quiet small">this session</span>
              : <button class="ghost small danger-text" disabled={busy || identities.length < 2} onClick={() => setToRemove(i)}
                  title={identities.length < 2 ? "It's your only way in." : undefined}>Remove</button>}
          </div>
        </div>
      ))}
      {missing.length > 0 && (
        <>
          <div class="row">
            {missing.map((p) => (
              <button class="ghost" disabled={busy} onClick={() => run(async () => { location.href = (await api.bind(p)).url; })}>
                Add {providerName(p)}
              </button>
            ))}
          </div>
          <p class="quiet small">A second way in is how you get back if you lose the first.</p>
        </>
      )}

      {error && <Notice kind="error">{error}</Notice>}
      <h2>Sign out</h2>
      <div class="row">
        <button class="ghost" disabled={busy} onClick={() => run(async () => { await api.signOut(); location.href = '/sign-in'; })}>Sign out</button>
        <button class="ghost" disabled={busy} onClick={() => run(async () => { await api.signOutEverywhere(); location.href = '/sign-in'; })}>
          Sign out everywhere
        </button>
      </div>

      {toRemove && (
        <Confirm title={`Remove ${providerName(toRemove.provider)}?`} confirm="Remove" danger
          onCancel={() => setToRemove(null)}
          onConfirm={() => {
            const i = toRemove;
            setToRemove(null);
            run(() => api.unbind(i.provider, i.account_id)).then((ok) => ok && setIdentities(identities.filter((x) => x !== i)));
          }}>
          <p>You won't be able to sign in with it any more, and the sessions it started end.</p>
        </Confirm>
      )}
    </>
  );
}

/** The blog's title and description: one form, Cancel and Save always shown, enabled once something changed. */
function BlogForm({ blogId, onRenamed }: { blogId: string; onRenamed: (title: string) => void }) {
  const [saved, setSaved] = useState<BlogSettings | null>(null);
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [load, setLoad] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const { busy, error, run } = useAction();

  const reset = (b: BlogSettings) => { setSaved(b); setTitle(b.title); setDescription(b.description); };
  useEffect(() => { api.blog(blogId).then(reset).catch((e) => setLoad(e.message)); }, [blogId]);

  if (load) return <Notice kind="error">{load}</Notice>;
  if (!saved) return <p class="quiet">Loading…</p>;
  const dirty = title.trim() !== saved.title || description.trim() !== saved.description;
  const save = () => {
    // Only what changed is sent: a field left out stays as it is.
    const patch: { title?: string; description?: string } = {};
    if (title.trim() !== saved.title) patch.title = title.trim();
    if (description.trim() !== saved.description) patch.description = description.trim();
    run(() => api.saveBlog(blogId, patch).then((b) => { reset(b); onRenamed(b.title); setDone(true); setTimeout(() => setDone(false), 2000); }));
  };

  return (
    <form class="card form" onSubmit={(e) => { e.preventDefault(); if (dirty) save(); }}>
      <label class="field">
        <span>Title</span>
        <input value={title} maxLength={saved.title_max} required onInput={(e) => setTitle((e.target as HTMLInputElement).value)} />
      </label>
      <label class="field">
        <span>Description</span>
        <textarea value={description} maxLength={saved.description_max} rows={3}
          onInput={(e) => setDescription((e.target as HTMLTextAreaElement).value)} />
        <span class="quiet count">{description.length} / {saved.description_max}</span>
      </label>
      <span class="quiet small">Readers see these at the top of every page, in feeds and in link previews.</span>
      {error && <Notice kind="error">{error}</Notice>}
      {done && <Notice kind="ok">Saved.</Notice>}
      <div class="row end">
        <button type="button" class="ghost" disabled={busy || !dirty} onClick={() => reset(saved)}>Cancel</button>
        <button type="submit" class="primary" disabled={busy || !dirty || title.trim() === ''}>Save</button>
      </div>
    </form>
  );
}

/** Theme and text size for this device. Applied at once: there's nothing to save on the server. */
function DisplayForm() {
  const [d, setD] = useState<Display>(readDisplay);
  const pick = (next: Display) => { setD(next); saveDisplay(next); };
  const themes: [ThemeChoice, string][] = [['system', 'Auto'], ['light', 'Light'], ['dark', 'Dark']];
  const texts: [TextChoice, string][] = [['standard', 'Standard'], ['large', 'Larger']];
  return (
    <div class="card form">
      <div class="field">
        <span id="theme-label">Theme</span>
        <div class="segmented three" role="radiogroup" aria-labelledby="theme-label">
          {themes.map(([v, label]) => (
            <button type="button" role="radio" aria-checked={d.theme === v} class={d.theme === v ? 'seg active' : 'seg'} onClick={() => pick({ ...d, theme: v })}>
              <span class="seg-name">{label}</span>
            </button>
          ))}
        </div>
      </div>
      <div class="field">
        <span id="text-label">Text size</span>
        <div class="segmented two" role="radiogroup" aria-labelledby="text-label">
          {texts.map(([v, label]) => (
            <button type="button" role="radio" aria-checked={d.text === v} class={d.text === v ? 'seg active' : 'seg'} onClick={() => pick({ ...d, text: v })}>
              <span class="seg-name">{label}</span>
            </button>
          ))}
        </div>
      </div>
      <span class="quiet small">Auto follows your device's light or dark setting. These apply on this device only.</span>
    </div>
  );
}
