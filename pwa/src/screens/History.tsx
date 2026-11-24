import { useEffect, useState } from 'preact/hooks';
import { api, levelName, when, type HistoryEvent, type Level } from '../api';
import { Empty, Notice, useAction } from '../components/ui';

/**
 * What happened on the blog, newest first, in words: who, what, and the post it touched. For the owner, every agent
 * action carries its undo (docs/specs/history.md): one tap, or the reason it no longer applies.
 */
export function History({ blog }: { blog: string }) {
  const [events, setEvents] = useState<HistoryEvent[] | null>(null);
  const [more, setMore] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const { busy, error: undoError, run } = useAction();

  const load = () => api.history(blog).then((e) => { setEvents(e); setMore(e.length === 50); }).catch((x) => setError(x.message));
  useEffect(() => { setEvents(null); load(); }, [blog]);

  const older = () => {
    const last = events?.[events.length - 1];
    if (last) api.history(blog, last.id).then((e) => { setEvents([...(events ?? []), ...e]); setMore(e.length === 50); });
  };

  if (error) return <Notice kind="error">{error}</Notice>;
  if (!events) return <p class="quiet">Loading…</p>;

  return (
    <>
      <h1>History</h1>
      {undoError && <Notice kind="error">{undoError}</Notice>}
      {events.length === 0 && <Empty>Nothing has happened yet.</Empty>}
      <ol class="timeline">
        {events.map((e) => (
          <li key={e.id} class={e.actor.startsWith('connection:') ? 'agent' : e.actor === 'system' ? 'system' : 'owner'}>
            <span><strong>{e.actor_label}</strong> {describe(e)}</span>
            <span class="quiet">{when(e.at)}</span>
            {e.undo && (e.undo.done
              ? <span class="quiet small undo-done">Undone</span>
              : <span class="undo">
                  <button class="ghost small" disabled={busy || e.undo.why_not !== null} title={e.undo.why_not ?? undefined}
                    onClick={() => run(() => api.undo(blog, e.id)).then((ok) => { if (ok) load(); })}>
                    Undo: {e.undo.label.charAt(0).toLowerCase() + e.undo.label.slice(1)}
                  </button>
                  {e.undo.why_not && <span class="quiet small">{e.undo.why_not}</span>}
                </span>)}
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
    case 'revision_saved': {
      const extra = (e.payload as { extra?: { suggestion?: string; kind?: string; name?: string } }).extra;
      if (extra?.suggestion === 'accepted') return <>added the suggested {extra.kind} “{extra.name}” to {post}</>;
      if (extra?.suggestion === 'dismissed') return <>dismissed the suggested {extra.kind} “{extra.name}” on {post}</>;
      return <>saved revision {p.revision} of {post}</>;
    }
    case 'revision_restored': return <>put an older version of {post} back</>;
    case 'post_submitted': return <>submitted {post} for review</>;
    case 'post_published': return <>published {post}{p.via === 'schedule' ? ' (as scheduled)' : ''}</>;
    case 'post_scheduled': return <>scheduled {post}</>;
    case 'post_unpublished': return <>took {post} back to draft</>;
    case 'post_trashed': return <>moved {post} to the trash</>;
    case 'post_restored': return <>restored {post} from the trash</>;
    case 'schedule_dropped': return <>didn't publish {post}: {String(p.reason ?? '')}</>;
    case 'approval_requested': return <>asked to {act(p.action)} {post}</>;
    case 'approval_approved': return <>approved: {act(p.action)} {post}</>;
    case 'approval_declined': return <>declined: {act(p.action)} {post}</>;
    case 'approval_stale': return <>approved a request that no longer applied ({act(p.action)} {post}); nothing changed</>;
    case 'connection_created': return <>connected {post}</>;
    case 'connection_level_changed': return <>set {post} to {levelName[p.to as Level] ?? String(p.to)}</>;
    case 'connection_revoked': return <>revoked {post}</>;
    case 'blog_created': return <>created the blog</>;
    case 'blog_settings_changed': return <>{settingsChanged(e.payload as Record<string, { from: unknown; to: unknown }>)}</>;
    case 'approval_changes_requested': return <>asked for changes: {String(p.action)} {post}</>;
    case 'brief_changed': return <>updated how the blog sounds ({((e.payload as { changed?: string[] }).changed ?? []).map((c) => briefPart[c] ?? c).join(', ')})</>;
    case 'note_written': return <>wrote a note to the agents{post ? <> about {post}</> : null}</>;
    case 'note_retired': return <>retired a note to the agents</>;
    case 'term_created': return <>created the {String(p.kind)} “{String(p.name)}”</>;
    case 'media_upload_started': return <>started adding a picture, {String(p.filename)}</>;
    case 'media_added': return <>added a picture, {String(p.filename)}</>;
    case 'media_upload_expired': return <>let an unfinished picture upload go</>;
    case 'media_trashed': return <>moved a picture to the trash</>;
    case 'media_restored': return <>restored a picture from the trash</>;
    case 'action_undone': return <>undid an agent's action ({undoneWords[String(p.kind)] ?? String(p.kind).replaceAll('_', ' ')}){post ? <> on {post}</> : null}</>;
    case 'post_imported': return <>was imported: {post}</>;
    case 'import_started': return <>started an import from Echo of Dreams</>;
    case 'import_finished': return <>finished importing from Echo of Dreams</>;
    case 'import_failed': return <>stopped importing from Echo of Dreams: {String(p.error ?? '')}</>;
    default: return <>{e.kind.replaceAll('_', ' ')} {post}</>;
  }
}

/** What a settings change did, in words: each field with its new value. */
function settingsChanged(changed: Record<string, { from: unknown; to: unknown }>) {
  const parts = Object.entries(changed).map(([field, c]) => {
    switch (field) {
      case 'title': return `renamed the blog “${String(c.to)}”`;
      case 'description': return c.to ? 'changed the blog\'s description' : 'cleared the blog\'s description';
      case 'theme': return `set the theme to ${String(c.to).charAt(0).toUpperCase()}${String(c.to).slice(1)}`;
      case 'theme_picker': return c.to ? 'let readers try the themes' : 'stopped readers trying themes';
      case 'oidc': return c.to ? `set up sign-in with ${String((c.to as { display_name?: string }).display_name ?? 'a provider')}` : 'removed the sign-in provider';
      default: return `changed ${field.replaceAll('_', ' ')}`;
    }
  });
  return parts.length > 0 ? parts.join(', ') : 'changed the blog\'s settings';
}

/** A request's action in words: posts' are verbs already; the blog's are spelt out. */
const act = (a: unknown) => a === 'trash_media' ? 'trash a picture' : a === 'edit_settings' ? "change the blog's title or description" : String(a);

const undoneWords: Record<string, string> = {
  post_created: 'a new draft', revision_saved: 'an edit', post_published: 'publishing', post_scheduled: 'a schedule',
  post_unpublished: 'taking a post down', post_trashed: 'trashing', post_restored: 'restoring', media_added: 'a picture',
  media_trashed: 'trashing a picture', blog_settings_changed: "a change to the blog's title or description",
};

const briefPart: Record<string, string> = { audience: 'who it\'s for', voice: 'the voice', avoid: 'what to avoid', examples: 'the examples' };
