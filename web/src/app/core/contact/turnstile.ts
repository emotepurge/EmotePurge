import { InjectionToken } from '@angular/core';

/** The subset of Cloudflare Turnstile's `window.turnstile` global this app actually calls. */
export interface TurnstileRenderOptions {
  sitekey: string;
  theme: 'light' | 'dark';
  /** ISO 639-1 code or 'auto' — Cloudflare's own vocabulary. */
  language: string;
  callback: (token: string) => void;
  'error-callback'?: () => void;
  'expired-callback'?: () => void;
}

export interface TurnstileApi {
  render(container: HTMLElement, options: TurnstileRenderOptions): string;
  remove(widgetId: string): void;
  /** Clears a widget's completed/expired state so a rejected token cannot be resubmitted verbatim. */
  reset(widgetId: string): void;
}

declare global {
  interface Window {
    turnstile?: TurnstileApi;
  }
}

// render=explicit: this app calls turnstile.render() itself once the form is ready, rather than
// letting the script auto-render every data-sitekey div on the page the moment it loads.
const SCRIPT_SRC = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
const SCRIPT_ID = 'app-turnstile-script';

let loadPromise: Promise<TurnstileApi> | null = null;

/**
 * Injects the Turnstile script at most once per page load and resolves with the global API once it
 * is ready. Module-level `loadPromise` rather than a class field: the script tag itself is a
 * singleton DOM resource regardless of how many components ask for it, and a second `ContactPage`
 * instance (a route re-entry) must reuse the same in-flight or already-resolved load rather than
 * injecting a second `<script>` tag.
 *
 * Called only from `ContactPage`, and only once that page's form is actually about to render (spec
 * requirement) — never eagerly from `app.config.ts` or `index.html`, so a visitor who never opens
 * `/contact` never loads Cloudflare's script at all.
 */
function loadTurnstileScript(): Promise<TurnstileApi> {
  if (window.turnstile) {
    return Promise.resolve(window.turnstile);
  }
  if (loadPromise) {
    return loadPromise;
  }

  loadPromise = new Promise<TurnstileApi>((resolve, reject) => {
    const onReady = () => {
      if (window.turnstile) {
        resolve(window.turnstile);
      } else {
        reject(new Error('Turnstile script loaded without exposing window.turnstile.'));
      }
    };

    // On failure, both the cached promise and the failed <script> tag are cleared before
    // rejecting. Neither used to be: the cached `loadPromise` would keep answering every future
    // caller with the same already-rejected promise, and the dead tag would keep this function
    // taking the "existing" branch below forever — so a visitor who returned to /contact (a fresh
    // `ContactPage` instance, e.g. after a transient network blip or reconnecting Wi-Fi) could
    // never get a genuine second attempt at loading the widget.
    const onError = () => {
      loadPromise = null;
      document.getElementById(SCRIPT_ID)?.remove();
      reject(new Error('Failed to load the Turnstile script.'));
    };

    const existing = document.getElementById(SCRIPT_ID);
    if (existing) {
      existing.addEventListener('load', onReady, { once: true });
      existing.addEventListener('error', onError, { once: true });
      return;
    }

    const script = document.createElement('script');
    script.id = SCRIPT_ID;
    script.src = SCRIPT_SRC;
    script.async = true;
    script.defer = true;
    script.addEventListener('load', onReady, { once: true });
    script.addEventListener('error', onError, { once: true });
    document.head.appendChild(script);
  });

  return loadPromise;
}

/**
 * Injection seam for `loadTurnstileScript`, the same shape as `EVENT_SOURCE_FACTORY`
 * (`core/live/event-source.factory.ts`): real network/DOM work has no place in a Vitest jsdom run
 * (an external `<script src>` never fires `load`/`error` there, so the real implementation would
 * hang forever), so tests override this token with a stub instead of reaching into `window.turnstile`.
 */
export const TURNSTILE_LOADER = new InjectionToken<() => Promise<TurnstileApi>>(
  'TURNSTILE_LOADER',
  {
    providedIn: 'root',
    factory: () => loadTurnstileScript,
  },
);
