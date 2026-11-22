import { useEffect, useState } from 'preact/hooks';
import { api, bytes, when, type MediaItem } from '../api';
import { Icon } from '../components/Icon';
import { Confirm, Empty, Notice, useAction } from '../components/ui';

/** The blog's pictures: what each is, who added it, the Markdown to embed it, and trash / restore. */
export function Media({ blog }: { blog: string }) {
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
                  <button class="ghost small danger-text" disabled={busy} onClick={() => setToTrash(m)}>Trash</button>
                </>
              ) : (
                <button class="ghost small" disabled={busy} onClick={() => move(m, 'restore')}>Restore</button>
              )}
            </div>
          </article>
        ))}
      </div>

      {toTrash && (
        <Confirm title={`Trash ${toTrash.filename}?`} confirm="Trash" danger
          onCancel={() => setToTrash(null)}
          onConfirm={() => { const m = toTrash; setToTrash(null); move(m, 'trash'); }}>
          <p>Readers stop seeing it at once, also in posts that show it (allow up to an hour for caches). It stays in the trash; restore puts it back at the same address.</p>
        </Confirm>
      )}
    </>
  );
}
