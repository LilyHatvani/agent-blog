import { useEffect, useState } from 'preact/hooks';
import { api, levelName, when, type Connection, type Level, type OwnerNote, type Role } from '../api';
import { Icon } from '../components/Icon';
import { Confirm, Empty, Notice, useAction } from '../components/ui';

const levels: Level[] = ['propose', 'publish', 'full'];

const short: Record<Level, string> = {
  propose: 'drafts, you publish',
  publish: 'publishes on its own',
  full: 'runs the content',
};

const roleWords: Record<Role, string> = { owner: 'the owner', editor: 'an editor', writer: 'a writer' };

/** Why a level isn't open to this agent: its person's role holds it lower (docs/specs/members.md). */
const heldBy = (c: Connection, l: Level) =>
  c.cap && levels.indexOf(l) > levels.indexOf(c.cap)
    ? `${c.person.name} is ${roleWords[c.person.role ?? 'writer']}: their agent goes at most to ${levelName[c.cap]}.`
    : null;

/** What an agent's level means to its person (not the owner): who decides what it asks. */
const meansToPerson = (c: Connection) =>
  c.works_at === 'publish'
    ? 'It publishes and schedules on its own; trashing waits for the owner.'
    : c.person.role === 'editor'
      ? 'It writes drafts and suggests; publishing waits for you, another editor or the owner to decide.'
      : 'It writes drafts and suggests, on your posts; publishing waits for the owner or an editor.';

/**
 * The agents connected to this blog: the owner sees every one, with whose it is, and sets each level (Full confirmed
 * in plain words, never above what its person may do); a writer or an editor sees their own, renames and revokes them.
 */
export function Agents({ blogId, role }: { blogId: string; role: Role }) {
  const owner = role === 'owner';
  const [list, setList] = useState<Connection[] | null>(null);
  const [load, setLoad] = useState<string | null>(null);
  const [toFull, setToFull] = useState<Connection | null>(null);
  const [toRevoke, setToRevoke] = useState<Connection | null>(null);
  const [copied, setCopied] = useState(false);
  const { busy, error, run } = useAction();
  const mcpUrl = `${location.origin}/mcp`;

  const refresh = () => api.connections().then((c) => setList(c.filter((x) => x.blog.id === blogId))).catch((e) => setLoad(e.message));
  useEffect(() => { refresh(); }, [blogId]);

  const setLevel = (c: Connection, level: Level, confirmFull = false) => {
    if (level === 'full' && !confirmFull) { setToFull(c); return; }
    run(() => api.setLevel(c.id, level, confirmFull)).then((ok) => { if (ok) refresh(); });
  };

  return (
    <>
      <h1>Agents</h1>
      <div class="card">
        <strong>Connect an agent</strong>
        <span class="quiet">{owner
          ? 'Add this address as a custom connector in claude.ai or ChatGPT, or as an MCP server in Claude Code. You approve it here, and it starts at Propose.'
          : `Add this address as a custom connector in claude.ai or ChatGPT, or as an MCP server in Claude Code, and approve it here. It is your agent: it starts at Propose, never does more than you may (as ${roleWords[role]}, ${role === 'editor' ? 'at most Publish' : 'Propose'}), and the owner sets its level.`}</span>
        <div class="row">
          <code class="url">{mcpUrl}</code>
          <button class="ghost small" onClick={() => navigator.clipboard?.writeText(mcpUrl).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500); })}>
            <Icon name={copied ? 'check' : 'copy'} /> {copied ? 'Copied' : 'Copy'}
          </button>
        </div>
      </div>

      {owner && <TellAgents blogId={blogId} />}

      {load && <Notice kind="error">{load}</Notice>}
      {error && <Notice kind="error">{error}</Notice>}
      {list?.length === 0 && <Empty>{owner ? 'No agents yet.' : 'You have no agent on this blog yet.'}</Empty>}
      {list?.map((c) => (
        <article class="card" key={c.id}>
          <div class="card-head">
            <span><strong>{c.label}</strong>{!c.mine && <span class="quiet small"> · {c.person.name}'s agent</span>}</span>
            <span class="quiet">{c.last_used_at ? `used ${when(c.last_used_at)}` : 'not used yet'}</span>
          </div>
          {c.can_set_level ? <>
          <div class="segmented" role="radiogroup" aria-label={`Level for ${c.label}`}>
            {levels.map((l) => (
              <button role="radio" aria-checked={c.level === l} class={c.level === l ? 'seg active' : 'seg'}
                disabled={busy || !c.can_set_level || heldBy(c, l) !== null}
                title={!c.can_set_level ? 'The owner sets the level.' : heldBy(c, l) ?? undefined}
                onClick={() => c.level !== l && setLevel(c, l)}>
                <span class="seg-name">{levelName[l]}</span>
                <span class="seg-hint">{short[l]}</span>
              </button>
            ))}
          </div>
          <span class="quiet">{c.level_means}</span>
          {c.works_at && c.works_at !== c.level && (
            <span class="quiet small why-not">It works at {levelName[c.works_at]} while {c.person.name} is {roleWords[c.person.role ?? 'writer']}.</span>
          )}
          {c.cap && c.cap !== 'full' && c.can_set_level && c.works_at === c.level && (
            <span class="quiet small why-not">{heldBy(c, 'full')}</span>
          )}
          </> : <>
            <p class="agent-level">Works at <strong>{levelName[c.works_at ?? 'propose']}</strong></p>
            <span class="quiet">{meansToPerson(c)}</span>
            <span class="quiet small why-not">The owner sets its level{c.cap !== 'full' && c.cap ? `, at most ${levelName[c.cap]} for your agent` : ''}.</span>
          </>}
          <NameOnPosts connection={c} onSaved={refresh} />
          <div class="row end">
            <button class="ghost danger-text small" disabled={busy} onClick={() => setToRevoke(c)}>Revoke</button>
          </div>
        </article>
      ))}

      {toFull && (
        <Confirm title={`Give ${toFull.label} Full?`} confirm="Give it Full"
          onCancel={() => setToFull(null)}
          onConfirm={() => { const c = toFull; setToFull(null); setLevel(c, 'full', true); }}>
          <p>This agent will publish, change and trash posts <strong>without asking you</strong>.</p>
          <p>You can see everything it does in History, put any old version of a post back, and restore anything from the trash. It can never empty the trash, change its own level, or touch your sign-in.</p>
        </Confirm>
      )}
      {toRevoke && (
        <Confirm title={`Revoke ${toRevoke.label}?`} confirm="Revoke" danger
          onCancel={() => setToRevoke(null)}
          onConfirm={() => { const c = toRevoke; setToRevoke(null); run(() => api.revoke(c.id)).then((ok) => { if (ok) refresh(); }); }}>
          <p>Its next call is refused, and it can't refresh its way back in. To use it again, connect it again. Its posts and their history stay.</p>
        </Confirm>
      )}
    </>
  );
}

const noteKind: Record<OwnerNote['kind'], string> = {
  general: 'To all agents',
  approved: 'On approving',
  declined: 'On declining',
  changes_requested: 'Changes asked',
};

/**
 * Notes to every agent (decision 0008): what you'd otherwise repeat in each chat. The newest ones go to every session
 * in get_blog, as your words; a note that no longer holds is retired, never deleted.
 */
function TellAgents({ blogId }: { blogId: string }) {
  const [notes, setNotes] = useState<OwnerNote[] | null>(null);
  const [sent, setSent] = useState(10);
  const [text, setText] = useState('');
  const [showRetired, setShowRetired] = useState(false);
  const { busy, error, run } = useAction();
  const refresh = () => api.notes(blogId).then((r) => { setNotes(r.notes); setSent(r.sent_to_agents); }).catch(() => setNotes([]));
  useEffect(() => { refresh(); }, [blogId]);

  const current = (notes ?? []).filter((n) => !n.retired_at);
  const retired = (notes ?? []).filter((n) => n.retired_at);
  return (
    <div class="card">
      <strong>Tell your agents</strong>
      <span class="quiet small">Every agent reads your {sent} newest notes when it starts, as your own words. Your reasons on the Inbox land here too.</span>
      <form class="note-form" onSubmit={(e) => {
        e.preventDefault();
        run(() => api.addNote(blogId, text.trim())).then((ok) => { if (ok) { setText(''); refresh(); } });
      }}>
        <textarea rows={2} value={text} maxLength={2000} aria-label="A note to every agent" placeholder="e.g. Every post needs an excerpt."
          onInput={(e) => setText((e.target as HTMLTextAreaElement).value)} />
        <div class="row end"><button class="primary small" disabled={busy || text.trim() === ''}>Send to all agents</button></div>
      </form>
      {error && <Notice kind="error">{error}</Notice>}
      {current.map((n, i) => (
        <div class={i < sent ? 'note' : 'note muted'} key={n.id}>
          <div class="note-head">
            <span class="quiet small">{noteKind[n.kind]}{n.post_title ? <> · <a href={`#/posts/${n.post}`}>{n.post_title}</a></> : null} · {when(n.at)}{i >= sent ? ' · no longer sent (older)' : ''}</span>
            <button class="ghost small" disabled={busy} onClick={() => run(() => api.retireNote(blogId, n.id)).then((ok) => { if (ok) refresh(); })}>Retire</button>
          </div>
          <p class="note-text">{n.note}</p>
        </div>
      ))}
      {retired.length > 0 && (
        <button class="ghost small fit" onClick={() => setShowRetired(!showRetired)} aria-expanded={showRetired}>
          {showRetired ? 'Hide retired notes' : `Retired notes (${retired.length})`}
        </button>
      )}
      {showRetired && retired.map((n) => (
        <div class="note muted" key={n.id}>
          <span class="quiet small">{noteKind[n.kind]} · retired {when(n.retired_at)}</span>
          <p class="note-text">{n.note}</p>
        </div>
      ))}
    </div>
  );
}

/** This agent's own name (docs/specs/bylines.md): the app's History and structured data use it; readers see the person behind the agent, never this name. Empty: its client's own name. */
function NameOnPosts({ connection: c, onSaved }: { connection: Connection; onSaved: () => void }) {
  const [name, setName] = useState(c.byline_name_set ? c.byline_name : '');
  const { busy, error, run } = useAction();
  const saved = c.byline_name_set ? c.byline_name : '';
  const dirty = name.trim() !== saved;
  return (
    <form class="name-on-posts" onSubmit={(e) => { e.preventDefault(); run(async () => { await api.setBylineName(c.id, name.trim()); onSaved(); }); }}>
      <label class="field">
        <span>Name on posts</span>
        <input type="text" maxLength={60} placeholder={c.byline_name} value={name} onInput={(e) => setName((e.target as HTMLInputElement).value)} />
      </label>
      <div class="row end">
        <button type="button" class="ghost small" disabled={busy || !dirty} onClick={() => setName(saved)}>Cancel</button>
        <button class="primary small" disabled={busy || !dirty}>Save</button>
      </div>
      {error && <Notice kind="error">{error}</Notice>}
    </form>
  );
}
