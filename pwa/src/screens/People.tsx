import { useEffect, useState } from 'preact/hooks';
import { api, when, type Invitation, type Person } from '../api';
import { Icon } from '../components/Icon';
import { Confirm, GuideLink, Notice, useAction } from '../components/ui';

const day = (at: string) => new Date(at.endsWith('Z') ? at : at + 'Z').toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
const roleName = { owner: 'Owner', editor: 'Editor', writer: 'Writer' } as const;
const invitationState = { pending: 'waiting', accepted: 'accepted', expired: 'ran out', withdrawn: 'withdrawn' } as const;

/**
 * Who writes for the blog (docs/specs/members.md): the owner invites with a link that works once, for a week; sets
 * each person's role; removes them, keeping everything they wrote under their name. The owner's screen alone.
 */
export function People({ blog }: { blog: string }) {
  const [people, setPeople] = useState<Person[] | null>(null);
  const [invitations, setInvitations] = useState<Invitation[]>([]);
  const [load, setLoad] = useState<string | null>(null);
  const [role, setRole] = useState<'writer' | 'editor'>('writer');
  const [note, setNote] = useState('');
  const [link, setLink] = useState<{ url: string; role: string; expires_at: string } | null>(null);
  const [copied, setCopied] = useState(false);
  const [toRemove, setToRemove] = useState<Person | null>(null);
  const { busy, error, run } = useAction();

  const refresh = () => api.people(blog).then((r) => { setPeople(r.people); setInvitations(r.invitations); }).catch((e) => setLoad(e.message));
  useEffect(() => { refresh(); }, [blog]);

  if (load) return <Notice kind="error">{load}</Notice>;
  if (!people) return <p class="quiet">Loading…</p>;
  const waiting = invitations.filter((i) => i.state === 'pending');
  const past = invitations.filter((i) => i.state !== 'pending').slice(0, 10);

  return (
    <>
      <a class="back" href="#/account"><Icon name="back" /> Account</a>
      <h1>People</h1>
      <p class="quiet small">Who writes for this blog with you. Nobody joins without your invitation. <GuideLink guide="people" /></p>

      {error && <Notice kind="error">{error}</Notice>}

      {people.map((p) => (
        <div class="card" key={p.account}>
          <div class="card-head">
            <span><strong>{p.name}</strong> <span class="quiet small">{roleName[p.role]}{p.role !== 'owner' && <> · since {day(p.joined_at)}</>}</span></span>
            {p.role !== 'owner' && (
              <span class="row nowrap">
                <button class="ghost small" disabled={busy} onClick={() =>
                  run(() => api.setRole(blog, p.account, p.role === 'editor' ? 'writer' : 'editor')).then((ok) => { if (ok) refresh(); })}>
                  Make {p.role === 'editor' ? 'writer' : 'editor'}
                </button>
                <button class="ghost small danger-text" disabled={busy} onClick={() => setToRemove(p)}>Remove</button>
              </span>
            )}
          </div>
          {p.role === 'owner' && <span class="quiet small">Everything: settings, agents, the Inbox, and who is here.</span>}
          {p.role === 'editor' && <span class="quiet small">Writes, edits any post, publishes and schedules.</span>}
          {p.role === 'writer' && <span class="quiet small">Writes drafts, edits their own, submits them for review.</span>}
        </div>
      ))}

      <h2>Invite someone</h2>
      {link ? (
        <div class="card form">
          <strong>The link, for one {link.role}</strong>
          <span class="quiet small">Send it however you like. It is shown only now, works once, until {new Date(link.expires_at + (link.expires_at.endsWith('Z') ? '' : 'Z')).toLocaleDateString()}.</span>
          <input class="mono" readOnly value={link.url} aria-label="Invitation link" onFocus={(e) => (e.target as HTMLInputElement).select()} />
          <div class="row end">
            <button class="ghost" onClick={() => { setLink(null); setCopied(false); }}>Done</button>
            <button class="primary" onClick={() => navigator.clipboard?.writeText(link.url).then(() => setCopied(true))}>
              <Icon name={copied ? 'check' : 'copy'} /> {copied ? 'Copied' : 'Copy'}
            </button>
          </div>
        </div>
      ) : (
        <form class="card form" onSubmit={(e) => {
          e.preventDefault();
          run(async () => {
            const made = await api.invite(blog, role, note);
            setLink({ url: made.link, role: made.role, expires_at: made.expires_at });
            setNote('');
            await refresh();
          });
        }}>
          <div class="chips" role="radiogroup" aria-label="Role">
            {(['writer', 'editor'] as const).map((r) => (
              <button type="button" role="radio" aria-checked={role === r} class={role === r ? 'chip active' : 'chip'} onClick={() => setRole(r)}>{roleName[r]}</button>
            ))}
          </div>
          <span class="quiet small">{role === 'editor'
            ? 'An editor writes, edits any post, publishes and schedules. Trashing, settings and agents stay yours.'
            : 'A writer writes drafts and submits them; you or an editor publish.'}</span>
          <label class="field">
            <span>Who it's for <span class="quiet small">(only you see it)</span></span>
            <input value={note} maxLength={200} placeholder="Anna, for the travel posts" onInput={(e) => setNote((e.target as HTMLInputElement).value)} />
          </label>
          <div class="row end"><button class="primary" disabled={busy}>Invite</button></div>
        </form>
      )}

      {waiting.length > 0 && (
        <>
          <h2>Waiting</h2>
          {waiting.map((i) => (
            <div class="card" key={i.id}>
              <div class="card-head">
                <span><strong>{roleName[i.role]}</strong> <span class="quiet small">{i.note || 'no note'} · made {when(i.created_at)}</span></span>
                <button class="ghost small" disabled={busy} onClick={() => run(() => api.withdrawInvitation(blog, i.id)).then((ok) => { if (ok) refresh(); })}>Withdraw</button>
              </div>
            </div>
          ))}
        </>
      )}
      {past.length > 0 && (
        <>
          <h2>Earlier invitations</h2>
          {past.map((i) => (
            <p class="quiet small" key={i.id}>
              {roleName[i.role]}{i.note && <>, {i.note}</>}: {invitationState[i.state]}
              {i.state === 'accepted' && i.used_by && <> by <strong>{i.used_by}</strong></>}
              {i.used_at && <> {when(i.used_at)}</>}
            </p>
          ))}
        </>
      )}

      {toRemove && (
        <Confirm title={`Remove ${toRemove.name}?`} confirm="Remove" danger onCancel={() => setToRemove(null)}
          onConfirm={() => {
            const p = toRemove;
            setToRemove(null);
            run(() => api.removePerson(blog, p.account)).then((ok) => { if (ok) refresh(); });
          }}>
          <p>They can't sign in to this blog any more, from now on. Everything they wrote stays, under their name: posts, revisions and history.</p>
          <p>You can invite them again later.</p>
        </Confirm>
      )}
    </>
  );
}
