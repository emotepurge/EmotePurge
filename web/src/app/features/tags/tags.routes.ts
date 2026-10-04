import { Routes } from '@angular/router';

import { TagsPage } from './tags-page';

/**
 * Lazy like `usage-stats.routes.ts` (spec E25, #264): the page pulls in the sprite grid, the CDK
 * scroller and the 7TV set service, none of which the initial bundle needs. `canActivate` sits on
 * the parent entry in app.routes.ts. No leave guard yet — the tags page starts no 7TV run in T-B;
 * T-C adds the page-neutral run guard here.
 */
export const TAGS_ROUTES: Routes = [{ path: '', component: TagsPage }];
