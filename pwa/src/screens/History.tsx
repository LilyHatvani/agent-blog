import { useEffect, useState } from 'preact/hooks';
import { api, levelName, when, type HistoryEvent, type Level } from '../api';
import { Empty, Notice } from '../components/ui';

/** What happened on the blog, newest first, in words: who, what, and the post it touched. */
export function History({ blog }: { blog: string }) {
  const [events, setEvents] = useState<HistoryEvent[] | null>(null);
  const [more, setMore] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setEvents(null);
    api.history(blog).then((e) => { setEvents(e); setMore(e.length === 50); }).catch((x) => setError(x.message));
  }, [blog]);

  const older = () => {
    const last = events?.[events.length - 1];
    if (last) api.history(blog, last.id).then((e) => { setEvents([...(events ?? []), ...e]); setMore(e.length === 50); });
  };

  if (error) return <Notice kind="error">{error}</Notice>;
  if (!events) return <p class="quiet">Loading…</p>;

  return (
    <>
      <h1>History</h1>
      {events.length === 0 && <Empty>Nothing has happened yet.</Empty>}
      <ol class="timeline">
        {events.map((e) => (
          <li key={e.id} class={e.actor.startsWith('connection:') ? 'agent' : e.actor === 'system' ? 'system' : 'owner'}>
            <span><strong>{e.actor_label}</strong> {describe(e)}</span>
            <span class="quiet">{when(e.at)}</span>
          </li>
        ))}
      </ol>
      {more && events.length > 0 && <button class="ghost" onClick={older}>Older</button>}
    </>
  );
}

function describe(e: HistoryEvent) {
  const post = e.post && e.subject ? <a href={`#/posts/${e.post}`}>“{e.subject}”</a> : e.subject ? <>“{e.subject}”</> : null;
  const p = e.payload as Record<string, string | number | null | undefined>;
  switch (e.kind) {
    case 'post_created': return <>wrote a draft, {post}</>;
    case 'revision_saved': return <>saved revision {p.revision} of {post}</>;
    case 'revision_restored': return <>put an older version of {post} back</>;
    case 'post_submitted': return <>submitted {post} for review</>;
    case 'post_published': return <>published {post}{p.via === 'schedule' ? ' (as scheduled)' : ''}</>;
    case 'post_scheduled': return <>scheduled {post}</>;
    case 'post_unpublished': return <>took {post} back to draft</>;
    case 'post_trashed': return <>moved {post} to the trash</>;
    case 'post_restored': return <>restored {post} from the trash</>;
    case 'schedule_dropped': return <>didn't publish {post}: {String(p.reason ?? '')}</>;
    case 'approval_requested': return <>asked to {String(p.action)} {post}</>;
    case 'approval_approved': return <>approved: {String(p.action)} {post}</>;
    case 'approval_declined': return <>declined: {String(p.action)} {post}</>;
    case 'approval_stale': return <>approved a request that no longer applied ({String(p.action)} {post}); nothing changed</>;
    case 'connection_created': return <>connected {post}</>;
    case 'connection_level_changed': return <>set {post} to {levelName[p.to as Level] ?? String(p.to)}</>;
    case 'connection_revoked': return <>revoked {post}</>;
    case 'blog_created': return <>created the blog</>;
    default: return <>{e.kind.replaceAll('_', ' ')} {post}</>;
  }
}
