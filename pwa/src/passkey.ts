// Passkeys in the browser (docs/specs/auth.md § Passkeys): the server's WebAuthn options are JSON with base64url bytes;
// the browser wants ArrayBuffers, and its answer goes back as JSON the same way. The server pages carry the same few
// lines (Auth/Pages.cs) for setup, sign-in and invitations; this is the app's half, for adding one to your account.

const b64 = (buf: ArrayBuffer) =>
  btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const bytes = (s: string) =>
  Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((s.length + 3) % 4)), (c) => c.charCodeAt(0));

type Descriptor = { id: string; type: string; transports?: string[] };
type CreationOptionsJson = { challenge: string; user: { id: string; name: string; displayName: string }; excludeCredentials?: Descriptor[] } & Record<string, unknown>;

export const passkeysSupported = () => typeof window !== 'undefined' && 'PublicKeyCredential' in window;

/** Asks this device for a new passkey for the given creation options; returns its answer as JSON. */
export async function createPasskey(o: CreationOptionsJson): Promise<unknown> {
  const publicKey = {
    ...o,
    challenge: bytes(o.challenge),
    user: { ...o.user, id: bytes(o.user.id) },
    excludeCredentials: (o.excludeCredentials ?? []).map((c) => ({ ...c, id: bytes(c.id) })),
  } as unknown as PublicKeyCredentialCreationOptions;
  const c = (await navigator.credentials.create({ publicKey })) as PublicKeyCredential;
  if (typeof (c as { toJSON?: () => unknown }).toJSON === 'function') return (c as unknown as { toJSON: () => unknown }).toJSON();
  const r = c.response as AuthenticatorAttestationResponse;
  return {
    id: c.id, rawId: b64(c.rawId), type: c.type, authenticatorAttachment: c.authenticatorAttachment ?? null,
    clientExtensionResults: c.getClientExtensionResults(),
    response: { clientDataJSON: b64(r.clientDataJSON), attestationObject: b64(r.attestationObject), transports: r.getTransports?.() ?? [] },
  };
}
