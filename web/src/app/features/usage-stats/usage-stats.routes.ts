import { Routes } from '@angular/router';

import { usageStatsLeaveGuard } from './usage-stats-leave.guard';
import { UsageStatsPage } from './usage-stats-page';

/**
 * Split out of app.routes.ts (issue #264): the leave-confirmation guard here reaches into
 * SevenTvImportService, dragging the whole 7TV import engine plus CDK Dialog/Overlay/Scrolling
 * along with it — dependencies no route needs before this page is actually opened. Loading this
 * file via the parent's `loadChildren` gives that dependency graph its own lazy chunk. `component`
 * is a plain, static reference rather than a nested `loadComponent`: that would split
 * UsageStatsPage into a second chunk for no size benefit. `canActivate` stays on the parent route;
 * only `canDeactivate` needed to move.
 *
 * `usageStatsLeaveGuard`'s `leadsToSameRoute` depends on this route's `routeConfig` keeping the
 * same object identity across navigations — verified by usage-stats.routes.spec.ts. Measurements
 * and the rejected alternative are in docs/DECISIONS.md, 2026-09-25.
 */
export const USAGE_STATS_ROUTES: Routes = [
  {
    path: '',
    component: UsageStatsPage,
    canDeactivate: [usageStatsLeaveGuard],
  },
];
