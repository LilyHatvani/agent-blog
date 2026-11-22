import { useEffect, useState } from 'preact/hooks';
import { api, when, type Check, type PostDetail, type RevisionContent } from '../api';
import { CheckSummary } from '../components/Check';
import { Icon } from '../components/Icon';
import { Confirm, Notice, Preview, StatusPill, useAction } from '../components/ui';
import { Editor } from '../components/Editor';

/** One post: its preview as readers see it, what can be done to it now, and its history with restore. */
export function PostView({ id }: { id: string }) {
  const [detail, setDetail] = useState<PostDetail | null>(null);
  const [load, setLoad] = useState<string | null>(null);
  const [viewing, setViewing] = useState<RevisionContent | null>(null);
  const [scheduling, setScheduling] = useState(false);
  const [at, setAt] = useState('');
  const [confirmTrash, setConfirmTrash] = useState(false);
  const [editing, setEditing] = useState(false);
  const [check, setCheck] = useState<Check | null>(null);
  const [checking, setChecking] = useState(false);
  const [checkError, setCheckError] = useState<string | null>(null);
  const [editError, setEditError] = useState<string | null>(null);
  const { busy, error, run } = useAction();

  const refresh = () => api.post(id).then(setDetail).catch((e) => setLoad(e.message));
  useEffect(() => { refresh(); }, [id]);

  if (load) return <Notice kind="error">{load}</Notice>;
  if (!detail) return <p class="quiet">Loading…</p>;
  const { post } = detail;
  const act = (op: 'publish' | 'schedule' | 'unpublish' | 'trash' | 'restore', body?: { base_revision?: string; at?: string }) =>
    run(() => api.act(post.id, op, body)).then((ok) => { if (ok) refresh(); });

  return (
    <>
      <a class="back" href="#/posts"><Icon name="back" /> Posts</a>
      <div class="card-head"><h1>{post.title}</h1><StatusPill status={post.status} /></div>
      <p class="quiet">
        Revision {post.revision_number} · changed {when(post.updated_at)}
        {post.status === 'live' && <> · <a href={`/posts/${post.slug}`} target="_blank" rel="noopener">open on the site</a></>}
        {post.status === 'scheduled' && post.publish_at && <> · goes live {new Date(post.publish_at + 'Z').toLocaleString()}</>}
      </p>

      {post.terms.length > 0 && (
        <div class="terms-row" aria-label="Categories and tags">
          {post.terms.map((t) => <span class={`term-chip ${t.kind}`}>{t.kind === 'tag' ? '#' : ''}{t.name}</span>)}
        </div>
      )}
      {post.suggested.length > 0 && (
        <div class="card">
          <strong>Suggested by the agent</strong>
          <span class="quiet small">Names it used that don't exist on the blog yet. Add makes them, and puts them on this post.</span>
          {post.suggested.map((s) => (
            <div class="suggestion" key={s.kind + s.name}>
              <span>{s.kind === 'tag' ? 'Tag' : 'Category'}: <strong>{s.name}</strong></span>
              <span class="row">
                <button class="ghost small" disabled={busy} onClick={() => run(() => api.decideSuggestion(post.id, 'dismiss', s.kind, s.name)).then((ok) => { if (ok) refresh(); })}>Dismiss</button>
                <button class="primary small" disabled={busy} onClick={() => run(() => api.decideSuggestion(post.id, 'accept', s.kind, s.name)).then((ok) => { if (ok) refresh(); })}>Add</button>
              </span>
            </div>
          ))}
        </div>
      )}

      {error && <Notice kind="error">{error}</Notice>}
      <div class="row wrap">
        {(post.status === 'draft' || post.status === 'review') && (
          <>
            <button class="primary" disabled={busy} onClick={() => act('publish', { base_revision: post.revision })}>Publish now</button>
            <button class="ghost" disabled={busy} onClick={() => setScheduling(!scheduling)} aria-expanded={scheduling}>Schedule…</button>
          </>
        )}
        {(post.status === 'live' || post.status === 'scheduled') && (
          <button class="ghost" disabled={busy} onClick={() => act('unpublish')}>Back to draft</button>
        )}
        {post.status === 'trash'
          ? <button class="primary" disabled={busy} onClick={() => act('restore')}>Restore</button>
          : <button class="ghost danger-text" disabled={busy} onClick={() => setConfirmTrash(true)}>Move to trash</button>}
      </div>
      {scheduling && (
        <form class="row" onSubmit={(e) => {
          e.preventDefault();
          if (at) act('schedule', { base_revision: post.revision, at: new Date(at).toISOString() }).then(() => setScheduling(false));
        }}>
          <label class="field">
            <span>Goes live at</span>
            <input type="datetime-local" required value={at} onInput={(e) => setAt((e.target as HTMLInputElement).value)} />
          </label>
          <button class="primary" disabled={busy || !at}>Schedule</button>
        </form>
      )}

      {editing ? (
        <Editor start={{ title: post.title, excerpt: post.excerpt, body_md: detail.body_md }} saveLabel="Save" busy={busy} error={editError}
          onCancel={() => { setEditing(false); setEditError(null); }}
          onSave={(edits) => {
            setEditError(null);
            api.editPost(post.id, { base_revision: post.revision, ...edits })
              .then(() => { setEditing(false); refresh(); })
              .catch((e) => setEditError(e.status === 409
                ? 'The post changed while you were editing (an agent saved a new revision). Your text is still here: copy what you need, cancel, and edit the new version.'
                : e.message));
          }}>
          {post.status === 'live' && <Notice kind="info">This post is live: saving changes it for readers at once.</Notice>}
        </Editor>
      ) : (
        <>
          <div class="heading-row">
            <h2>Preview</h2>
            <span class="row">
              {post.status !== 'trash' && <button class="ghost small" disabled={checking} onClick={() => {
                setChecking(true);
                api.check(post.id).then(setCheck).catch((e) => setCheckError(e.message)).finally(() => setChecking(false));
              }}>{checking ? 'Checking…' : 'Check'}</button>}
              {post.status !== 'trash' && <button class="ghost small" onClick={() => setEditing(true)}>Edit</button>}
            </span>
          </div>
          {checkError && <Notice kind="error">{checkError}</Notice>}
          {check && <CheckSummary check={check} open />}
          <div class="sheet"><Preview html={detail.html} /></div>
        </>
      )}

      <h2>History</h2>
      <ol class="revisions">
        {detail.revisions.map((r) => (
          <li key={r.id}>
            <button class="row-button" onClick={() => api.revision(post.id, r.id).then(setViewing)}>
              <span><strong>Revision {r.number}</strong> by {r.by_label}</span>
              <span class="quiet">{when(r.at)}</span>
            </button>
          </li>
        ))}
      </ol>

      {viewing && (
        <div class="backdrop" onClick={(e) => e.target === e.currentTarget && setViewing(null)}>
          <div class="dialog wide" role="dialog" aria-modal="true" aria-labelledby="rev-title">
            <h2 id="rev-title">Revision {viewing.number} <span class="quiet">by {viewing.by_label}, {when(viewing.at)}</span></h2>
            <h3>{viewing.title}</h3>
            <div class="dialog-body scroll"><Preview html={viewing.html} /></div>
            <div class="row end">
              <button class="ghost" onClick={() => setViewing(null)}>Close</button>
              {viewing.id !== post.revision && (
                <button class="primary" disabled={busy} onClick={() =>
                  run(() => api.restoreRevision(post.id, viewing.id)).then((ok) => { if (ok) { setViewing(null); refresh(); } })}>
                  Put this version back
                </button>
              )}
            </div>
            <p class="quiet small">Putting it back makes a new revision with this content. Nothing in the history is lost.</p>
          </div>
        </div>
      )}

      {confirmTrash && (
        <Confirm title="Move to the trash?" confirm="Move to trash" danger
          onCancel={() => setConfirmTrash(false)}
          onConfirm={() => { setConfirmTrash(false); act('trash'); }}>
          <p>It leaves the site. It stays in the trash, with its history, until you restore it.</p>
        </Confirm>
      )}
    </>
  );
}
