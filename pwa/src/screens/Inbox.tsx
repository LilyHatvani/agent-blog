import { useEffect, useState } from 'preact/hooks';
import { api, type AnnounceScreen, statusName, when, type Decision, type PostSummary, type Request, type TermRef, type BlogRequest } from '../api';
import { Diff } from '../components/Diff';
import { Editor, type Draft } from '../components/Editor';
import { CheckSummary } from '../components/Check';
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
  const [announce, setAnnounce] = useState<AnnounceScreen['attention']>([]);
  const [others, setOthers] = useState<BlogRequest[]>([]);
  const [load, setLoad] = useState<string | null>(null);

  const refresh = () =>
    Promise.all([api.requests(), api.posts(blog, 'review'), api.announce(blog).then((a) => a.attention).catch(() => []),
      api.blogRequests().catch(() => [] as BlogRequest[])])
      .then(([r, p, a, b]) => {
        const mine = r.filter((q) => q.blog === blog);
        const blogs = b.filter((q) => q.blog === blog);
        setRequests(mine);
        setReview(p);
        setAnnounce(a);
        setOthers(blogs);
        onCount(mine.length + a.length + blogs.length);
      })
      .catch((e) => setLoad(e.message));

  useEffect(() => { refresh(); }, [blog]);

  if (load) return <Notice kind="error">{load}</Notice>;
  if (!requests) return <p class="quiet">Loading…</p>;

  return (
    <>
      <h1>Inbox</h1>
      {requests.length === 0 && review.length === 0 && announce.length === 0 && others.length === 0 && (
        <Empty>Nothing needs you. When an agent asks for something above its level, it shows up here.</Empty>
      )}
      {requests.length > 0 && <h2>Waiting for you</h2>}
      {requests.map((q) => <RequestCard key={q.id} q={q} onDone={refresh} />)}
      {others.length > 0 && <h2>The blog</h2>}
      {others.map((q) => <BlogRequestCard key={q.id} q={q} onDone={refresh} />)}
      {announce.length > 0 && <h2>Announcements</h2>}
      {announce.map((w) => (
        <a class="card link" href={`#/posts/${w.post}`} key={w.id}>
          <div class="card-head"><strong>{w.post_title ?? 'A post'}</strong><span class="quiet small">{w.account_label}</span></div>
          <span class={w.state === 'waiting' ? 'quiet small' : 'small danger-text'}>{w.state === 'waiting'
            ? (w.why_waiting === 'manual' ? 'For Patreon: copy it there, then It\'s there.' : w.why_waiting === 'agent_text' ? 'An agent wrote what to say: read it, then Announce.' : 'An agent published it: read what will be posted, then Announce.')
            : w.state === 'unconfirmed' ? "May not have gone out: look, then Send again or It's there." : `Failed: ${w.error ?? ''}`}</span>
        </a>
      ))}
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
  const [editing, setEditing] = useState<Draft | null>(null);
  const editable = (q.action === 'edit' || q.action === 'publish' || q.action === 'schedule') && q.still_applies;
  // What the editor starts from: the agent's proposal for an edit, the post as it is for a publish or schedule.
  const startEditing = () => {
    if (q.action === 'edit' && q.proposal)
      setEditing({ title: q.proposal.title ?? '', excerpt: q.proposal.excerpt ?? '', body_md: q.proposal.body_md ?? '' });
    else run(() => api.post(q.post).then((p) => setEditing({ title: p.post.title, excerpt: p.post.excerpt, body_md: p.body_md })));
  };
  const [note, setNote] = useState('');
  const decide = (d: Decision, text?: string) =>
    run(() => api.decide(q.id, d, text?.trim() || undefined)).then((ok) => { if (ok) { setWriting(null); setNote(''); onDone(); } });

  return (
    <article class="card">
      <div class="card-head">
        <span><strong>{q.agent}</strong>{q.agent_person && <span class="quiet"> ({q.agent_person}'s agent)</span>} {asks[q.action]} <a href={`#/posts/${q.post}`}>“{q.post_title}”</a></span>
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
      {q.checks && q.still_applies && <CheckSummary check={q.checks} />}
      {!q.still_applies && (
        <Notice kind="info">{q.why_not ?? "The post changed since the agent asked, so approving won't apply anything."} Decline it, or let the agent ask again.</Notice>
      )}
      {editable && !editing && !writing && (
        <button class="ghost small fit" onClick={startEditing}>Edit before approving</button>
      )}
      {editing && (
        <Editor start={editing} saveLabel={q.action === 'edit' ? 'Approve with my edits' : q.action === 'publish' ? 'Publish with my edits' : 'Schedule with my edits'}
          busy={busy} error={error} onCancel={() => setEditing(null)}
          onSave={(edits) => run(() => api.decide(q.id, 'approve', undefined, edits)).then((ok) => { if (ok) { setEditing(null); onDone(); } })}>
          <span class="quiet small">Your changes are saved as your own revision on top of {q.action === 'edit' ? "the agent's" : 'the post'}, so each of you keeps your words.</span>
        </Editor>
      )}
      {error && !editing && <Notice kind="error">{error}</Notice>}
      {editing ? null : writing ? (
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

/** A request beyond posts: what the agent asks to do to the blog, with what it is now, and Approve / Decline. */
function BlogRequestCard({ q, onDone }: { q: BlogRequest; onDone: () => void }) {
  const [declining, setDeclining] = useState(false);
  const [note, setNote] = useState('');
  const { busy, error, run } = useAction();
  const decide = (d: 'approve' | 'decline') => run(() => api.decideBlogRequest(q.id, d, d === 'decline' ? note.trim() || undefined : undefined)).then((ok) => { if (ok) onDone(); });
  return (
    <div class="card">
      <span><strong>{q.agent}</strong> {q.action === 'trash_media' ? 'wants to move a picture to the trash' : "wants to change the blog's title or description"}</span>
      <span class="quiet small">{when(q.created_at)}</span>
      {q.media && (
        <div class="row">
          <img class="thumb small-thumb" src={q.media.path} alt="" />
          <span class="quiet small">{q.media.filename}: readers stop seeing it in every post that shows it. You can restore it from Media.</span>
        </div>
      )}
      {q.action === 'edit_settings' && (
        <dl class="change-list">
          {q.change.title != null && <><dt>Title</dt><dd><s>{q.was.title}</s> → <strong>{q.change.title}</strong></dd></>}
          {q.change.description != null && <><dt>Description</dt><dd><s>{q.was.description || '(none)'}</s> → <strong>{q.change.description || '(none)'}</strong></dd></>}
        </dl>
      )}
      {q.why_not && <Notice kind="info">{q.why_not} Approving won't change anything: decline it, or let the agent ask again.</Notice>}
      {error && <Notice kind="error">{error}</Notice>}
      {declining ? (
        <form class="note-form" onSubmit={(e) => { e.preventDefault(); decide('decline'); }}>
          <textarea rows={2} maxLength={2000} value={note} aria-label="Why? (optional, the agent reads it)" placeholder="Why? (optional, the agent reads it)"
            onInput={(e) => setNote((e.target as HTMLTextAreaElement).value)} />
          <div class="row end">
            <button type="button" class="ghost small" disabled={busy} onClick={() => setDeclining(false)}>Cancel</button>
            <button class="primary small" disabled={busy}>Decline</button>
          </div>
        </form>
      ) : (
        <div class="row">
          <button class="primary" disabled={busy || q.why_not !== null} onClick={() => decide('approve')}>Approve</button>
          <button class="ghost" disabled={busy} onClick={() => setDeclining(true)}>Decline</button>
        </div>
      )}
    </div>
  );
}
