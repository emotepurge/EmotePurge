import { Routes } from '@angular/router';

import { adminGuard } from './core/auth/admin.guard';
import { authGuard } from './core/auth/auth.guard';
import { homeGuard } from './core/auth/home.guard';
import { channelManageGuard } from './core/channels/channel-manage.guard';
import { usageStatsAccessGuard } from './core/channels/usage-stats-access.guard';
import { voteSessionAccessGuard } from './core/voting/vote-session-access.guard';
import { LoginPage } from './features/login/login-page';
import { usageStatsLeaveGuard } from './features/usage-stats/usage-stats-leave.guard';

export const routes: Routes = [
  {
    // Public marketing page, no guard — first stop for anonymous visitors, e.g. via a shared link.
    path: 'welcome',
    loadComponent: () => import('./features/landing/landing-page').then((m) => m.LandingPage),
  },
  { path: 'login', component: LoginPage },
  {
    // Operator-supplied imprint/privacy (issue #247): reachable without being logged in, outside
    // every auth guard, and outside the app shell — same reasoning as 'welcome'/'login' above.
    // `data.kind` is read by LegalPage from the route snapshot, not bound as a component input.
    path: 'imprint',
    data: { kind: 'imprint' },
    loadComponent: () => import('./features/legal/legal-page').then((m) => m.LegalPage),
  },
  {
    path: 'privacy',
    data: { kind: 'privacy' },
    loadComponent: () => import('./features/legal/legal-page').then((m) => m.LegalPage),
  },
  {
    // Contact form (docs/DECISIONS.md 2026-09-24, "contact form"): reachable without being logged
    // in, outside every auth guard and outside the app shell — same reasoning as 'imprint'/'privacy'
    // above, and the § 5 DDG requirement is explicit that it must be reachable before login.
    path: 'contact',
    loadComponent: () => import('./features/contact/contact-page').then((m) => m.ContactPage),
  },
  {
    path: '',
    loadComponent: () => import('./features/shell/app-shell').then((m) => m.AppShell),
    children: [
      {
        path: '',
        loadComponent: () =>
          import('./features/overview/overview-page').then((m) => m.OverviewPage),
        canActivate: [homeGuard],
      },
      {
        // Cross-channel — a user's voting history isn't scoped to a single channelName route value,
        // so this is a sibling of the overview route, not nested under channels/:channelName.
        path: 'my-votings',
        loadComponent: () =>
          import('./features/my-votings/my-votings-page').then((m) => m.MyVotingsPage),
        canActivate: [authGuard],
      },
      {
        // Global-admin area. The guard reads the cached /me; every endpoint behind these pages is
        // additionally protected server-side by GlobalAdminAuthorizationFilter.
        path: 'admin',
        loadComponent: () => import('./features/admin/admin-layout').then((m) => m.AdminLayout),
        canActivate: [adminGuard],
        children: [
          { path: '', redirectTo: 'monitoring', pathMatch: 'full' },
          {
            path: 'monitoring',
            loadComponent: () =>
              import('./features/admin/admin-monitoring-page').then((m) => m.AdminMonitoringPage),
          },
          {
            path: 'channels',
            loadComponent: () =>
              import('./features/admin/admin-channels-page').then((m) => m.AdminChannelsPage),
          },
          {
            // Support drilldown for one channel. Sibling of the list rather than nested under the
            // channel workspace: it is an admin view of the worker's state, not a channel page.
            path: 'channels/:channelName',
            loadComponent: () =>
              import('./features/admin/admin-channel-detail-page').then(
                (m) => m.AdminChannelDetailPage,
              ),
          },
          {
            path: 'users',
            loadComponent: () =>
              import('./features/admin/admin-users-page').then((m) => m.AdminUsersPage),
          },
          {
            path: 'audit-log',
            loadComponent: () =>
              import('./features/admin/admin-audit-log-page').then((m) => m.AdminAuditLogPage),
          },
        ],
      },
      {
        path: 'channels/:channelName',
        loadComponent: () =>
          import('./features/channel-workspace/channel-workspace-layout').then(
            (m) => m.ChannelWorkspaceLayout,
          ),
        children: [
          { path: '', redirectTo: 'usage-stats', pathMatch: 'full' },
          {
            // Must actually be allowed to view THIS channel's usage stats (Twitch mod/broadcaster/
            // admin OR 7TV editor — usageStatsAccessGuard), not just be logged in, unlike the
            // vote-session pages below. canDeactivate asks before leaving mid-import (R11, #72) —
            // it only fires while a run is active and never touches the run itself.
            path: 'usage-stats',
            loadComponent: () =>
              import('./features/usage-stats/usage-stats-page').then((m) => m.UsageStatsPage),
            canActivate: [usageStatsAccessGuard],
            canDeactivate: [usageStatsLeaveGuard],
          },
          {
            // Requires login only — the list itself has no per-session role restriction.
            path: 'vote-sessions',
            loadComponent: () =>
              import('./features/voting/vote-session-list-page').then((m) => m.VoteSessionListPage),
            canActivate: [authGuard],
          },
          {
            // The strictest of the three channel guards: canManage, not canViewUsageStats — the
            // activity feed names which moderator did what, and the channel's 7TV editors are
            // frequently outside the mod team.
            path: 'activity',
            loadComponent: () =>
              import('./features/channel-workspace/channel-activity-page').then(
                (m) => m.ChannelActivityPage,
              ),
            canActivate: [channelManageGuard],
          },
          {
            // Requires login AND being part of this specific session's target audience
            // (voteSessionAccessGuard — anonymous share-link viewing was removed, see decision log).
            path: 'vote-sessions/:sessionId',
            loadComponent: () =>
              import('./features/voting/vote-session-detail-page').then(
                (m) => m.VoteSessionDetailPage,
              ),
            canActivate: [voteSessionAccessGuard],
          },
        ],
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
