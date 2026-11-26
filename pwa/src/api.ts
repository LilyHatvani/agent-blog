// The owner API (docs/specs/app.md). Every write carries X-Blog-Request (the CSRF guard, auth.md); a 401 means the
// session ended, so the app goes to sign-in and comes back here.

export type Status = 'draft' | 'review' | 'scheduled' | 'live' | 'trash';
export type Level = 'propose' | 'publish' | 'full';

export interface Me {
  account: { id: string; display_name: string };
  /** author_path: their author page on that blog (/author/{slug}), shown to readers once something of theirs is live. */
  blogs: { id: string; slug: string; title: string; role: Role; author_path: string | null; author_profile_url: string | null }[];
  /** made_at: the host a passkey was made for (docs/specs/address-move.md); null for other ways in. */
  identities: { provider: string; account_id: string; display_name: string | null; created_at: string; this_session: boolean; made_at?: string | null }[];
  /** The host passkeys are made for now, and whether this account has one for it (false only when all its passkeys are for an old address). */
  passkey_host?: string | null;
  passkey_here?: boolean;
  providers: string[];
  /** What each offered provider's button says, for the ones the owner names (the trusted provider, `oidc`). */
  provider_labels?: Record<string, string>;
  /** A demo guest (docs/specs/demo.md): everything shows, every change is refused with the reason. */
  demo: boolean;
}

export interface PostSummary {
  id: string;
  slug: string;
  status: Status;
  title: string;
  excerpt: string;
  revision: string;
  revision_number: number;
  publish_at: string | null;
  published_at: string | null;
  updated_at: string;
  terms: TermRef[];
  suggested: { kind: TermKind; name: string }[];
}

export type TermKind = 'category' | 'tag';
export interface TermRef { kind: TermKind; slug: string; name: string }
/** A category marked as a section; title is the section's own title, or the category's name. */
export interface SectionRef { id: string; slug: string; name: string; title: string }
/** A section's own fields (docs/specs/taxonomy.md § Sections). */
export interface SectionInfo { title: string; intro: string; seo_title: string; meta_description: string; nav_order: number; show_on_home: boolean }
/** A category, with its section mark when it is one. */
export interface CategoryInfo { id: string; slug: string; name: string; description: string; live_posts?: number; posts?: number; section: SectionInfo | null }
/** What the owner changes on a category; a field left out stays. is_section false unmarks it. */
export type CategoryChange = Partial<Pick<SectionInfo, 'title' | 'intro' | 'nav_order' | 'show_on_home'>> & { is_section?: boolean; name?: string; description?: string };
/** One place a post was announced: the network, and the post's address there when the network gave one. */
export interface Announced { network: Network; url: string | null }

export interface BlogSettings {
  id: string; slug: string; title: string; description: string; title_max: number; description_max: number;
  theme: string; theme_picker: boolean; themes: { name: string; label: string; description: string }[];
  search_ping: boolean;
  bylines: boolean; author_name: string; author_name_default: string;
  pings: { service: 'indexnow' | 'websub'; at: string; status: number | null; error: string | null; urls: number }[];
}

export interface Revision { id: string; number: number; title: string; by: string; by_label: string; at: string }

export interface Byline { text: string; credits: { name: string; kind: 'person' | 'agent'; role: 'wrote' | 'approved' | 'published' | 'changed' }[] }

/** What the signed-in person may do with a post (docs/specs/members.md); the server decides again on every call. */
export interface Can {
  role: Role; mine: boolean; edit: boolean; submit: boolean; publish: boolean; trash: boolean; restore_revision: boolean; decide_suggestions: boolean;
}

export interface PostDetail { post: PostSummary; body_md: string; html: string; revisions: Revision[]; byline: Byline | null; bylines_on: boolean; can: Can }

export type Role = 'owner' | 'editor' | 'writer';
/**
 * signed_in: false for an imported author nobody has signed in as yet (an invitation can claim the account).
 * imported: the import brought them in; while nobody has signed in as them, the owner can fold them into their own
 * account (This is me, docs/specs/import.md).
 */
export interface Person { account: string; name: string; role: Role; joined_at: string; author_path: string | null; signed_in: boolean; stranded?: boolean; imported?: boolean }

export interface ImportJob {
  id: string; source: 'eod'; source_url: string; profile_base: string | null; state: 'queued' | 'running' | 'done' | 'failed';
  counts: Record<string, number>; log: { level: 'info' | 'warning' | 'error'; text: string }[]; error: string | null;
  update_changed: boolean;
  created_at: string; started_at: string | null; finished_at: string | null;
}
export interface Invitation {
  id: string; role: 'editor' | 'writer'; note: string; state: 'pending' | 'accepted' | 'expired' | 'withdrawn';
  created_at: string; expires_at: string; used_at: string | null; used_by: string | null;
}

export interface RevisionContent extends Revision { excerpt: string; body_md: string; html: string }

export interface Request {
  id: string;
  blog: string;
  post: string;
  post_title: string;
  action: 'edit' | 'publish' | 'schedule' | 'unpublish' | 'trash' | 'restore';
  state: string;
  agent: string;
  /** Whose agent asked, when it isn't the owner's. */
  agent_person: string | null;
  created_at: string;
  still_applies: boolean;
  why_not: string | null;
  proposal: {
    title: string | null; excerpt: string | null; body_md: string | null; html: string | null; at: string | null;
    terms: TermRef[] | null; suggested: { kind: TermKind; name: string }[] | null;
  } | null;
  current: { title: string; excerpt: string; body_md: string; terms: TermRef[] } | null;
  checks?: Check;
}

export type Decision = 'approve' | 'decline' | 'changes';

export interface Brief {
  audience: string; voice: string; avoid: string; updated_at: string | null;
  examples: { id: string; slug: string; title: string; status: Status }[];
  limits: { audience: number; voice: number; avoid: number; examples: number };
}

export interface OwnerNote {
  id: string; kind: 'general' | 'approved' | 'declined' | 'changes_requested'; note: string;
  post: string | null; post_title: string | null; request: string | null; action: string | null;
  at: string; retired_at: string | null;
}

export interface Connection {
  id: string;
  blog: { id: string; title: string };
  label: string;
  level: Level;
  level_means: string;
  client_id: string | null;
  byline_name: string;
  byline_name_set: boolean;
  created_at: string;
  last_used_at: string | null;
  /** Whose agent it is, and the most they let it do: it works at the lower of its level and that. */
  person: { id: string; name: string; role: Role | null };
  mine: boolean;
  cap: Level | null;
  works_at: Level | null;
  can_set_level: boolean;
}

export interface MediaItem {
  id: string;
  status: 'ready' | 'trash';
  path: string;
  markdown: string;
  filename: string;
  alt: string;
  content_type: string;
  size_bytes: number;
  source_url: string | null;
  by: string;
  by_label: string;
  created_at: string;
  ready_at: string | null;
  trashed_at: string | null;
}

/** An agent's request beyond posts (docs/specs/requests.md): a picture to trash, or the blog's title or description. */
export interface BlogRequest {
  id: string; blog: string; agent: string; action: 'trash_media' | 'edit_settings'; state: string; created_at: string;
  media: { id: string; filename: string; path: string } | null;
  change: { title?: string | null; description?: string | null }; was: { title?: string; description?: string; status?: string };
  why_not: string | null; note: string | null;
}

export interface HistoryEvent {
  id: number;
  kind: string;
  actor: string;
  actor_label: string;
  target_type: string;
  subject: string | null;
  post: string | null;
  payload: Record<string, unknown>;
  at: string;
  /** For the owner, on an agent's action: what undoing it does, or why it can't now (docs/specs/history.md). */
  undo: { label: string; why_not: string | null; done: boolean } | null;
}

export type ChannelKind = 'webpush' | 'discord' | 'slack' | 'ntfy' | 'webhook';

export interface Delivery {
  id: string; channel: string; device: string | null; event: string; status: 'pending' | 'sent' | 'failed' | 'dead' | 'skipped';
  attempts: number; http_status: number | null; error: string | null; created_at: string; sent_at: string | null; next_at: string | null;
}

export interface Channel {
  id: string; kind: ChannelKind; label: string; events: string[]; detail: 'full' | 'minimal'; enabled: boolean; hint: string;
  failing_since: string | null; last_error: string | null; disabled_reason: string | null; created_at: string;
  devices: { id: string; label: string; created_at: string; endpoint_tail: string }[];
  recent: Delivery[];
}

export interface Notifications {
  available: boolean; reason: string | null; vapid_public_key: string | null;
  events: { kind: string; label: string; default: boolean }[];
  kinds: ChannelKind[];
  channels: Channel[];
}

export interface NewChannel {
  kind: ChannelKind; label: string; events?: string[]; detail?: 'full' | 'minimal';
  url?: string; server?: string; topic?: string; token?: string;
}

export interface Cdn {
  connected: boolean; provider: 'cloudflare' | null; zone_id: string | null; edge_seconds: number | null;
  connected_at: string | null; last_purge_at: string | null; failing_since: string | null; last_error: string | null;
  refused: boolean; waiting: number; default_edge_seconds: number; min_edge_seconds: number; max_edge_seconds: number;
}

export interface Check {
  ready: boolean; summary: string; problems: number; warnings: number; notes: number;
  findings: { check: string; level: 'problem' | 'warning' | 'note'; message: string; where: string | null }[];
  similar_posts: { id: string; slug: string; title: string; status: Status; similarity: number }[];
  links: { outside: number; checked: number; broken: number; unconfirmed: number; not_checked: number };
  stats: { words: number; reading_minutes: number; headings: number; pictures: number; links: number };
}

export type Network = 'bluesky' | 'mastodon' | 'discord' | 'slack' | 'telegram' | 'x' | 'threads' | 'facebook' | 'email' | 'patreon';
export interface SocialAccount {
  id: string; network: Network; label: string; hint: string; auto: boolean; enabled: boolean;
  last_error: string | null; failing_since: string | null; created_at: string;
  /** The sections whose new posts it announces; empty: every post. scope_lost: none of them is a section any more. */
  section_ids: string[]; scope_lost: boolean;
  /** Patreon only: the page Open Patreon goes to, when the owner gave one. */
  page?: string | null;
}
export type AnnouncementState = 'none' | 'draft' | 'waiting' | 'scheduled' | 'posted' | 'failed' | 'unconfirmed' | 'skipped' | 'cancelled';
export interface Composed { text: string; length: number; max: number; unit: string; problems: string[]; fits: boolean; is_default: boolean }
export interface AnnounceScreen {
  available: boolean; reason: string | null; delay_minutes: number; networks: Network[]; accounts: SocialAccount[];
  attention: { id: string; post: string; post_title: string | null; account: string; account_label: string | null;
    state: AnnouncementState; why_waiting: string | null; error: string | null; updated_at: string }[];
}
export interface Announcement {
  account: SocialAccount; id: string | null; state: AnnouncementState; why_waiting: 'agent_published' | 'agent_text' | 'manual' | null;
  text: string | null; text_by: string | null; text_by_label: string | null; due_at: string | null; attempts: number;
  url: string | null; error: string | null; notes: string[]; sent_at: string | null; composed: Composed | null; credentials_open: boolean;
  emails: { queued: number; sent: number; waiting: number; failed: number; skipped: number } | null;
}
export interface NewSocialAccount {
  network: Network; label?: string; handle?: string; password?: string; instance?: string; token?: string; url?: string; chat?: string;
  api_key?: string; api_secret?: string; access_token?: string; access_secret?: string;
  provider?: 'resend' | 'smtp'; host?: string; port?: number; user?: string; from?: string; from_name?: string;
  /** Threads: the user id (empty for the token's own profile); Facebook: the Page id. */
  account_id?: string;
}
export interface Subscribers {
  confirmed: number; pending: number;
  subscribers: { id: string; email: string | null; state: 'pending' | 'confirmed'; created_at: string; confirmed_at: string | null }[];
}

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) {
    super(message);
  }
}

export async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  let r: Response;
  try {
    r = await fetch(path, {
      method,
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-Blog-Request': '1', Accept: 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new ApiError(0, 'offline', "Can't reach the server. Check your connection and try again.");
  }
  if (r.status === 401) {
    location.href = '/sign-in?returnTo=' + encodeURIComponent('/admin' + location.hash);
    throw new ApiError(401, 'unauthenticated', 'Sign in again.');
  }
  if (r.status === 204) return undefined as T;
  const data = await r.json().catch(() => null);
  if (!r.ok) throw new ApiError(r.status, data?.code ?? 'error', data?.detail ?? `The server answered ${r.status}.`);
  return data as T;
}

export type ImportedExport = { imported: Record<string, number>; pictures: number; subscribers: number };

/** Sends a file as the request body (an export's zip): the same session, CSRF header and errors as every other call. */
async function upload<T>(path: string, file: File): Promise<T> {
  let r: Response;
  try {
    r = await fetch(path, {
      method: 'POST', credentials: 'same-origin', body: file,
      headers: { 'Content-Type': 'application/zip', 'X-Blog-Request': '1', Accept: 'application/json' },
    });
  } catch {
    throw new ApiError(0, 'offline', "Can't reach the server. Check your connection and try again.");
  }
  const data = await r.json().catch(() => null);
  if (!r.ok) throw new ApiError(r.status, data?.code ?? 'error', data?.detail ?? `The server answered ${r.status}.`);
  return data as T;
}

export const api = {
  me: () => call<Me>('GET', '/api/me'),
  exportUrl: (blog: string) => `/api/blogs/${blog}/export`,
  importExport: (blog: string, file: File) => upload<ImportedExport>(`/api/blogs/${blog}/import-export`, file),
  posts: (blog: string, status?: Status) =>
    call<{ posts: PostSummary[] }>('GET', `/api/blogs/${blog}/posts${status ? `?status=${status}` : ''}`).then((r) => r.posts),
  /** The post list as the Posts screen shows it: filtered by status and section, with where each post was announced. */
  postList: (blog: string, status?: Status, section?: string) => {
    const q = new URLSearchParams();
    if (status) q.set('status', status);
    if (section) q.set('category', section);
    const s = q.toString();
    return call<{ posts: PostSummary[]; announced: Record<string, Announced[]> }>('GET', `/api/blogs/${blog}/posts${s ? `?${s}` : ''}`);
  },
  sections: (blog: string) => call<{ sections: SectionRef[] }>('GET', `/api/blogs/${blog}/terms`).then((r) => r.sections),
  post: (id: string) => call<PostDetail>('GET', `/api/posts/${id}`),
  revision: (post: string, revision: string) => call<RevisionContent>('GET', `/api/posts/${post}/revisions/${revision}`),
  act: (post: string, op: 'publish' | 'unpublish' | 'trash' | 'restore' | 'submit' | 'schedule', body?: { base_revision?: string; at?: string }) =>
    call<unknown>('POST', `/api/posts/${post}/${op}`, body),
  restoreRevision: (post: string, revision: string) => call<unknown>('POST', `/api/posts/${post}/revisions/${revision}/restore`),
  decideSuggestion: (post: string, decision: 'accept' | 'dismiss', kind: TermKind, name: string) =>
    call<unknown>('POST', `/api/posts/${post}/suggestions/${decision}`, { kind, name }),
  blog: (id: string) => call<BlogSettings>('GET', `/api/blogs/${id}`),
  saveBlog: (id: string, patch: { title?: string; description?: string; theme?: string; theme_picker?: boolean; search_ping?: boolean; bylines?: boolean; author_name?: string }) => call<BlogSettings>('PATCH', `/api/blogs/${id}`, patch),
  requests: () => call<{ requests: Request[] }>('GET', '/api/requests').then((r) => r.requests),
  decide: (id: string, decision: Decision, note?: string, edits?: { title?: string; excerpt?: string; body_md?: string }) =>
    call<unknown>('POST', `/api/requests/${id}/${decision}`, note || edits ? { note, edits } : undefined),
  setAuthorLink: (blog: string, profile_url: string) => call<unknown>('PATCH', `/api/blogs/${blog}/me/author`, { profile_url }),
  createPost: (blog: string, d: { title: string; excerpt?: string; body_md?: string }) =>
    call<{ post: PostSummary }>('POST', `/api/blogs/${blog}/posts`, d),
  people: (blog: string) => call<{ people: Person[]; invitations: Invitation[] }>('GET', `/api/blogs/${blog}/people`),
  invite: (blog: string, role: 'editor' | 'writer', note: string, account?: string) =>
    call<{ id: string; role: string; expires_at: string; link: string }>('POST', `/api/blogs/${blog}/invitations`, { role, note, account }),
  imports: (blog: string) => call<{ imports: ImportJob[] }>('GET', `/api/blogs/${blog}/imports`).then((r) => r.imports),
  startImport: (blog: string, source_url: string, profile_base: string, author_map?: Record<string, string>, update_changed?: boolean) =>
    call<{ import: ImportJob }>('POST', `/api/blogs/${blog}/imports`, { source_url, profile_base, author_map, update_changed }),
  withdrawInvitation: (blog: string, id: string) => call<unknown>('POST', `/api/blogs/${blog}/invitations/${id}/withdraw`),
  setRole: (blog: string, account: string, role: 'editor' | 'writer') => call<unknown>('PATCH', `/api/blogs/${blog}/people/${account}`, { role }),
  removePerson: (blog: string, account: string) => call<unknown>('DELETE', `/api/blogs/${blog}/people/${account}`),
  foldIntoMe: (blog: string, account: string) => call<unknown>('POST', `/api/blogs/${blog}/people/${account}/fold`),
  editPost: (id: string, body: { base_revision: string; title?: string; excerpt?: string; body_md?: string; categories?: string[]; tags?: string[] }) =>
    call<unknown>('PATCH', `/api/posts/${id}`, body),
  categories: (blog: string) => call<{ categories: CategoryInfo[] }>('GET', `/api/blogs/${blog}/terms`).then((r) => r.categories),
  createCategory: (blog: string, name: string, description?: string) =>
    call<{ category: CategoryInfo }>('POST', `/api/blogs/${blog}/categories`, { name, description }),
  editCategory: (blog: string, slug: string, change: CategoryChange) =>
    call<{ category: CategoryInfo }>('PATCH', `/api/blogs/${blog}/categories/${slug}`, change),
  check: (post: string, links = true) => call<Check>('GET', `/api/posts/${post}/check?links=${links}`),
  preview: (body_md: string) => call<{ html: string }>('POST', '/api/preview', { body_md }),
  brief: (blog: string) => call<Brief>('GET', `/api/blogs/${blog}/brief`),
  saveBrief: (blog: string, b: { audience: string; voice: string; avoid: string; examples: string[] }) => call<Brief>('PUT', `/api/blogs/${blog}/brief`, b),
  notes: (blog: string) => call<{ notes: OwnerNote[]; sent_to_agents: number }>('GET', `/api/blogs/${blog}/notes`),
  addNote: (blog: string, note: string) => call<{ note: OwnerNote }>('POST', `/api/blogs/${blog}/notes`, { note }),
  retireNote: (blog: string, id: string) => call<unknown>('POST', `/api/blogs/${blog}/notes/${id}/retire`),
  connections: () => call<{ connections: Connection[] }>('GET', '/api/connections').then((r) => r.connections),
  setLevel: (id: string, level: Level, confirmFull: boolean) =>
    call<unknown>('PATCH', `/api/connections/${id}`, { level, confirm_full: confirmFull }),
  setBylineName: (id: string, name: string) => call<unknown>('PATCH', `/api/connections/${id}`, { byline_name: name }),
  revoke: (id: string) => call<unknown>('DELETE', `/api/connections/${id}`),
  media: (blog: string, status?: 'ready' | 'trash') =>
    call<{ enabled: boolean; media: MediaItem[] }>('GET', `/api/blogs/${blog}/media${status ? `?status=${status}` : ''}`),
  moveMedia: (id: string, op: 'trash' | 'restore') => call<{ media: MediaItem }>('POST', `/api/media/${id}/${op}`),
  blogRequests: () => call<{ requests: BlogRequest[] }>('GET', '/api/blog-requests').then((r) => r.requests),
  decideBlogRequest: (id: string, decision: 'approve' | 'decline', note?: string) =>
    call<unknown>('POST', `/api/blog-requests/${id}/${decision}`, note ? { note } : undefined),
  undo: (blog: string, eventId: number) => call<unknown>('POST', `/api/blogs/${blog}/history/${eventId}/undo`),
  history: (blog: string, before?: number) =>
    call<{ events: HistoryEvent[] }>('GET', `/api/blogs/${blog}/history${before ? `?before=${before}` : ''}`).then((r) => r.events),
  announce: (blog: string) => call<AnnounceScreen>('GET', `/api/blogs/${blog}/announce`),
  setAnnounceDelay: (blog: string, minutes: number) => call<unknown>('PUT', `/api/blogs/${blog}/announce`, { delay_minutes: minutes }),
  addSocialAccount: (blog: string, a: NewSocialAccount) => call<{ account: SocialAccount }>('POST', `/api/blogs/${blog}/social-accounts`, a),
  changeSocialAccount: (blog: string, id: string, change: { label?: string; auto?: boolean; enabled?: boolean; section_ids?: string[] }) =>
    call<{ account: SocialAccount }>('PATCH', `/api/blogs/${blog}/social-accounts/${id}`, change),
  removeSocialAccount: (blog: string, id: string) => call<unknown>('DELETE', `/api/blogs/${blog}/social-accounts/${id}`),
  subscribers: (blog: string) => call<Subscribers>('GET', `/api/blogs/${blog}/subscribers`),
  removeSubscriber: (blog: string, id: string) => call<unknown>('DELETE', `/api/blogs/${blog}/subscribers/${id}`),
  testEmail: (blog: string, account: string, to: string) =>
    call<{ sent: boolean; outcome: string; message: string | null }>('POST', `/api/blogs/${blog}/social-accounts/${account}/test`, { to }),
  announcements: (post: string) => call<{ post: string; status: Status; link: string | null; announcements: Announcement[] }>('GET', `/api/posts/${post}/announcements`),
  composeAnnouncement: (post: string, account: string, text: string | null) =>
    call<Composed>('POST', `/api/posts/${post}/announcements/${account}/compose`, { text }),
  writeAnnouncement: (post: string, account: string, text: string | null) =>
    call<{ id: string; state: AnnouncementState; text: string | null; composed: Composed }>('PUT', `/api/posts/${post}/announcements/${account}`, { text }),
  announcementAction: (post: string, account: string, action: 'send' | 'resend' | 'done' | 'cancel') =>
    call<unknown>('POST', `/api/posts/${post}/announcements/${account}/${action}`),
  notifications: (blog: string) => call<Notifications>('GET', `/api/blogs/${blog}/notifications`),
  addChannel: (blog: string, c: NewChannel) => call<{ channel: Channel; signing_secret: string | null }>('POST', `/api/blogs/${blog}/channels`, c),
  changeChannel: (blog: string, id: string, change: { label?: string; events?: string[]; detail?: 'full' | 'minimal'; enabled?: boolean }) =>
    call<{ channel: Channel }>('PATCH', `/api/blogs/${blog}/channels/${id}`, change),
  removeChannel: (blog: string, id: string) => call<unknown>('DELETE', `/api/blogs/${blog}/channels/${id}`),
  testChannel: (blog: string, id: string) => call<{ deliveries: Delivery[] }>('POST', `/api/blogs/${blog}/channels/${id}/test`),
  addDevice: (blog: string, subscription: PushSubscriptionJSON) => call<unknown>('POST', `/api/blogs/${blog}/push/devices`, subscription),
  removeThisDevice: (blog: string, endpoint: string) => call<unknown>('POST', `/api/blogs/${blog}/push/devices/remove`, { endpoint }),
  removeDevice: (blog: string, id: string) => call<unknown>('DELETE', `/api/blogs/${blog}/push/devices/${id}`),
  cdn: (blog: string) => call<Cdn>('GET', `/api/blogs/${blog}/cdn`),
  connectCdn: (blog: string, zone_id: string, token: string, edge_seconds: number) => call<Cdn>('PUT', `/api/blogs/${blog}/cdn`, { zone_id, token, edge_seconds }),
  disconnectCdn: (blog: string) => call<{ disconnected: boolean; note: string | null }>('DELETE', `/api/blogs/${blog}/cdn`),
  bind: (provider: string) => call<{ url: string }>('POST', `/api/auth/identities/${provider}`),
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  beginPasskey: () => call<{ ceremony: string; options: any }>('POST', '/api/auth/passkeys/begin', {}),
  finishPasskey: (ceremony: string, credential: unknown) =>
    call<{ provider: string; account_id: string }>('POST', '/api/auth/passkeys/finish', { ceremony, credential }),
  unbind: (provider: string, accountId: string) =>
    call<unknown>('DELETE', `/api/auth/identities/${encodeURIComponent(provider)}/${encodeURIComponent(accountId)}`),
  signOut: () => call<unknown>('POST', '/api/auth/sign-out'),
  signOutEverywhere: () => call<{ ended: number }>('POST', '/api/auth/sign-out-everywhere'),
};

export const levelName: Record<Level, string> = { propose: 'Propose', publish: 'Publish', full: 'Full' };

export const statusName: Record<Status, string> = {
  draft: 'Draft',
  review: 'In review',
  scheduled: 'Scheduled',
  live: 'Live',
  trash: 'Trash',
};

export function bytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${Math.round(n / 1024)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}

export function when(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso.endsWith('Z') || iso.includes('+') ? iso : iso + 'Z');
  const now = Date.now();
  const minutes = Math.round((now - d.getTime()) / 60000);
  if (minutes >= 0 && minutes < 1) return 'just now';
  if (minutes >= 0 && minutes < 60) return `${minutes} min ago`;
  if (minutes >= 0 && minutes < 24 * 60) return `${Math.round(minutes / 60)} h ago`;
  return d.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
}
