// Line icons, 24 px grid, drawn with currentColor so they follow the theme.
export type IconName = 'inbox' | 'posts' | 'agents' | 'history' | 'account' | 'external' | 'check' | 'x' | 'back' | 'copy';

const paths: Record<IconName, string> = {
  inbox: 'M4 13h4l2 3h4l2-3h4M4 13l2-8h12l2 8v6H4z',
  posts: 'M7 4h10v16H7zM10 8h4M10 12h4M10 16h2',
  agents: 'M12 3l2.2 4.6 5 .7-3.6 3.5.9 5-4.5-2.4-4.5 2.4.9-5L4.8 8.3l5-.7z',
  history: 'M4 12a8 8 0 1 0 2.4-5.7M4 4v4h4M12 8v4l3 2',
  account: 'M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM4 20c1.5-4 4.5-6 8-6s6.5 2 8 6',
  external: 'M14 4h6v6M20 4l-9 9M18 14v6H4V6h6',
  check: 'M5 12l5 5 9-10',
  x: 'M6 6l12 12M18 6L6 18',
  back: 'M15 5l-7 7 7 7',
  copy: 'M8 8h11v12H8zM5 16V4h11',
};

export function Icon({ name }: { name: IconName }) {
  return (
    <svg class="icon" viewBox="0 0 24 24" width="20" height="20" aria-hidden="true" fill="none" stroke="currentColor"
      stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
      <path d={paths[name]} />
    </svg>
  );
}
