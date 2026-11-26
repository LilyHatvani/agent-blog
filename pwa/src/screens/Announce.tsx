import { useEffect, useState } from 'preact/hooks';
import { api, when, type AnnounceScreen, type Network, type NewSocialAccount, type SectionRef, type SocialAccount, type Subscribers } from '../api';
import { Confirm, Empty, GuideLink, Notice, useAction } from '../components/ui';

export const networkName: Record<Network, string> = {
  bluesky: 'Bluesky', mastodon: 'Mastodon', discord: 'Discord', slack: 'Slack', telegram: 'Telegram', x: 'X', threads: 'Threads', facebook: 'Facebook Page', email: 'Email', patreon: 'Patreon',
};

/** What each network asks for, in the words of its own settings screens (docs/specs/announce.md § Accounts). */
type Field = { name: keyof NewSocialAccount; label: string; hint?: string; secret?: boolean; placeholder?: string; optional?: boolean };

/** Email: the From address on a domain the service has verified, then the service's own credentials. */
const emailFields = (provider: string): Field[] => [
  { name: 'from', label: 'From address', placeholder: 'posts@yourdomain.com', hint: 'On a domain your mail service lets you send from.' },
  { name: 'from_name', label: 'From name', optional: true, hint: 'Empty: the blog\'s title.' },
  ...(provider === 'smtp' ? [
    { name: 'host', label: 'SMTP server', placeholder: 'smtp.example.com' },
    { name: 'port', label: 'Port', placeholder: '587', optional: true, hint: '587 (STARTTLS) or 465 (TLS). On Railway, SMTP works on the Pro plan only: use Resend there.' },
    { name: 'user', label: 'User', optional: true },
    { name: 'password', label: 'Password', secret: true, optional: true },
  ] as Field[] : [
    { name: 'api_key', label: 'Resend API key', secret: true, placeholder: 're_…', hint: 'Resend: API Keys, Create API Key, with Sending access. Verify your domain under Domains first.' },
  ] as Field[]),
];

const fields: Record<Network, Field[]> = {
  email: [],
  threads: [
    { name: 'token', label: 'Long-lived access token', secret: true,
      hint: 'From your own Meta app with the Threads use case (threads_basic, threads_content_publish), your profile added as a Threads tester. It lasts 60 days; the blog renews it every week.' },
    { name: 'account_id', label: 'Threads user id', optional: true, placeholder: 'empty: the token\'s own profile' },
  ],
  facebook: [
    { name: 'account_id', label: 'Page id', placeholder: '104000000000001', hint: 'On the Page: About, Page transparency. Or in the Graph API Explorer.' },
    { name: 'token', label: 'Page access token', secret: true,
      hint: 'In the Graph API Explorer, with pages_manage_posts and pages_read_engagement, made from a long-lived user token: then it doesn\'t expire.' },
  ],
  patreon: [{ name: 'url', label: 'Your Patreon page', optional: true, placeholder: 'https://www.patreon.com/yourname',
    hint: 'Patreon can\'t be posted to for you. Each new post waits on its page with the words ready to copy, then you tap It\'s there.' }],
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
  const [sections, setSections] = useState<SectionRef[]>([]);
  const { busy, error, run } = useAction();
  const load = () => api.announce(blog).then((d) => { setData(d); setDelay(String(d.delay_minutes)); });
  useEffect(() => { run(load); api.sections(blog).then(setSections).catch(() => setSections([])); }, [blog]);

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
                ? (w.why_waiting === 'manual' ? 'copy it to Patreon, then It\'s there.' : w.why_waiting === 'agent_text' ? 'an agent wrote what to say. Read it, then Announce.' : 'an agent published it. Read it, then Announce.')
                : w.state === 'unconfirmed' ? "its answer was lost. Look there, then Send again or It's there." : `failed. ${w.error ?? ''}`}</span>
            </a>
          ))}
        </>
      )}

      <h2>Accounts</h2>
      {data.accounts.length === 0 && <Empty>No accounts yet.</Empty>}
      {data.accounts.map((a) => <AccountCard key={a.id} blog={blog} account={a} sections={sections} onChange={() => run(load)} />)}
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

function AccountCard({ blog, account: a, sections, onChange }: { blog: string; account: SocialAccount; sections: SectionRef[]; onChange: () => void }) {
  const [removing, setRemoving] = useState(false);
  const { busy, error, run } = useAction();
  const change = (c: { auto?: boolean; enabled?: boolean; section_ids?: string[] }) => run(async () => { await api.changeSocialAccount(blog, a.id, c); onChange(); });
  // Kept sections that are still sections; an id whose section was unmarked drops out the next time the owner ticks.
  const kept = a.section_ids.filter((id) => sections.some((s) => s.id === id));
  const toggle = (id: string, on: boolean) => change({ section_ids: on ? [...kept, id] : kept.filter((x) => x !== id) });
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
      {sections.length > 0 && (
        <div class="account-sections">
          <span class="quiet small">{a.scope_lost ? 'Announces nothing on its own. Only posts in:'
            : kept.length === 0 ? 'Announces every new post. Tick sections to keep it to them:' : 'Announces only new posts in:'}</span>
          <div class="row wrap">
            {sections.map((s) => (
              <label class="check" key={s.id}>
                <input type="checkbox" checked={kept.includes(s.id)} disabled={busy || !a.enabled || !a.auto}
                  onChange={(e) => toggle(s.id, (e.target as HTMLInputElement).checked)} />
                <span>{s.title}</span>
              </label>
            ))}
          </div>
          {!a.auto && a.enabled && <p class="quiet small">Sections apply to new posts it announces on its own: turn on Announce new posts first.</p>}
        </div>
      )}
      {a.scope_lost && (
        <Notice kind="error">It was kept to sections that aren't sections any more, so it announces nothing on its own. Tick a
          section, or <button class="link" disabled={busy} onClick={() => change({ section_ids: [] })}>announce every post</button>.</Notice>
      )}
      {a.network === 'x' && <p class="quiet small">X charges per post with a link, from the credits on your X developer account.</p>}
      {a.network === 'email' && <EmailExtras blog={blog} account={a} />}
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
  const provider = values.provider ?? 'resend';
  const list = network === 'email' ? emailFields(provider) : fields[network];
  const ready = list.every((f) => f.optional || (values[f.name] ?? '').trim().length > 0);
  return (
    <form class="card" onSubmit={(e) => {
      e.preventDefault();
      const body = { network, ...values, provider: network === 'email' ? provider : undefined, port: values.port ? Number(values.port) : undefined } as NewSocialAccount;
      run(async () => { await api.addSocialAccount(blog, body); onDone(); });
    }}>
      <strong>Add {networkName[network]}</strong>
      {network === 'x' && <Notice kind="info">X's API isn't free: each post with a link costs credits you buy on the X developer portal.</Notice>}
      {network === 'email' && (
        <label class="field">
          <span>Mail service</span>
          <select value={provider} onChange={(e) => set('provider', (e.target as HTMLSelectElement).value)}>
            <option value="resend">Resend</option>
            <option value="smtp">SMTP (any other service)</option>
          </select>
        </label>
      )}
      {list.map((f) => (
        <label class="field">
          <span>{f.label}{f.optional && <span class="quiet"> (optional)</span>}</span>
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

/** The Email account's own: a test to an address you type, and who subscribed, with Remove (docs/specs/subscribe.md). */
function EmailExtras({ blog, account }: { blog: string; account: SocialAccount }) {
  const [list, setList] = useState<Subscribers | null>(null);
  const [open, setOpen] = useState(false);
  const [to, setTo] = useState('');
  const [tested, setTested] = useState<{ sent: boolean; message: string | null } | null>(null);
  const { busy, error, run } = useAction();
  const load = () => api.subscribers(blog).then(setList);
  useEffect(() => { run(load); }, [blog]);
  return (
    <div class="email-extras">
      <p class="small">
        {list ? <>{list.confirmed} subscriber{list.confirmed === 1 ? '' : 's'}{list.pending > 0 ? `, ${list.pending} not confirmed yet` : ''}</> : '…'}
        {' · '}<a href="/subscribe" target="_blank" rel="noopener">the form</a>
        {list && list.subscribers.length > 0 && <> · <button class="link small" onClick={() => setOpen(!open)} aria-expanded={open}>{open ? 'Hide' : 'Show'} who</button></>}
      </p>
      {open && list && (
        <ul class="subscribers">
          {list.subscribers.map((x) => (
            <li key={x.id}>
              <span>{x.email ?? '(no address)'} <span class="quiet small">{x.state === 'pending' ? 'not confirmed' : `since ${when(x.confirmed_at)}`}</span></span>
              <button class="ghost small danger-text" disabled={busy} onClick={() => run(async () => { await api.removeSubscriber(blog, x.id); await load(); })}>Remove</button>
            </li>
          ))}
        </ul>
      )}
      <form class="row" onSubmit={(e) => { e.preventDefault(); run(async () => setTested(await api.testEmail(blog, account.id, to))); }}>
        <label class="field grow">
          <span>Send a test to</span>
          <input type="email" value={to} placeholder="you@example.com" onInput={(e) => setTo((e.target as HTMLInputElement).value)} />
        </label>
        <button class="ghost small" disabled={busy || !to.includes('@')}>Send test</button>
      </form>
      {tested && <p class={tested.sent ? 'small ok-text' : 'small danger-text'}>{tested.sent ? 'Sent. Check that inbox.' : tested.message}</p>}
      {error && <Notice kind="error">{error}</Notice>}
    </div>
  );
}
