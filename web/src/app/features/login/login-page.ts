import { NgOptimizedImage } from '@angular/common';
import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { LOGO_SRC } from '../../shared/branding/logo';
import { AccountMenu } from '../../shared/ui/account-menu';
import { Button } from '../../shared/ui/button';

/**
 * Exactly what `TwitchOAuthDefaults.RequestedScopes` sends to id.twitch.tv/oauth2/authorize
 * (`src/EmotePurge.Core/Twitch/TwitchModels.cs`). Listed here rather than summarised, because the
 * visitor is one click away from Twitch's own consent screen and will read the same two lines
 * there — a page that says "we only need a little" and is then contradicted by the real dialog has
 * spent its credibility at the worst possible moment.
 *
 * Both are read scopes. If a write scope is ever added, this list has to grow with it; the
 * identifiers stay out of the translation files because they are literals, not language.
 */
const SCOPES = [
  { id: 'user:read:moderated_channels', key: 'moderatedChannels' },
  { id: 'user:read:subscriptions', key: 'subscriptions' },
];

@Component({
  selector: 'app-login-page',
  template: `
    <div class="flex min-h-screen flex-col bg-page text-fg">
      <!-- The login page renders outside the shell, so it brings both display-preference
           controls along itself rather than leaving the visitor without a theme switch. -->
      <header class="flex items-center justify-between px-4 py-3">
        <a routerLink="/welcome" class="flex items-center gap-2 text-lg font-semibold">
          <img
            [ngSrc]="logoSrc"
            width="24"
            height="24"
            disableOptimizedSrcset
            alt=""
            class="h-6 w-6"
          />
          Emote Purge
        </a>
        <app-account-menu />
      </header>

      <!-- No card. A card is a boundary against its neighbours, and on this page there are none —
           the old one was a box drawn around the only thing present, floating in a void. What the
           page owes instead is an answer to the question actually being asked at this moment:
           what is Twitch about to be asked for. -->
      <main class="flex flex-1 items-center justify-center px-4 py-12">
        <div class="flex w-full max-w-md flex-col gap-6">
          <div class="flex flex-col gap-2">
            <h1 class="text-3xl font-bold tracking-tight">{{ 'login.title' | transloco }}</h1>
            <p class="text-fg-muted">{{ 'login.subtitle' | transloco }}</p>
          </div>

          <button
            type="button"
            appButton="primary"
            buttonSize="lg"
            class="self-start"
            (click)="login()"
          >
            {{ 'login.loginButton' | transloco }}
          </button>

          <div class="flex flex-col gap-3 border-t border-border pt-6">
            <h2
              class="font-mono text-[11px] font-medium tracking-[0.13em] text-fg-secondary uppercase"
            >
              {{ 'login.scopes.title' | transloco }}
            </h2>
            <dl class="flex flex-col gap-2">
              @for (scope of scopes; track scope.id) {
                <div class="flex flex-col gap-0.5">
                  <dt class="font-mono text-xs text-fg-secondary">{{ scope.id }}</dt>
                  <dd class="text-sm text-fg-muted">
                    {{ 'login.scopes.' + scope.key | transloco }}
                  </dd>
                </div>
              }
            </dl>
            <p class="text-sm text-fg-muted">{{ 'login.scopes.note' | transloco }}</p>
          </div>
        </div>
      </main>
    </div>
  `,
  imports: [AccountMenu, Button, NgOptimizedImage, RouterLink, TranslocoPipe],
})
export class LoginPage {
  private readonly authService = inject(AuthService);

  protected readonly scopes = SCOPES;
  protected readonly logoSrc = LOGO_SRC;

  protected login(): void {
    this.authService.login();
  }
}
