import { useEffect, useState } from 'preact/hooks';
import { api, type Me, type Role } from './api';
import { Icon, type IconName } from './components/Icon';
import { Notice } from './components/ui';
import { Inbox } from './screens/Inbox';
import { Posts } from './screens/Posts';
import { PostView } from './screens/PostView';
import { Agents } from './screens/Agents';
import { History } from './screens/History';
import { Account } from './screens/Account';
import { Media } from './screens/Media';
import { Notifications } from './screens/Notifications';
import { Announce } from './screens/Announce';
import { People } from './screens/People';
import { NewPost } from './screens/NewPost';
import { Import } from './screens/Import';
import { Integrations } from './screens/Integrations';

/** Hash routes: #/ inbox, #/posts, #/posts/new, #/posts/<id>, #/media, #/agents, #/history, #/account, #/account/people, #/notifications, #/announce. */
function useRoute(): string[] {
  const read = () => location.hash.replace(/^#\/?/, '').split('?')[0].split('/').filter(Boolean);
  const [route, setRoute] = useState(read);
  useEffect(() => {
    const on = () => setRoute(read());
    addEventListener('hashchange', on);
    return () => removeEventListener('hashchange', on);
  }, []);
  return route;
}

const tabs: { path: string; label: string; icon: IconName; roles: Role[] }[] = [
  { path: '', label: 'Inbox', icon: 'inbox', roles: ['owner', 'editor'] },
  { path: 'posts', label: 'Posts', icon: 'posts', roles: ['owner', 'editor', 'writer'] },
  { path: 'agents', label: 'Agents', icon: 'agents', roles: ['owner', 'editor', 'writer'] },
  { path: 'history', label: 'History', icon: 'history', roles: ['owner', 'editor'] },
  { path: 'account', label: 'Account', icon: 'account', roles: ['owner', 'editor', 'writer'] },
];

/** The screens that are the owner's alone (docs/specs/members.md): an editor or a writer is told so, not shown an error. */
const ownerOnly = new Set(['notifications', 'announce', 'integrations']);

export function App() {
  const route = useRoute();
  const [me, setMe] = useState<Me | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [blogId, setBlogId] = useState<string | null>(() => {
    try { return localStorage.getItem('agent-blog.blog'); } catch { return null; }
  });
  const [waiting, setWaiting] = useState(0);
  const [stopped, setStopped] = useState<string[]>([]);

  useEffect(() => {
    api.me().then(setMe).catch((e) => setError(e.message));
  }, []);

  const current = me && (me.blogs.find((b) => b.id === blogId) ?? me.blogs[0]);
  // A notification channel that stopped or keeps failing is said on every screen until it's fixed: never silent.
  useEffect(() => {
    if (!current || current.role !== 'owner') return;
    api.notifications(current.id)
      .then((n) => setStopped(n.channels.filter((c) => !c.enabled || c.failing_since).map((c) => c.label)))
      .catch(() => setStopped([]));
  }, [current?.id, section(route)]);

  // Outcomes a sign-in sends back (adding a second way in): shown once, then the URL is cleaned.
  const [flash, setFlash] = useState(() => {
    const q = new URLSearchParams(location.search);
    const bound = q.get('bound');
    const failed = q.get('error');
    const joined = q.get('joined');
    if (bound || failed || joined) history.replaceState(null, '', location.pathname + location.hash);
    return joined ? { ok: true, text: 'Welcome: you\'re on the blog. Posts is where you write; New post starts one.' }
      : bound ? { ok: true, text: `${bound === 'google' ? 'Google' : 'Discord'} added. You can sign in with it now.` }
      : failed ? { ok: false, text: failed === 'identity_taken' ? 'That account already belongs to someone else here.' : 'Adding that sign-in didn\'t finish. Try again.' }
      : null;
  });

  // A flash belongs to the screen it landed on: moving on clears it.
  const at = route.join('/');
  const [flashAt] = useState(at);
  useEffect(() => { if (at !== flashAt) setFlash(null); }, [at]);

  if (error) return <main class="page"><Notice kind="error">{error}</Notice></main>;
  if (!me) return <main class="page"><p class="quiet">Loading…</p></main>;
  if (me.blogs.length === 0) return <main class="page"><Notice kind="info">You aren't on a blog here (any more). If someone invited you, open their link again, or ask them for a new one.</Notice></main>;

  const blog = me.blogs.find((b) => b.id === blogId) ?? me.blogs[0];
  const pick = (id: string) => {
    setBlogId(id);
    try { localStorage.setItem('agent-blog.blog', id); } catch { /* private mode: the pick lasts this visit */ }
  };

  const role = blog.role;
  // The owner's and an editor's start is the Inbox (what waits for them to decide); a writer's is Posts.
  const [top, second] = route.length === 0 && role === 'writer' ? ['posts'] : route;
  let screen;
  if (role !== 'owner' && ownerOnly.has(top ?? '') || (role === 'writer' && top === 'history') || (role !== 'owner' && top === 'account' && (second === 'people' || second === 'import')))
    screen = <Notice kind="info">That screen is the owner's. <a href="#/posts">Posts</a></Notice>;
  else switch (top) {
    case 'posts': screen = second === 'new' ? <NewPost blog={blog.id} /> : second ? <PostView id={second} /> : <Posts blog={blog.id} role={role} />; break;
    case 'media': screen = <Media blog={blog.id} owner={role === 'owner'} />; break;
    case 'agents': screen = <Agents blogId={blog.id} role={role} />; break;
    case 'history': screen = <History blog={blog.id} />; break;
    case 'notifications': screen = <Notifications blog={blog.id} />; break;
    case 'announce': screen = <Announce blog={blog.id} />; break;
    case 'integrations': screen = <Integrations blog={blog.id} />; break;
    case 'account': screen = second === 'people' ? <People blog={blog.id} /> : second === 'import' ? <Import blog={blog.id} /> : <Account me={me} blogId={blog.id} role={role} blogTitle={blog.title} onRenamed={(title) =>
      setMe({ ...me, blogs: me.blogs.map((b) => (b.id === blog.id ? { ...b, title } : b)) })} />; break;
    default: screen = <Inbox blog={blog.id} onCount={setWaiting} />;
  }

  return (
    <div class="shell">
      <header class="top">
        {me.blogs.length > 1 ? (
          <select class="blog-pick" value={blog.id} onChange={(e) => pick((e.target as HTMLSelectElement).value)} aria-label="Blog">
            {me.blogs.map((b) => <option value={b.id}>{b.title}</option>)}
          </select>
        ) : <span class="blog-title">{blog.title}</span>}
        {me.demo
          ? <span class="view-site quiet" title="The demo's blog isn't public: it exists only in here.">View site <Icon name="external" /></span>
          : <a class="view-site" href="/" target="_blank" rel="noopener">View site <Icon name="external" /></a>}
      </header>
      <main class="page">
        {me.demo && (
          <Notice kind="info">
            <strong>Demo.</strong> This is a sample blog with two agents and a writer. Look around as much as you like; changes
            are switched off, and trying one says so.
          </Notice>
        )}
        {flash && <Notice kind={flash.ok ? 'ok' : 'error'}>{flash.text}</Notice>}
        {stopped.length > 0 && top !== 'notifications' && (
          <Notice kind="error">Notifications to {stopped.join(', ')} aren't getting through. <a href="#/notifications">See why</a></Notice>
        )}
        {screen}
      </main>
      <nav class="tabs" aria-label="Sections">
        {tabs.filter((t) => t.roles.includes(role)).map((t) => {
          // Media lives under Posts: it's what posts are made of.
          const active = (top === 'media' ? 'posts' : top === 'notifications' || top === 'announce' ? 'account' : top ?? '') === t.path;
          return (
            <a href={`#/${t.path}`} class={active ? 'tab active' : 'tab'} aria-current={active ? 'page' : undefined}>
              <span class="tab-icon">
                <Icon name={t.icon} />
                {t.path === '' && waiting > 0 && <span class="badge" aria-label={`${waiting} waiting`}>{waiting}</span>}
              </span>
              <span class="tab-label">{t.label}</span>
            </a>
          );
        })}
      </nav>
    </div>
  );
}

const section = (route: string[]) => route[0] ?? '';
