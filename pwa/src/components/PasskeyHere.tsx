import { api, type Me } from '../api';
import { createPasskey, passkeysSupported } from '../passkey';
import { Notice, useAction } from './ui';

/**
 * After the blog moved to a new address (docs/specs/address-move.md): a passkey only works at the address it was made
 * for, so someone whose passkeys are all for the old one is asked to make one here. Shown until they do.
 */
export function PasskeyHere({ me, onAdded }: { me: Me; onAdded: () => void }) {
  const { busy, error, run } = useAction();
  if (me.demo || me.passkey_here !== false) return null;
  const old = [...new Set(me.identities.filter((i) => i.provider === 'passkey' && i.made_at && i.made_at !== me.passkey_host).map((i) => i.made_at))];
  return (
    <Notice kind="info">
      <strong>This blog moved to {me.passkey_host}.</strong> Your passkeys were made for {old.join(', ')}, so they don't work here.
      Make one for this address, so you can sign in with it next time.{' '}
      <button class="ghost small" disabled={busy || !passkeysSupported()} title={passkeysSupported() ? undefined : "This browser can't make passkeys."}
        onClick={() => run(async () => {
          const { ceremony, options } = await api.beginPasskey();
          let credential: unknown;
          try { credential = await createPasskey(options); } catch (e) { if ((e as Error).name === 'NotAllowedError') return; throw e; }
          await api.finishPasskey(ceremony, credential);
          onAdded();
        })}>
        Add a passkey here
      </button>
      {error && <span class="error small"> {error}</span>}
    </Notice>
  );
}
