import { expect, test } from './support/test';

import { AUTH_USER, mockAuditLog, mockAuthMe, mockWorkerHealth } from './support/mocks';

// Its own file rather than an addition to admin-channels.e2e.spec.ts: that file's two tests share a
// channel-list fixture this page has no use for, and the audit log is a separate route with its own
// mock surface.
const ADMIN_USER = { ...AUTH_USER, isGlobalAdmin: true };

test.describe('global admin on /admin/audit-log', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, ADMIN_USER);
    await mockWorkerHealth(page, 'connected');
  });

  test('sees entries with translated actions, actor, channel link and details', async ({
    page,
  }) => {
    await mockAuditLog(page, {
      1: [
        { id: 3, action: 'channel.purge', channelName: 'oldchannel', actorLogin: 'sensitron' },
        {
          id: 2,
          action: 'emotes.syncDeleted',
          channelName: 'handofblood',
          detail: { kind: 'emoteCount', count: 12, text: null },
        },
        {
          id: 1,
          action: 'voteSession.create',
          channelName: 'handofblood',
          targetType: 'voteSession',
          targetId: '42',
          detail: { kind: 'title', count: null, text: 'Sommer-Purge' },
        },
      ],
    });

    await page.goto('/admin/audit-log');

    // The action string is never shown raw — every one of the seven has a label. Scoped to the
    // list rows because the filter toolbar's segments carry the same labels.
    await expect(page.getByRole('listitem').getByText('Channel gelöscht')).toBeVisible();
    await expect(
      page.getByRole('listitem').getByText('Emotes als gelöscht markiert'),
    ).toBeVisible();
    await expect(page.getByRole('listitem').getByText('Abstimmung erstellt')).toBeVisible();

    // Actor and details come from the row itself; the channel is a link into its workspace.
    await expect(page.getByText('von sensitron').first()).toBeVisible();
    await expect(page.getByText('12 Emotes')).toBeVisible();
    await expect(page.getByText('„Sommer-Purge“')).toBeVisible();
    await expect(page.getByRole('link', { name: '#oldchannel' })).toHaveAttribute(
      'href',
      '/channels/oldchannel/usage-stats',
    );
  });

  test('pagination requests the next page and renders its rows', async ({ page }) => {
    await mockAuditLog(page, {
      1: [{ id: 30, action: 'channel.join', channelName: 'handofblood' }],
      2: [{ id: 5, action: 'channel.leave', channelName: 'sensitron' }],
    });

    await page.goto('/admin/audit-log');
    await expect(page.getByRole('listitem').getByText('Channel beigetreten')).toBeVisible();

    const secondPageRequest = page.waitForRequest(
      (request) =>
        request.url().includes('/api/admin/audit-log') && request.url().includes('page=2'),
    );
    await page.getByRole('button', { name: 'Weiter' }).click();
    await secondPageRequest;

    await expect(page.getByRole('listitem').getByText('Channel verlassen')).toBeVisible();
    await expect(page.getByRole('listitem').getByText('Channel beigetreten')).toHaveCount(0);
  });

  test('action filter narrows the list and jumps back to page 1', async ({ page }) => {
    await mockAuditLog(page, {
      1: [
        { id: 30, action: 'channel.join', channelName: 'handofblood' },
        { id: 29, action: 'channel.purge', channelName: 'oldchannel' },
      ],
      2: [{ id: 5, action: 'channel.leave', channelName: 'sensitron' }],
    });

    await page.goto('/admin/audit-log');
    // Move off page 1 first, so the filter's page reset is observable in the request below.
    await page.getByRole('button', { name: 'Weiter' }).click();
    await expect(page.getByRole('listitem').getByText('Channel verlassen')).toBeVisible();

    const filteredRequest = page.waitForRequest(
      (request) =>
        request.url().includes('action=channel.purge') && request.url().includes('page=1'),
    );
    await page.getByRole('radio', { name: 'Channel gelöscht' }).click();
    await filteredRequest;

    await expect(page.getByRole('listitem').getByText('Channel gelöscht')).toBeVisible();
    await expect(page.getByRole('listitem').getByText('Channel beigetreten')).toHaveCount(0);
    await expect(page.getByRole('listitem').getByText('Channel verlassen')).toHaveCount(0);
  });

  test('channel search filters live while typing; reset restores the unfiltered list', async ({
    page,
  }) => {
    await mockAuditLog(page, {
      1: [
        { id: 2, action: 'channel.join', channelName: 'handofblood' },
        { id: 1, action: 'channel.join', channelName: 'sensitron' },
      ],
    });

    await page.goto('/admin/audit-log');
    const channelInput = page.getByRole('textbox', { name: 'Nach Channel filtern' });

    // No Enter, no blur — typing alone must trigger the (debounced) request, same live feel as
    // the emote grid's filter. Typed with capitals: the server normalizes, the mock mirrors that.
    const filteredRequest = page.waitForRequest((request) =>
      request.url().includes('channel=HandOfBlood'),
    );
    await channelInput.fill('HandOfBlood');
    await filteredRequest;

    await expect(page.getByRole('link', { name: '#handofblood' })).toBeVisible();
    await expect(page.getByRole('link', { name: '#sensitron' })).toHaveCount(0);

    // A filter matching nothing shows the filtered empty state, whose reset button clears
    // every filter (two reset buttons exist then — toolbar and empty state — hence first()).
    await channelInput.fill('doesnotexist');
    await expect(page.getByText('Keine Einträge für diese Filter.')).toBeVisible();

    await page.getByRole('button', { name: 'Filter zurücksetzen' }).first().click();
    await expect(page.getByRole('link', { name: '#sensitron' })).toBeVisible();
    await expect(channelInput).toHaveValue('');
  });

  test('selecting the channel-less revoke action disables and clears the channel filter', async ({
    page,
  }) => {
    await mockAuditLog(page, {
      1: [
        { id: 2, action: 'channel.join', channelName: 'handofblood' },
        { id: 1, action: 'user.revokeSessions', targetType: 'user', targetId: '4712' },
      ],
    });

    await page.goto('/admin/audit-log');
    const channelInput = page.getByRole('textbox', { name: 'Nach Channel filtern' });
    await channelInput.fill('handofblood');

    // A revoke entry has no channel — combining it with a channel filter could only match nothing,
    // so picking that action clears the typed value and disables the input.
    const filteredRequest = page.waitForRequest(
      (request) =>
        request.url().includes('action=user.revokeSessions') && !request.url().includes('channel='),
    );
    await page.getByRole('radio', { name: 'User-Sessions widerrufen' }).click();
    await filteredRequest;

    await expect(channelInput).toBeDisabled();
    await expect(channelInput).toHaveValue('');
    await expect(page.getByRole('listitem').getByText('User-Sessions widerrufen')).toBeVisible();

    // Switching back re-enables the input.
    await page.getByRole('radio', { name: 'Alle' }).click();
    await expect(channelInput).toBeEnabled();
  });

  test('header, tabs and filter toolbar stay pinned while the list scrolls', async ({ page }) => {
    await mockAuditLog(page, {
      1: Array.from({ length: 25 }, (_, i) => ({
        id: 100 - i,
        action: 'channel.join',
        channelName: 'handofblood',
      })),
    });

    await page.goto('/admin/audit-log');
    await expect(page.getByRole('listitem').getByText('Channel beigetreten').first()).toBeVisible();

    await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
    expect(await page.evaluate(() => window.scrollY)).toBeGreaterThan(0);

    // The three sticky layers (design doc §8.5): shell header, admin tab bar, filter toolbar...
    await expect(page.getByRole('link', { name: 'Emote Purge' })).toBeInViewport();
    await expect(page.getByRole('link', { name: 'Audit-Log' })).toBeInViewport();
    await expect(page.getByRole('textbox', { name: 'Nach Channel filtern' })).toBeInViewport();
    // ...while the page title above them scrolls away like normal content.
    await expect(page.getByRole('heading', { level: 1 })).not.toBeInViewport();
  });

  test('renders the empty state when nothing has been audited yet', async ({ page }) => {
    await mockAuditLog(page, { 1: [] });

    await page.goto('/admin/audit-log');

    await expect(page.getByText('Noch keine Einträge.')).toBeVisible();
    // No pager for a single (empty) page — Pager renders nothing below totalPages 2.
    await expect(page.getByRole('button', { name: 'Weiter' })).toHaveCount(0);
  });
});
