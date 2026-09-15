import { Page, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockVoteSessionList,
  mockWorkerHealth,
} from './support/mocks';

const SESSION = { id: 7, title: 'Frühjahrsputz' };

// Same tagging technique as emote-import.e2e.spec.ts's "dock outcomes … region that outlives the
// dock" (#134 follow-up): tag every role="status" element present at rest, then require the
// region that later carries the feedback text to still carry that tag. A plain count-under-retry
// check can pass for the wrong reason — the feedback clears after 2000 ms
// (vote-session-list-page.ts) while the count assertion itself retries for 1000 ms, so on a slow
// run a defect's extra node can self-clear inside that retry window before it is ever counted.
const AT_REST = 'data-e2e-at-rest';

async function tagStatusRegionsAtRest(page: Page): Promise<void> {
  await page.evaluate((attribute) => {
    document.querySelectorAll('[role="status"]').forEach((node) => {
      node.setAttribute(attribute, '');
    });
  }, AT_REST);
}

test.describe('vote session list — copy-link acknowledgement', () => {
  test.beforeEach(async ({ page, context }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockVoteSessionList(page, 'sensitron', [SESSION]);
    // Chromium withholds Clipboard-API writes without an explicit grant — without this the click
    // below falls into the 'error' branch, which is a different (also covered elsewhere) path.
    await context.grantPermissions(['clipboard-write', 'clipboard-read']);
  });

  // #134: a role="status" region that enters the DOM together with its content announces nothing
  // to most screen reader/browser pairings — only a mutation *inside* an already-mounted region is
  // announced. Pins the fix (docs/UI-Designsprache.md §4.5): the sr-only status region on the row
  // is mounted permanently and only its text changes via @if; a click must never add a *new*
  // status node to the row, and the visible twin next to it is aria-hidden so nothing is read out
  // twice.
  test('copy-link acknowledgement lives in an already-mounted status region, not a freshly mounted one', async ({
    page,
  }) => {
    await page.goto('/channels/sensitron/vote-sessions');

    const row = page.getByRole('listitem').filter({ hasText: SESSION.title });
    await expect(row.getByRole('button', { name: 'Link kopieren' })).toBeVisible();

    // At rest: the row carries no "Kopiert" status text yet. Every role="status" element present
    // now is tagged, so the region that later carries the feedback text can be checked for
    // identity rather than merely counted.
    const rowStatus = () => row.getByRole('status');
    await expect(rowStatus().filter({ hasText: 'Kopiert' })).toHaveCount(0);
    await tagStatusRegionsAtRest(page);

    await row.getByRole('button', { name: 'Link kopieren' }).click();

    const notice = rowStatus().filter({ hasText: 'Kopiert' });
    await expect(notice).toBeVisible();

    // The region the text appeared in must be one that already existed at rest, not a freshly
    // created node — checked with a short timeout so a defect that only self-clears after the
    // component's own feedback window (2000 ms) cannot pass by outliving this assertion's window.
    await expect(notice).toHaveAttribute(AT_REST, '', { timeout: 1000 });

    // The visible copy is a separate, aria-hidden element — otherwise the same message is spoken
    // twice, once from the live region and once from the visible text.
    await expect(row.locator('[aria-hidden="true"]').filter({ hasText: 'Kopiert' })).toBeVisible();
  });
});
