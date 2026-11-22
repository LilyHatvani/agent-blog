import { useEffect, useRef, useState } from 'preact/hooks';
import { api, when, type Announcement, type AnnouncementState, type Composed, type Status } from '../api';
import { networkName } from '../screens/Announce';
import { Notice, useAction } from './ui';

const stateWords: Record<AnnouncementState, string> = {
  none: 'not announced', draft: 'written, goes when the post is live', waiting: 'waits for you', scheduled: 'goes out soon',
  posted: 'posted', failed: 'failed', unconfirmed: 'may not have gone out', skipped: 'not sent', cancelled: 'cancelled',
};

/**
 * Where a post was announced, and what will be (docs/specs/announce.md): per account, the words with the network's own
 * count as they are typed, and the owner's buttons. Only the owner sends; nothing here is an agent's.
 */
export function Announcements({ post, status }: { post: string; status: Status }) {
  const [list, setList] = useState<Announcement[] | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const load = () => api.announcements(post).then((r) => setList(r.announcements)).catch((e) => setFailed(e.message));
  useEffect(() => { load(); }, [post, status]);

  if (failed) return <Notice kind="error">{failed}</Notice>;
  if (!list) return null;
  if (list.length === 0) return <p class="quiet small">No social accounts yet. <a href="#/announce">Add one</a> to announce new posts.</p>;
  return <>{list.map((a) => <AnnouncementCard key={a.account.id} post={post} status={status} a={a} onChange={load} />)}</>;
}

function AnnouncementCard({ post, status, a, onChange }: { post: string; status: Status; a: Announcement; onChange: () => void }) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(a.text ?? a.composed?.text ?? '');
  const [count, setCount] = useState<Composed | null>(a.composed);
  const { busy, error, run } = useAction();
  const timer = useRef<number | undefined>(undefined);

  // The count as the network counts it, a moment after typing stops.
  useEffect(() => {
    if (!editing) return;
    clearTimeout(timer.current);
    timer.current = setTimeout(() => api.composeAnnouncement(post, a.account.id, text).then(setCount).catch(() => undefined), 300) as unknown as number;
    return () => clearTimeout(timer.current);
  }, [text, editing]);

  const act = (action: 'send' | 'resend' | 'done' | 'cancel') => run(async () => { await api.announcementAction(post, a.account.id, action); onChange(); });
  const save = (value: string | null) => run(async () => {
    await api.writeAnnouncement(post, a.account.id, value);
    setEditing(false);
    onChange();
  });
  const changeable = ['none', 'draft', 'waiting', 'scheduled', 'failed'].includes(a.state);
  const live = status === 'live';
  const words = a.composed?.text;

  return (
    <div class="card announcement">
      <div class="card-head">
        <span><strong>{a.account.label}</strong> {a.account.label !== networkName[a.account.network] && <span class="quiet small">{networkName[a.account.network]}</span>}</span>
        <span class={`pill ann-${a.state}`}>{a.state === 'draft' && live ? 'written, not sent' : stateWords[a.state]}</span>
      </div>
      {a.state === 'waiting' && (
        <Notice kind="info">{a.why_waiting === 'agent_text'
          ? `${a.text_by_label ?? 'An agent'} wrote what to say. Read it, change it if you like, and tap Announce.`
          : 'An agent published this post on its own. Read what will be posted, and tap Announce.'}</Notice>
      )}
      {a.state === 'scheduled' && a.due_at && <p class="quiet small">Goes out {when(a.due_at)}{a.attempts > 0 ? `, try ${a.attempts + 1}` : ''}.</p>}
      {a.state === 'unconfirmed' && <Notice kind="error">The answer was lost, and {networkName[a.account.network]} can't be asked. Look there: if it's missing, Send again; if it's there, It's there.</Notice>}
      {a.error && a.state !== 'unconfirmed' && <p class={a.state === 'failed' ? 'small danger-text' : 'quiet small'}>{a.error}</p>}
      {a.url && <p class="small"><a href={a.url} target="_blank" rel="noopener">See it on {networkName[a.account.network]}</a>{a.sent_at ? ` · ${when(a.sent_at)}` : ''}</p>}
      {a.notes.map((n) => <p class="quiet small">{n}</p>)}
      {!a.credentials_open && <Notice kind="error">This account's credentials don't open: remove it and add it again.</Notice>}

      {editing ? (
        <form onSubmit={(e) => { e.preventDefault(); save(text); }}>
          <label class="field">
            <span>What it says <span class="quiet">(the link is added)</span></span>
            <textarea rows={4} value={text} onInput={(e) => setText((e.target as HTMLTextAreaElement).value)} />
          </label>
          {count && (
            <p class={count.fits ? 'quiet small' : 'small danger-text'}>
              {count.length} of {count.max} {count.unit}{count.problems.length > 0 ? `: ${count.problems.join(' ')}` : ''}
            </p>
          )}
          {error && <Notice kind="error">{error}</Notice>}
          <div class="row end">
            <button class="ghost" type="button" onClick={() => { setEditing(false); setText(a.text ?? a.composed?.text ?? ''); }}>Cancel</button>
            <button class="primary" disabled={busy || !text.trim() || count?.fits === false}>Save</button>
          </div>
        </form>
      ) : (
        <>
          {words && a.state !== 'posted' && <pre class="announce-text">{words}</pre>}
          {a.state !== 'posted' && a.composed && (
            <p class="quiet small">
              {a.composed.is_default ? 'The default: title, excerpt, tags.' : `Written by ${a.text_by_label ?? 'you'}.`} {a.composed.length} of {a.composed.max} {a.composed.unit}.
            </p>
          )}
          {error && <Notice kind="error">{error}</Notice>}
          <div class="row wrap">
            {['none', 'draft', 'waiting', 'failed', 'skipped', 'cancelled', 'scheduled'].includes(a.state) && (
              <button class={a.state === 'waiting' ? 'primary small' : 'ghost small'} disabled={busy || !live || !a.account.enabled || !a.credentials_open}
                title={!live ? 'Publish the post first.' : !a.account.enabled ? 'The account is turned off.' : undefined}
                onClick={() => act('send')}>{a.state === 'scheduled' ? 'Announce now' : a.state === 'failed' ? 'Try again' : 'Announce'}</button>
            )}
            {a.state === 'unconfirmed' && (
              <>
                <button class="primary small" disabled={busy} onClick={() => act('resend')}>Send again</button>
                <button class="ghost small" disabled={busy} onClick={() => act('done')}>It's there</button>
              </>
            )}
            {changeable && a.credentials_open && <button class="ghost small" disabled={busy} onClick={() => setEditing(true)}>Edit text</button>}
            {changeable && a.text && <button class="ghost small" disabled={busy} onClick={() => save(null)}>Use the default</button>}
            {['draft', 'waiting', 'scheduled', 'failed'].includes(a.state) && <button class="ghost small danger-text" disabled={busy} onClick={() => act('cancel')}>Don't announce</button>}
          </div>
        </>
      )}
    </div>
  );
}
