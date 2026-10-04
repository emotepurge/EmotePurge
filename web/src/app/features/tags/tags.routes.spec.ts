import { Route, Routes } from '@angular/router';
import { describe, expect, it } from 'vitest';

import { routes } from '../../app.routes';
import { usageStatsAccessGuard } from '../../core/channels/usage-stats-access.guard';
import { TAGS_ROUTES } from './tags.routes';

/**
 * The access check of the tags page lives on its parent entry in app.routes.ts, not in this module
 * (same split as usage-stats, #264) — so the one thing worth pinning is that the entry still carries
 * the guard and still delegates to exactly this module. Router behaviour of the lazy split itself is
 * covered by usage-stats.routes.spec.ts; T-C adds the run guard here and its tests with it.
 */
function child(parent: Route | undefined, path: string): Route | undefined {
  return parent?.children?.find((route) => route.path === path);
}

describe('app.routes.ts wiring for tags', () => {
  it('guards the tags route with usage-stats access and loads TAGS_ROUTES lazily', async () => {
    const shell = routes.find((route) => route.path === '');
    const tagsNode = child(child(shell, 'channels/:channelName'), 'tags');

    expect(tagsNode?.canActivate).toEqual([usageStatsAccessGuard]);
    expect(tagsNode?.loadComponent).toBeUndefined();
    expect(await (tagsNode!.loadChildren!() as Promise<Routes>)).toBe(TAGS_ROUTES);
  });
});
