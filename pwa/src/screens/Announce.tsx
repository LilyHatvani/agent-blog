import { useEffect, useState } from 'preact/hooks';
import { api, when, type AnnounceScreen, type Network, type NewSocialAccount, type SocialAccount } from '../api';
import { Confirm, Empty, GuideLink, Notice, useAction } from '../components/ui';

export const networkName: Record<Network, string> = {
  bluesky: 'Bluesky', mastodon: 'Mastodon', discord: 'Discord', slack: 'Slack', telegram: 'Telegram', x: 'X',
};

/** What each network asks for, in the words of its own settings screens (docs/specs/announce.md § Accounts). */
const fields: Record<Network, { name: keyof NewSocialAccount; label: string; hint?: string; secret?: boolean; placeholder?: string }[]> = {
  bluesky: [
    { name: 'handle', label: 'Handle', placeholder: 'name.bsky.social' },
    { name: 'password', label: 'App password', secret: true, placeholder: 'xxxx-xxxx-xxxx-xxxx',
      hint: 'Settings, Privacy and security, App passwords. Never your account password.' },
  ],
  mastodon: [
    { name: 'instance', label: 'Instance', placeholder: 'mastodon.social' },
    { name: 'token', label: 'Access token', secret: true,
      hint: 'Preferences, Development, New application, with write:statuses and write:media. Copy "Your access token".' },
  ],
  discord: [{ name: 'url', label: 'Webhook URL', secret: true, placeholder: 'https://discord.com/api/webhooks/…',
    hint: 'Channel settings, Integrations, Webhooks, New Webhook, Copy Webhook URL.' }],
  slack: [{ name: 'url', label: 'Webhook URL', secret: true, placeholder: 'https://hooks.slack.com/services/…',
    hint: 'A Slack app with Incoming Webhooks on, Add New Webhook to Workspace, pick the channel.' }],
  telegram: [
    { name: 'token', label: 'Bot token', secret: true, placeholder: '123456789:AA…', hint: 'From @BotFather: /newbot.' },
    { name: 'chat', label: 'Channel', placeholder: '@yourchannel',
      hint: 'Make the bot an administrator of the channel who may post messages. A private chat: its numeric id.' },
  ],
  x: [
    { name: 'api_key', label: 'API key', secret: true },
    { name: 'api_secret', label: 'API key secret', secret: true },
    { name: 'access_token', label: 'Access token', secret: true },
    { name: 'access_secret', label: 'Access token secret', secret: true,
      hint: 'In the X developer portal: the app with Read and write permission, then generate the access token and secret.' },
  ],
};

/**
 * Where new posts are announced (docs/specs/announce.md): the owner's accounts, the wait before an announcement goes,
 * and what needs the owner (waiting for a tap, unconfirmed, failed).
 */
export function Announce({ blog }: { blog: string }) {
  const [data, setData] = useState<AnnounceScreen | null>(null);
  const [adding, setAdding] = useState<Network | null>(null);
  const [delay, setDelay] = useState('');
  const { busy, error, run } = useAction();
  const load = () => api.announce(blog).then((d) => { setData(d); setDelay(String(d.delay_minutes)); });
  useEffect(() => { run(load); }, [blog]);

  if (!data) return error ? <Notice kind="error">{error}</Notice> : <p class="quiet">Loading…</p>;
  const delayDirty = delay !== String(data.delay_minutes);

  return (
    <>
      <h1>Announce</h1>
      <p class="quiet">New posts go to your accounts after a short wait. When an agent published a post on its own, or wrote what to say, it waits for you to tap Announce on the post. <GuideLink guide="announce">How to set it up</GuideLink></p>
      {!data.available && <Notice kind="info">{data.reason}</Notice>}
      {error && <Notice kind="error">{error}</Notice>}

      {data.attention.length > 0 && (
        <>
          <h2>Needs you</h2>
          {data.attention.map((w) => (
            <a class="card link" href={`#/posts/${w.post}`} key={w.id}>
              <div class="card-head"><strong>{w.post_title ?? 'A post'}</strong><span class="quiet small">{when(w.updated_at)}</span></div>
              <span class="quiet small">{w.account_label ?? 'An account'}: </span>
              <span class={w.state === 'waiting' ? 'small' : 'small danger-text'}>{w.state === 'waiting'
                ? (w.why_waiting === 'agent_text' ? 'an agent wrote what to say. Read it, then Announce.' : 'an agent published it. Read it, then Announce.')
                : w.state === 'unconfirmed' ? "its answer was lost. Look there, then Send again or It's there." : `failed. ${w.error ?? ''}`}</span>
            </a>
          ))}
        </>
      )}

      <h2>Accounts</h2>
      {data.accounts.length === 0 && <Empty>No accounts yet.</Empty>}
      {data.accounts.map((a) => <AccountCard key={a.id} blog={blog} account={a} onChange={() => run(load)} />)}
      {adding ? (
        <AddAccount blog={blog} network={adding} onDone={() => { setAdding(null); run(load); }} onCancel={() => setAdding(null)} />
      ) : (
        <div class="row wrap">
          {data.networks.map((n) => (
            <button class="ghost" disabled={!data.available} title={data.available ? undefined : data.reason ?? undefined} onClick={() => setAdding(n)}>
              Add {networkName[n]}
            </button>
          ))}
        </div>
      )}

      <h2>The wait</h2>
      <form class="row" onSubmit={(e) => {
        e.preventDefault();
        run(async () => { await api.setAnnounceDelay(blog, Number(delay)); await load(); });
      }}>
        <label class="field">
          <span>Announce after (minutes)</span>
          <input type="number" min={0} max={1440} required value={delay} onInput={(e) => setDelay((e.target as HTMLInputElement).value)} />
        </label>
        <button class="ghost" type="button" disabled={busy || !delayDirty} onClick={() => setDelay(String(data.delay_minutes))}>Cancel</button>
        <button class="primary" disabled={busy || !delayDirty}>Save</button>
      </form>
      <p class="quiet small">Time to fix a typo you notice once it's live, or cancel the announcement on the post, before it goes out.</p>
    </>
  );
}

function AccountCard({ blog, account: a, onChange }: { blog: string; account: SocialAccount; onChange: () => void }) {
  const [removing, setRemoving] = useState(false);
  const { busy, error, run } = useAction();
  const change = (c: { auto?: boolean; enabled?: boolean }) => run(async () => { await api.changeSocialAccount(blog, a.id, c); onChange(); });
  return (
    <div class={a.enabled ? 'card' : 'card off'}>
      <div class="card-head">
        <span><strong>{a.label}</strong> <span class="quiet small">{[a.label !== networkName[a.network] && networkName[a.network], a.hint !== a.label && a.hint].filter(Boolean).join(' · ')}</span></span>
        <button class="ghost small danger-text" disabled={busy} onClick={() => setRemoving(true)}>Remove</button>
      </div>
      {a.last_error && <Notice kind="error">{a.failing_since ? `Failing since ${when(a.failing_since)}: ` : ''}{a.last_error}</Notice>}
      <div class="row wrap">
      <label class="check">
        <input type="checkbox" checked={a.auto} disabled={busy || !a.enabled} onChange={(e) => change({ auto: (e.target as HTMLInputElement).checked })} />
        <span>Announce new posts</span>
      </label>
      <label class="check">
        <input type="checkbox" checked={a.enabled} disabled={busy} onChange={(e) => change({ enabled: (e.target as HTMLInputElement).checked })} />
        <span>On</span>
      </label>
      </div>
      {a.network === 'x' && <p class="quiet small">X charges per post with a link, from the credits on your X developer account.</p>}
      {error && <Notice kind="error">{error}</Notice>}
      {removing && (
        <Confirm title={`Remove ${a.label}?`} confirm="Remove" danger onCancel={() => setRemoving(false)}
          onConfirm={() => { setRemoving(false); run(async () => { await api.removeSocialAccount(blog, a.id); onChange(); }); }}>
          <p>Its credentials are deleted here, and what was waiting to go to it is cancelled. What it already posted stays on {networkName[a.network]}.</p>
        </Confirm>
      )}
    </div>
  );
}

function AddAccount({ blog, network, onDone, onCancel }: { blog: string; network: Network; onDone: () => void; onCancel: () => void }) {
  const [values, setValues] = useState<Record<string, string>>({});
  const { busy, error, run } = useAction();
  const set = (name: string, value: string) => setValues({ ...values, [name]: value });
  const ready = fields[network].every((f) => (values[f.name] ?? '').trim().length > 0);
  return (
    <form class="card" onSubmit={(e) => {
      e.preventDefault();
      run(async () => { await api.addSocialAccount(blog, { network, ...values } as NewSocialAccount); onDone(); });
    }}>
      <strong>Add {networkName[network]}</strong>
      {network === 'x' && <Notice kind="info">X's API isn't free: each post with a link costs credits you buy on the X developer portal.</Notice>}
      {fields[network].map((f) => (
        <label class="field">
          <span>{f.label}</span>
          {f.secret
            ? <input type="password" autocomplete="off" spellcheck={false} placeholder={f.placeholder}
                value={values[f.name] ?? ''} onInput={(e) => set(f.name, (e.target as HTMLInputElement).value)} />
            : <input type="text" autocomplete="off" spellcheck={false} placeholder={f.placeholder}
                value={values[f.name] ?? ''} onInput={(e) => set(f.name, (e.target as HTMLInputElement).value)} />}
          {f.hint && <span class="quiet small">{f.hint}</span>}
        </label>
      ))}
      <label class="field">
        <span>Name <span class="quiet">(optional)</span></span>
        <input type="text" maxLength={100} value={values.label ?? ''} onInput={(e) => set('label', (e.target as HTMLInputElement).value)} />
      </label>
      <p class="quiet small">Kept encrypted on the server and never shown again.</p>
      {error && <Notice kind="error">{error}</Notice>}
      <div class="row end">
        <button class="ghost" type="button" onClick={onCancel}>Cancel</button>
        <button class="primary" disabled={busy || !ready}>Add</button>
      </div>
    </form>
  );
}
