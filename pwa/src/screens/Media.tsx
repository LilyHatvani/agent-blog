import { useEffect, useState } from 'preact/hooks';
import { api, bytes, when, type Cdn, type MediaItem } from '../api';
import { Icon } from '../components/Icon';
import { Confirm, Empty, GuideLink, Notice, useAction } from '../components/ui';

/** The blog's pictures: what each is, who added it, the Markdown to embed it, and trash / restore. */
export function Media({ blog, owner }: { blog: string; owner: boolean }) {
  const [status, setStatus] = useState<'ready' | 'trash'>('ready');
  const [data, setData] = useState<{ enabled: boolean; media: MediaItem[] } | null>(null);
  const [load, setLoad] = useState<string | null>(null);
  const [toTrash, setToTrash] = useState<MediaItem | null>(null);
  const [copied, setCopied] = useState<string | null>(null);
  const { busy, error, run } = useAction();

  const refresh = () => api.media(blog, status).then(setData).catch((e) => setLoad(e.message));
  useEffect(() => { setData(null); refresh(); }, [blog, status]);

  const move = (m: MediaItem, op: 'trash' | 'restore') => run(() => api.moveMedia(m.id, op)).then((ok) => { if (ok) refresh(); });
  const copy = (m: MediaItem) => navigator.clipboard?.writeText(m.markdown).then(() => {
    setCopied(m.id);
    setTimeout(() => setCopied(null), 1500);
  });

  return (
    <>
      <a class="back" href="#/posts"><Icon name="back" /> Posts</a>
      <h1>Media</h1>
      <div class="chips" role="tablist" aria-label="Show">
        {(['ready', 'trash'] as const).map((s) => (
          <button role="tab" aria-selected={status === s} class={status === s ? 'chip active' : 'chip'} onClick={() => setStatus(s)}>
            {s === 'ready' ? 'In use' : 'Trash'}
          </button>
        ))}
      </div>
      {load && <Notice kind="error">{load}</Notice>}
      {error && <Notice kind="error">{error}</Notice>}
      {!data && !load && <p class="quiet">Loading…</p>}
      {data && !data.enabled && (
        <Notice kind="info">This server has no bucket configured, so agents can't add pictures yet. Set <code>Blog__Bucket__*</code> on the server.</Notice>
      )}
      {data?.media.length === 0 && data.enabled && (
        <Empty>{status === 'trash' ? 'The trash is empty.' : 'No pictures yet. An agent adds them with upload_media or upload_media_from_url.'}</Empty>
      )}
      <div class="media-grid">
        {data?.media.map((m) => (
          <article class="card media-card" key={m.id}>
            {m.status === 'ready'
              ? <a href={m.path} target="_blank" rel="noopener"><img class="thumb" src={m.path} alt={m.alt} loading="lazy" /></a>
              : <div class="thumb thumb-gone" aria-hidden="true"><Icon name="image" /></div>}
            <strong class="media-name">{m.filename}</strong>
            <span class="quiet">{m.alt || 'No description'}</span>
            <span class="quiet small">{bytes(m.size_bytes)} · {m.by_label} · {when(m.created_at)}</span>
            {m.source_url && <span class="quiet small media-source">from {new URL(m.source_url).host}</span>}
            <div class="row wrap">
              {m.status === 'ready' ? (
                <>
                  <button class="ghost small" onClick={() => copy(m)}><Icon name={copied === m.id ? 'check' : 'copy'} /> {copied === m.id ? 'Copied' : 'Markdown'}</button>
                  <button class="ghost small danger-text" disabled={busy || !owner} title={owner ? undefined : 'Only the owner trashes pictures.'} onClick={() => setToTrash(m)}>Trash</button>
                </>
              ) : (
                <button class="ghost small" disabled={busy || !owner} title={owner ? undefined : 'Only the owner restores pictures.'} onClick={() => move(m, 'restore')}>Restore</button>
              )}
            </div>
          </article>
        ))}
      </div>

      {!owner && <p class="quiet small why-not">Copy a picture's Markdown into a post. Trashing and restoring pictures is the owner's.</p>}
      {owner && <>
      <h2>Cloudflare</h2>
      <p class="quiet small"><GuideLink guide="pictures-and-cloudflare">How to connect it</GuideLink></p>
      <CloudflareCard blog={blog} />
      </>}

      {toTrash && (
        <Confirm title={`Trash ${toTrash.filename}?`} confirm="Trash" danger
          onCancel={() => setToTrash(null)}
          onConfirm={() => { const m = toTrash; setToTrash(null); move(m, 'trash'); }}>
          <p>Readers stop seeing it at once, also in posts that show it (allow up to an hour for browsers' caches; with Cloudflare connected, its edge lets go at once). It stays in the trash; restore puts it back at the same address.</p>
        </Confirm>
      )}
    </>
  );
}

const edgeChoices = [
  { seconds: 86400, label: '1 day' },
  { seconds: 7 * 86400, label: '7 days' },
  { seconds: 30 * 86400, label: '30 days' },
];

/**
 * Cloudflare in front of the pictures (decision 0009): with the blog's domain on Cloudflare, connecting a zone with a
 * purge-only token lets Cloudflare keep pictures for days (no egress from the bucket per view) and trash still takes
 * them away at once. A rare setting: it sits below the pictures, never above them.
 */
function CloudflareCard({ blog }: { blog: string }) {
  const [cdn, setCdn] = useState<Cdn | null>(null);
  const [open, setOpen] = useState(false);
  const [zone, setZone] = useState('');
  const [token, setToken] = useState('');
  const [edge, setEdge] = useState(7 * 86400);
  const [leaving, setLeaving] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const { busy, error, run } = useAction();
  useEffect(() => { api.cdn(blog).then(setCdn).catch(() => setCdn(null)); }, [blog]);
  if (!cdn) return null;
  const days = (s: number | null) => (s ? `${Math.round(s / 86400)} day${s >= 2 * 86400 ? 's' : ''}` : '');

  if (cdn.connected) return (
    <div class="card">
      <div class="card-head">
        <span><strong>Connected</strong> <span class="quiet small">zone {cdn.zone_id?.slice(0, 6)}… · edge keeps pictures {days(cdn.edge_seconds)}</span></span>
        <button class="ghost small danger-text" disabled={busy} onClick={() => setLeaving(true)}>Disconnect</button>
      </div>
      {cdn.refused && <Notice kind="error">{cdn.last_error} Trashed pictures can stay at Cloudflare for up to {days(cdn.edge_seconds)}; new copies get an hour until it works again.</Notice>}
      {!cdn.refused && cdn.failing_since && <Notice kind="error">Purging is failing since {when(cdn.failing_since)}: {cdn.last_error}</Notice>}
      <p class="quiet small">
        {cdn.waiting > 0 ? `${cdn.waiting} trashed picture${cdn.waiting === 1 ? '' : 's'} waiting to leave the edge. ` : ''}
        {cdn.last_purge_at ? `Last purge ${when(cdn.last_purge_at)}.` : 'Nothing purged yet.'}
      </p>
      {note && <Notice kind="info">{note}</Notice>}
      {error && <Notice kind="error">{error}</Notice>}
      {leaving && (
        <Confirm title="Disconnect Cloudflare?" confirm="Disconnect" danger onCancel={() => setLeaving(false)}
          onConfirm={() => run(async () => { setLeaving(false); const r = await api.disconnectCdn(blog); setNote(r.note); setCdn(await api.cdn(blog)); })}>
          Every picture is purged from Cloudflare's edge first, then the token is deleted here. Pictures go back to an hour at the edge.
        </Confirm>
      )}
    </div>
  );

  return (
    <div class="card">
      <p class="quiet small">If this blog's domain is on Cloudflare (proxied), connect it: Cloudflare keeps pictures for days instead of an hour, so readers don't cost bucket egress, and trashing a picture still takes it away at once. Without it, pictures are cached for an hour.</p>
      {note && <Notice kind="info">{note}</Notice>}
      {!open ? (
        <div class="row"><button class="ghost" onClick={() => setOpen(true)}>Connect Cloudflare</button></div>
      ) : (
        <div class="stack">
          <p class="quiet small">In Cloudflare: the domain's Overview shows the <strong>Zone ID</strong>. Then My Profile, API Tokens, Create Token, Custom: permission <strong>Zone → Cache Purge → Purge</strong>, for this zone only. Nothing else: the token can only empty caches.</p>
          <label class="field">Zone ID<input value={zone} autocomplete="off" spellcheck={false} onInput={(e) => setZone((e.target as HTMLInputElement).value)} /></label>
          <label class="field">API token<input value={token} type="password" autocomplete="off" onInput={(e) => setToken((e.target as HTMLInputElement).value)} /></label>
          <fieldset>
            <legend>How long Cloudflare keeps a picture</legend>
            {edgeChoices.map((c) => (
              <label class="check"><input type="radio" name="edge" checked={edge === c.seconds} onChange={() => setEdge(c.seconds)} /> {c.label}</label>
            ))}
          </fieldset>
          {error && <Notice kind="error">{error}</Notice>}
          <div class="row end">
            <button class="ghost" disabled={busy} onClick={() => setOpen(false)}>Cancel</button>
            <button class="primary" disabled={busy || !zone.trim() || !token.trim()}
              onClick={() => run(async () => { setCdn(await api.connectCdn(blog, zone, token, edge)); setToken(''); setOpen(false); })}>Connect</button>
          </div>
        </div>
      )}
    </div>
  );
}
