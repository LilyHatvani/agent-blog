import { useEffect, useState } from 'preact/hooks';
import { api, statusName, when, type PostSummary, type Role, type Status } from '../api';
import { Icon } from '../components/Icon';
import { Empty, Notice, StatusPill } from '../components/ui';

const filters: (Status | undefined)[] = [undefined, 'draft', 'review', 'scheduled', 'live', 'trash'];

export function Posts({ blog, role }: { blog: string; role: Role }) {
  const [status, setStatus] = useState<Status | undefined>(() => {
    const q = new URLSearchParams(location.hash.split('?')[1] ?? '');
    return (q.get('status') as Status) ?? undefined;
  });
  const [posts, setPosts] = useState<PostSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setPosts(null);
    api.posts(blog, status).then(setPosts).catch((e) => setError(e.message));
  }, [blog, status]);

  return (
    <>
      <div class="heading-row">
        <h1>Posts</h1>
        <span class="row nowrap">
          <a class="button ghost small" href="#/media"><Icon name="image" /> Media</a>
          <a class="button primary small" href="#/posts/new">New post</a>
        </span>
      </div>
      <div class="chips" role="tablist" aria-label="Filter by status">
        {filters.map((f) => (
          <button role="tab" aria-selected={status === f} class={status === f ? 'chip active' : 'chip'}
            onClick={() => {
              setStatus(f);
              history.replaceState(null, '', `#/posts${f ? `?status=${f}` : ''}`);
            }}>
            {f ? statusName[f] : 'All'}
          </button>
        ))}
      </div>
      {error && <Notice kind="error">{error}</Notice>}
      {!posts && !error && <p class="quiet">Loading…</p>}
      {posts?.length === 0 && (
        <Empty>{status === 'trash' ? 'The trash is empty.' : role === 'owner' ? 'No posts here yet. Write one, or ask your agent for a draft.' : 'No posts here yet. New post starts one.'}</Empty>
      )}
      {posts?.map((p) => (
        <a class="card link" href={`#/posts/${p.id}`} key={p.id}>
          <div class="card-head"><strong>{p.title}</strong><StatusPill status={p.status} /></div>
          {p.excerpt && <span class="excerpt">{p.excerpt}</span>}
          <span class="quiet">
            {p.status === 'scheduled' && p.publish_at ? `Goes live ${new Date(p.publish_at + 'Z').toLocaleString()}` : `Changed ${when(p.updated_at)}`}
            {' · '}revision {p.revision_number}
          </span>
        </a>
      ))}
    </>
  );
}
