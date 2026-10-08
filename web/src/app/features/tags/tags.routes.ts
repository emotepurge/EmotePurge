import { Routes } from '@angular/router';

import { sevenTvRunLeaveGuard } from '../../shared/seven-tv/seven-tv-run-leave.guard';
import { TagsPage } from './tags-page';

/**
 * Lazy like `usage-stats.routes.ts` (spec E25, #264): the page pulls in the sprite grid, the CDK
 * scroller, the 7TV set service and — since T-C — the run dock with the import engine, none of
 * which the initial bundle needs. `canActivate` sits on the parent entry in app.routes.ts. The
 * page-neutral run guard (spec 9.5) asks before leaving while an import (a tag play-in) or an undo
 * is running, exactly as on the usage page.
 */
export const TAGS_ROUTES: Routes = [
  { path: '', component: TagsPage, canDeactivate: [sevenTvRunLeaveGuard] },
];
