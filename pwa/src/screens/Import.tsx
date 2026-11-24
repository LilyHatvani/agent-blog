import { useEffect, useState } from 'preact/hooks';
import { api, when, type ImportedExport, type ImportJob, type Person } from '../api';
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
  const [people, setPeople] = useState<Person[]>([]);
  const [load, setLoad] = useState<string | null>(null);
  const [source, setSource] = useState('https://serverapi-staging.up.railway.app');
  const [profiles, setProfiles] = useState('https://chat.echoofdreams.eu');
  // "This author is an account already here": source author (slug or name) → a member of this blog.
  const [map, setMap] = useState<{ key: string; account: string }[]>([]);
  const { busy, error, run } = useAction();

  const refresh = () => api.imports(blog).then(setJobs).catch((e) => setLoad(e.message));
  useEffect(() => {
    refresh();
    api.people(blog).then((r) => setPeople(r.people)).catch(() => { /* the map is optional; the import runs without it */ });
  }, [blog]);
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
      <FromExport blog={blog} />
      <h1>Import from Echo of Dreams</h1>
      <p class="quiet small">Every published post comes over with its address, date, tags and category; its pictures are copied
        here; each author becomes a writer under their own name. Nothing is announced or emailed. <GuideLink guide="import" /></p>

      <form class="card form" onSubmit={(e) => {
        e.preventDefault();
        run(async () => {
          const author_map: Record<string, string> = {};
          for (const r of map) if (r.key.trim() && r.account) author_map[r.key.trim()] = r.account;
          await api.startImport(blog, source, profiles, Object.keys(author_map).length > 0 ? author_map : undefined);
          await refresh();
        });
      }}>
        <label class="field">
          <span>Echo of Dreams' address <span class="quiet small">(its API)</span></span>
          <input type="url" required value={source} onInput={(e) => setSource((e.target as HTMLInputElement).value)} />
        </label>
        <label class="field">
          <span>Where profiles live <span class="quiet small">(each author's link points there; empty for none)</span></span>
          <input type="url" value={profiles} onInput={(e) => setProfiles((e.target as HTMLInputElement).value)} />
        </label>
        <div class="field">
          <span>This author is an account here <span class="quiet small">(optional: their posts land on that account, not a new writer — at least, your own)</span></span>
          {map.map((r, i) => (
            <div class="row nowrap" key={i}>
              <input value={r.key} placeholder="Their slug or name on the source" aria-label="Source author"
                onInput={(e) => setMap(map.map((x, j) => (j === i ? { ...x, key: (e.target as HTMLInputElement).value } : x)))} />
              <select value={r.account} aria-label="Account on this blog"
                onChange={(e) => setMap(map.map((x, j) => (j === i ? { ...x, account: (e.target as HTMLSelectElement).value } : x)))}>
                <option value="">Choose…</option>
                {people.map((p) => <option value={p.account}>{p.name}</option>)}
              </select>
              <button type="button" class="ghost small" onClick={() => setMap(map.filter((_, j) => j !== i))}>Remove</button>
            </div>
          ))}
          <button type="button" class="ghost small" onClick={() => setMap([...map, { key: '', account: '' }])}>Add an author</button>
        </div>
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

/**
 * Bringing in an Agent Blog export (docs/specs/export.md): the zip "Export everything" made on another instance, into
 * this blog while it has nothing in it yet. The server says what's wrong before it changes anything.
 */
function FromExport({ blog }: { blog: string }) {
  const [file, setFile] = useState<File | null>(null);
  const [done, setDone] = useState<ImportedExport | null>(null);
  const { busy, error, run } = useAction();
  return (
    <>
      <h1>Import an Agent Blog export</h1>
      <p class="quiet small">The .agent-blog.zip from Account → Export everything on another instance: posts with every revision,
        pictures, people, notes, agents' history and subscribers, at the same addresses. It goes into a blog with nothing in it
        yet. People sign in again (Invite to claim), agents connect again, and announcing and notifications are set up again
        here. Nothing old is announced or sent. <GuideLink guide="export" /></p>
      {done ? (
        <Notice kind="ok">Imported: {done.imported.post ?? 0} posts, {done.imported.post_revision ?? 0} revisions, {done.pictures} pictures,{' '}
          {Math.max(0, (done.imported.account ?? 0))} people to invite, {done.subscribers} subscribers, {done.imported.event ?? 0} things in History.{' '}
          <a href="#/account/people">Invite them to claim their accounts</a></Notice>
      ) : (
        <form class="card form" onSubmit={(e) => { e.preventDefault(); if (file) run(async () => setDone(await api.importExport(blog, file))); }}>
          <label class="field">
            <span>The export</span>
            <input type="file" accept=".zip,application/zip" onChange={(e) => setFile((e.target as HTMLInputElement).files?.[0] ?? null)} />
          </label>
          {error && <Notice kind="error">{error}</Notice>}
          <div class="row end">
            <button class="primary" disabled={busy || !file} title={file ? undefined : 'Choose the export first.'}>{busy ? 'Importing…' : 'Import'}</button>
          </div>
        </form>
      )}
    </>
  );
}
