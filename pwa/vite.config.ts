import { defineConfig, type Plugin } from 'vite';
import preact from '@preact/preset-vite';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';

/**
 * The service worker (Relay's pattern, no Workbox): `src/sw.js` gets the built app shell and a hash of it, so every
 * build installs a new worker and caches exactly what was built.
 */
function serviceWorker(): Plugin {
  return {
    name: 'agent-blog-service-worker',
    apply: 'build',
    enforce: 'post',
    generateBundle(_options, bundle) {
      const shell = Object.keys(bundle).filter((f) => /\.(js|css)$/.test(f)).map((f) => `/admin/${f}`);
      const hash = createHash('sha256');
      for (const name of Object.keys(bundle).sort()) {
        const out = bundle[name];
        hash.update(name).update(out.type === 'chunk' ? out.code : out.source);
      }
      const source = readFileSync(new URL('./src/sw.js', import.meta.url), 'utf8')
        .replace('__PRECACHE__', JSON.stringify(['/admin/manifest.webmanifest', '/admin/icon.svg', ...shell].sort()))
        .replace('__VERSION__', JSON.stringify(hash.digest('hex').slice(0, 12)));
      this.emitFile({ type: 'asset', fileName: 'sw.js', source });
    },
  };
}

export default defineConfig({
  // The server serves the app at /admin and every app path under it.
  base: '/admin/',
  plugins: [preact(), serviceWorker()],
  build: { target: 'es2022', outDir: 'dist', modulePreload: { polyfill: false } },
  server: { port: 5181, proxy: { '/api': process.env.BLOG_API_ORIGIN ?? 'http://localhost:8080' } },
});
