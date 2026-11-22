// Display settings for this device (Account → Display): the theme and the text size. A per-device convenience, so it
// lives in this browser's storage, never on the server; without storage (a private window) the defaults apply.

export type ThemeChoice = 'system' | 'light' | 'dark';
export type TextChoice = 'standard' | 'large';
export interface Display { theme: ThemeChoice; text: TextChoice }

const KEY = 'agent-blog.display';
const defaults: Display = { theme: 'system', text: 'standard' };

export function readDisplay(): Display {
  try {
    const v = JSON.parse(localStorage.getItem(KEY) ?? '{}');
    return {
      theme: ['system', 'light', 'dark'].includes(v.theme) ? v.theme : defaults.theme,
      text: ['standard', 'large'].includes(v.text) ? v.text : defaults.text,
    };
  } catch {
    return defaults;
  }
}

export function applyDisplay(d: Display) {
  const root = document.documentElement;
  if (d.theme === 'system') root.removeAttribute('data-theme'); else root.setAttribute('data-theme', d.theme);
  if (d.text === 'standard') root.removeAttribute('data-text'); else root.setAttribute('data-text', d.text);
}

export function saveDisplay(d: Display) {
  applyDisplay(d);
  try { localStorage.setItem(KEY, JSON.stringify(d)); } catch { /* private mode: it lasts this visit */ }
}
