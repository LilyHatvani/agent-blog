import { useEffect, useState } from 'preact/hooks';
import { api, statusName, when, type Announced, type PostSummary, type Role, type SectionRef, type Status } from '../api';
import { Icon } from '../components/Icon';
import { NetworkIcon } from '../components/NetworkIcon';
import { Empty, Notice, StatusPill } from '../components/ui';

const filters: (Status | undefined)[] = [undefined, 'draft', 'review', 'scheduled', 'live', 'trash'];

/** The list's filters live in the address, so a reload or a shared link keeps them. */
function readQuery() {
  const q = new URLSearchParams(location.hash.split('?')[1] ?? '');
  return { status: (q.get('status') as Status) ?? undefined, section: q.get('section') ?? undefined };
}

function writeQuery(status: Status | undefined, section: string | undefined) {
  const q = new URLSearchParams();
  if (status) q.set('status', status);
  if (section) q.set('section', section);
  const s = q.toString();
  history.replaceState(null, '', `#/posts${s ? `?${s}` : ''}`);
}

export function Posts({ blog, role }: { blog: string; role: Role }) {
  const [status, setStatus] = useState<Status | undefined>(() => readQuery().status);
  const [section, setSection] = useState<string | undefined>(() => readQuery().section);
  const [sections, setSections] = useState<SectionRef[]>([]);
  const [posts, setPosts] = useState<PostSummary[] | null>(null);
  const [announced, setAnnounced] = useState<Record<string, Announced[]>>({});
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.sections(blog).then(setSections).catch(() => setSections([]));
  }, [blog]);

  useEffect(() => {
    setPosts(null);
    setError(null);
    api.postList(blog, status, section)
      .then((r) => { setPosts(r.posts); setAnnounced(r.announced ?? {}); })
      .catch((e) => setError(e.message));
  }, [blog, status, section]);

  const pickStatus = (f: Status | undefined) => { setStatus(f); writeQuery(f, section); };
  const pickSection = (s: string | undefined) => { setSection(s); writeQuery(status, s); };
  const sectionName = sections.find((s) => s.slug === section)?.title;

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
          <button role="tab" aria-selected={status === f} class={status === f ? 'chip active' : 'chip'} onClick={() => pickStatus(f)}>
            {f ? statusName[f] : 'All'}
          </button>
        ))}
      </div>
      {sections.length > 0 && (
        <div class="chips" role="tablist" aria-label="Filter by section">
          <button role="tab" aria-selected={!section} class={!section ? 'chip active' : 'chip'} onClick={() => pickSection(undefined)}>
            Every section
          </button>
          {sections.map((s) => (
            <button role="tab" aria-selected={section === s.slug} class={section === s.slug ? 'chip active' : 'chip'}
              onClick={() => pickSection(s.slug)}>
              {s.title}
            </button>
          ))}
        </div>
      )}
      {error && <Notice kind="error">{error}</Notice>}
      {!posts && !error && <p class="quiet">Loading…</p>}
      {posts?.length === 0 && (
        <Empty>
          {section && sectionName ? `No posts in ${sectionName} here.`
            : status === 'trash' ? 'The trash is empty.'
            : role === 'owner' ? 'No posts here yet. Write one, or ask your agent for a draft.' : 'No posts here yet. New post starts one.'}
        </Empty>
      )}
      {posts?.map((p) => {
        const where = announced[p.id] ?? [];
        return (
          <a class="card link" href={`#/posts/${p.id}`} key={p.id}>
            <div class="card-head">
              <strong>{p.title}</strong>
              <span class="row nowrap card-marks">
                {where.length > 0 && (
                  <span class="announced" aria-label={`Announced on ${where.length} ${where.length === 1 ? 'account' : 'accounts'}`}>
                    {where.map((a, i) => <NetworkIcon key={`${a.network}-${i}`} network={a.network} />)}
                  </span>
                )}
                <StatusPill status={p.status} />
              </span>
            </div>
            {p.excerpt && <span class="excerpt">{p.excerpt}</span>}
            <span class="quiet">
              {p.status === 'scheduled' && p.publish_at ? `Goes live ${new Date(p.publish_at + 'Z').toLocaleString()}` : `Changed ${when(p.updated_at)}`}
              {' · '}revision {p.revision_number}
            </span>
          </a>
        );
      })}
    </>
  );
}
