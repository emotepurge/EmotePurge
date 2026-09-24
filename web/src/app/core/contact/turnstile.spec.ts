import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { TurnstileApi } from './turnstile';

const SCRIPT_ID = 'app-turnstile-script';

/**
 * `TURNSTILE_LOADER`'s real, un-overridden factory (`loadTurnstileScript`) in isolation. jsdom never
 * actually fetches an external `<script src>`, so every case here drives the module by hand:
 * injecting a fake `window.turnstile` and dispatching a synthetic `load`/`error` `Event` on the
 * script element the module itself appended, the same way a real browser would once the real script
 * finished — `contact-page.spec.ts` covers `ContactPage`'s own behaviour against a stubbed loader,
 * this file covers the loader itself, and `contact.e2e.spec.ts`'s `mockTurnstile` covers the real
 * script tag actually loading in a real browser.
 *
 * `vi.resetModules()` + a fresh dynamic `import()` per test is load-bearing, not decoration: the
 * module's own `loadPromise`/script-tag-reuse logic is deliberately process-wide singleton state
 * (see its doc comment — "the script tag itself is a singleton DOM resource"), which would
 * otherwise leak between test cases in this same file. `TestBed.inject` (rather than reaching into
 * the token's internals) is what resolves the freshly re-imported module's own factory correctly.
 */
describe('loadTurnstileScript', () => {
  beforeEach(() => {
    vi.resetModules();
    delete (window as { turnstile?: TurnstileApi }).turnstile;
    document.getElementById(SCRIPT_ID)?.remove();
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    delete (window as { turnstile?: TurnstileApi }).turnstile;
    document.getElementById(SCRIPT_ID)?.remove();
  });

  async function freshLoader(): Promise<() => Promise<TurnstileApi>> {
    const { TURNSTILE_LOADER } = await import('./turnstile');
    return TestBed.inject(TURNSTILE_LOADER);
  }

  it('appends the script tag with the explicit-render URL and resolves once it fires load', async () => {
    const loader = await freshLoader();

    const promise = loader();
    const script = document.getElementById(SCRIPT_ID) as HTMLScriptElement | null;
    expect(script).not.toBeNull();
    expect(script!.src).toContain('https://challenges.cloudflare.com/turnstile/v0/api.js');
    expect(script!.src).toContain('render=explicit');

    const fakeApi: TurnstileApi = { render: () => 'id', remove: () => {}, reset: () => {} };
    window.turnstile = fakeApi;
    script!.dispatchEvent(new Event('load'));

    await expect(promise).resolves.toBe(fakeApi);
  });

  it('rejects when the script fires an error event', async () => {
    const loader = await freshLoader();

    const promise = loader();
    document.getElementById(SCRIPT_ID)!.dispatchEvent(new Event('error'));

    await expect(promise).rejects.toThrow('Failed to load the Turnstile script.');
  });

  it('rejects when load fires without window.turnstile ever being set', async () => {
    const loader = await freshLoader();

    const promise = loader();
    document.getElementById(SCRIPT_ID)!.dispatchEvent(new Event('load'));

    await expect(promise).rejects.toThrow('window.turnstile');
  });

  it('resolves immediately, without appending a script tag, when window.turnstile already exists', async () => {
    const loader = await freshLoader();
    const fakeApi: TurnstileApi = { render: () => 'id', remove: () => {}, reset: () => {} };
    window.turnstile = fakeApi;

    const result = await loader();

    expect(result).toBe(fakeApi);
    expect(document.getElementById(SCRIPT_ID)).toBeNull();
  });

  it('reuses the same in-flight load rather than appending a second script tag', async () => {
    const loader = await freshLoader();

    const first = loader();
    const second = loader();
    expect(document.querySelectorAll(`#${SCRIPT_ID}`)).toHaveLength(1);

    const fakeApi: TurnstileApi = { render: () => 'id', remove: () => {}, reset: () => {} };
    window.turnstile = fakeApi;
    document.getElementById(SCRIPT_ID)!.dispatchEvent(new Event('load'));

    const [firstResult, secondResult] = await Promise.all([first, second]);
    expect(firstResult).toBe(fakeApi);
    expect(secondResult).toBe(fakeApi);
  });

  /**
   * Codex P2 (docs/DECISIONS.md 2026-09-24 revision): before this fix, a failed load left the
   * rejected `loadPromise` and the dead `<script>` tag both in place. A visitor returning to
   * `/contact` (a fresh `ContactPage`, e.g. after a network blip) then re-injected nothing new —
   * `loader()` short-circuited to the same already-rejected promise (`loadPromise` still set) and
   * even a fresh call would have taken the "existing tag" branch on a tag that could never fire
   * `load` again. Retrying is only possible once both are cleared.
   */
  it('clears the cached promise and the failed script tag on error, so a second call retries with a fresh one', async () => {
    const loader = await freshLoader();

    const promise = loader();
    const failedScript = document.getElementById(SCRIPT_ID) as HTMLScriptElement | null;
    expect(failedScript).not.toBeNull();
    failedScript!.dispatchEvent(new Event('error'));

    await expect(promise).rejects.toThrow('Failed to load the Turnstile script.');
    expect(document.getElementById(SCRIPT_ID)).toBeNull();

    const fakeApi: TurnstileApi = { render: () => 'id', remove: () => {}, reset: () => {} };
    const retry = loader();
    const retryScript = document.getElementById(SCRIPT_ID) as HTMLScriptElement | null;
    expect(retryScript).not.toBeNull();
    expect(retryScript).not.toBe(failedScript);
    window.turnstile = fakeApi;
    retryScript!.dispatchEvent(new Event('load'));

    await expect(retry).resolves.toBe(fakeApi);
  });

  /**
   * Codex P2 (docs/DECISIONS.md 2026-09-24 revision): the `load`-without-`window.turnstile` branch
   * used to reject without the cleanup the `error` branch above already had, leaving the same two
   * traps behind — the cached, already-rejected `loadPromise` answering every future caller, and the
   * inert `<script>` tag (which will never fire `load` or `error` again) keeping this function on the
   * "existing tag" branch forever. Mirrors the error-path retry test above for the other failure shape.
   */
  it('clears the cached promise and the script tag when load fires without window.turnstile, so a second call retries with a fresh one', async () => {
    const loader = await freshLoader();

    const promise = loader();
    const failedScript = document.getElementById(SCRIPT_ID) as HTMLScriptElement | null;
    expect(failedScript).not.toBeNull();
    failedScript!.dispatchEvent(new Event('load'));

    await expect(promise).rejects.toThrow('window.turnstile');
    expect(document.getElementById(SCRIPT_ID)).toBeNull();

    const fakeApi: TurnstileApi = { render: () => 'id', remove: () => {}, reset: () => {} };
    const retry = loader();
    const retryScript = document.getElementById(SCRIPT_ID) as HTMLScriptElement | null;
    expect(retryScript).not.toBeNull();
    expect(retryScript).not.toBe(failedScript);
    window.turnstile = fakeApi;
    retryScript!.dispatchEvent(new Event('load'));

    await expect(retry).resolves.toBe(fakeApi);
  });

  it('reuses an already-present script tag (a second component instance) instead of appending another', async () => {
    const loader = await freshLoader();

    // Simulate a script tag left behind by an earlier ContactPage instance in the same session.
    const existing = document.createElement('script');
    existing.id = SCRIPT_ID;
    document.head.appendChild(existing);

    const promise = loader();
    expect(document.querySelectorAll(`#${SCRIPT_ID}`)).toHaveLength(1);

    const fakeApi: TurnstileApi = { render: () => 'id', remove: () => {}, reset: () => {} };
    window.turnstile = fakeApi;
    existing.dispatchEvent(new Event('load'));

    await expect(promise).resolves.toBe(fakeApi);
  });
});
