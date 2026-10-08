import { defineConfig, devices } from '@playwright/test';

// All specs mock every `/api/**` call via page.route (see e2e/support/mocks.ts) — the dev server's
// proxy target (the .NET Api on :5151) never needs to be reachable, so these run standalone in CI
// without Postgres/Redis/a live Twitch OAuth app.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 2 : 0,
  reporter: 'list',
  use: {
    baseURL: 'http://localhost:4300',
    trace: 'on-first-retry',
    // Every spec asserts on hardcoded German copy. The app now auto-detects the browser language
    // (see LanguageService.resolveInitialLang) — without pinning this, a CI runner's default
    // Chromium locale (en-US) would render English text and break those assertions.
    locale: 'de-DE',
    // No run may reach the 7TV CDN: Chromium resolves cdn.7tv.app to nothing, so a request that
    // gets past the suite-wide stub in e2e/support/test.ts fails at DNS instead of going out — and
    // that fixture fails the test for it. Routing happens before DNS, so the stub itself still
    // answers. Both projects inherit this (neither device descriptor brings launchOptions of its
    // own); a project that sets its own launchOptions must carry the argument along. The measure
    // and audit configs deliberately do not have it.
    launchOptions: { args: ['--host-resolver-rules=MAP cdn.7tv.app ~NOTFOUND'] },
  },
  webServer: {
    command: 'npx ng serve --port 4300 --proxy-config proxy.conf.json',
    url: 'http://localhost:4300',
    reuseExistingServer: !process.env['CI'],
    timeout: 120_000,
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
      testIgnore: /touch-mobile\.e2e\.spec\.ts/,
    },
    // A second project rather than a per-test context: `pointer: coarse` follows from the device
    // descriptor's hasTouch/isMobile, and those are context-level options that a test cannot set for
    // itself. Only touch-mobile.e2e.spec.ts runs here — every other spec asserts desktop behaviour
    // and would have to be rewritten for a viewport it was never about.
    {
      name: 'mobile-chrome',
      use: { ...devices['Pixel 5'] },
      testMatch: /touch-mobile\.e2e\.spec\.ts/,
    },
  ],
});
