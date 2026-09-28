import { test as base, type Request, type Route } from '@playwright/test';

/**
 * The one place e2e specs import `test` and `expect` from — never `@playwright/test` directly (the
 * lint config enforces it for every spec under `e2e/`; see docs/DECISIONS.md, 2026-09-28).
 *
 * What the import buys: every test runs with the 7TV CDN stubbed and guarded.
 *
 * - The regular Playwright config makes `cdn.7tv.app` unresolvable for Chromium
 *   (`--host-resolver-rules`), so no run can reach the real CDN. Routing happens before DNS, so a
 *   route still answers normally.
 * - An automatic fixture routes `https://cdn.7tv.app/**` on the browser context to a 1×1 PNG, so a
 *   sprite loads (and becomes visible) instead of failing. A spec that needs something else — real
 *   dimensions, another format, a recording — adds its own `page.route` for the CDN (page routes
 *   win over context routes) and answers through {@link fulfillCdnStub}.
 * - The same fixture fails the test if a CDN request got past every stub: such a request goes to
 *   the network, hits the resolver block and fails with `net::ERR_NAME_NOT_RESOLVED`. Cancelled
 *   requests (`net::ERR_ABORTED` and the like) are ignored on purpose — a sprite whose url changes
 *   or a row the virtual scroll recycles cancels a request that was stubbed, not one that escaped.
 *
 * `support/mocks.ts` deliberately does not import this module: the measure config reuses it and must
 * hit the real CDN.
 */

export { expect } from '@playwright/test';
export type { Download, Locator, Page, Request, Route } from '@playwright/test';

/** Every url on the 7TV CDN, as a route pattern. */
export const CDN_URL_PATTERN = 'https://cdn.7tv.app/**';

const CDN_HOST = 'cdn.7tv.app';

/** The failure a request gets at the resolver block — and only there. */
const ESCAPED_REQUEST_ERROR = 'net::ERR_NAME_NOT_RESOLVED';

/**
 * How long the guard waits, after the test body, for CDN requests that are still open. Only paid
 * when one is: a request that escaped the stubs fails at the resolver within milliseconds.
 */
const OPEN_REQUEST_GRACE_MS = 1_000;

/** A transparent 1×1 PNG — what the CDN stub answers unless a spec asks for something else. */
const PNG_1X1 = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
  'base64',
);

/**
 * Answers a CDN request from the stub — by default with the 1×1 PNG. A spec that needs a real image
 * (other dimensions, another format) passes its own body and content type.
 */
export function fulfillCdnStub(
  route: Route,
  body: Buffer = PNG_1X1,
  contentType = 'image/png',
): Promise<void> {
  return route.fulfill({ status: 200, contentType, body });
}

export const test = base.extend<{ sevenTvCdn: void }>({
  sevenTvCdn: [
    async ({ context }, use) => {
      await context.route(CDN_URL_PATTERN, (route) => fulfillCdnStub(route));

      const open = new Set<Request>();
      const escaped: string[] = [];
      const isCdn = (request: Request) => new URL(request.url()).hostname === CDN_HOST;
      const onRequest = (request: Request) => {
        if (isCdn(request)) open.add(request);
      };
      const onFinished = (request: Request) => {
        open.delete(request);
      };
      const onFailed = (request: Request) => {
        open.delete(request);
        if (isCdn(request) && request.failure()?.errorText === ESCAPED_REQUEST_ERROR) {
          escaped.push(request.url());
        }
      };
      context.on('request', onRequest);
      context.on('requestfinished', onFinished);
      context.on('requestfailed', onFailed);

      await use();

      const deadline = Date.now() + OPEN_REQUEST_GRACE_MS;
      while (open.size > 0 && Date.now() < deadline) {
        await new Promise((resolve) => setTimeout(resolve, 20));
      }
      context.off('request', onRequest);
      context.off('requestfinished', onFinished);
      context.off('requestfailed', onFailed);

      if (escaped.length > 0) {
        throw new Error(
          `${escaped.length} request(s) to ${CDN_HOST} got past every stub and failed with ` +
            `${ESCAPED_REQUEST_ERROR} at the resolver block. Answer them through the context stub ` +
            `or fulfillCdnStub() (e2e/support/test.ts) instead of route.continue():\n` +
            escaped.map((url) => `  ${url}`).join('\n'),
        );
      }
    },
    { auto: true },
  ],
});
