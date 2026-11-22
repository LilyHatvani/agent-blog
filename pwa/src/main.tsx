import { render } from 'preact';
import { App } from './app';
import './styles.css';
import { applyDisplay, readDisplay } from './display';

// Before the first paint, so a chosen theme never flashes the other one.
applyDisplay(readDisplay());

render(<App />, document.getElementById('app')!);

// The worker caches the shell for a quick start; it never caches the API.
if ('serviceWorker' in navigator && import.meta.env.PROD) {
  navigator.serviceWorker.register('/admin/sw.js', { scope: '/admin/' }).catch(() => undefined);
}
