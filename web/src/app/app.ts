import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { NavigationHistoryService } from './core/routing/navigation-history.service';
import { ThemeService } from './core/theme/theme.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: `<router-outlet />`,
})
export class App {
  // Injected here and nowhere else on purpose: this is the only component rendered on *every*
  // route. The theme menu itself lives in the app shell, but the landing page and the login page
  // render outside it — and a root-provided service is not constructed until somebody asks for it.
  // Without this line a visitor on either of those pages keeps whatever public/theme-init.js
  // stamped at load and does not follow a live OS theme change, because the service that listens
  // for it was never created.
  private readonly themeService = inject(ThemeService);

  // Same reasoning as above, for a different symptom: NavigationHistoryService has to be listening
  // before the FIRST NavigationEnd of the session, not just before the first page that happens to
  // read it (see the service's own doc comment) — LegalPage is one such reader, and it can be that
  // very first page (a deep link straight to /imprint).
  private readonly navigationHistoryService = inject(NavigationHistoryService);
}
