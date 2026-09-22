import { Component, computed, inject, input } from '@angular/core';

import { PointerModeService } from '../../core/pointer/pointer-mode.service';
import { DIALOG_TITLE_ID } from './dialog';
import { SheetDrag } from './sheet-drag';

/**
 * The inside of every CDK dialog: surface, padding, heading, body, action row.
 *
 * The hull (`rounded-lg bg-surface p-6 shadow-overlay`) stood hand-written in nine components, and
 * around it the details had drifted the way copies do — five heading variants, three heading
 * margins, two of the nine with no heading and therefore no accessible name at all.
 *
 * Spacing is the shell's job, not the caller's: the body is a flex column, so a dialog composes its
 * content out of siblings instead of stacking `mb-*` on each one. Content that belongs together
 * more tightly than the default rhythm wraps itself in its own `flex flex-col gap-1`.
 *
 * The width comes from `.cdk-overlay-pane.app-dialog-panel` (styles.css) — the pane owns it, not the
 * content. Three dialogs used to also set their own `w-[26rem]`/`w-[28rem]`, which either matched
 * the pane cap or silently fought it.
 *
 * On a coarse pointer the same dialog is a bottom sheet: the pane's geometry comes from a media
 * query in styles.css, and what is added here is the chrome that only a sheet has — a grab handle
 * and the drag that dismisses it. Sticky, because the pane is the scroll container, and a handle
 * that scrolls out of reach is not a handle. (`-top-6` is what makes that pin land flush: it
 * cancels the `-mt-6` that bleeds the handle over the hull's own top padding.)
 *
 * The handle carries the hull's own `rounded-t-2xl`, because its negative margins bleed it into the
 * corner areas the hull's radius leaves unpainted — without it the sheet reads square-topped and the
 * hull's radius is invisible. `overflow-hidden` on the hull would fix it too and must not be used:
 * it would make the hull a scroll container of its own and break the handle's sticky pin.
 *
 * The handle is sized as a touch target, not around its 4-px bar: `min-h-11` is the 44-px comfort
 * target from the design language (§10). SheetDrag also starts a drag anywhere in a sheet that is
 * scrolled to the top, but that only survives on a pane with nothing to scroll — as soon as the
 * content is longer than the sheet, the browser claims a downward touch as a scroll and cancels the
 * pointer. On a real phone the handle is therefore the reliable grab area, and at the bar's own
 * height it was 24 px of it.
 *
 * The heading block is the second grab area, for the same reason and because that is what a native
 * sheet does. `data-sheet-handle` on the wrapper makes it one — that also lifts the scroll-position
 * check, deliberately: a drag started on the heading means the sheet, never the content. `contents`
 * on a fine pointer keeps the wrapper out of the layout entirely, so the desktop dialog is boxed
 * exactly as before and a dialog that renders neither heading form cannot collect a stray gap.
 *
 * The action row is sticky to the pane's bottom edge (#226), the same bleed-and-pin technique as
 * the handle above, mirrored: `-mx-6 -mb-6` cancels the hull's own padding on those three sides so
 * the row's margin box reaches the pane's edges, and `-bottom-6` cancels that same negative margin
 * back out of the sticky offset so the pinned position lands flush rather than 24 px short (or
 * long) of it — exactly the relationship `-top-6`/`-mt-6` already has on the handle, just facing the
 * other edge. The row then re-declares its own padding (`px-6 pt-4 pb-6`) so its content sits where
 * the hull's `p-6` used to put it. This has to be `position: sticky`, not a flex-column height chain
 * (`h-full` on the hull, `overflow-y-auto` on the body) that would pin the footer structurally: two
 * component hosts sit between the pane and the hull (both `display: inline` by default) and a
 * percentage/`h-full` chain does not survive them — the same reason `.app-dialog-panel` itself is
 * the scroll container instead of the hull (see `styles.css`). Sticky needs no height chain, only a
 * scrolling ancestor, which the pane already is.
 *
 * `bg-surface` and `border-t border-border` are load-bearing, not decoration: without its own opaque
 * surface the sticky row would let scrolled-under content show through, and without the rule the
 * boundary between "still scrolling" and "always visible" would be invisible. `rounded-b-lg` only
 * applies on a fine pointer — the sheet's hull is square at the bottom (it is flush with the screen
 * edge, `hullClasses` never rounds that corner there), so rounding the footer to match would round a
 * corner the hull itself does not have.
 */
@Component({
  selector: 'app-dialog-shell',
  imports: [SheetDrag],
  template: `
    <div [class]="hullClasses()" [appSheetDrag]="isSheet()">
      @if (isSheet()) {
        <div
          data-sheet-handle
          class="sticky -top-6 -mx-6 -mt-6 flex min-h-11 touch-none items-start justify-center rounded-t-2xl bg-surface py-3"
          aria-hidden="true"
        >
          <span class="h-1 w-9 rounded-full bg-border-strong"></span>
        </div>
      }

      <div [class]="headingClasses()" [attr.data-sheet-handle]="isSheet() ? '' : null">
        <!-- For a heading that is more than text (the drilldown's emote thumbnail). The projected
             markup carries id="app-dialog-title" on its own h2 — see DIALOG_TITLE_ID. -->
        <ng-content select="[dialog-header]" />
        @if (dialogTitle(); as title) {
          <h2 [id]="titleId" class="text-lg font-semibold text-balance text-fg">{{ title }}</h2>
        }
      </div>

      <div class="flex flex-col gap-3"><ng-content /></div>

      <!-- Cancel goes first, always: the CDK's first-tabbable autoFocus default then lands on the
           harmless control, which is what makes an explicit cdkFocusInitial unnecessary. Sticky to
           the pane's bottom edge (#226) so the row stays reachable while the body scrolls — see the
           class doc comment above for why this is a sticky offset and not a height chain. -->
      <div [class]="actionsClasses()">
        <ng-content select="[dialog-actions]" />
      </div>
    </div>
  `,
})
export class DialogShell {
  /** Already translated. Omitted only when a `[dialog-header]` renders the heading instead. */
  readonly dialogTitle = input<string>();

  private readonly pointerMode = inject(PointerModeService);

  protected readonly isSheet = this.pointerMode.isCoarse;

  protected readonly hullClasses = computed(
    () =>
      'flex flex-col gap-4 bg-surface p-6 shadow-overlay ' +
      (this.isSheet() ? 'rounded-t-2xl' : 'rounded-lg'),
  );

  // `gap-4` only in the sheet case, because that is the only case where the wrapper is a box at all
  // — on a fine pointer `contents` dissolves it and the hull spaces the heading itself, as before.
  //
  // `-mt-4 pt-4` swallows the hull's own gap above the heading and gives the same space back as
  // padding this element owns. Nothing moves; what changes is who answers for those 16 px. Left to
  // the hull they were neither a handle nor `touch-none`, so a gesture starting there had to win a
  // race against the browser's own scrolling — and they sit exactly where a thumb aims, between the
  // bar it can see and the heading it can read. Two drag zones with a dead strip between them are
  // worse than one small zone, because the strip is invisible and the failure looks like caprice.
  protected readonly headingClasses = computed(() =>
    this.isSheet() ? 'flex touch-none flex-col gap-4 -mt-4 pt-4' : 'contents',
  );

  protected readonly titleId = DIALOG_TITLE_ID;

  // `-bottom-6`/`-mb-6`/`-mx-6` mirror the handle's `-top-6`/`-mt-6`/`-mx-6` (see the class doc
  // comment); `rounded-b-lg` only on a fine pointer, because the sheet's hull has no bottom radius
  // to match (flush with the screen edge instead).
  protected readonly actionsClasses = computed(
    () =>
      'sticky -bottom-6 -mx-6 -mb-6 flex flex-wrap items-center justify-end gap-2 border-t ' +
      'border-border bg-surface px-6 pt-4 pb-6 ' +
      (this.isSheet() ? '' : 'rounded-b-lg'),
  );
}
