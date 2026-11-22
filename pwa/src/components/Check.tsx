import { useState } from 'preact/hooks';
import type { Check } from '../api';

const levelWord = { problem: 'Problem', warning: 'Worth a look', note: 'Note' } as const;

/**
 * What check_post found (docs/specs/check.md): one line that says whether readers would hit anything, and the findings
 * on a tap. The same on the Inbox card (the check of exactly what is asked) and on a post.
 */
export function CheckSummary({ check, open: startOpen = false }: { check: Check; open?: boolean }) {
  const [open, setOpen] = useState(startOpen);
  const shown = check.findings.filter((f) => f.level !== 'note' || open);
  const tone = check.problems > 0 ? 'problem' : check.warnings > 0 ? 'warning' : 'ok';
  return (
    <div class={`preflight ${tone}`}>
      <button class="row-button preflight-head" onClick={() => setOpen(!open)} aria-expanded={open}>
        <span><strong>{check.problems > 0 ? 'Readers would hit something' : check.warnings > 0 ? 'Ready, with things worth a look' : 'Ready'}</strong>
          {' '}<span class="quiet small">{check.summary} · {check.stats.words} words · {check.stats.reading_minutes} min{check.links.outside > 0 ? ` · ${check.links.checked}/${check.links.outside} links checked` : ''}</span></span>
        <span class="quiet small">{open ? 'Hide' : 'Details'}</span>
      </button>
      {open && (
        <ul class="findings">
          {shown.length === 0 && <li class="quiet small">Nothing to fix.</li>}
          {shown.map((f) => (
            <li class={f.level}>
              <span class="level">{levelWord[f.level]}</span> {f.message}
              {f.where && <code class="where">{f.where}</code>}
            </li>
          ))}
          {check.similar_posts.map((s) => (
            <li class="note"><span class="level">Similar</span> <a href={`#/posts/${s.id}`}>“{s.title}”</a> <span class="quiet small">{Math.round(s.similarity * 100)}% alike</span></li>
          ))}
        </ul>
      )}
    </div>
  );
}
