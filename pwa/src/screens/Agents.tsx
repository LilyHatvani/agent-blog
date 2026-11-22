import { useEffect, useState } from 'preact/hooks';
import { api, levelName, when, type Connection, type Level } from '../api';
import { Icon } from '../components/Icon';
import { Confirm, Empty, Notice, useAction } from '../components/ui';

const levels: Level[] = ['propose', 'publish', 'full'];

const short: Record<Level, string> = {
  propose: 'drafts, you publish',
  publish: 'publishes on its own',
  full: 'runs the content',
};

/** The agents connected to this blog: their level (Full confirmed in plain words), and revoking. */
export function Agents({ blogId }: { blogId: string }) {
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
        <span class="quiet">Add this address as a custom connector in claude.ai or ChatGPT, or as an MCP server in Claude Code. You approve it here, and it starts at Propose.</span>
        <div class="row">
          <code class="url">{mcpUrl}</code>
          <button class="ghost small" onClick={() => navigator.clipboard?.writeText(mcpUrl).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500); })}>
            <Icon name={copied ? 'check' : 'copy'} /> {copied ? 'Copied' : 'Copy'}
          </button>
        </div>
      </div>

      {load && <Notice kind="error">{load}</Notice>}
      {error && <Notice kind="error">{error}</Notice>}
      {list?.length === 0 && <Empty>No agents yet.</Empty>}
      {list?.map((c) => (
        <article class="card" key={c.id}>
          <div class="card-head">
            <strong>{c.label}</strong>
            <span class="quiet">{c.last_used_at ? `used ${when(c.last_used_at)}` : 'not used yet'}</span>
          </div>
          <div class="segmented" role="radiogroup" aria-label={`Level for ${c.label}`}>
            {levels.map((l) => (
              <button role="radio" aria-checked={c.level === l} class={c.level === l ? 'seg active' : 'seg'} disabled={busy}
                onClick={() => c.level !== l && setLevel(c, l)}>
                <span class="seg-name">{levelName[l]}</span>
                <span class="seg-hint">{short[l]}</span>
              </button>
            ))}
          </div>
          <span class="quiet">{c.level_means}</span>
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
