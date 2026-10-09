import { Component, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

const BASE = 'flex shrink-0 items-center whitespace-nowrap border-b-2 px-3 text-sm transition';
const ACTIVE = `${BASE} border-accent text-fg`;
const INACTIVE = `${BASE} border-transparent text-fg-muted hover:text-fg-body`;

/**
 * One tab in an in-page tab bar.
 *
 * The pattern was correct and copied by hand seven times — three tabs in the channel workspace,
 * four in the admin area — each carrying the same two-line class ternary inline. Nothing was wrong
 * with any single copy; the problem is that the design language calls the tab bar a cross-page
 * contract (§8.1, §8.5) and a contract that lives in seven pasted string literals drifts the first
 * time one of them is edited.
 *
 * `display: contents` on the host: the bar is a flex row with a fixed height, and the anchor has to
 * be the flex child itself for `items-center` to carry that height exactly. A wrapper element would
 * become the flex child and the anchor would centre inside a box of its own.
 */
@Component({
  selector: 'app-tab-link',
  imports: [RouterLink, RouterLinkActive],
  host: { class: 'contents' },
  template: `
    <a
      [routerLink]="link()"
      routerLinkActive
      ariaCurrentWhenActive="page"
      #state="routerLinkActive"
      [class]="state.isActive ? active : inactive"
    >
      {{ label() }}
    </a>
  `,
})
export class TabLink {
  /** Relative to the parent route, like the inline `routerLink` values it replaces. */
  readonly link = input.required<string>();
  /** Already translated — the label is page copy, so the host owns its key. */
  readonly label = input.required<string>();

  protected readonly active = ACTIVE;
  protected readonly inactive = INACTIVE;
}
