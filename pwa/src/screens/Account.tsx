import { useEffect, useState } from 'preact/hooks';
import { api, when, type BlogSettings, type Brief, type Me, type PostSummary, type Role } from '../api';
import { readDisplay, saveDisplay, type Display, type TextChoice, type ThemeChoice } from '../display';
import { Confirm, GuideLink, Notice, useAction } from '../components/ui';
import { createPasskey, passkeysSupported } from '../passkey';

const providerName = (p: string) => (p === 'google' ? 'Google' : p === 'discord' ? 'Discord' : p === 'passkey' ? 'Passkey' : p);

/** The blog's title and description, display settings, your ways in (a second one is how you get back if you lose the first), sign out. */
export function Account({ me, blogId, role, blogTitle, onRenamed }: { me: Me; blogId: string; role: Role; blogTitle: string; onRenamed: (title: string) => void }) {
  const membership = me.blogs.find((b) => b.id === blogId);
  const authorPath = membership?.author_path;
  const { busy, error, run } = useAction();
  const [toRemove, setToRemove] = useState<Me['identities'][number] | null>(null);
  const [identities, setIdentities] = useState(me.identities);
  const missing = me.providers.filter((p) => !identities.some((i) => i.provider === p));

  return (
    <>
      <h1>Account</h1>
      <p>Signed in as <strong>{me.account.display_name}</strong>.</p>
      {authorPath && (
        <p class="quiet small">Your author page: <a href={authorPath} target="_blank" rel="noopener">{authorPath}</a>. Readers reach it from your
          name under your posts, once one is live and bylines are on.</p>
      )}
      {authorPath && <AuthorLinkForm blogId={blogId} saved={membership?.author_profile_url ?? ''} />}

      {role !== 'owner' ? (
        <p>You write for <strong>{blogTitle}</strong> as {role === 'editor'
          ? 'an editor: you write, edit any post, publish and schedule'
          : 'a writer: you write drafts, edit your own, and submit them for review'}. The owner keeps the settings, agents
          and who is on the blog. Readers see you by the name above.</p>
      ) : <>
      <h2>This blog</h2>
      <BlogForm blogId={blogId} onRenamed={onRenamed} />

      <h2>How this blog sounds</h2>
      <p class="quiet small"><GuideLink guide="voice-and-feedback">What agents do with it</GuideLink></p>
      <BriefForm blogId={blogId} />

      <h2>Notifications</h2>
      <p><a href="#/notifications">Where the blog tells you what needs you</a>: this phone, Discord, Slack, ntfy or a webhook.</p>

      <h2>Announce</h2>
      <p><a href="#/announce">Where new posts are announced</a>: Bluesky, Mastodon, Discord, Slack, Telegram, X, or by email to your subscribers.</p>

      <h2>People</h2>
      <p><a href="#/account/people">Who writes for this blog</a>: invite writers and editors, change their roles, remove them.</p>

      <h2>Import</h2>
      <p><a href="#/account/import">Bring a blog over from Echo of Dreams</a>: its posts, pictures and authors.</p>
      </>}

      <h2>Display</h2>
      <DisplayForm />

      <h2>Ways to sign in</h2>
      {identities.map((i) => (
        <div class="card" key={i.provider + i.account_id}>
          <div class="card-head">
            <span><strong>{providerName(i.provider)}</strong> <span class="quiet">{i.provider === 'passkey'
              ? `added ${when(i.created_at)}`
              : i.display_name ?? i.account_id}</span></span>
            {i.this_session
              ? <span class="quiet small">this session</span>
              : <button class="ghost small danger-text" disabled={busy || identities.length < 2} onClick={() => setToRemove(i)}
                  title={identities.length < 2 ? "It's your only way in." : undefined}>Remove</button>}
          </div>
        </div>
      ))}
      <div class="row">
        <button class="ghost" disabled={busy || !passkeysSupported()} title={passkeysSupported() ? undefined : "This browser can't make passkeys."}
          onClick={() => run(async () => {
            const { ceremony, options } = await api.beginPasskey();
            let credential: unknown;
            // Cancelling the device's prompt changes nothing and says nothing.
            try { credential = await createPasskey(options); } catch (e) { if ((e as Error).name === 'NotAllowedError') return; throw e; }
            const added = await api.finishPasskey(ceremony, credential);
            setIdentities([...identities, { provider: added.provider, account_id: added.account_id, display_name: null,
              created_at: new Date().toISOString(), this_session: false }]);
          })}>
          Add a passkey
        </button>
        {missing.map((p) => (
          <button class="ghost" disabled={busy} onClick={() => run(async () => { location.href = (await api.bind(p)).url; })}>
            Add {providerName(p)}
          </button>
        ))}
      </div>
      <p class="quiet small">A second way in is how you get back if you lose the first. A passkey lives on this phone or computer
        and signs you in with your face, fingerprint or screen lock.</p>

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
  const [theme, setTheme] = useState('paper');
  const [picker, setPicker] = useState(false);
  const [ping, setPing] = useState(true);
  const [bylines, setBylines] = useState(true);
  const [authorName, setAuthorName] = useState('');
  const [load, setLoad] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const { busy, error, run } = useAction();

  const reset = (b: BlogSettings) => {
    setSaved(b); setTitle(b.title); setDescription(b.description); setTheme(b.theme); setPicker(b.theme_picker); setPing(b.search_ping); setBylines(b.bylines); setAuthorName(b.author_name);
  };
  useEffect(() => { api.blog(blogId).then(reset).catch((e) => setLoad(e.message)); }, [blogId]);

  if (load) return <Notice kind="error">{load}</Notice>;
  if (!saved) return <p class="quiet">Loading…</p>;
  const dirty = title.trim() !== saved.title || description.trim() !== saved.description || theme !== saved.theme || picker !== saved.theme_picker || ping !== saved.search_ping || bylines !== saved.bylines || authorName.trim() !== saved.author_name;
  const save = () => {
    // Only what changed is sent: a field left out stays as it is.
    const patch: { title?: string; description?: string; theme?: string; theme_picker?: boolean; search_ping?: boolean; bylines?: boolean; author_name?: string } = {};
    if (title.trim() !== saved.title) patch.title = title.trim();
    if (description.trim() !== saved.description) patch.description = description.trim();
    if (theme !== saved.theme) patch.theme = theme;
    if (picker !== saved.theme_picker) patch.theme_picker = picker;
    if (ping !== saved.search_ping) patch.search_ping = ping;
    if (bylines !== saved.bylines) patch.bylines = bylines;
    if (authorName.trim() !== saved.author_name) patch.author_name = authorName.trim();
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

      <fieldset class="field themes-pick">
        <legend>Theme for readers</legend>
        {saved.themes.map((t) => (
          <label class={theme === t.name ? 'theme-option active' : 'theme-option'} key={t.name}>
            <input type="radio" name="theme" value={t.name} checked={theme === t.name} onChange={() => setTheme(t.name)} />
            <span class="theme-text">
              <span class="theme-name"><strong>{t.label}</strong>{t.name === saved.theme && <span class="badge-inline">in use</span>}</span>
              <span class="quiet small">{t.description}</span>
            </span>
            <a class="small" href={`/?theme=${t.name === saved.theme ? 'own' : t.name}`} target="_blank" rel="noopener">Preview</a>
          </label>
        ))}
      </fieldset>
      <label class="check">
        <input type="checkbox" checked={picker} onChange={(e) => setPicker((e.target as HTMLInputElement).checked)} />
        <span>
          Let readers try the themes
          <span class="quiet small">A bar at the top of your blog switches between them, for whoever is reading, without changing yours. Previews always work for you.</span>
        </span>
      </label>
      <label class="check">
        <input type="checkbox" checked={ping} onChange={(e) => setPing((e.target as HTMLInputElement).checked)} />
        <span>
          Tell search engines when a post changes
          <span class="quiet small">Bing and others (IndexNow) and feed readers (WebSub) hear about a post as it goes live, changes or comes down, instead of when they next look. Drafts are never named.</span>
          {saved.pings.map((p) => (
            <span class="quiet small">{p.service === 'indexnow' ? 'IndexNow' : 'WebSub'}: {p.error ?? `told ${when(p.at)}`}</span>
          ))}
        </span>
      </label>
      <label class="check">
        <input type="checkbox" checked={bylines} onChange={(e) => setBylines((e.target as HTMLInputElement).checked)} />
        <span>
          Show who wrote each post
          <span class="quiet small">Under each post, in the feeds and to search engines: "Written by Claude · approved by you", from each post's history. Off, the blog alone is named. <GuideLink guide="bylines">How it's worked out</GuideLink></span>
        </span>
      </label>
      <label class="field">
        <span>Your name on posts</span>
        <input type="text" maxLength={60} placeholder={saved.author_name_default} disabled={!bylines} value={authorName}
          onInput={(e) => setAuthorName((e.target as HTMLInputElement).value)} />
        <span class="quiet small">Empty: your account's name, {saved.author_name_default}. Each agent's name is under Agents.</span>
      </label>
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
/** Your own link on your author page: where else readers find you (your Echo of Dreams profile, your site). */
function AuthorLinkForm({ blogId, saved: initial }: { blogId: string; saved: string }) {
  const [saved, setSaved] = useState(initial);
  const [url, setUrl] = useState(initial);
  const { busy, error, run } = useAction();
  const dirty = url.trim() !== saved;
  return (
    <form class="card form" onSubmit={(e) => {
      e.preventDefault();
      run(async () => { await api.setAuthorLink(blogId, url.trim()); setSaved(url.trim()); });
    }}>
      <label class="field">
        <span>Your link <span class="quiet small">(on your author page; empty for none)</span></span>
        <input type="url" inputMode="url" maxLength={300} placeholder="https://chat.echoofdreams.eu/@you" value={url}
          onInput={(e) => setUrl((e.target as HTMLInputElement).value)} />
      </label>
      {error && <Notice kind="error">{error}</Notice>}
      <div class="row end">
        <button type="button" class="ghost small" disabled={busy || !dirty} onClick={() => setUrl(saved)}>Cancel</button>
        <button class="primary small" disabled={busy || !dirty}>Save</button>
      </div>
    </form>
  );
}

function DisplayForm() {
  const [d, setD] = useState<Display>(readDisplay);
  const pick = (next: Display) => { setD(next); saveDisplay(next); };
  const themes: [ThemeChoice, string][] = [['system', 'Auto'], ['light', 'Light'], ['dark', 'Dark']];
  const texts: [TextChoice, string][] = [['standard', 'Standard'], ['large', 'Larger']];
  return (
    <div class="card form">
      <div class="field">
        <span id="theme-label">Light or dark</span>
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

/**
 * The brief: who the blog is for, its voice, what never to do, posts that show it. Every agent session gets it first,
 * marked as your own words (decision 0008). One form, Cancel and Save.
 */
function BriefForm({ blogId }: { blogId: string }) {
  const [saved, setSaved] = useState<Brief | null>(null);
  const [posts, setPosts] = useState<PostSummary[]>([]);
  const [audience, setAudience] = useState('');
  const [voice, setVoice] = useState('');
  const [avoid, setAvoid] = useState('');
  const [examples, setExamples] = useState<string[]>([]);
  const [load, setLoad] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const { busy, error, run } = useAction();

  const reset = (b: Brief) => { setSaved(b); setAudience(b.audience); setVoice(b.voice); setAvoid(b.avoid); setExamples(b.examples.map((e) => e.id)); };
  useEffect(() => {
    api.brief(blogId).then(reset).catch((e) => setLoad(e.message));
    api.posts(blogId).then((p) => setPosts(p.filter((x) => x.status !== 'trash'))).catch(() => undefined);
  }, [blogId]);

  if (load) return <Notice kind="error">{load}</Notice>;
  if (!saved) return <p class="quiet">Loading…</p>;
  const same = (a: string[], b: string[]) => a.length === b.length && a.every((x, i) => x === b[i]);
  const dirty = audience.trim() !== saved.audience || voice.trim() !== saved.voice || avoid.trim() !== saved.avoid
    || !same(examples, saved.examples.map((e) => e.id));
  const toggle = (id: string) => setExamples(examples.includes(id) ? examples.filter((x) => x !== id)
    : examples.length < saved.limits.examples ? [...examples, id] : examples);
  const save = () => run(() => api.saveBrief(blogId, { audience: audience.trim(), voice: voice.trim(), avoid: avoid.trim(), examples })
    .then((b) => { reset(b); setDone(true); setTimeout(() => setDone(false), 2000); }));

  return (
    <form class="card form" onSubmit={(e) => { e.preventDefault(); if (dirty) save(); }}>
      <span class="quiet small">Every agent reads this first, as your own words, and writes to it. Say it once instead of in every chat.</span>
      <label class="field">
        <span>Who it's for</span>
        <textarea rows={2} value={audience} maxLength={saved.limits.audience} placeholder="e.g. Developers who build with AI agents."
          onInput={(e) => setAudience((e.target as HTMLTextAreaElement).value)} />
      </label>
      <label class="field">
        <span>The voice</span>
        <textarea rows={3} value={voice} maxLength={saved.limits.voice} placeholder="e.g. Plain and direct. Short sentences. Dry humour is fine."
          onInput={(e) => setVoice((e.target as HTMLTextAreaElement).value)} />
      </label>
      <label class="field">
        <span>Never</span>
        <textarea rows={2} value={avoid} maxLength={saved.limits.avoid} placeholder="e.g. No hype words, no emojis, no &quot;in today's world&quot;."
          onInput={(e) => setAvoid((e.target as HTMLTextAreaElement).value)} />
      </label>
      <fieldset class="field examples-pick">
        <legend>Posts that sound right <span class="quiet small">(up to {saved.limits.examples})</span></legend>
        {posts.length === 0 && <span class="quiet small">No posts yet. Come back once a few sound like you.</span>}
        {posts.map((p) => (
          <label class="check" key={p.id}>
            <input type="checkbox" checked={examples.includes(p.id)} disabled={!examples.includes(p.id) && examples.length >= saved.limits.examples}
              onChange={() => toggle(p.id)} />
            <span>{p.title} <span class="quiet small">{p.status === 'live' ? '' : `· ${p.status}`}</span></span>
          </label>
        ))}
      </fieldset>
      {error && <Notice kind="error">{error}</Notice>}
      {done && <Notice kind="ok">Saved. The next agent session reads it.</Notice>}
      <div class="row end">
        <button type="button" class="ghost" disabled={busy || !dirty} onClick={() => reset(saved)}>Cancel</button>
        <button type="submit" class="primary" disabled={busy || !dirty}>Save</button>
      </div>
    </form>
  );
}
