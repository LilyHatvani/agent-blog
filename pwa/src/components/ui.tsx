import type { ComponentChildren } from 'preact';
import { useEffect, useRef, useState } from 'preact/hooks';
import type { Status } from '../api';
import { statusName } from '../api';

export function Notice({ kind, children }: { kind: 'info' | 'ok' | 'error'; children: ComponentChildren }) {
  return <p class={`notice ${kind}`} role={kind === 'error' ? 'alert' : 'status'}>{children}</p>;
}

export function StatusPill({ status }: { status: Status }) {
  return <span class={`pill ${status}`}>{statusName[status]}</span>;
}

/** A link to the owner's guide for a screen (docs/guides), opened beside the app. */
export function GuideLink({ guide, children }: { guide: string; children?: ComponentChildren }) {
  return <a class="guide-link small" href={`https://github.com/LilyHatvani/agent-blog/blob/develop/docs/guides/${guide}.md`} target="_blank" rel="noopener">{children ?? 'How it works'}</a>;
}

export function Empty({ children }: { children: ComponentChildren }) {
  return <p class="empty">{children}</p>;
}

/** Sanitized HTML from the server (the same the readers get: raw HTML escaped, unsafe links removed). */
export function Preview({ html }: { html: string }) {
  return <div class="preview" dangerouslySetInnerHTML={{ __html: html }} />;
}

/**
 * A confirmation that says what will happen in plain words. Escape and the backdrop cancel; focus starts on Cancel,
 * so a stray Enter never confirms.
 */
export function Confirm(props: {
  title: string;
  children: ComponentChildren;
  confirm: string;
  danger?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const cancel = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    cancel.current?.focus();
    const key = (e: KeyboardEvent) => e.key === 'Escape' && props.onCancel();
    addEventListener('keydown', key);
    return () => removeEventListener('keydown', key);
  }, []);
  return (
    <div class="backdrop" onClick={(e) => e.target === e.currentTarget && props.onCancel()}>
      <div class="dialog" role="alertdialog" aria-modal="true" aria-labelledby="dialog-title">
        <h2 id="dialog-title">{props.title}</h2>
        <div class="dialog-body">{props.children}</div>
        <div class="row end">
          <button ref={cancel} class="ghost" onClick={props.onCancel}>Cancel</button>
          <button class={props.danger ? 'danger' : 'primary'} onClick={props.onConfirm}>{props.confirm}</button>
        </div>
      </div>
    </div>
  );
}

/** Runs one action at a time, showing its error in words. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const run = async (work: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await work();
      return true;
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Something went wrong.');
      return false;
    } finally {
      setBusy(false);
    }
  };
  return { busy, error, run, setError };
}
