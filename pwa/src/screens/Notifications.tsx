import { useEffect, useState } from 'preact/hooks';
import { api, when, type Channel, type ChannelKind, type Delivery, type Notifications as Screen } from '../api';
import { Confirm, Empty, Notice, useAction } from '../components/ui';
import { Icon } from '../components/Icon';

const kindName: Record<ChannelKind, string> = {
  webpush: 'This app, on your phones', discord: 'Discord', slack: 'Slack', ntfy: 'ntfy', webhook: 'Webhook (Zapier, Make, n8n, your code)',
};

/** Short names for a channel's card, where the long ones crowd the line. */
const shortKind: Record<ChannelKind, string> = { webpush: 'Phones', discord: 'Discord', slack: 'Slack', ntfy: 'ntfy', webhook: 'Webhook' };

const statusWords: Record<Delivery['status'], string> = {
  pending: 'waiting', sent: 'sent', failed: 'will retry', dead: 'not delivered', skipped: 'not sent',
};

const pushSupported = () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;

function fromBase64Url(s: string): Uint8Array<ArrayBuffer> {
  const b = atob(s.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (s.length % 4)) % 4));
  const out = new Uint8Array(new ArrayBuffer(b.length));
  for (let i = 0; i < b.length; i++) out[i] = b.charCodeAt(i);
  return out;
}

/**
 * Where the blog tells you what needs you (docs/specs/notifications.md): this device, Discord, Slack, ntfy or a webhook.
 * Each channel picks its events and how much it says; Send test shows it working; the last deliveries say what went,
 * what was skipped and why, and what failed.
 */
export function Notifications({ blog }: { blog: string }) {
  const [data, setData] = useState<Screen | null>(null);
  const [adding, setAdding] = useState<ChannelKind | null>(null);
  const [secret, setSecret] = useState<string | null>(null);
  const { busy, error, run } = useAction();
  const load = () => api.notifications(blog).then(setData);
  useEffect(() => { run(load); }, [blog]);

  if (!data) return error ? <Notice kind="error">{error}</Notice> : <p class="quiet">Loading…</p>;
  const push = data.channels.find((c) => c.kind === 'webpush');
  const others = data.channels.filter((c) => c.kind !== 'webpush');

  return (
    <>
      <h1>Notifications</h1>
      <p class="quiet">What needs you, where you'll see it: an agent asking you to decide, a draft sent for review, a schedule that didn't go live, a new agent connected.</p>
      {!data.available && <Notice kind="info">{data.reason}</Notice>}
      {error && <Notice kind="error">{error}</Notice>}
      {secret && (
        <div class="card">
          <p><strong>The webhook's signing secret.</strong> Copy it into whatever receives the webhook, to check each request came from this blog. It isn't shown again.</p>
          <p><code class="secret">{secret}</code></p>
          <div class="row">
            <button class="ghost small" onClick={() => navigator.clipboard?.writeText(secret)}><Icon name="copy" /> Copy</button>
            <button class="ghost small" onClick={() => setSecret(null)}>Done</button>
          </div>
        </div>
      )}

      <h2>This device</h2>
      <ThisPhone blog={blog} data={data} channel={push} disabled={!data.available || busy} onChange={() => run(load)} />
      {push && <ChannelCard blog={blog} channel={push} events={data.events} onChange={() => run(load)} />}

      <h2>Other places</h2>
      {others.length === 0 && <Empty>No other channels yet.</Empty>}
      {others.map((c) => <ChannelCard key={c.id} blog={blog} channel={c} events={data.events} onChange={() => run(load)} />)}

      {adding ? (
        <AddChannel blog={blog} kind={adding} events={data.events}
          onDone={(s) => { setAdding(null); if (s) setSecret(s); run(load); }} onCancel={() => setAdding(null)} />
      ) : (
        <div class="row wrap">
          {(['discord', 'slack', 'ntfy', 'webhook'] as ChannelKind[]).map((k) => (
            <button class="ghost" disabled={!data.available} title={data.available ? undefined : data.reason ?? undefined} onClick={() => setAdding(k)}>
              Add {k === 'webhook' ? 'webhook' : kindName[k]}
            </button>
          ))}
        </div>
      )}
    </>
  );
}

/** Turning push on for the phone in hand: the browser asks, then the subscription goes to the server. */
function ThisPhone({ blog, data, channel, disabled, onChange }: { blog: string; data: Screen; channel?: Channel; disabled: boolean; onChange: () => void }) {
  const [endpoint, setEndpoint] = useState<string | null | undefined>(undefined);
  const { busy, error, run } = useAction();
  useEffect(() => {
    if (!pushSupported()) { setEndpoint(null); return; }
    navigator.serviceWorker.ready.then((r) => r.pushManager.getSubscription()).then((s) => setEndpoint(s?.endpoint ?? null)).catch(() => setEndpoint(null));
  }, [channel?.devices.length]);

  if (!pushSupported()) {
    return <Notice kind="info">This browser can't show notifications from the app. On an iPhone, add the app to the Home Screen first (Share, then Add to Home Screen) and open it from there.</Notice>;
  }
  const listed = endpoint && channel?.devices.some((d) => endpoint.endsWith(d.endpoint_tail));
  const on = () => run(async () => {
    if (await Notification.requestPermission() !== 'granted') throw new Error('Notifications are blocked for this site: allow them in the browser settings, then try again.');
    const reg = await navigator.serviceWorker.ready;
    const sub = await reg.pushManager.getSubscription()
      ?? await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: fromBase64Url(data.vapid_public_key!) });
    await api.addDevice(blog, sub.toJSON());
    setEndpoint(sub.endpoint);
    onChange();
  });
  const off = () => run(async () => {
    const sub = await (await navigator.serviceWorker.ready).pushManager.getSubscription();
    if (sub) {
      await api.removeThisDevice(blog, sub.endpoint).catch(() => undefined);
      await sub.unsubscribe();
    }
    setEndpoint(null);
    onChange();
  });
  return (
    <>
      <div class="row">
        {listed
          ? <button class="ghost" disabled={busy} onClick={off}>Turn off on this device</button>
          : <button class="primary" disabled={busy || disabled || !data.vapid_public_key} onClick={on}><Icon name="bell" /> Turn on for this device</button>}
      </div>
      {error && <Notice kind="error">{error}</Notice>}
    </>
  );
}

function ChannelCard({ blog, channel: c, events, onChange }: { blog: string; channel: Channel; events: Screen['events']; onChange: () => void }) {
  const [open, setOpen] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [tested, setTested] = useState<Delivery[] | null>(null);
  const [chosen, setChosen] = useState(c.events);
  const [detail, setDetail] = useState(c.detail);
  const { busy, error, run } = useAction();
  const dirty = detail !== c.detail || chosen.length !== c.events.length || chosen.some((e) => !c.events.includes(e));

  return (
    <div class={c.enabled ? 'card' : 'card off'}>
      <div class="card-head">
        <span><strong>{c.label}</strong> <span class="quiet small">{c.kind === 'webpush' ? `${c.devices.length} phone${c.devices.length === 1 ? '' : 's'}` : `${shortKind[c.kind]} · ${c.hint}`}</span></span>
        <button class="ghost small" onClick={() => setOpen(!open)} aria-expanded={open}>{open ? 'Close' : 'Settings'}</button>
      </div>
      {!c.enabled && <Notice kind="error">Stopped{c.disabled_reason ? `: ${c.disabled_reason}` : '.'}</Notice>}
      {c.enabled && c.failing_since && <Notice kind="error">Failing since {when(c.failing_since)}: {c.last_error}</Notice>}
      <div class="row wrap">
        <button class="ghost small" disabled={busy || (c.kind === 'webpush' && c.devices.length === 0)}
          title={c.kind === 'webpush' && c.devices.length === 0 ? 'Turn notifications on for a phone first.' : undefined}
          onClick={() => run(async () => { setTested((await api.testChannel(blog, c.id)).deliveries); onChange(); })}>Send test</button>
        {!c.enabled && <button class="ghost small" disabled={busy} onClick={() => run(async () => { await api.changeChannel(blog, c.id, { enabled: true }); onChange(); })}>Turn back on</button>}
      </div>
      {tested && tested.map((d) => <p class={d.status === 'sent' ? 'small ok-text' : 'small danger-text'}>{d.status === 'sent' ? 'Test sent.' : `Test ${statusWords[d.status]}: ${d.error ?? ''}`}</p>)}
      {open && (
        <div class="stack">
          <fieldset>
            <legend>Tell me when</legend>
            {events.map((e) => (
              <label class="check">
                <input type="checkbox" checked={chosen.includes(e.kind)}
                  onChange={(ev) => setChosen((ev.target as HTMLInputElement).checked ? [...chosen, e.kind] : chosen.filter((k) => k !== e.kind))} />
                {e.label}
              </label>
            ))}
          </fieldset>
          <fieldset>
            <legend>How much it says</legend>
            <label class="check"><input type="radio" name={`detail-${c.id}`} checked={detail === 'full'} onChange={() => setDetail('full')} /> The post's title and who did it</label>
            <label class="check"><input type="radio" name={`detail-${c.id}`} checked={detail === 'minimal'} onChange={() => setDetail('minimal')} /> Only what happened (for a channel others can read)</label>
          </fieldset>
          {c.kind === 'webpush' && c.devices.map((d) => (
            <div class="card-head"><span>{d.label || 'A phone'} <span class="quiet small">since {when(d.created_at)}</span></span>
              <button class="ghost small danger-text" disabled={busy} onClick={() => run(async () => { await api.removeDevice(blog, d.id); onChange(); })}>Remove</button></div>
          ))}
          <h3>Last deliveries</h3>
          {c.recent.length === 0 ? <p class="quiet small">Nothing sent yet.</p> : (
            <ul class="log">
              {c.recent.map((d) => (
                <li><span class={`dot ${d.status}`} /> {events.find((e) => e.kind === d.event)?.label ?? (d.event === 'test' ? 'Test' : d.event)} · {statusWords[d.status]} · {when(d.sent_at ?? d.created_at)}
                  {d.error && <span class="quiet small"> — {d.error}</span>}</li>
              ))}
            </ul>
          )}
          <div class="row end">
            <button class="ghost danger-text" disabled={busy} onClick={() => setRemoving(true)}>Remove channel</button>
            <button class="ghost" disabled={busy || !dirty} onClick={() => { setChosen(c.events); setDetail(c.detail); }}>Cancel</button>
            <button class="primary" disabled={busy || !dirty || chosen.length === 0} title={chosen.length === 0 ? 'Choose at least one event.' : undefined}
              onClick={() => run(async () => { await api.changeChannel(blog, c.id, { events: chosen, detail }); onChange(); })}>Save</button>
          </div>
        </div>
      )}
      {error && <Notice kind="error">{error}</Notice>}
      {removing && (
        <Confirm title={`Remove ${c.label}?`} confirm="Remove" danger onCancel={() => setRemoving(false)}
          onConfirm={() => run(async () => { setRemoving(false); await api.removeChannel(blog, c.id); onChange(); })}>
          It stops at once, and its webhook address or token is deleted here. What it already sent stays in the log.
        </Confirm>
      )}
    </div>
  );
}

function AddChannel({ blog, kind, events, onDone, onCancel }: {
  blog: string; kind: ChannelKind; events: Screen['events']; onDone: (signingSecret: string | null) => void; onCancel: () => void;
}) {
  const [label, setLabel] = useState(kind === 'ntfy' ? 'ntfy' : kindName[kind].split(' ')[0]);
  const [url, setUrl] = useState('');
  const [server, setServer] = useState('https://ntfy.sh');
  const [topic, setTopic] = useState(() => 'blog-' + Array.from(crypto.getRandomValues(new Uint8Array(9)), (b) => b.toString(16).padStart(2, '0')).join(''));
  const [token, setToken] = useState('');
  const { busy, error, run } = useAction();
  const help: Record<string, string> = {
    discord: 'In Discord: the channel\'s settings, Integrations, Webhooks, New Webhook, Copy Webhook URL.',
    slack: 'In Slack: an app with Incoming Webhooks turned on, Add New Webhook to Workspace, pick the channel, copy the URL.',
    webhook: 'An https address that takes a POST. Each request is signed (Standard Webhooks); you get the secret once, next.',
    ntfy: 'Subscribe to the topic in the ntfy app. On ntfy.sh anyone who knows the topic can read it: keep the long random one, or use a protected topic with a token.',
  };
  return (
    <div class="card">
      <h3>Add {kindName[kind]}</h3>
      <p class="quiet small">{help[kind]}</p>
      <label class="field">Name<input value={label} maxLength={100} onInput={(e) => setLabel((e.target as HTMLInputElement).value)} /></label>
      {kind === 'ntfy' ? (
        <>
          <label class="field">Server<input value={server} inputMode="url" onInput={(e) => setServer((e.target as HTMLInputElement).value)} /></label>
          <label class="field">Topic<input value={topic} maxLength={64} onInput={(e) => setTopic((e.target as HTMLInputElement).value)} /></label>
          <label class="field">Access token (optional)<input value={token} type="password" autocomplete="off" onInput={(e) => setToken((e.target as HTMLInputElement).value)} /></label>
        </>
      ) : (
        <label class="field">Webhook address<input value={url} inputMode="url" type="password" autocomplete="off" placeholder="https://…"
          onInput={(e) => setUrl((e.target as HTMLInputElement).value)} /></label>
      )}
      <p class="quiet small">It sends: {events.filter((e) => e.default).map((e) => e.label.toLowerCase()).join('; ')}. You can change that after.</p>
      {error && <Notice kind="error">{error}</Notice>}
      <div class="row end">
        <button class="ghost" disabled={busy} onClick={onCancel}>Cancel</button>
        <button class="primary" disabled={busy || !label.trim() || (kind === 'ntfy' ? !topic.trim() : !url.trim())}
          onClick={() => run(async () => {
            const r = await api.addChannel(blog, kind === 'ntfy' ? { kind, label, server, topic, token: token || undefined } : { kind, label, url });
            onDone(r.signing_secret);
          })}>Add</button>
      </div>
    </div>
  );
}
