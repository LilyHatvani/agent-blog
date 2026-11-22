import { useEffect, useState } from 'preact/hooks';
import { api, type Me } from './api';
import { Icon, type IconName } from './components/Icon';
import { Notice } from './components/ui';
import { Inbox } from './screens/Inbox';
import { Posts } from './screens/Posts';
import { PostView } from './screens/PostView';
import { Agents } from './screens/Agents';
import { History } from './screens/History';
import { Account } from './screens/Account';

/** Hash routes: #/ inbox, #/posts, #/posts/<id>, #/agents, #/history, #/account. */
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

const tabs: { path: string; label: string; icon: IconName }[] = [
  { path: '', label: 'Inbox', icon: 'inbox' },
  { path: 'posts', label: 'Posts', icon: 'posts' },
  { path: 'agents', label: 'Agents', icon: 'agents' },
  { path: 'history', label: 'History', icon: 'history' },
  { path: 'account', label: 'Account', icon: 'account' },
];

export function App() {
  const route = useRoute();
  const [me, setMe] = useState<Me | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [blogId, setBlogId] = useState<string | null>(() => {
    try { return localStorage.getItem('agent-blog.blog'); } catch { return null; }
  });
  const [waiting, setWaiting] = useState(0);

  useEffect(() => {
    api.me().then(setMe).catch((e) => setError(e.message));
  }, []);

  // Outcomes a sign-in sends back (adding a second way in): shown once, then the URL is cleaned.
  const [flash] = useState(() => {
    const q = new URLSearchParams(location.search);
    const bound = q.get('bound');
    const failed = q.get('error');
    if (bound || failed) history.replaceState(null, '', location.pathname + location.hash);
    return bound ? { ok: true, text: `${bound === 'google' ? 'Google' : 'Discord'} added. You can sign in with it now.` }
      : failed ? { ok: false, text: failed === 'identity_taken' ? 'That account already belongs to someone else here.' : 'Adding that sign-in didn\'t finish. Try again.' }
      : null;
  });

  if (error) return <main class="page"><Notice kind="error">{error}</Notice></main>;
  if (!me) return <main class="page"><p class="quiet">Loading…</p></main>;
  if (me.blogs.length === 0) return <main class="page"><Notice kind="info">You don't own a blog here yet.</Notice></main>;

  const blog = me.blogs.find((b) => b.id === blogId) ?? me.blogs[0];
  const pick = (id: string) => {
    setBlogId(id);
    try { localStorage.setItem('agent-blog.blog', id); } catch { /* private mode: the pick lasts this visit */ }
  };

  const [top, second] = route;
  let screen;
  switch (top) {
    case 'posts': screen = second ? <PostView id={second} /> : <Posts blog={blog.id} />; break;
    case 'agents': screen = <Agents blogId={blog.id} />; break;
    case 'history': screen = <History blog={blog.id} />; break;
    case 'account': screen = <Account me={me} />; break;
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
        <a class="view-site" href="/" target="_blank" rel="noopener">View site <Icon name="external" /></a>
      </header>
      <main class="page">
        {flash && <Notice kind={flash.ok ? 'ok' : 'error'}>{flash.text}</Notice>}
        {screen}
      </main>
      <nav class="tabs" aria-label="Sections">
        {tabs.map((t) => {
          const active = (top ?? '') === t.path;
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
