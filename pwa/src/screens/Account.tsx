import { useState } from 'preact/hooks';
import { api, type Me } from '../api';
import { Confirm, Notice, useAction } from '../components/ui';

const providerName = (p: string) => (p === 'google' ? 'Google' : p === 'discord' ? 'Discord' : p);

/** Who you are, your ways in (a second one is how you get back if you lose the first), sign out. */
export function Account({ me }: { me: Me }) {
  const { busy, error, run } = useAction();
  const [toRemove, setToRemove] = useState<Me['identities'][number] | null>(null);
  const [identities, setIdentities] = useState(me.identities);
  const missing = me.providers.filter((p) => !identities.some((i) => i.provider === p));

  return (
    <>
      <h1>Account</h1>
      <p>Signed in as <strong>{me.account.display_name}</strong>.</p>

      <h2>Ways to sign in</h2>
      {identities.map((i) => (
        <div class="card" key={i.provider + i.account_id}>
          <div class="card-head">
            <span><strong>{providerName(i.provider)}</strong> <span class="quiet">{i.display_name ?? i.account_id}</span></span>
            {i.this_session
              ? <span class="quiet small">this session</span>
              : <button class="ghost small danger-text" disabled={busy || identities.length < 2} onClick={() => setToRemove(i)}
                  title={identities.length < 2 ? "It's your only way in." : undefined}>Remove</button>}
          </div>
        </div>
      ))}
      {missing.length > 0 && (
        <>
          <div class="row">
            {missing.map((p) => (
              <button class="ghost" disabled={busy} onClick={() => run(async () => { location.href = (await api.bind(p)).url; })}>
                Add {providerName(p)}
              </button>
            ))}
          </div>
          <p class="quiet small">A second way in is how you get back if you lose the first.</p>
        </>
      )}

      {error && <Notice kind="error">{error}</Notice>}
      <h2>Sign out</h2>
      <div class="row">
        <button class="ghost" disabled={busy} onClick={() => run(async () => { await api.signOut(); location.href = '/sign-in'; })}>Sign out</button>
        <button class="ghost" disabled={busy} onClick={() => run(async () => { await api.signOutEverywhere(); location.href = '/sign-in'; })}>
          Sign out everywhere
        </button>
      </div>

      {toRemove && (
        <Confirm title={`Remove ${providerName(toRemove.provider)}?`} confirm="Remove" danger
          onCancel={() => setToRemove(null)}
          onConfirm={() => {
            const i = toRemove;
            setToRemove(null);
            run(() => api.unbind(i.provider, i.account_id)).then((ok) => ok && setIdentities(identities.filter((x) => x !== i)));
          }}>
          <p>You won't be able to sign in with it any more, and the sessions it started end.</p>
        </Confirm>
      )}
    </>
  );
}
