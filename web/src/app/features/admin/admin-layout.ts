import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { BackLink } from '../../shared/ui/back-link';
import { TabLink } from '../../shared/ui/tab-link';

/**
 * Shell of the global-admin area. Visibility-only: the route sits behind adminGuard and every
 * endpoint the child pages will call is additionally gated server-side by
 * GlobalAdminAuthorizationFilter. Tab bar follows ChannelWorkspaceLayout's pattern.
 */
@Component({
  selector: 'app-admin-layout',
  imports: [BackLink, RouterOutlet, TabLink, TranslocoPipe],
  template: `
    <div>
      <!-- The tab bar below only navigates WITHIN /admin — without this the area was a dead end
           (design doc §8.6). Same header row shape as ChannelWorkspaceLayout. -->
      <div class="mb-4 flex flex-wrap items-center gap-x-4 gap-y-2">
        <app-back-link link="/" [label]="'nav.overview' | transloco" />
        <h1 class="text-2xl font-bold tracking-tight">{{ 'admin.title' | transloco }}</h1>
      </div>

      <!-- Sticky under the h-14 shell header; h-10 is a contract — filter toolbars pin at
           top-24 (= 14 + 10). Links are flex/items-center so the fixed height carries exactly. -->
      <nav
        class="app-sticky-bar top-14 mb-6 flex h-10 gap-2 overflow-x-auto border-b border-border"
      >
        <app-tab-link link="monitoring" [label]="'admin.tabs.monitoring' | transloco" />
        <app-tab-link link="channels" [label]="'admin.tabs.channels' | transloco" />
        <app-tab-link link="users" [label]="'admin.tabs.users' | transloco" />
        <app-tab-link link="audit-log" [label]="'admin.tabs.auditLog' | transloco" />
      </nav>

      <router-outlet />
    </div>
  `,
})
export class AdminLayout {}
