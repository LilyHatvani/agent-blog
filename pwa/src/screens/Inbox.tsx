import { useEffect, useState } from 'preact/hooks';
import { api, statusName, when, type Decision, type PostSummary, type Request, type TermRef } from '../api';
import { Diff } from '../components/Diff';
import { Empty, Notice, Preview, StatusPill, useAction } from '../components/ui';

const asks: Record<Request['action'], string> = {
  edit: 'wants to change',
  publish: 'wants to publish',
  schedule: 'wants to schedule',
  unpublish: 'wants to unpublish',
  trash: 'wants to trash',
  restore: 'wants to restore',
};

/** What needs the owner: agents' requests, and drafts submitted for review. */
export function Inbox({ blog, onCount }: { blog: string; onCount: (n: number) => void }) {
  const [requests, setRequests] = useState<Request[] | null>(null);
  const [review, setReview] = useState<PostSummary[]>([]);
  const [load, setLoad] = useState<string | null>(null);

  const refresh = () =>
    Promise.all([api.requests(), api.posts(blog, 'review')])
      .then(([r, p]) => {
        const mine = r.filter((q) => q.blog === blog);
        setRequests(mine);
        setReview(p);
        onCount(mine.length);
      })
      .catch((e) => setLoad(e.message));

  useEffect(() => { refresh(); }, [blog]);

  if (load) return <Notice kind="error">{load}</Notice>;
  if (!requests) return <p class="quiet">Loading…</p>;

  return (
    <>
      <h1>Inbox</h1>
      {requests.length === 0 && review.length === 0 && (
        <Empty>Nothing needs you. When an agent asks for something above its level, it shows up here.</Empty>
      )}
      {requests.length > 0 && <h2>Waiting for you</h2>}
      {requests.map((q) => <RequestCard key={q.id} q={q} onDone={refresh} />)}
      {review.length > 0 && <h2>Ready for review</h2>}
      {review.map((p) => (
        <a class="card link" href={`#/posts/${p.id}`} key={p.id}>
          <div class="card-head"><strong>{p.title}</strong><StatusPill status={p.status} /></div>
          <span class="quiet">{statusName[p.status]} · revision {p.revision_number} · {when(p.updated_at)}</span>
        </a>
      ))}
    </>
  );
}

/** Categories and tags an edit adds or removes, and names it suggests that don't exist yet. */
function TermChange({ before, after, suggested }: { before: TermRef[]; after: TermRef[]; suggested: { kind: string; name: string }[] }) {
  const key = (t: TermRef) => `${t.kind}:${t.slug}`;
  const added = after.filter((t) => !before.some((b) => key(b) === key(t)));
  const removed = before.filter((t) => !after.some((a) => key(a) === key(t)));
  if (added.length === 0 && removed.length === 0 && suggested.length === 0) return null;
  const label = (t: { kind: string; name: string }) => (t.kind === 'tag' ? '#' : '') + t.name;
  return (
    <p class="small">
      {added.length > 0 && <>Adds <ins>{added.map(label).join(', ')}</ins>. </>}
      {removed.length > 0 && <>Removes <del>{removed.map(label).join(', ')}</del>. </>}
      {suggested.length > 0 && <>Suggests new: {suggested.map(label).join(', ')}.</>}
    </p>
  );
}

function RequestCard({ q, onDone }: { q: Request; onDone: () => void }) {
  const [open, setOpen] = useState(false);
  const { busy, error, run } = useAction();
  const [writing, setWriting] = useState<'changes' | 'decline' | null>(null);
  const [note, setNote] = useState('');
  const decide = (d: Decision, text?: string) =>
    run(() => api.decide(q.id, d, text?.trim() || undefined)).then((ok) => { if (ok) { setWriting(null); setNote(''); onDone(); } });

  return (
    <article class="card">
      <div class="card-head">
        <span><strong>{q.agent}</strong> {asks[q.action]} <a href={`#/posts/${q.post}`}>“{q.post_title}”</a></span>
      </div>
      <span class="quiet">{when(q.created_at)}</span>
      {q.action === 'schedule' && q.proposal?.at && (
        <p>For <strong>{new Date(q.proposal.at).toLocaleString()}</strong>.</p>
      )}
      {q.action === 'edit' && q.proposal && (
        <>
          {q.current && q.proposal.title !== q.current.title && (
            <p class="small">Title: <del>{q.current.title}</del> → <ins>{q.proposal.title}</ins></p>
          )}
          {q.current && q.proposal.excerpt !== q.current.excerpt && (
            <p class="small">Summary: <del>{q.current.excerpt || '(none)'}</del> → <ins>{q.proposal.excerpt || '(none)'}</ins></p>
          )}
          {q.current && q.proposal.terms && <TermChange before={q.current.terms} after={q.proposal.terms} suggested={q.proposal.suggested ?? []} />}
          {q.current && <Diff before={q.current.body_md} after={q.proposal.body_md ?? ''} />}
          <button class="ghost small fit" onClick={() => setOpen(!open)} aria-expanded={open}>
            {open ? 'Hide the whole new version' : 'Show the whole new version'}
          </button>
          {open && (
            <div class="proposal">
              <h3>{q.proposal.title}</h3>
              {q.proposal.excerpt && <p class="quiet">{q.proposal.excerpt}</p>}
              <Preview html={q.proposal.html ?? ''} />
            </div>
          )}
        </>
      )}
      {!q.still_applies && (
        <Notice kind="info">{q.why_not ?? "The post changed since the agent asked, so approving won't apply anything."} Decline it, or let the agent ask again.</Notice>
      )}
      {error && <Notice kind="error">{error}</Notice>}
      {writing ? (
        <form class="note-form" onSubmit={(e) => { e.preventDefault(); decide(writing, note); }}>
          <label class="field">
            <span>{writing === 'changes' ? 'What should change?' : 'Why? (optional, the agent reads it)'}</span>
            <textarea rows={3} value={note} maxLength={2000} required={writing === 'changes'} autoFocus
              onInput={(e) => setNote((e.target as HTMLTextAreaElement).value)}
              placeholder={writing === 'changes' ? 'e.g. Cut the intro, say what changed, no exclamation marks.' : ''} />
          </label>
          <div class="row end">
            <button type="button" class="ghost" disabled={busy} onClick={() => { setWriting(null); setNote(''); }}>Cancel</button>
            <button type="submit" class={writing === 'changes' ? 'primary' : 'ghost danger-text'} disabled={busy || (writing === 'changes' && note.trim() === '')}>
              {writing === 'changes' ? 'Send to the agent' : 'Decline'}
            </button>
          </div>
        </form>
      ) : (
        <div class="row wrap decide">
          <button class="primary" disabled={busy || !q.still_applies} onClick={() => decide('approve')}
            title={q.still_applies ? undefined : q.why_not ?? undefined}>Approve</button>
          <button class="ghost" disabled={busy} onClick={() => setWriting('changes')}>Ask for changes</button>
          <button class="ghost" disabled={busy} onClick={() => setWriting('decline')}>Decline</button>
        </div>
      )}
    </article>
  );
}
