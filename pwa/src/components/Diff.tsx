// What an edit changes, line by line: removed lines struck through, added lines marked. A plain longest-common-
// subsequence over the Markdown's lines; a post is small enough that the quadratic table never matters (and past
// MAX lines it says so instead of trying).

type Op = { kind: 'same' | 'add' | 'del'; text: string };

const MAX = 1500;

export function diffLines(before: string, after: string): Op[] | null {
  const a = before.split('\n');
  const b = after.split('\n');
  if (a.length > MAX || b.length > MAX) return null;
  const n = a.length;
  const m = b.length;
  const lcs: Uint16Array[] = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--)
      lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
  const ops: Op[] = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) { ops.push({ kind: 'same', text: a[i] }); i++; j++; }
    else if (lcs[i + 1][j] >= lcs[i][j + 1]) ops.push({ kind: 'del', text: a[i++] });
    else ops.push({ kind: 'add', text: b[j++] });
  }
  while (i < n) ops.push({ kind: 'del', text: a[i++] });
  while (j < m) ops.push({ kind: 'add', text: b[j++] });
  return ops;
}

/** The changed lines with a line of context around each change; long unchanged stretches fold away. */
export function Diff({ before, after }: { before: string; after: string }) {
  const ops = diffLines(before, after);
  if (!ops) return <p class="quiet">This change is too long to compare line by line. The full new version is below.</p>;
  if (ops.every((o) => o.kind === 'same')) return <p class="quiet">The text is unchanged.</p>;
  const near = (k: number) => ops.slice(Math.max(0, k - 1), k + 2).some((o) => o.kind !== 'same');
  const rows: preact.JSX.Element[] = [];
  let folded = false;
  ops.forEach((o, k) => {
    if (o.kind === 'same' && !near(k)) {
      if (!folded) rows.push(<div class="diff-fold" key={`f${k}`}>⋯</div>);
      folded = true;
      return;
    }
    folded = false;
    rows.push(
      <div class={`diff-line ${o.kind}`} key={k}>
        <span class="diff-mark" aria-hidden="true">{o.kind === 'add' ? '+' : o.kind === 'del' ? '−' : ' '}</span>
        {o.kind === 'add' ? <ins>{o.text || ' '}</ins> : o.kind === 'del' ? <del>{o.text || ' '}</del> : <span>{o.text || ' '}</span>}
      </div>,
    );
  });
  return <div class="diff" role="group" aria-label="What changes">{rows}</div>;
}
