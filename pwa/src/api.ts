// The owner API (docs/specs/app.md). Every write carries X-Blog-Request (the CSRF guard, auth.md); a 401 means the
// session ended, so the app goes to sign-in and comes back here.

export type Status = 'draft' | 'review' | 'scheduled' | 'live' | 'trash';
export type Level = 'propose' | 'publish' | 'full';

export interface Me {
  account: { id: string; display_name: string };
  blogs: { id: string; slug: string; title: string; role: string }[];
  identities: { provider: string; account_id: string; display_name: string | null; this_session: boolean }[];
  providers: string[];
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

export interface BlogSettings {
  id: string; slug: string; title: string; description: string; title_max: number; description_max: number;
  theme: string; theme_picker: boolean; themes: { name: string; label: string; description: string }[];
}

export interface Revision { id: string; number: number; title: string; by: string; by_label: string; at: string }

export interface PostDetail { post: PostSummary; body_md: string; html: string; revisions: Revision[] }

export interface RevisionContent extends Revision { excerpt: string; body_md: string; html: string }

export interface Request {
  id: string;
  blog: string;
  post: string;
  post_title: string;
  action: 'edit' | 'publish' | 'schedule' | 'unpublish' | 'trash' | 'restore';
  state: string;
  agent: string;
  created_at: string;
  still_applies: boolean;
  why_not: string | null;
  proposal: {
    title: string | null; excerpt: string | null; body_md: string | null; html: string | null; at: string | null;
    terms: TermRef[] | null; suggested: { kind: TermKind; name: string }[] | null;
  } | null;
  current: { title: string; excerpt: string; body_md: string; terms: TermRef[] } | null;
}

export interface Connection {
  id: string;
  blog: { id: string; title: string };
  label: string;
  level: Level;
  level_means: string;
  client_id: string | null;
  created_at: string;
  last_used_at: string | null;
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
}

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) {
    super(message);
  }
}

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
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

export const api = {
  me: () => call<Me>('GET', '/api/me'),
  posts: (blog: string, status?: Status) =>
    call<{ posts: PostSummary[] }>('GET', `/api/blogs/${blog}/posts${status ? `?status=${status}` : ''}`).then((r) => r.posts),
  post: (id: string) => call<PostDetail>('GET', `/api/posts/${id}`),
  revision: (post: string, revision: string) => call<RevisionContent>('GET', `/api/posts/${post}/revisions/${revision}`),
  act: (post: string, op: 'publish' | 'unpublish' | 'trash' | 'restore' | 'submit' | 'schedule', body?: { base_revision?: string; at?: string }) =>
    call<unknown>('POST', `/api/posts/${post}/${op}`, body),
  restoreRevision: (post: string, revision: string) => call<unknown>('POST', `/api/posts/${post}/revisions/${revision}/restore`),
  decideSuggestion: (post: string, decision: 'accept' | 'dismiss', kind: TermKind, name: string) =>
    call<unknown>('POST', `/api/posts/${post}/suggestions/${decision}`, { kind, name }),
  blog: (id: string) => call<BlogSettings>('GET', `/api/blogs/${id}`),
  saveBlog: (id: string, patch: { title?: string; description?: string; theme?: string; theme_picker?: boolean }) => call<BlogSettings>('PATCH', `/api/blogs/${id}`, patch),
  requests: () => call<{ requests: Request[] }>('GET', '/api/requests').then((r) => r.requests),
  decide: (id: string, decision: 'approve' | 'decline') => call<unknown>('POST', `/api/requests/${id}/${decision}`),
  connections: () => call<{ connections: Connection[] }>('GET', '/api/connections').then((r) => r.connections),
  setLevel: (id: string, level: Level, confirmFull: boolean) =>
    call<unknown>('PATCH', `/api/connections/${id}`, { level, confirm_full: confirmFull }),
  revoke: (id: string) => call<unknown>('DELETE', `/api/connections/${id}`),
  media: (blog: string, status?: 'ready' | 'trash') =>
    call<{ enabled: boolean; media: MediaItem[] }>('GET', `/api/blogs/${blog}/media${status ? `?status=${status}` : ''}`),
  moveMedia: (id: string, op: 'trash' | 'restore') => call<{ media: MediaItem }>('POST', `/api/media/${id}/${op}`),
  history: (blog: string, before?: number) =>
    call<{ events: HistoryEvent[] }>('GET', `/api/blogs/${blog}/history${before ? `?before=${before}` : ''}`).then((r) => r.events),
  bind: (provider: string) => call<{ url: string }>('POST', `/api/auth/identities/${provider}`),
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
