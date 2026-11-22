import { useEffect, useState } from 'preact/hooks';
import { api } from '../api';
import { Notice, Preview } from './ui';

export interface Draft { title: string; excerpt: string; body_md: string }

/** The fields that differ from where the editor started: only those are sent (a field left out stays as it is). */
export function changed(from: Draft, to: Draft): Partial<Draft> {
  const out: Partial<Draft> = {};
  if (to.title !== from.title) out.title = to.title;
  if (to.excerpt !== from.excerpt) out.excerpt = to.excerpt;
  if (to.body_md !== from.body_md) out.body_md = to.body_md;
  return out;
}

/**
 * A plain Markdown editor for the phone: title, summary, body, and a Preview tab that shows exactly what readers will
 * see (the server's own sanitizer). Cancel and the save button are always there; save is off until something changed.
 */
export function Editor({ start, saveLabel, busy, error, onSave, onCancel, children }: {
  start: Draft;
  saveLabel: string;
  busy: boolean;
  error?: string | null;
  onSave: (edits: Partial<Draft>, all: Draft) => void;
  onCancel: () => void;
  children?: preact.ComponentChildren;
}) {
  const [d, setD] = useState<Draft>(start);
  const [tab, setTab] = useState<'write' | 'preview'>('write');
  const [html, setHtml] = useState<string | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const edits = changed(start, d);
  const dirty = Object.keys(edits).length > 0;

  useEffect(() => {
    if (tab !== 'preview') return;
    setHtml(null);
    api.preview(d.body_md).then((r) => setHtml(r.html)).catch((e) => setPreviewError(e.message));
  }, [tab]);

  return (
    <form class="card form editor" onSubmit={(e) => { e.preventDefault(); if (dirty && d.title.trim()) onSave(edits, d); }}>
      {children}
      <label class="field">
        <span>Title</span>
        <input value={d.title} maxLength={300} required onInput={(e) => setD({ ...d, title: (e.target as HTMLInputElement).value })} />
      </label>
      <label class="field">
        <span>Summary <span class="quiet small">(for lists and link previews)</span></span>
        <textarea rows={2} value={d.excerpt} maxLength={1000} onInput={(e) => setD({ ...d, excerpt: (e.target as HTMLTextAreaElement).value })} />
      </label>
      <div class="chips" role="tablist" aria-label="Body">
        <button type="button" role="tab" aria-selected={tab === 'write'} class={tab === 'write' ? 'chip active' : 'chip'} onClick={() => setTab('write')}>Write</button>
        <button type="button" role="tab" aria-selected={tab === 'preview'} class={tab === 'preview' ? 'chip active' : 'chip'} onClick={() => setTab('preview')}>Preview</button>
      </div>
      {tab === 'write' ? (
        <textarea class="body-input" aria-label="Body (Markdown)" value={d.body_md} rows={14} spellcheck
          onInput={(e) => setD({ ...d, body_md: (e.target as HTMLTextAreaElement).value })} />
      ) : previewError ? <Notice kind="error">{previewError}</Notice>
        : html === null ? <p class="quiet">Rendering…</p>
        : <div class="sheet"><Preview html={html} /></div>}
      {error && <Notice kind="error">{error}</Notice>}
      <div class="row end">
        <button type="button" class="ghost" disabled={busy} onClick={onCancel}>Cancel</button>
        <button type="submit" class="primary" disabled={busy || !dirty || !d.title.trim()}>{saveLabel}</button>
      </div>
    </form>
  );
}
