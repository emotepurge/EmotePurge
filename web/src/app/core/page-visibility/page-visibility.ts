import { DOCUMENT } from '@angular/common';
import { DestroyRef, Signal, inject, signal } from '@angular/core';

/**
 * Whether the page is currently visible, following `visibilitychange`. Pollers use it to stop
 * asking the server while the tab is in the background (spec section 8) — a hidden admin tab has
 * nobody to show the answer to.
 *
 * Must be called from an injection context; the listener is removed with the injecting scope.
 */
export function pageVisible(): Signal<boolean> {
  const document = inject(DOCUMENT);
  const destroyRef = inject(DestroyRef);

  const visible = signal(document.visibilityState !== 'hidden');
  const onChange = () => visible.set(document.visibilityState !== 'hidden');

  document.addEventListener('visibilitychange', onChange);
  destroyRef.onDestroy(() => document.removeEventListener('visibilitychange', onChange));

  return visible.asReadonly();
}
