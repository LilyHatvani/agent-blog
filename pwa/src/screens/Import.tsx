import { useEffect, useState } from 'preact/hooks';
import { api, when, type ImportJob } from '../api';
import { Icon } from '../components/Icon';
import { GuideLink, Notice, useAction } from '../components/ui';

const stateName = { queued: 'waiting to start', running: 'running', done: 'done', failed: 'stopped' } as const;

/**
 * Bringing a blog over from Echo of Dreams (docs/specs/import.md): the owner gives EoD's address, the server does the
 * rest in the background, and this screen shows how far it got and what it left out. Running it again skips what's
 * already here.
 */
export function Import({ blog }: { blog: string }) {
  const [jobs, setJobs] = useState<ImportJob[] | null>(null);
  const [load, setLoad] = useState<string | null>(null);
  const [source, setSource] = useState('https://serverapi-staging.up.railway.app');
  const [profiles, setProfiles] = useState('https://chat.echoofdreams.eu');
  const { busy, error, run } = useAction();

  const refresh = () => api.imports(blog).then(setJobs).catch((e) => setLoad(e.message));
  useEffect(() => { refresh(); }, [blog]);
  const active = jobs?.some((j) => j.state === 'queued' || j.state === 'running') ?? false;
  // While one runs, its counts and log follow along.
  useEffect(() => {
    if (!active) return;
    const t = setInterval(refresh, 3000);
    return () => clearInterval(t);
  }, [active, blog]);

  if (load) return <Notice kind="error">{load}</Notice>;
  return (
    <>
      <a class="back" href="#/account"><Icon name="back" /> Account</a>
      <h1>Import from Echo of Dreams</h1>
      <p class="quiet small">Every published post comes over with its address, date, tags and category; its pictures are copied
        here; each author becomes a writer under their own name. Nothing is announced or emailed. <GuideLink guide="import" /></p>

      <form class="card form" onSubmit={(e) => { e.preventDefault(); run(async () => { await api.startImport(blog, source, profiles); await refresh(); }); }}>
        <label class="field">
          <span>Echo of Dreams' address <span class="quiet small">(its API)</span></span>
          <input type="url" required value={source} onInput={(e) => setSource((e.target as HTMLInputElement).value)} />
        </label>
        <label class="field">
          <span>Where profiles live <span class="quiet small">(each author's link points there; empty for none)</span></span>
          <input type="url" value={profiles} onInput={(e) => setProfiles((e.target as HTMLInputElement).value)} />
        </label>
        {error && <Notice kind="error">{error}</Notice>}
        <div class="row end">
          <button class="primary" disabled={busy || active} title={active ? 'An import is running.' : undefined}>
            {jobs && jobs.length > 0 ? 'Import again' : 'Import'}
          </button>
        </div>
        {active && <span class="quiet small why-not">An import is running; the button comes back when it's done.</span>}
      </form>

      {jobs?.map((j) => (
        <div class="card" key={j.id}>
          <div class="card-head">
            <strong>{stateName[j.state]}</strong>
            <span class="quiet small">{when(j.created_at)}</span>
          </div>
          <span class="quiet small">
            {j.counts.imported ?? 0} imported · {j.counts.already_here ?? 0} already here · {j.counts.failed ?? 0} failed ·{' '}
            {j.counts.pictures ?? 0} pictures · {j.counts.pictures_missing ?? 0} left out · {j.counts.authors ?? 0} new writers
          </span>
          {j.error && <Notice kind="error">{j.error}</Notice>}
          {j.log.length > 0 && (
            <details>
              <summary class="small">What happened ({j.log.length})</summary>
              <ul class="import-log">
                {j.log.slice().reverse().map((l) => <li class={l.level}>{l.text}</li>)}
              </ul>
            </details>
          )}
        </div>
      ))}
    </>
  );
}
