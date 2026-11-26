import { useEffect, useState } from 'preact/hooks';
import { api, type PostDetail, type SectionRef } from '../api';
import { Notice } from './ui';

/**
 * Which section a post is in: one select on the post's page. Choosing saves at once as a revision (the post's other
 * categories stay; only the section changes), the same core edit an agent's edit_post makes. Disabled, with why, when
 * this person can't change the post.
 */
export function SectionPicker({ blog, detail, onSaved }: { blog: string; detail: PostDetail; onSaved: () => void }) {
  const [sections, setSections] = useState<SectionRef[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api.sections(blog).then(setSections).catch(() => setSections([])); }, [blog]);

  const { post, can } = detail;
  if (!sections || sections.length === 0) return null; // no sections on this blog: nothing to choose
  const slugs = new Set(sections.map((s) => s.slug));
  const current = post.terms.find((t) => t.kind === 'category' && slugs.has(t.slug))?.slug ?? '';
  const why = can.edit ? undefined : can.mine ? 'Live or scheduled: an editor or the owner changes it.' : "Someone else's post.";

  const choose = async (slug: string) => {
    const others = post.terms.filter((t) => t.kind === 'category' && !slugs.has(t.slug)).map((t) => t.name);
    const chosen = sections.find((s) => s.slug === slug);
    setBusy(true);
    setError(null);
    try {
      await api.editPost(post.id, { base_revision: post.revision, categories: chosen ? [...others, chosen.name] : others });
      onSaved();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div class="section-picker">
      <label class="row nowrap">
        <span class="quiet small">Section</span>
        <select value={current} disabled={busy || !can.edit} title={why} onChange={(e) => choose((e.target as HTMLSelectElement).value)}>
          <option value="">No section</option>
          {sections.map((s) => <option value={s.slug}>{s.title}</option>)}
        </select>
        {why && <span class="quiet small">{why}</span>}
      </label>
      {error && <Notice kind="error">{error}</Notice>}
    </div>
  );
}
