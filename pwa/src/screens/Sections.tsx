import { useEffect, useState } from 'preact/hooks';
import { api, type CategoryChange, type CategoryInfo } from '../api';
import { Icon } from '../components/Icon';
import { Empty, GuideLink, Notice, useAction } from '../components/ui';

/**
 * The owner's sections (docs/specs/taxonomy.md § Sections): any category can be one, with its own landing page, feeds,
 * a place in the site's nav, and a say over the home page. Each category is its own small form: Save sends only what
 * changed, Cancel puts back what was saved.
 */
export function Sections({ blog }: { blog: string }) {
  const [categories, setCategories] = useState<CategoryInfo[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [asSection, setAsSection] = useState(true);
  const add = useAction();

  const load = () => api.categories(blog).then(setCategories).catch((e) => setError(e.message));
  useEffect(() => { load(); }, [blog]);

  const create = () => add.run(async () => {
    const { category } = await api.createCategory(blog, name.trim());
    if (asSection) await api.editCategory(blog, category.slug, { is_section: true });
    setName('');
    await load();
  });

  const sections = categories?.filter((c) => c.section).sort((a, b) => a.section!.nav_order - b.section!.nav_order || a.name.localeCompare(b.name)) ?? [];
  const plain = categories?.filter((c) => !c.section) ?? [];

  return (
    <>
      <a class="back" href="#/account"><Icon name="back" /> Account</a>
      <h1>Sections</h1>
      <p class="quiet">A section is a category with its own page, its own feeds and a place in the site's nav. Put a post in
        one from the post's page. <GuideLink guide="sections" /></p>
      {error && <Notice kind="error">{error}</Notice>}
      {!categories && !error && <p class="quiet">Loading…</p>}

      <form class="card" onSubmit={(e) => { e.preventDefault(); if (name.trim()) create(); }}>
        <strong>New category</strong>
        <div class="row wrap">
          <input value={name} placeholder="Name" aria-label="Name of the new category" maxLength={100}
            onInput={(e) => setName((e.target as HTMLInputElement).value)} />
          <label class="check">
            <input type="checkbox" checked={asSection} onChange={(e) => setAsSection((e.target as HTMLInputElement).checked)} />
            <span>As a section</span>
          </label>
          <button class="primary small" type="submit" disabled={add.busy || !name.trim()}>Add</button>
        </div>
        {add.error && <Notice kind="error">{add.error}</Notice>}
      </form>

      {categories && (
        <>
          <h2>Sections</h2>
          {sections.length === 0 && <Empty>No sections yet. Mark a category below, or add one above.</Empty>}
          {sections.map((c) => <CategoryForm key={c.id} blog={blog} category={c} onSaved={load} />)}
          <h2>Other categories</h2>
          {plain.length === 0 && <Empty>Every category is a section.</Empty>}
          {plain.map((c) => <CategoryForm key={c.id} blog={blog} category={c} onSaved={load} />)}
        </>
      )}
    </>
  );
}

type Draft = { is_section: boolean; title: string; intro: string; nav_order: number; show_on_home: boolean };

const draftOf = (c: CategoryInfo): Draft => ({
  is_section: !!c.section, title: c.section?.title ?? c.name, intro: c.section?.intro ?? '',
  nav_order: c.section?.nav_order ?? 0, show_on_home: c.section?.show_on_home ?? true,
});

function CategoryForm({ blog, category, onSaved }: { blog: string; category: CategoryInfo; onSaved: () => void }) {
  const saved = draftOf(category);
  const [draft, setDraft] = useState(saved);
  const { busy, error, run } = useAction();
  useEffect(() => setDraft(draftOf(category)), [category]);

  // Only what changed is sent; unmarking sends is_section alone (the section's fields go with the mark).
  const change: CategoryChange = {};
  if (draft.is_section !== saved.is_section) change.is_section = draft.is_section;
  if (draft.is_section) {
    if (draft.title !== saved.title) change.title = draft.title;
    if (draft.intro !== saved.intro) change.intro = draft.intro;
    if (draft.nav_order !== saved.nav_order) change.nav_order = draft.nav_order;
    if (draft.show_on_home !== saved.show_on_home) change.show_on_home = draft.show_on_home;
  }
  const dirty = Object.keys(change).length > 0;
  const set = <K extends keyof Draft>(k: K, v: Draft[K]) => setDraft({ ...draft, [k]: v });
  const posts = category.posts ?? 0;

  return (
    <form class="card" onSubmit={(e) => { e.preventDefault(); if (dirty) run(() => api.editCategory(blog, category.slug, change)).then((ok) => { if (ok) onSaved(); }); }}>
      <div class="card-head">
        <strong>{category.name}</strong>
        <span class="quiet small">{posts} {posts === 1 ? 'post' : 'posts'}{category.live_posts ? `, ${category.live_posts} live` : ''}</span>
      </div>
      <label class="check">
        <input type="checkbox" checked={draft.is_section} onChange={(e) => set('is_section', (e.target as HTMLInputElement).checked)} />
        <span>A section</span>
      </label>
      {draft.is_section && (
        <>
          <label class="field">
            <span>Title on its page and in the nav</span>
            <input value={draft.title} maxLength={200} onInput={(e) => set('title', (e.target as HTMLInputElement).value)} />
          </label>
          <label class="field">
            <span>Intro, above its posts (Markdown)</span>
            <textarea value={draft.intro} rows={3} onInput={(e) => set('intro', (e.target as HTMLTextAreaElement).value)} />
          </label>
          <div class="row wrap">
            <label class="field nav-order">
              <span>Place in the nav</span>
              <input type="number" value={draft.nav_order} step={1}
                onInput={(e) => set('nav_order', Number.parseInt((e.target as HTMLInputElement).value || '0', 10) || 0)} />
            </label>
            <label class="check">
              <input type="checkbox" checked={draft.show_on_home} onChange={(e) => set('show_on_home', (e.target as HTMLInputElement).checked)} />
              <span>Its posts show on the home page</span>
            </label>
          </div>
        </>
      )}
      {!draft.is_section && saved.is_section && (
        <p class="quiet small">Unmarked, it loses its page's intro, its nav place and its feeds; its posts stay in the category.</p>
      )}
      {error && <Notice kind="error">{error}</Notice>}
      <div class="row end">
        <button class="ghost small" type="button" disabled={busy || !dirty} onClick={() => setDraft(saved)}>Cancel</button>
        <button class="primary small" type="submit" disabled={busy || !dirty}>Save</button>
      </div>
    </form>
  );
}
