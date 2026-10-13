# Operations

The parts of running this project that live as code in this repository: what the API expects
from whatever reverse proxy sits in front of it, the backup script and how to restore from what
it produces, and how to reach a local development build from a phone. No particular hosting
setup is assumed.

## Running behind a reverse proxy

The API process serves both the JSON API and the Angular SPA from `wwwroot/`; there is no
separate frontend process to proxy. It listens on `:8080` inside the container and on `:5151`
locally. TLS is terminated by the proxy — the app calls no `UseHttpsRedirection()`, because
Kestrel only ever listens on plain HTTP. Five contracts matter, each enforced or relied on by
code in `src/EmotePurge.Api/`:

- **The application sets its own security headers.** `Program.cs` writes
  `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Strict-Transport-Security`
  and `Content-Security-Policy` on every response. Setting them again in the proxy produces
  duplicates; a site-wide header block applied to all virtual hosts must exclude this one.
- **Buffering must be off for the Server-Sent-Events streams.** The API sets
  `X-Accel-Buffering: no` on SSE responses (`Endpoints/LiveEndpoints.cs`), which nginx and
  compatible proxies honour, but set the equivalent explicitly too (`proxy_buffering off` for
  `/api/`). Without it the live stream stays invisible until a buffer fills — which looks like
  a broken feature, not a proxy setting.
- **The read timeout has to outlast an idle stream.** The API itself writes at least every 5
  seconds (`LiveStreamKeepaliveOptions.KeepaliveInterval` in `Endpoints/LiveEndpoints.cs`) —
  immediately on open, then whenever the subscription has been idle that long; the broker's own
  15-second heartbeat (`HeartbeatInterval` in `Infrastructure/Redis/RedisLiveEventStream.cs`)
  still exists underneath but rarely fires first. nginx defaults to 60. Raise it generously for
  the SSE paths rather than relying on either heartbeat staying under a default somebody may
  lower. Behind a proxy like Cloudflare, an abandoned stream keeps its per-login slot for roughly
  15–25 s regardless of how often the origin writes — the keepalive alone does not make the proxy
  chain notice a cancelled browser tab promptly (measured 2026-09-11, see the DECISIONS entry of
  that date correcting the diagnosis, #128). That is why the SPA releases a stream explicitly, via
  `DELETE /api/live/connections/{id}`, the moment it closes an `EventSource` on purpose, instead of
  waiting on the proxy. A reverse proxy in front of `/api/` must pass the `DELETE` method through
  like any other verb — ordinary configurations already do this without extra setup.
- **`X-Forwarded-Proto` and `X-Forwarded-For` must be set.** Both cookies are `Secure`
  unconditionally — the auth cookie through `CookieSecurePolicy.Always`, the OAuth state cookie in
  `Endpoints/AuthEndpoints.cs` through a hard `Secure = true` since 2026-09-16 — so neither depends
  on the proxy any more. `X-Forwarded-For` still matters on its own: the rate limiter partitions
  anonymous requests by remote address, and without the header every anonymous caller shares one
  bucket, which is enough to push a monitoring probe into `429`.
- **The proxy must reach the container from `127.0.0.0/8`, `[::1]` or `172.16.0.0/12`.**
  `app.UseForwardedHeaders` trusts those two and nothing else: the loopback defaults plus Docker's
  default address pool, which covers the bridge gateway a host-native proxy arrives from. A proxy
  placed outside both ranges has its forwarded headers dropped without a log line. The trust list is
  deliberately not empty — an empty one trusts any sender, see the DECISIONS entry of 2026-09-16.
- **Do not expose the container port beyond loopback.** In `docker-compose.prod.yml` it is published
  as `127.0.0.1:<port>:8080`. Binding it to `0.0.0.0` would put a spoofable path in front of the
  trusted-network check rather than behind it.

Three smaller points. Responses under `/api` carry `Cache-Control: no-store` (`Program.cs`) as
per-user, cookie-authenticated data — do not cache those paths at the proxy. Enable HTTP/2
explicitly if the proxy does not: under HTTP/1.1 the six-connections-per-origin limit applies,
and several tabs holding open SSE streams can starve each other. And the API rate-limits per
user, answering `429` (`RejectionStatusCode` in `Program.cs`), while a limit added at the proxy
usually answers differently — nginx `limit_req` returns `503` — so the status code tells you
which layer rejected a throttled client. No raw WebSocket endpoint exists; SSE needs no
`Upgrade` handling.

## Legal pages (imprint, privacy policy)

The repository is public and self-hostable, so it ships no imprint or privacy policy text of its
own (issue #247) — a fork must not carry the original operator's legal identity. Content comes
from Markdown files the operator supplies on the host, mounted read-only into the container and
never baked into an image.

1. Create a directory on the host, e.g. `/opt/emotepurge/legal`, and put up to four files in it:

   | File | Required | Content |
   |---|---|---|
   | `imprint.de.md` | for the imprint to appear at all | German imprint |
   | `imprint.en.md` | optional | English imprint |
   | `privacy.de.md` | for the privacy policy to appear at all | German privacy policy |
   | `privacy.en.md` | optional | English privacy policy |

   **German is authoritative.** A document is considered configured only once its German file
   exists — an English file with no German counterpart next to it is treated the same as no file
   at all (`ILegalContentService`/`LegalContentService` in `EmotePurge.Infrastructure/Services/`).
   If the English file is missing, an English request answers with the German text plus a flag
   the frontend reads to show "only available in German" instead of silently mixing languages.
   The frontend pages live at `/imprint` and `/privacy`; they read from the API's
   `GET /api/legal/availability` and `GET /api/legal/{imprint,privacy}/{de,en}`.
2. `docker-compose.prod.yml`'s `api` service already carries the mount and the variable, pointed
   at `/opt/emotepurge/legal:/legal:ro` — create that directory on the VPS and put the files from
   step 1 there. `docker-compose.yml` (local dev) mounts `./legal-content` the same way, so the
   feature is testable locally by creating that (gitignored, empty-by-default) directory next to
   the repo — neither line needs editing, only the host directory needs to exist and be filled.
3. Redeploy (`docker compose up -d --build` locally, or pull + recreate in Portainer for prod —
   this needs no database migration and no code change, only the mount and the environment
   variable). The two endpoints and the footer links appear as soon as the container restarts
   with the new configuration; nothing needs to be rebuilt.

An edit to an existing file is picked up on the **next request**, not only on a restart —
`LegalContentService` caches each file's rendered HTML keyed by its own last-write time and
re-renders only when that changes, so there is no cache to flush by hand. Markdown is rendered
to HTML **server-side** with raw HTML disabled (Markdig `DisableHtml()`), so a literal
`<script>` typed into the source file cannot execute — it is escaped like any other text. The same
pipeline does **not** enable the pipe-table extension, so a Markdown table renders as plain text
with literal `|` characters instead of an HTML table — write lists instead of tables in these
files. Both
`/api/legal/availability` (tells the frontend which links to show) and the document endpoints are
anonymous and IP-partitioned like `GET /api/health`, but behind their own `PublicLegal` policy
(`RateLimiting:PublicLegal`, 60/min by default) rather than a share of `PublicHealth`'s — the two
have unrelated legitimate callers (browser visitors vs. two machines on fixed cadences) and must
not be able to exhaust each other's budget.

`.env.example`-style template: none is checked in, because every line would either be empty or a
placeholder path with nothing to demonstrate — `Legal:ContentPath` is documented here instead,
the way `BACKUP_DIR`/`RETENTION_DAYS` above are.

## Contact form

The imprint § 5 DDG requires a second, rapid contact route besides the listed e-mail address —
`/contact` (docs/DECISIONS.md 2026-09-24, "contact form") is that route: a small form, protected
by a Cloudflare Turnstile challenge, that e-mails whatever a visitor submits to the operator over
plain SMTP. Like the legal pages above, the repository is public and self-hostable and ships no
mailbox or Turnstile credentials of its own — an unconfigured instance shows a notice pointing at
the imprint's e-mail address instead of a broken form, not an error.

**You are responsible for linking `/contact` from your own imprint text** (`imprint.de.md`/
`imprint.en.md`, see "Legal pages" above) — the form exists independently of the imprint, but § 5
DDG's second-route requirement is about the imprint pointing somewhere fast, so add a line such as
"You can also reach us via our [contact form](https://your-domain/contact)." once you have set the
form up.

### 1. Create a Turnstile widget

1. In the [Cloudflare dashboard](https://dash.cloudflare.com/), go to **Turnstile** (left sidebar)
   and **Add widget**.
2. **Domain:** your instance's public hostname (e.g. `emotepurge.app`) — Turnstile validates the
   token's origin against this, so a mismatch fails every real submission.
3. **Widget mode:** Managed is the sensible default; any mode works, `ContactPage` always renders
   it explicitly (`turnstile.render()`, not the auto-render `data-sitekey` attribute).
4. Copy the **Site Key** and **Secret Key** it generates — the site key is public by design (it
   ships to the browser via `GET /api/contact/config`), the secret key never leaves the API and
   goes only into `TURNSTILE_SECRET_KEY` below.

### 2. Pick an SMTP account

Any account that speaks SMTP works — a dedicated mailbox is recommended so the credentials in
`.env` are scoped to exactly this one purpose, not a personal inbox. Two examples, in general
terms (exact steps change on the provider's side over time, so check their current documentation
rather than following these as a literal walkthrough):

- **A dedicated Gmail account:** enable 2-Step Verification, then create an **App Password**
  (Google Account → Security → App passwords) — use that, not the account's login password, as
  `CONTACT_SMTP_PASSWORD`. Host `smtp.gmail.com`, port `587`, security `StartTls`.
- **A dedicated Proton Mail account (paid plan, SMTP submission requires Proton Mail Bridge or a
  paid plan's SMTP/IMAP support):** Proton's own documentation covers the current setup; the
  resulting host/port/security values go into the same three variables below.

Either way, `CONTACT_FROM_ADDRESS` is that mailbox's own address (what recipients see as the
sender), and `CONTACT_TO_ADDRESS` is wherever submissions should actually land — the same address,
or a different inbox the account forwards to.

### 3. Fill in `.env`

```
CONTACT_SMTP_HOST=smtp.example.com
CONTACT_SMTP_PORT=587
CONTACT_SMTP_USERNAME=contact@example.com
CONTACT_SMTP_PASSWORD=<app password, not the account login password>
CONTACT_SMTP_SECURITY=StartTls
CONTACT_FROM_ADDRESS=contact@example.com
CONTACT_TO_ADDRESS=contact@example.com
TURNSTILE_SITE_KEY=<from step 1>
TURNSTILE_SECRET_KEY=<from step 1>
```

`CONTACT_SMTP_SECURITY` is one of `StartTls` (the common case on port 587), `SslOnConnect`
(implicit TLS, typically port 465), or `Auto` (let MailKit negotiate) — case-insensitive.
`CONTACT_SMTP_USERNAME`/`_PASSWORD` may stay empty for an SMTP relay that does not require
authentication (e.g. a local network relay); every other variable is required for the feature to
report itself available at all (`GET /api/contact/config`, `ContactOptions.IsAvailable`) — a
partially filled-in set behaves exactly like an empty one, not a startup error. **Since the
2026-09-24 revision, `CONTACT_FROM_ADDRESS`/`CONTACT_TO_ADDRESS` are also checked for shape**: a
typo that leaves either one non-blank but unparseable as a mailbox (a stray `user@` with no domain,
a leading `@example.com` with no local part) reads as "not available" the same way an empty value
does, rather than the form accepting submissions it would then fail to send. If `/contact` shows the
"currently unavailable" notice right after filling in `.env`, double-check both addresses for a typo
before suspecting the SMTP account itself.

Redeploy (`docker compose up -d --build` locally, pull + recreate in Portainer for prod) — no
database migration, only environment variables. `/contact` and `GET /api/contact/config` pick up
the new configuration as soon as the container restarts.

### What ships in Development

`appsettings.Development.json` already points Turnstile at Cloudflare's own official,
publicly-documented always-passing test pair (site key `1x00000000000000000000AA`, secret key
`1x0000000000000000000000000000000AA` — see
[developers.cloudflare.com/turnstile/troubleshooting/testing](https://developers.cloudflare.com/turnstile/troubleshooting/testing/))
and SMTP at `localhost:1025` with no authentication, `Security: "None"`. Nothing sits there by
default — run a throwaway SMTP catcher to actually see the mail, e.g.
[Mailpit](https://github.com/axllent/mailpit):

```
docker run -d --name emotepurge-mailpit -p 127.0.0.1:1025:1025 -p 127.0.0.1:8025:8025 axllent/mailpit
```

Then open `http://localhost:8025` to watch submissions arrive while running the Api locally
(`dotnet run --project src/EmotePurge.Api`) or via `docker compose up -d --build`. Remove the
container (`docker rm -f emotepurge-mailpit`) when done — it holds no state worth keeping.

## Global admins

The admin area (`/admin/*`) and the rights that go with it (purging a channel, deleting an account,
and lifting a broadcaster's re-add lock) are reserved to an allowlist, configured by **numeric
Twitch user id**, not by login: a login can be released after a rename and registered by someone
else, an id cannot.

| Setting | Env variable | Notes |
|---|---|---|
| `Auth:AdminTwitchUserIds` | `ADMIN_TWITCH_USER_IDS` | Comma-separated ids (`1234,5678`). Compared exactly, as text. A JSON array in `appsettings.json` works too; the comma-separated env variable wins over it. |
| `Auth:AdminTwitchLogins` | `ADMIN_TWITCH_LOGINS` | Transitional fallback, case-insensitive logins. Used **only** while the id list is empty. |

**Transition.** As soon as the id list is non-empty it decides alone and the login list is ignored.
The Api logs exactly one of these warnings at startup, with counts only, never the values:

- `Auth:AdminTwitchLogins is ignored because Auth:AdminTwitchUserIds is configured (N id(s), M login(s))`
  — expected after migrating while the old login variable is still set; remove `ADMIN_TWITCH_LOGINS`
  when convenient. Until then the warning repeats on every start.
- `The global admin allowlist is login-based (M login(s)); migrate to Auth:AdminTwitchUserIds`
  — only logins are configured; set `ADMIN_TWITCH_USER_IDS`.

With neither list set nobody is an admin (the area answers a blank 403).

**Lifting a broadcaster's lock needs the id list.** An admin recognised only through the login
fallback keeps every other admin right, but a join against a locked channel is refused for them like
for a moderator (`403 channel_locked_by_broadcaster`, no confirmation dialog): overriding a streamer's
own decision must not hang on a reassignable login. Set `ADMIN_TWITCH_USER_IDS` before an admin needs
to lift a lock.

**Finding your id.** `GET https://api.twitch.tv/helix/users?login=<login>` with an App Access Token
(the same credentials the worker's Helix calls use) returns it in `data[0].id`; a third-party lookup
tool works as well.

**Local development.** `appsettings.json` ships an empty id list and the login `sensitron`, so a
plain `dotnet run` shows the login-based warning. To use an id locally, set it as a user secret in
the Api project: `dotnet user-secrets set "Auth:AdminTwitchUserIds" "<your-id>" --project
src/EmotePurge.Api`.

**Deploying.** Set `ADMIN_TWITCH_USER_IDS` in the stack's environment (Portainer) **before** updating
the stack with a build that contains the broadcaster self-purge (#245). The variable is read at
startup; recreate the `api` container after changing it.

## Admin dashboard

`/admin/monitoring` (global admins only, see below) has three sections besides the health and
capacity cards.

- **Activity:** how many channels were observed live per UTC day, for the last 30 days, 90 days or
  13 months. The running day is partial; a day without any observation is a gap, not a zero.
- **Rate-limit exceptions:** which policies rejected requests and which channels or callers caused
  it, with a 24-hour trend and a details dialog.
- **Load and work:** eleven small charts on one shared time axis for host CPU, host memory, host
  load average, process and container CPU and container memory of Api and Worker, active and live
  channels, and the Worker's chat lines and emote matches. Ranges are 1 hour, 24 hours (default),
  7 days, 30 days, 90 days and 13 months.

### Where the load ranges read from

| Range | Source | Bucket width |
|---|---|---|
| 1 hour, 24 hours | `SystemLoadSamples` (raw samples, kept 7 days) | 1 minute, 5 minutes |
| 7 days, 30 days, 90 days, 13 months | `SystemLoadHourly` (hourly rollups, kept 13 months) | 1 hour, 2 hours, 6 hours, 1 day |

The server buckets the whole range and answers with one compact response; the browser never merges
partial data. Raw ranges show the P95 and maximum of the valid observations per bucket. Rollup
ranges can only show what the hourly rows keep, so they show the highest stored hourly P95 and
maximum per bucket (the axis titles say so), the sum of the stored interval counters for chat lines
and emote matches, and no percentage against a CPU or memory limit (the rollup keeps only the last
limit of each hour). Swap is not rolled up and has no long-range chart. A bucket without data is
drawn as a gap, never as zero. The counting start on production is 2026-10-11 18:47 CEST; earlier
time is empty by construction.

### Caching, polling and the busy answer

- Each Api instance caches the load response per range: 30 seconds for 1 hour and 24 hours, 60
  seconds for the longer ranges. The activity series is cached for 60 seconds, the rate-limit
  snapshot and its trend for 30 seconds.
- Concurrent requests for the same range share one rebuild. Across all ranges at most one load
  rebuild runs per Api instance at a time; a request that has waited 5 seconds for its turn gets
  `503` with `Retry-After: 5` and no body. That is expected under bursts, not a fault, and the page
  keeps the last complete chart and marks it stale.
- An open page re-reads every 60 seconds for the 1-hour and 24-hour ranges and every 5 minutes for
  the longer ones, and pauses while the tab is in the background (it reloads when the tab returns).
- There is no new migration and no new setting for the dashboard.

### What `SystemLoad:IntervalSeconds` does to the charts

Rates are derived from pairs of consecutive samples of the same source and process instance. A pair
further apart than 2.5 times the **currently configured** `SystemLoad:IntervalSeconds` is dropped
(a restart or an outage leaves a gap instead of a bogus rate), exactly as in the rollup.

- Changing the interval therefore changes the gap rule for history too. After a change, the raw
  ranges (1 hour, 24 hours) can show fewer CPU pairs, or none, for samples taken under the old
  cadence for up to 24 hours; for example, going from 30 s to 1 s drops 30-second pairs because
  they now exceed 2.5 s. Going from 1 s to 30 s drops nothing, because 1-second pairs stay below
  75 s. The Worker's rollup no longer recomputes hours that were already rolled up: it recomputes
  a recent hour only when its raw sample count differs from the stored one (and the next hour with
  samples), and it never replaces stored CPU figures with `null`. Hours already rolled up normally keep
  the values computed under the old interval, and lowering the interval no longer erases them.
  One partial-loss case remains: a late sample arriving after an interval decrease makes that hour
  dirty, so it and the next hour with samples are recomputed under the new interval. The guard keeps a
  CPU figure group only if its new result is entirely null; a group that still has some valid pairs is
  overwritten with the reduced result.
- The remaining limit: hours rolled up for the first time after a change are computed under the new
  interval. This covers hours that closed after the old Worker's last hourly run and before the
  restart, the hour that contains the restart, and hours after a long Worker outage. After a
  decrease (for example 30 s to 1 s) their 30-second pairs are dropped, so those hours show no
  process or host CPU. The interval used is not stored with the rows.
- A cadence of 1 second is a stress setting: it multiplies the raw rows read for a 24-hour chart
  (86,400 instead of 2,880 rows per source over 24 hours at the default; 172,800 instead of 5,760 across both sources), so keep it for short experiments.
- Memory, channel and workload series do not depend on pairs and are unaffected.
- The two paths read different containers' settings: the raw ranges apply the **Api's** configured
  `SystemLoad:IntervalSeconds`, the hourly rollup the **Worker's**. After changing it, recreate both
  containers; otherwise the raw and the hourly ranges apply different gap rules and diverge.

## Excluding a chatter (GDPR objection)

The worker processes public chat on a legitimate-interest basis (GDPR Art. 6(1)(f)) to count emote
usage; it stores no message text and no chatter identity, but every message is briefly held in
memory with the sender's Twitch user ID for bot detection. Anyone relying on that basis must honour
an objection under Art. 21. Objections are expected to arrive by e-mail and to be rare — there is no
self-service opt-out (chat command or web form) and none is planned.

1. From the objection, find the chatter's **numeric Twitch user ID** — never the login, which can
   change. The Twitch API (`GET https://api.twitch.tv/helix/users?login=<login>`, an App Access
   Token, the same credentials the worker's own Helix calls already use) or a third-party lookup
   tool both return it.
2. Add the ID to `TWITCH_EXCLUDED_CHATTER_IDS` in the `.env` next to `docker-compose.prod.yml` on
   the VPS — comma-separated if the variable already holds other IDs, same shape as
   `TWITCH_ADDITIONAL_BOT_ACCOUNT_IDS` above it.
3. Recreate the worker so it picks up the new environment (`docker compose -f
   docker-compose.prod.yml up -d --no-deps worker` in Portainer's stack directory, or the
   equivalent redeploy through Portainer's UI). The change takes effect only after this restart —
   `Twitch:ExcludedChatterIds` is read once, at startup, not polled.
4. The worker logs how many IDs are configured (`Configured N excluded chatter id(s).`) on the next
   start — never the IDs themselves, and never a login — so the restart can be confirmed from the
   container logs without looking at the `.env` again.

From the moment the worker restarts, a message from an excluded ID is dropped before it reaches
either the emote counters or the bot detector — the sender is no longer processed at all, in any
category. There is nothing to do retroactively: already aggregated usage counts contain no
identity, so no per-person removal is possible or necessary against them.

**The harness (issue #69) honours the same list, since 2026-09-24 (#260).** `docker compose
--profile harness run ...` reads `Twitch:ExcludedChatterIds` through the same
`IExcludedChatterFilter` as the worker and drops an excluded chatter's archived messages before
they reach `ReplayDayCounter` — a replay can therefore not resurface what the live path no longer
counts. No separate step is needed for it beyond updating `TWITCH_EXCLUDED_CHATTER_IDS` in step 2
above: the harness is a one-shot process, so every invocation already starts fresh with the current
`.env`, unlike the worker's step 3, which only needs an explicit restart because it otherwise keeps
running.

One consequence worth knowing before changing the list mid-measurement: a 30-day binding run is
invoked several times (a large channel's window is ~490 MB against the 200 MB per-invocation cap),
and each invocation resumes the previous one's unfinished `.jsonl` by default. Changing
`TWITCH_EXCLUDED_CHATTER_IDS` between two such invocations ends that resume — the next invocation
starts a fresh file and re-fetches the whole window instead of continuing the interrupted one, so an
exclusion-list change mid-run costs the archive requests already spent on it. This is deliberate
(a changed policy must not silently keep counts gathered under the old one), but it means: finish a
binding run before adding an ID if at all possible, and expect a resumed multi-invocation run to
restart from scratch if the list changes underneath it.

The same drift check applies to `--report-only`: recomputing an existing run's file refuses with
exit code 7 if `TWITCH_EXCLUDED_CHATTER_IDS` has changed since the file was written (distinct from
exit code 3, which means the file itself cannot be recomputed at all — a foreign algorithm version,
a missing or damaged header, and the like). There is no way to recompute around this: finish a fresh
run instead, the same advice as above for a list change mid-measurement.

**A report written before this feature existed (`AlgorithmVersion` `"harness-2"`, everything from
before 2026-09-24/#260) stays recomputable — but only with an empty `TWITCH_EXCLUDED_CHATTER_IDS`.**
Such a file never honoured any exclusion list at all, so its day lines are a faithful re-evaluation
only under today's *empty* list; with anything configured, `--report-only` refuses with exit code 7,
same as a genuinely changed list on a newer file. This is why the binding `"harness-2"` reports of
2026-10-08 stay recomputable with a later worker image (needed if a formula changes later): recompute
them before adding any ID to `TWITCH_EXCLUDED_CHATTER_IDS`, or with it temporarily emptied.

## Blocking a channel from being rejoined (GDPR objection)

The chatter exclusion above stops processing a single person's messages; it does not stop a
broadcaster's own channel from being tracked again. `DELETE /{channelName}/purge` (admin area)
deletes a channel's row and its whole history, but without a block list any moderator or
broadcaster could immediately join it again through the ordinary join route — the objection would
have no lasting effect. `Channels:ExcludedChannelIds` (env `EXCLUDED_CHANNEL_IDS`) closes that gap:
every path that could create or reactivate a `Channel` row for chat observation refuses a blocked
id, and **no caller is exempt, including a global admin** — the only way to undo a block is to
remove the id from the list.

For a streamer's own objection to their channel being tracked at all, in this order:

1. From the objection, find the channel's **numeric Twitch broadcaster ID** — never the login,
   which can change — the same way as for a chatter ID above (`GET
   https://api.twitch.tv/helix/users?login=<login>`).
2. Add the ID to `EXCLUDED_CHANNEL_IDS` in the `.env` next to `docker-compose.prod.yml` on the VPS
   — comma-separated if the variable already holds other IDs, same shape as
   `TWITCH_EXCLUDED_CHATTER_IDS` above.
3. Recreate **both** `api` and `worker` (`docker compose -f docker-compose.prod.yml up -d --no-deps
   api worker` in Portainer's stack directory, or the equivalent redeploy through Portainer's UI) so
   both pick up the new environment — the join endpoint lives in the Api, the identity reconcile in
   the Worker, and `Channels:ExcludedChannelIds` is read once, at startup, not polled.
4. Only **then** purge the channel in the admin area (`DELETE /{channelName}/purge`). Doing this
   last, after the block already takes effect, closes the exact gap this list exists for: without
   this order, the channel could be rejoined in the moments between the purge and the block actually
   being active.

The join endpoint answers `403` with `{ errorCode: "channel_excluded" }` for a blocked channel —
a short, neutral frontend message ("This channel cannot be added.") that names neither a legal
objection nor a reason. Like the chatter list, only a count is ever logged, never an id.

**What step 3 does by itself.** Once `worker` restarts with the new `EXCLUDED_CHANNEL_IDS`, a
channel row that already carries the blocked Twitch id is no longer on the worker's active roster:
boot recovery does not join it or sync its 7TV set, the periodic 7TV resync and the live poll skip
it, a JOIN or RESYNC command for it is ignored, and should the worker still be in that chat anyway
(a LEAVE that got lost), the periodic resync's roster prune parts it within two resync ticks
(`SevenTv:ResyncIntervalSeconds`, default 60 — so one to two minutes). The row itself stays active
in the database until the identity reconcile below deactivates it, which happens in the reconcile's
first pass right after boot recovery. A row that has **no** Twitch id yet (created while Twitch
could not be asked) cannot be matched against the list without asking Twitch, so for such a row
the reconcile's first pass is what stops observation. Purging in step 4 is still recommended — it
is what actually deletes the channel's data.

**Since 2026-09-24, the identity reconcile enforces the block list on its own, without waiting for
step 4.** Once `worker` has picked up the new `EXCLUDED_CHANNEL_IDS` (step 3), its hourly identity
reconcile (`Twitch:IdentityReconcileIntervalMinutes`, default 60) deactivates — same write as an
ordinary leave: `IsBotActive` off, the retention clock stamped, a LEAVE published — any channel row
that is still active and whose Twitch id turns out to be on the list, whether the row already knew
that id or only just resolved it through its current login. That closes the gap step 4 used to guard
against by itself: even if the channel is never purged, it stops being observed within one reconcile
interval of the block taking effect. Purging in step 4 is still the right thing to do and still
recommended — it is what actually deletes the channel's data — but it is no longer what keeps the
objection enforced.

## A broadcaster removes their own channel

Besides the e-mail route above, a broadcaster can erase their channel's data themselves. In the
channel workspace the broadcaster (only them: no moderator, no 7TV editor, no admin) sees the quiet
danger button **Delete channel data**. It opens a dialog that states what will be deleted — emote
count, vote sessions, live days and the mod team's tags — and unlocks after the channel name is typed
out. The data summary behind that dialog is `GET /api/channels/{name}/data-summary`; the deletion is
`DELETE /api/channels/{name}/data`. Both answer only to the account that owns the channel, proven by
the stored Twitch id. For a row without one, the deletion asks Helix whether the login belongs to the
caller; the summary does not call Twitch and answers only when the row's name is the caller's current
login. If the broadcaster renamed and an old row under the previous login still holds their Twitch id,
the deletion removes that row too, and the summary counts both.

**What is deleted.** The channel row and everything bound to it: emotes, usage statistics, live
days, vote sessions with their ballots and votes, set observations and leave observations, and the
**mod team's emote tags** with their entries, placements, activations and operations. Each deleted
row writes a `channel.purge` audit entry with `{ "reason": "broadcasterRequest" }`.

**What stays.**

- Audit-log entries, for 12 months, like every audit entry.
- Backups, for as long as your backup chain keeps them (`RETENTION_DAYS` locally; the reference
  deployment's chain holds them for at most 90 days (14 on the VPS, 30 on the NAS, 60 on OneDrive, plus up to 30 more in the OneDrive recycle bin, which Microsoft empties on its own schedule), which is also what the dialog tells the
  broadcaster; see [Database backup and restore](#database-backup-and-restore)).
- Redis keys, until their TTL runs out: role caches, the resync cooldown, the foreign-channel and
  emote-set caches. None of them outlives its TTL.
- Log lines, until log rotation. Logs carry counts, never ids.
- `emotes.syncImported` audit entries of **other** channels that name the deleted channel as the
  source of an import. They are other channels' history; the privacy policy covers them.
- A lock row (next paragraph): the numeric Twitch id and a timestamp, nothing else.

**The lock.** The purge also writes a row to `BroadcasterChannelLocks` (primary key: the numeric
Twitch id; no foreign key, because the channel row it belongs to is exactly what was deleted). A
locked channel looks untracked everywhere the excluded-channel list is read — **seven read points**:
the join, the 7TV sync (including the boot-recovery warm-up), the worker's active roster, the
identity reconcile, the tracked-channel lookup behind the target picker and the editability
pre-check, and the two membership steps of the emote-set report. The live poll additionally stops
writing live minutes for locked and excluded channels, decided on Helix's user id.

The lock does not expire and the retention job does not know it. It is not secret: the join answers
`403 channel_locked_by_broadcaster`, with the date of the lock for an admin. How it relates to
`EXCLUDED_CHANNEL_IDS`: the two are independent and the env list wins where both apply; the env list
is immutable at runtime and the operator's tool for an objection received by e-mail, the lock is the
broadcaster's own and changes at runtime.

**Lifting the lock.** Only a join lifts it, never a timer:

- The broadcaster adds their own channel again (the prompt in the overview asks for the join); the
  `channel.join` audit entry carries `broadcasterLockLifted`.
- A global admin on the id list (not via the login fallback, see [Global admins](#global-admins))
  joins with `liftBroadcasterLock=true&confirmedLockedAtUtc=<lockedAtUtc>`. The first join without
  the flag is a pure read (`409 channel_locked_by_broadcaster` with `lockedAtUtc`); the admin channel
  list asks for confirmation, naming that date, and repeats the join with the flag and the date
  exactly as the `409` sent it. The lock is lifted only while its date is still that one; if the
  broadcaster has locked again since, the answer is another `409` with the new date and a new
  confirmation. The audit entry carries `liftedByAdmin: true` and the original lock date, so
  overriding a broadcaster's decision stays recognisable.

Nobody else can: a moderator or editor gets the `403`, and so does every admin in the rare case where
one join meets two different broadcasters' locks (Twitch gives the login to one locked account while
the row under it still stores another locked id) — one confirmation overrides one decision, not two.
That state settles once the identity reconcile has deactivated the stale row; if an admin really has to
override both, the lock rows are removed by hand in `BroadcasterChannelLocks`.

**Limits.**

- *Legacy rows.* A channel row without a stored Twitch id, created under an old login (a rename
  leftover), can be proven by nobody once that login no longer resolves to the caller. The purge
  leaves it; the identity reconcile deactivates an unresolvable row after seven days, and the
  180-day retention then deletes it. The dialog says so.
- *Helix outage at join time.* A join while Helix is unreachable creates an id-less row. If that
  channel is locked, the next reconcile deactivates the row and the owner's next join lifts the
  lock. Fail-closed and self-healing.
- *Uncounted time for an id-less row.* Such a row is counted only after 7TV has resolved its id.
  The retry spacing can be up to about an hour (`SevenTv:SearchBudget:ResolutionBackoffMaxSeconds`,
  default 3600, plus the shared search lockout). That is the spacing between two attempts, not a
  promised recovery time.
- *Tag-blocked duplicate.* A rename duplicate (id-less, old login) whose original row still holds
  the Twitch id is refused by the sync; once the mod team has given it a tag, even an empty one, the
  reconcile refuses the merge as well, and the row stays uncounted until a person removes the tag or
  the duplicate. This limit predates the self-purge and is independent of it.
- *Audit-log generation.* A channel created again under the same name sees only the audit entries
  from its own creation onward; its predecessor's entries stay hidden from the channel's log. The
  admin audit view is unbounded.

**Restore.** A restored backup brings back what was deleted after the backup was taken, the lock
table included: a channel purged after the backup date can be observed again until the purge is
repeated. Before a restore, read the **running** database: note the `channel.purge` entries with
`broadcasterRequest` since the backup date (or the lock table), and after the restore purge those
channels again and enter their ids in `EXCLUDED_CHANNEL_IDS` before `api` and `worker` start. If the
running database is no longer readable, those purges are lost; the operator repeats the deletion as
soon as it becomes known. The step-by-step version lives in the private backup runbook.

**Rolling back.** Only images that contain migration `AddBroadcasterChannelLocks` and the self-purge
code check the table. Rolling back to an older image does not: the purged channels stay purged, but a
locked channel could be added again until a newer image runs once more.

### Deploying this feature

1. **Migration first, by hand.** `AddBroadcasterChannelLocks` is additive (one new table), so the
   image still running ignores it. Apply it before the images, as with every migration here: tunnel
   to the VPS, `dotnet ef migrations list` (expect exactly this one as `(Pending)`; the one before it
   is `AddEmoteTagPlacements`), `dotnet ef database update`, `list` again. See "Prod-Migration" in
   `CLAUDE.md`.
2. **Set `ADMIN_TWITCH_USER_IDS`** in the stack environment (see [Global admins](#global-admins)).
   `ADMIN_TWITCH_LOGINS` can stay; it is ignored once the id list is set, and the startup warning
   saying so is the proof that the ids are in effect.
3. **Worker before Api.** Update `worker` first and wait until its identity-reconcile summary line
   names `LockedDeactivated` (printed on every summary line, zero included; at the latest after
   `Twitch:IdentityReconcileIntervalMinutes`). Only then update `api` — until the Api runs there is no
   purge endpoint, hence no lock a stale worker could miss. Roster, sync gate, reconcile and live
   coverage live in the Worker image, not only in the Api.
4. **Check.** Both running containers carry the same image revision (`docker inspect --format
   '{{index .Config.Labels "org.opencontainers.image.revision"}}' <api> <worker>`) and it is the
   merge commit; `/api/health` answers 200; the admin area opens with the id-based allowlist.

The deploy follows the merge on its own schedule; it does not wait for any other open decision.

## Data retention

Not to be confused with the backup rotation's `RETENTION_DAYS` above — this is a separate,
in-database mechanism that deletes or clears user and channel data on a schedule, independent of
whether any backup exists. `DataRetentionWorker` in `EmotePurge.Worker` enforces it once a day by
default.

### The periods

Fixed in `src/EmotePurge.Core/Services/RetentionPolicy.cs`, the one place in the code the numbers
stand:

| Data | Period | Measured from |
|---|---|---|
| Encrypted Twitch tokens | cleared 30 days after last activity | `max(LastLogin, LastSeenAtUtc)` |
| User account | deleted 365 days after last activity | `max(LastLogin, LastSeenAtUtc)` |
| Ended vote session, with its votes | deleted 365 days after it ended | `EndedAt` (falls back to `StartedAt`) |
| Audit log entry | deleted 365 days after it occurred | `OccurredAtUtc` |
| Channel after "leave" | deleted with its whole history 180 days after it was deactivated | `DeactivatedAtUtc` |
| Chat-log backfill run (the run row; its snapshot goes with it) | deleted 365 days after it finished | `FinishedAtUtc` |
| Raw system-load sample | deleted after 7 days, only after its own and its immediate successor's hourly rollups exist | sample hour and source match `SystemLoadHourly`; when a later sample exists for the same process identity and source, its hour must also match |
| Hourly system-load row | deleted after 13 calendar months | `HourUtc` |

"Last activity" is the later of a login and the daily "last seen" stamp `OnValidatePrincipal`
writes at most once per 24 hours — without it, a user who never logs out again (the session
cookie slides for 14 days) but never re-authenticates either would look inactive by `LastLogin`
alone. Active channels and their statistics are never touched. A channel's emote tags (#201)
have no period of their own: the six tables behind them (tags, entries, placements, activations,
operations and the sync's leave observations) go with the channel's purge, through the
foreign-key cascades. The personal-data periods are deliberately **not configurable** — the privacy
policy quotes them (issue #247), and an environment variable that could silently change one would
turn that text into a lie. System-load telemetry has separate operational periods and no personal
data retention statement. All periods remain code constants; what is configurable is only whether
the job writes and how often it runs (below).

**Migration backfill.** The migration that introduced `LastSeenAtUtc` and `DeactivatedAtUtc`
(`AddRetentionTimestamps`) backfills both columns to the migration's own timestamp for existing
rows — every existing user's `LastSeenAtUtc` becomes "now", and every already-inactive channel's
`DeactivatedAtUtc` becomes "now". Without that, an existing user who logged in weeks ago but has
kept a valid session since would look overdue for token clearing on the very first enforced pass,
and a dry run could not tell them apart from someone genuinely gone. The cost is the mirror image:
a genuinely stale account or channel from before the migration gets up to one extra period of
grace, measured from the migration instant rather than from whenever it actually went quiet.

### Dry run by default

`Retention:Enforce` defaults to `false`. In that mode the job counts what it *would* delete but
writes nothing except one thing: it stamps `DeactivatedAtUtc` on inactive channels that do not yet
carry it (rows deactivated by an older image before this feature existed, or in the gap between
applying the migration and deploying the image that stamps on leave) — without that stamp the
180-day period for those channels would never start. Every pass, dry run or enforced, logs
exactly one line per tick, even when every count is zero — the log line is deliberately the job's
only proof of being alive, so a Warning is the safety net against a `Retention:Enforce=false` that
gets deployed once and then forgotten for a year:

```
Retention dry run: nothing was deleted, Retention:Enforce is false. tokens cleared: 3; accounts deleted: 0, still active: 0, not found: 0, failed: 0, cap reached: False, votes deleted: 12 (4 in open sessions), audit entries pseudonymised: 5; vote sessions deleted: 2, votes deleted: 34, ballot entries deleted: 2; audit log entries deleted: 118; backfill runs deleted: 0; channels restamped: 1, purged: 0, still active: 0, not found: 0, failed: 0, emotes deleted: 0, usage rows deleted: 0, live days deleted: 0, vote sessions deleted: 0, votes deleted: 0, observation intervals deleted: 0; system-load raw samples deleted: 0, retained pending hourly rollup: 0, hourly rows deleted: 0
```

An enforced pass logs the identical set of fields at `Information` instead, prefixed
`Retention pass enforced.` — the counts then describe what was actually deleted, not a
projection. Every count is a plain number, never a login, a user id or a channel name (the same
rule the rest of the worker's logging follows). Reading the fields: the first clause of each
category is the "parent" row count (tokens cleared, accounts deleted, vote sessions deleted,
audit log entries deleted, channels purged); everything after it in that category is a cascade —
rows that go with the parent (votes with an account or a session, emotes/usage rows/live
days/vote sessions/votes with a channel). `still active`/`not found`/`failed` count per-item
outcomes that do not stop the rest of the category (a login won a race against an inactivity
deletion, a row already gone, or a transient failure — the item is retried on the next day's
pass). `cap reached: True` on the accounts category means `Retention:MaxAccountsPerRun` was hit
for this tick; the remaining overdue accounts are not lost, they are simply the first ones picked
up on the next pass. The raw-sample count `retained pending hourly rollup` reports samples over seven
days old that cannot yet be deleted because their own hour, or the immediate successor hour for the
same source and process identity, has no rollup. Retention leaves them intact so the Worker can catch
up later; this applies in dry-run and enforced mode.

The pass completes the personal-data categories, including channel restamping and purge, before it
starts system-load retention. Raw-sample and hourly-row retention are isolated from each other. If one
fails, the pass logs the exception, marks that category's counts unknown in the summary, and continues
with the other telemetry category.

**Recommended procedure:** deploy with the default (`RETENTION_ENFORCE` unset or `false`), watch
a handful of daily dry-run lines, and sanity-check the numbers against what you expect for your
instance's size and age (a large `accounts deleted` count on day one against a young instance is
worth investigating before switching on deletion, not after). Once the numbers look right, set
`RETENTION_ENFORCE=true` in the `.env` file next to your production compose file and recreate the
worker container (`docker compose up -d --build worker`, or the equivalent recreate in whatever
orchestrates your deployment) — no image rebuild is needed, only the environment variable and a
container recreate. To switch back to counting only, set `RETENTION_ENFORCE=false` (or unset it)
and recreate the worker container again; nothing already deleted comes back, but no further
deletions happen until it is set to `true` again.

### The other `Retention:*` keys

Only `Enforce` is meant to be flipped in production. The remaining three live in the worker's
`appsettings.json` and are not exposed as `.env`/compose variables — they pace the job rather than
change what it does, and changing them needs a rebuilt image:

| Key | Default | Meaning |
|---|---|---|
| `Retention:IntervalHours` | `24` | Hours between two passes. |
| `Retention:StartupDelayMinutes` | `10` | Minutes the job waits after the worker's boot recovery before its first pass, so a restart loop does not begin every start with a pass. |
| `Retention:MaxAccountsPerRun` | `100` | Ceiling on account deletions per pass; bounds how long the first enforced pass can run against an existing database. The rest follow on later passes, see `cap reached` above. |

### System-load sampling

Both Api and Worker collect a source-tagged sample every 30 seconds by default. The Worker
recomputes hourly summaries at startup and hourly, independently of `Retention:Enforce`; a five
minute grace allows late samples to arrive before an hour closes. It also fills older missing
hours that still have raw rows. Configure these values in the `.env` file; Compose passes them to
both services:

| Environment variable | Configuration key | Default | Meaning |
|---|---|---:|---|
| `SYSTEM_LOAD_ENABLED` | `SystemLoad:Enabled` | `true` | Enables or disables sampling in both hosts. |
| `SYSTEM_LOAD_INTERVAL_SECONDS` | `SystemLoad:IntervalSeconds` | `30` | Sampling cadence, from 1 through 3600 seconds. |
| `SYSTEM_LOAD_ROLLUP_GRACE_MINUTES` | `SystemLoad:RollupGraceMinutes` | `5` | Lateness allowance before an hour is closed. |

Before deploying images containing this feature, apply the additive schema migration against the
production database. It must run once before either the Api or Worker image starts, because both
write samples and the Worker also reads and writes hourly rows:

```sh
dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
```

Run the command from the checked-out release containing the migration, with the production
connection string set. Do not start the new images before it succeeds. The migration was created
with `dotnet ef migrations add AddSystemLoadSampling --project src/EmotePurge.Infrastructure
--startup-project src/EmotePurge.Api`. The samples are shown in the load and work section of the
admin dashboard (see [Admin dashboard](#admin-dashboard)); the Worker rollup and retention count
logs remain the way to inspect the rollup itself.

Sampling contains no personal identifiers. It records process and host resource measurements and
aggregate worker activity counts, so these periods do not change the personal-data retention list
or require an addition to the privacy statement.
The Worker's live-channel count is unknown until its first successful poll after each process start;
Redis snapshots left by the previous process are ignored.

### Account deletion on request

For anyone who asks you to delete their account by email (or however you take such requests): an
admin (one of `Auth:AdminTwitchUserIds`, see [Global admins](#global-admins)) can delete a single account immediately from the admin
user list, independent of the retention job's schedule and independent of whether the account is
inactive. The row's delete action asks for the account's Twitch login typed out before it
unlocks, the same typed-confirmation dialog the channel list's purge action uses — an accidental
click cannot trigger it.

Deletion goes through the same path the retention job uses (`IAccountDeletionService`):

- All of the account's votes are removed, in both open and already-ended vote sessions — a vote
  is an opinion with an author, so it does not survive the account, and no aggregate total is
  kept in its place.
- The account row itself, and with it the encrypted Twitch tokens stored on it, is removed.
- Audit log entries are **not** removed but pseudonymised: any entry where the account was the
  actor, or was the named target (`user.revokeSessions`, `user.invalidateRoleCache`), has its
  identifying fields replaced by the fixed marker `deleted-user`. Every other column and detail
  key is left as is — in particular, a channel's own history (a rename, say) that happens to share
  a name with the deleted account's login is untouched, since that is channel data, not account
  data. The deletion's own audit entry (`user.delete`) carries no identity at all, only the reason
  and the counts (votes removed, entries pseudonymised); if the deleted account is itself the
  actor of that entry (an admin deleting their own account), the marker is recorded as the actor
  too, so that entry cannot re-identify what the rest of the transaction just removed.
- Two pieces of Redis state tied to the account's id are cleared after the deletion commits: the
  role-cache keys (`modlist:`, `7tveditor:`, `subcheck:`) and the rate-limit telemetry's
  last-rejection slot. Each is idempotent and gets one retry if the first attempt fails; if both
  fail, the leftover keys still expire on their own — at most 10 minutes for the role-cache keys,
  at most 25 hours for the telemetry slot (which the very next rejected request from anyone
  overwrites anyway).

None of this reaches your backups: a dump taken before the deletion still contains the account,
and it stays recoverable from that dump for as long as your backups retain it. If a request needs
that closed off too, the affected backup generations need deleting by hand.

### Deploying this feature

The migration behind it (`AddRetentionTimestamps`) is additive — two new nullable columns plus
their backfill — so it does no harm against the *old* image still running while you apply it.
The reverse is not true: a *new* image expects those columns to exist. Apply the migration before
you deploy the new images, not after, the same rule this project follows for every migration
(`dotnet ef database update`, run by hand — migrations do not run automatically at app start).

## Emote tags: enabling play-in and removal runs

The tag play-in and removal runs (#201 T-C) are switched by `TAGS_RUNS_ENABLED` (`.env`, reaches the
Api as `Tags:RunsEnabled`, default `false`). The flag rides on `GET /api/channels/{c}/permissions` as
`tagRunsEnabled` and only decides whether the frontend offers the buttons; the routes exist either
way. Order:

1. Apply the migrations by hand (`AddEmoteTags`, `AddEmoteTagPlacements`, plus any epic migration
   prod does not have yet), as in `CLAUDE.md` "Prod-Migration". They are purely additive.
2. Update the **Api and the Worker image together.** The worker's behaviour changes without a worker
   source change: the sync now writes leave observations and entry stamps (`DECISIONS.md`, #201 T-C).
3. Run the readiness check below; leave the flag off until list A is empty.
4. Set `TAGS_RUNS_ENABLED=true` and update the stack once more.

**Why a check and not a log line:** the sync logs no per-channel resync summary, and the boot
recovery releases its gate even after errors, so "the worker started" proves nothing. A channel
whose last completed sync still comes from the old worker has no observations for rows archived since
the deploy, so a removal could propose placements the new rules would have expired. Only a completed
sync by the new worker counts.

Read the new worker container's start time (Portainer, or `docker inspect <worker> --format
'{{.State.StartedAt}}'`) and put it into both queries as `<WORKER_STARTED_AT>`. You run them through the
SSH tunnel, as for the migration (psql prompts for the password, so it stays out of shell history and the repository):

```
psql 'host=localhost port=15432 dbname=emotepurge user=emotepurge'
```

```sql
-- List A: must be EMPTY. Active, syncable channels whose last completed sync predates the new worker.
SELECT "ChannelName", "ActiveEmoteSetId", "LastSyncedAtUtc", "LastSyncAttemptAtUtc", "LastSyncFailureReason"
FROM "Channels"
WHERE "IsBotActive"
  AND "TwitchChannelId" IS NOT NULL
  AND "ActiveEmoteSetId" <> ''
  AND ("LastSyncFailureReason" IS NULL
       OR "LastSyncFailureReason" IN ('seventv_unavailable', 'seventv_response_unusable'))
  AND ("LastSyncedAtUtc" IS NULL OR "LastSyncedAtUtc" < '<WORKER_STARTED_AT>'::timestamptz)
ORDER BY "ChannelName";

-- List B: for information only. Active channels that cannot sync at all.
SELECT "ChannelName", "TwitchChannelId", "ActiveEmoteSetId", "LastSyncFailureReason"
FROM "Channels"
WHERE "IsBotActive"
  AND ("TwitchChannelId" IS NULL
       OR "ActiveEmoteSetId" = ''
       OR "LastSyncFailureReason" IN ('no_seventv_account', 'no_active_emote_set'))
ORDER BY "ChannelName";
```

- **List A** is empty one tick after the start (`SevenTv:ResyncIntervalSeconds`, default 60) on a healthy
  system. A channel with a transient failure (`seventv_unavailable`, `seventv_response_unusable`) or
  one held back by the wipe guard (no save, old `LastSyncedAtUtc`) stays listed: wait for the next tick
  or read that channel's worker log. A channel just added to `Channels:ExcludedChannelIds` stays listed too (the sync refuses it, so its `LastSyncedAtUtc` never advances) until the identity reconcile deactivates it. While A is not empty, the flag stays off.
- **List B** does not block the release. Those channels cannot carry tag runs (no active set, no
  Twitch id, or a permanent 7TV reason); check only that it
  matches the permanent cases you expect.

**Two assumptions.** Clock: Api and Worker stamp with `DateTime.UtcNow` and run on the same host; if
they ever move apart, keep both on NTP (an offset acts like a shifted credibility window). The window
itself, 30 minutes, is a constant in `SevenTvSyncService`, not a setting. **Measure once after the
deploy:** the sync's post-check reads once per channel per tick (one primary-key-indexed read) and the
first tick writes one observation per archived row. Time that read on the largest channel and watch
the first tick; the figures go into the PR.

Known follow-up: the loader for tracked sets in the tag flows is not yet on the #220 route, so a
tag run against a tracked set pays the foreign-permit cost the earlier route has.

## Emote placeholder marker

Migration `AddEmotePlaceholderMarker` adds `Emotes.IsPlaceholder` (`boolean NOT NULL DEFAULT false`).
It marks emote rows created for an emote the channel has never been observed to have in its active
set. Set-session ballots create such rows, and so does the chat-log backfill (see "Chat-log backfill"). The
7TV sync skips marked rows that never got an archive date when it looks for emotes that left the
active set. The marker is cleared once the emote really enters the active set, and also when an active
row is archived. The reasoning is in `docs/DECISIONS.md` (2026-10-09,
"Emotes.IsPlaceholder").

**This migration is never reverted in production.** An image without the marker treats every marked
row as an emote that just left the active set. It writes a false leave observation for each one and
discards tag placements because of it. The api/worker image pair that first shipped this migration is
the oldest image you may roll back to.

### Deploying it

1. **Migration first, by hand**, as described under "Prod-Migration" in `CLAUDE.md`: tunnel,
   `dotnet ef migrations list` (expect `AddEmotePlaceholderMarker` as `(Pending)`; the one before it
   is `AddBroadcasterChannelLocks`), `dotnet ef database update`, then `list` again. The migration
   adds the column and, in the same step, marks the ballot rows that carry an entry stamp. Those are
   rows created since 2026-10-05. Ballot rows created between 2026-09-22 and 2026-10-05 have no stamp,
   so they look like legacy rows and stay unmarked on purpose. Query 6 below counts them; the reason is
   in `docs/DECISIONS.md`.
2. **Update api and worker together.** Both images change: the Api creates ballot rows with the
   marker, and the Worker skips and clears it. Check that the running revision is the merge commit.
3. **Re-run the marking once, after step 2.** Between steps 1 and 2 the old Api keeps running and keeps
   creating ballot rows. It does not know the column, so those rows get the default `false`. Run the
   statements below once the new revision is confirmed. Then no old writer is left. In the same window,
   the old worker's sync and the old Api's restore report can un-archive a marked row without clearing
   the marker. Queries 4 and 5 show what that left behind.

The marking is safe to run any number of times. It only ever sets `true`, and only on rows that are
archived, have no archive date and have an entry stamp. Only the ballot insert produces that
combination. Every archive the sync or the set-centric delete report performs stamps `ArchivedAt`,
and rows from before the `LastEnteredSetAtUtc` column have no entry stamp. A row the new sync has
already un-archived is not archived, so it is correctly left alone. Run the statements through the
SSH tunnel, as for the migration. psql prompts for the password, so it stays out of your shell
history and out of the repository:

```
psql 'host=localhost port=15432 dbname=emotepurge user=emotepurge'
```

```sql
-- 1. Before: "unmarked" is the number of ballot rows the old Api created after the migration
--    (often 0). "ballot_shaped" is every row with the ballot combination, marked or not.
SELECT count(*) FILTER (WHERE NOT "IsPlaceholder") AS unmarked,
       count(*) AS ballot_shaped
FROM "Emotes"
WHERE "IsArchived" AND "ArchivedAt" IS NULL AND "LastEnteredSetAtUtc" IS NOT NULL;

-- 2. The marking itself (identical to the migration's statement). psql reports UPDATE <n>;
--    <n> must equal "unmarked" from step 1.
UPDATE "Emotes" SET "IsPlaceholder" = true
WHERE "IsArchived" AND "ArchivedAt" IS NULL AND "LastEnteredSetAtUtc" IS NOT NULL
  AND NOT "IsPlaceholder";

-- 3. After: "unmarked" must be 0. "ballot_shaped" normally stays the same; it only drops
--    if the sync un-archived one of those rows in between.
SELECT count(*) FILTER (WHERE NOT "IsPlaceholder") AS unmarked,
       count(*) AS ballot_shaped
FROM "Emotes"
WHERE "IsArchived" AND "ArchivedAt" IS NULL AND "LastEnteredSetAtUtc" IS NOT NULL;

-- 4. Active rows still marked: an old image un-archived them during the window without clearing
--    the marker. Normally 0 one resync tick after the redeploy (SevenTv:ResyncIntervalSeconds,
--    default 60): the new sync clears the marker on every active row the REST answer lists, and
--    archiving an active row clears it too. Exceptions that are harmless: rows in a channel the
--    sync refuses (excluded, or locked by its broadcaster) or cannot reach (a sync failure reason
--    is set), and rows 7TV's lagging REST cache does not list yet. They keep the marker until their
--    channel syncs; their leave is observed all the same.
SELECT count(*) AS active_marked FROM "Emotes" WHERE "IsPlaceholder" AND NOT "IsArchived";

-- 5. Marked rows with an archive date: an old image un-archived them and archived them again in the
--    window. The new code never produces this (it clears the marker whenever it archives an active
--    row). Usually 0. A non-zero count is harmless: the sync only skips marked rows WITHOUT an
--    archive date, so these rows' leaves are observed normally. No action needed.
SELECT count(*) AS marked_with_archive_date FROM "Emotes" WHERE "IsPlaceholder" AND "ArchivedAt" IS NOT NULL;

-- 6. Read-only, for information: ballot rows from 2026-09-22 to 2026-10-05, which carry no entry
--    stamp and stay unmarked on purpose. Legacy archived rows share the null stamps, but only a
--    ballot row has a null FirstSeenAt AND a LastSyncedAt from that period: nothing writes
--    LastSyncedAt on a row that stays archived. "still_on_a_ballot" narrows it further to rows a
--    vote session still references (deleting a session removes its ballot rows).
SELECT count(*) AS unstamped_ballot_rows,
       count(*) FILTER (WHERE EXISTS (SELECT 1 FROM "VoteSessionEmotes" s WHERE s."EmoteId" = e."Id")) AS still_on_a_ballot
FROM "Emotes" e
WHERE e."IsArchived" AND e."ArchivedAt" IS NULL AND e."LastEnteredSetAtUtc" IS NULL
  AND e."FirstSeenAt" IS NULL AND e."LastSyncedAt" >= '2026-09-22';
```

## Chat-log backfill

**Before you switch it on, update the privacy statement.** The backfill reads chat messages of third
parties from an archive the operator does not run. Add the archive as a data source to the
operator-owned `privacy.de.md` / `privacy.en.md` first (see "Legal pages"); no repository commit is
needed. The flag stays `false` on production until that text is live.

A channel manager (global admin, broadcaster or live moderator) can fill the days **before** a channel's
counting start from a public chat archive (`https://logs.cyex.app/` by default). A run covers 1, 3 or
6 months, matches the names of one chosen 7TV set, and writes the counts as `UsageStats` rows with
`Source = 1`. It never touches a live-counted day, and the usage page names the imported range and links
the archive wherever the numbers are shown. The feature is off by default.

### Configuration

Section `ChatLogBackfill` (and `ChatLogArchive`). Only three keys have a compose variable (`Enabled`,
`RequestDelaySeconds` and `ChatLogArchive:BaseUrl`); the others are tuning knobs you set as `ChatLogBackfill__<Key>` / `ChatLogArchive__<Key>` environment
variables on the service if you ever need them. Every value is validated at startup; a typo stops the
container with a readable message.

| Key | Default | Compose variable | Meaning |
|---|---|---|---|
| `ChatLogBackfill:Enabled` | `false` | `CHAT_LOG_BACKFILL_ENABLED` | The feature flag. Read by **api and worker**. Off: the backfill routes answer 404 `backfill_disabled`, the Settings tab is hidden, the worker loop logs one line and exits. |
| `ChatLogBackfill:RequestDelaySeconds` | `10` | `CHAT_LOG_BACKFILL_REQUEST_DELAY_SECONDS` | Worker-side spacing between archive requests, minimum 1. The effective spacing is the larger of this and `ChatLogArchive:RequestDelay`. Keep it at 10 or above. |
| `ChatLogBackfill:MaxBlockMegabytes` | `256` | — | Byte cap per weekly block (decompressed). Exceeding it fails the run with `block_too_large`. Bounds transfer, not memory. |
| `ChatLogBackfill:MaxRetryAfterSeconds` | `900` | — | Cap on one pause after an HTTP 429. |
| `ChatLogBackfill:TransportRetries` | `3` | — | Attempts per block on transport errors before the run fails. |
| `ChatLogBackfill:MaxConsecutivePauses` | `10` | — | 429 pauses on one block before the run fails with `rate_limited`. |
| `ChatLogBackfill:IdlePollSeconds` | `60` | — | How often an idle worker looks for queued runs when no signal arrives. |
| `ChatLogBackfill:CancelPollSeconds` | `2` | — | How often the worker checks whether the running run was cancelled. |
| `ChatLogArchive:BaseUrl` | `https://logs.cyex.app/` | `CHAT_LOG_ARCHIVE_BASE_URL` | The archive. Needed on **api** (stored per run, shown as the next run's source), **worker** (must equal the run's stored value, otherwise the run fails with `archive_mismatch`) and **harness**. Set it once in `.env`; compose passes it to all three. Changing it does not relabel old imports: the attribution follows the host stored with each imported day. |
| `ChatLogArchive:RangeBodyTimeout` | `00:05:00` | — | Body deadline for one weekly block. |
| `ChatLogArchive:MaxLineBytes` | `16384` | — | Longest decoded line the reader accepts; a longer one fails the run with `malformed_response`. |

Block length is a constant 7 days. In production set the three compose variables as Portainer stack
environment variables, not by editing `stack.env` by hand.

### The archive operator's condition

The archive's operator agreed to the use on 2026-10-08, asked for no API key, and asked for **a link to
the archive wherever imported numbers are shown**. The usage-page caption and the Settings tab carry
it. Vote-session ballots include imported days without a source line; that is an operator decision.
If the link is ever removed from a place that shows imported numbers, the agreement no longer holds.

### Enabling it

1. Privacy statement updated and live (above).
2. The running api image already contains the caption and the live-day fix: its `/i18n/de.json` has
   the key `usageStats.liveDaysInRangeFromStart` (which implies `usageStats.trackedSinceWithImport`).
   The second key proves that live-day statements no longer cover imported months, which have no live
   information. Never switch the flag on while an older image is running. The slot behaviour must be on as well:
   from #379 part B it always is; with an image before part B, keep the backfill off while the slot flag is off (see
   "Upgrade and rollback (#379, part B)").
3. Set `CHAT_LOG_BACKFILL_ENABLED=true` and **recreate api and worker** (Portainer: update the stack
   with "Re-pull image and redeploy"). Both read the flag at startup; one without the other leaves the
   tab visible and the queue unprocessed, or the other way round.
4. Smoke test on your own channel with a 1-month window. Check the progress, the result and the
   caption with its link on the usage page.

Exactly **one worker replica** is the supported deployment (compose pins it with `container_name`).
The loop holds a Postgres advisory lock, so a second replica would be harmless but useless.

### Monitoring

- Worker log lines starting `Chat-log backfill`: `run … running: <from>..<to>, week n of m`, then one
  `block i/n …` line per committed block (with lines and bytes), `completed`, `stopped`, `waiting …
  for the archive's cooldown`, and warnings for retries and failures. A 429 is a normal pause, not an
  error; a run that keeps being rate-limited fails with `rate_limited`.
- The `backfill.progress` event on the channel's live stream tells open pages to refetch. It carries no
  state; the Settings tab and the usage page read their numbers from the API.
- The run rows are the record. Completion and failure are not audited (only the request and the cancel
  are). Read them with psql:

```sql
SELECT "Id", "ChannelId", "Status", "WeeksDone", "WeeksTotal", "ErrorCode", "PausedUntilUtc",
       "RequestedAtUtc", "FinishedAtUtc"
FROM "ChatLogBackfillRuns" ORDER BY "Id" DESC LIMIT 20;
```

### Rolling back

**Switching it off is cheap, but cancel first.** `ChatLogBackfill:Enabled=false` on api **and** worker,
recreate both: the routes answer 404 `backfill_disabled` (including the cancel route), the tab
disappears, the worker loop logs `Chat-log backfill disabled.` and exits. Queued and paused rows stay
as they are, and a run stopped mid-block keeps its row on `running` on purpose: the worker only resets
interrupted runs while the flag is on. So cancel in the UI **before** you flip the flag; afterwards
there is no route left to do it. Imported data stays and is still shown with its disclosure, because
the usage caption does not depend on the flag.

**Going back to an image without caption support needs the cleanup below first**, even though the
schema stays: such an image would show imported numbers with no indication where they came from. The
same cleanup is required before reverting the migration `AddChatLogBackfill`; its `Down` refuses
(`RAISE EXCEPTION`) while any `UsageStats.Source = 1` row or any `ChatLogBackfillCoverage` row exists.

**The oldest image you may ever roll back to is the pair that first shipped
`AddEmotePlaceholderMarker`** (see "Emote placeholder marker"). That migration is never reverted. A
worker from before it treats the retained placeholder rows as credible leaves again, and ballot-created
placeholders carry votes and cannot be removed. Every rollback stops there.

Run these through the SSH tunnel, as for the migration; psql prompts for the password. The order
matters:

1. **Cancel active runs in the UI, while the flag is still on.** Wait until the Settings tab shows
   them cancelled (the worker notices within `CancelPollSeconds`). With the flag off the cancel route
   answers 404 `backfill_disabled` and the worker no longer resets interrupted runs, so this step
   cannot be done afterwards.
2. **Flag off on api and worker, and recreate both.** The proof that no writer is left is the new
   worker's log line `Chat-log backfill disabled.`, optionally together with this query returning 0
   (it looks for the session advisory lock the backfill loop holds):

   ```sql
   SELECT count(*) AS lock_holders FROM pg_locks
   WHERE locktype = 'advisory' AND objsubid = 1 AND granted
     AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
     AND classid::bigint = (hashtext('emotepurge:chatlog-backfill')::bigint >> 32) & 4294967295
     AND objid::bigint = hashtext('emotepurge:chatlog-backfill')::bigint & 4294967295;
   ```

   The `database` filter matters on a shared cluster: advisory locks are listed for every database, and without
   the filter a session of another database is counted (1 instead of 0 in the acceptance run).

   A row left on `running` is expected after this and is harmless: step 5 closes it.
3. **Remove the placeholder rows the backfill created (optional, restricted).** This runs **before**
   the run rows are cancelled or deleted, because the run's snapshot is the only proof which rows the
   backfill created (`CreatedRow`). It deletes exactly the rows that are placeholders, were created by
   a run, are not on any vote or ballot, and carry no live usage. A legacy archived row with votes, and
   a ballot placeholder that a backfill merely snapshotted, are never touched. Rows whose run was
   already removed by retention are no longer identifiable and stay. It is never run automatically.

   ```sql
   DELETE FROM "Emotes" e
   WHERE e."IsPlaceholder"
     AND EXISTS (SELECT 1 FROM "ChatLogBackfillRunEmotes" r WHERE r."EmoteId" = e."Id" AND r."CreatedRow")
     AND NOT EXISTS (SELECT 1 FROM "Votes" v WHERE v."EmoteId" = e."Id")
     AND NOT EXISTS (SELECT 1 FROM "VoteSessionEmotes" s WHERE s."EmoteId" = e."Id")
     AND NOT EXISTS (SELECT 1 FROM "UsageStats" u WHERE u."EmoteId" = e."Id" AND u."Source" = 0);
   ```

   The vote predicates are what keep a ballot's history intact: `Vote.Emote` cascades. **This step is global**:
   it covers every channel, and deleting a placeholder row takes its slots along by cascade (5 old placeholders
   of other runs and their slots in the acceptance run). There is no per-channel variant; to leave a channel
   alone, add `AND e."ChannelId" <> '<channel-id>'` to the statement.
3b. **Remove the slots the backfill created (optional, restricted; slot-aware images only).** Run it right
   after step 3 and before step 4, for the same reason as step 3: the run's snapshot is the only proof
   which slots the backfill created (`CreatedSlot`). It deletes exactly the slots that are archived with
   no archive time ("was never in"), were created by a run, and carry no live usage (`Source = 0`); a
   slot that live counting has used stays, and so does a seed that the enqueue merely settled — that one
   is not `CreatedSlot`. A placeholder removed in step 3 took its slots along already. Deleting a slot
   removes its imported rows with it, which step 4 would delete anyway. Skip it on a database without the
   `EmoteSlots` table. Seeds that a flag-off enqueue (an image from before #379 part B) created for active emotes
   are not archived and are not removed here: they remain as unsettled seeds without rows, inert (no numbers,
   no cards, no permanent 409), and are removed by "Sweeping row-less unsettled seeds (#379, part B)" below.

   ```sql
   DELETE FROM "EmoteSlots" s
   WHERE s."IsArchived" AND s."ArchivedAt" IS NULL
     AND EXISTS (SELECT 1 FROM "ChatLogBackfillRunEmotes" r WHERE r."SlotId" = s."Id" AND r."CreatedSlot")
     AND NOT EXISTS (SELECT 1 FROM "UsageStats" u WHERE u."SlotId" = s."Id" AND u."Source" = 0);
   ```

   The statement is `EmoteSlotRollbackSql.DeleteBackfillCreatedSlotsSql`; keep both texts identical.
4. **Delete the imported rows and the coverage.** Globally, or per channel by adding the channel id:

   ```sql
   -- Globally:
   DELETE FROM "UsageStats" WHERE "Source" = 1;
   DELETE FROM "ChatLogBackfillCoverage";

   -- One channel instead:
   DELETE FROM "UsageStats" u USING "Emotes" e
   WHERE u."EmoteId" = e."Id" AND e."ChannelId" = '<channel-id>' AND u."Source" = 1;
   DELETE FROM "ChatLogBackfillCoverage" WHERE "ChannelId" = '<channel-id>';
   ```

   The caption disappears with the coverage rows. The global variant removes **all** `Source = 1` rows and the
   coverage of **all** channels, including those of runs that have nothing to do with the rollback (104,000
   older imported rows on the acceptance copy). For a single channel use the per-channel variant, which removes
   all imported rows and the coverage of that channel from every run and every set; steps 3, 3b and 5 are
   global and are not scoped to the channel.
5. **Close the runs that are still open:**

   ```sql
   UPDATE "ChatLogBackfillRuns"
   SET "Status" = 'cancelled', "FinishedAtUtc" = now(), "ErrorCode" = 'worker_error'
   WHERE "Status" IN ('queued', 'running', 'paused');
   ```
6. Only now deploy the older image, or run `dotnet ef database update <previous migration>`. The
   revert drops `Source` and the four backfill tables and leaves `Emotes.IsPlaceholder` in place.

After the cleanup, verify: `SELECT count(*) FROM "UsageStats" WHERE "Source" = 1;` is 0, the usage page
shows the plain "counting since" sentence, and the vote rows you care about are still there.

## Slot-aware 7TV reconcile

Epic #365 tracks every set entry of an emote as its own slot (one emote under two aliases is two slots). **From #379
part B on the slot behaviour is always on** and there is no switch: the slot reconcile, the EventAPI deltas that add,
rename and archive single slots (an ambiguous dispatch writes no inventory and triggers a REST resync), the match map
that counts per slot, the v4 date overlay, the slot readers, and a backfill enqueue that binds every entry of the
previewed set to its own slot. An emote whose seed of the active set is still unsettled is not counted, and its card
falls back to emote mode, until the reconcile settles it (the first tick per channel after an update). An enqueue
answers 409 `backfill_slots_unsettled` without writing anything while an emote of the active set still has an unsettled
seed or an entry lacks the 7TV date it needs.

Images from before part B read `SevenTv:SlotReconcile:Enabled` (compose variable `SEVENTV_SLOT_RECONCILE_ENABLED`,
default `false`, read by api and worker at startup). With it off they ran the emote-wide reconcile and readers and
counted onto the slot of the (emote, active set) pair; their EventAPI deltas stayed emote-wide. That behaviour is gone
from the code; the next section says what it means for an update and a rollback.

### Upgrade and rollback (#379, part B)

**1. The variable is ignored.** `SEVENTV_SLOT_RECONCILE_ENABLED` and the key `SevenTv:SlotReconcile:Enabled` are read
by no image from part B on. Leaving them set does nothing and does not stop the startup (no unknown-key check exists),
so an old stack file or Portainer variable is harmless. The compose files still map the variable (default `true`),
only for the rollback window in point 4.

**2. Updating.** Sweep afterwards, always: "Sweeping row-less unsettled seeds (#379, part B)" below.

*a. Operators who switched the flag on* (production did, in phase 1 of #379): an ordinary update of api and worker
together, no migration (check `migrations list`: nothing pending). If the chat-log backfill is on, apply the admission
order below first, so that no run is active when the images change.

*b. Operators who never switched the flag on:* the update switches the slot behaviour on, from any flag-off state (the
first tick settles the seeds of the active set). Do the admission order before you update, because a run enqueued by
a flag-off image binds an unsettled seed, which part B can no longer follow:

1. **Close the enqueue:** stop the api.
2. **Drain:** let the worker finish its run or stop it; close runs that are still open with the statement of
   "Rolling back" under "Chat-log backfill", step 5.
3. **Count**, with api and worker both stopped:
   `SELECT count(*) FROM "ChatLogBackfillRuns" WHERE "Status" IN ('queued', 'running', 'paused');` must be 0.
4. **Update:** start the new images of api **and** worker together (never one of them against the old image of the
   other), then run the sweep.

*Fail-closed for an overlooked run.* A run that was enqueued by a flag-off image and is still open when the new
worker picks it up cannot be followed any more. Its first block whose aggregates name a slot the snapshot does not
hold under that emote any more (the reconcile settled the seed the run was bound to) fails the pair check in
`ReplaceBlockAsync` with an `ArgumentException`, and the run ends `Failed` with `ErrorCode` `worker_error`. The
sweep spares the seeds of such an active run, so this stays the outcome. Nothing is miscounted, but the run is
lost; enqueue it again after the sweep. You recognise it by the worker log line
`Chat-log backfill run <id> (<channel>) failed with an unexpected error.` followed by the exception
`System.ArgumentException: <n> aggregates name a slot and an emote that are not one snapshot row of run <id>.`
`(Parameter 'rows')`, and by a run row with status `failed`, `worker_error`, created before the update. A run with
that signature that was created after the update is a different problem.

**3. Rolling back to an image from before part B** (the code of `3720f7cf`, or the later build `30759199` with the same
application code; pick it by its sha tag, not `:latest`). No migration is involved and there is no `Down`; seeds the
sweep has deleted do not matter to the old image. Apply the admission order above here too, then:

1. Use a compose file that still carries the mapping `SEVENTV_SLOT_RECONCILE_ENABLED` (the one of `3720f7cf`, or the
   current one with its default `true`) and set the variable to `true` in the stack environment.
2. **Before you release the stack**, in both containers: `printenv SevenTv__SlotReconcile__Enabled` must print `true`.
   `false`, or nothing at all, means: stop and fix it. Otherwise the old image silently runs with the flag off:
   emote-wide counting and readers, and its api ignores `entries` in delete and restore reports.
3. Stop api and worker, start both together, check `printenv` again, and check that `GET /api/health` answers 200.

A rollback from a database in which two slots of one emote have counted on the same day also needs the collapse
statements before any `Down` of M2 or M3 (see below); the plain image rollback above needs none.

**4. When the mapping goes.** The compose mapping and the stack variable stay until the rollback window ends: the
first deploy after #379 that brings a migration, and at least 14 days. After that a small follow-up commit removes the
mapping from the compose files and this section's points 1 and 3, and the variable can be deleted from the stack.

### Switching the slot flag on (#379, phase 1)

> Applies only to images from before part B, which still read the flag. It is kept as the record of how production
> switched. With part B the switch-on happens by updating the images; see "Upgrade and rollback (#379, part B)".

The images that contain the slot code already run with the flag off; this window only flips the flag, there is no
migration and no new image. Plan one short window with api and worker stopped together, outside big streams. The SQL
below is read-only unless it says otherwise; table and column names are those of the schema. The statements of
"Populations" and the WAL check were run on copies of the development database in the flag-off state (the acceptance
run and a separate runbook test, both 2026-10-13). Run them against the production database through the tunnel described under
"Prod-Migration" in `CLAUDE.md` (bash; passwords as placeholders, never in a file). The commands below that read
`psql "$C"` expect the connection string in a shell variable of the same shell, set once before the window, for
example `C='host=localhost port=15432 dbname=emotepurge user=emotepurge'` (psql then prompts for the password).

**Admission order for backfill runs.** A run enqueued while the flag is off binds an unsettled seed, which nothing
after the switch can follow reliably. So: **close the enqueue first, then drain, then count, then switch**, and keep
the backfill off for as long as the slot flag is off, also after a rollback below. (`ChatLogBackfill:Enabled=false`
closes the enqueue too, but takes the cancel route away; stopping the api closes only the enqueue.)

1. **Close:** stop the api.
2. **Drain:** let the worker finish its run or stop it; close runs that are still open with the statement of
   "Rolling back" under "Chat-log backfill", step 5.
3. **Count runs**, with api and worker both stopped:
   `SELECT count(*) FROM "ChatLogBackfillRuns" WHERE "Status" IN ('queued', 'running', 'paused');` must be 0.
4. **Count seeds without rows**, also with both stopped: unsettled seeds that carry no usage row (an enqueue with the
   flag off can leave them) are expected to be 0; otherwise name them and decide before switching:
   `SELECT count(*) FROM "EmoteSlots" s WHERE s."SeedUnsettled" AND NOT EXISTS (SELECT 1 FROM "UsageStats" u WHERE u."SlotId" = s."Id");`
5. **Switch:** start the new configuration of api and worker together.

A run that slips through anyway is not counted wrongly. In phase 1 the worker follows a settle that moved its
snapshot rows: `SnapshotChanged` makes it re-read the snapshot, move the aggregates of the block to their rows' new
slots and hand the block in once more (log line "the snapshot changed under block"). Only a block it cannot follow
fails the run with `worker_error`. Images from part B no longer have `SnapshotChanged`: there a block that cannot be
followed fails the run at once, and that is the whole safety net (see "Upgrade and rollback").

**Preconditions** for the switch-on: the running images contain the slot code (`migrations list` shows nothing
pending, see M2 and M3 below; that proves the database state, not the running images, so check the image version
separately: inspect the running api and worker containers' image tag or revision label), `ChatLogBackfill__Enabled` is `false` in the stack, and the write-ahead-log cost has been
accepted: the WAL check below passes (under the threshold of roughly 2 MiB/min for your row count; all WAL figures here are MiB, 1 MiB = 1,048,576 bytes).

**WAL check before the switch (read-only).** With the flag on, every 60-second tick locks every reconcile target
row of every active channel `FOR NO KEY UPDATE`, also when nothing changes. The rows that tick locks are the
emote rows of channels with `IsBotActive` that are live, not archived, or own an active settled slot; before the
switch no slot is settled, and a placeholder row is archived by definition, so the count is the not-archived
emote rows (live entries that are not yet in the table are created by the first tick and are a small addition):

```sql
SELECT count(*) AS locked_rows FROM "Emotes" e JOIN "Channels" c ON c."Id" = e."ChannelId"
WHERE c."IsBotActive" AND NOT e."IsArchived";   -- predicate of RowLocks.LockEmotesForReconcileAsync, before the switch
SELECT setting::int AS c_seconds FROM pg_settings WHERE name = 'checkpoint_timeout';   -- C, in seconds
```

Measured on a copy with `pg_walinspect` (acceptance run of #379): 54 bytes per locked row and tick without
full-page images, 539 bytes per row on the first tick after a checkpoint (full-page images, once per checkpoint
cycle). So, per row and minute, with tick interval `I` = `SevenTv:ResyncIntervalSeconds` (60) and checkpoint
interval `C` in seconds:

`B/row/min = 54 * (60/I) + 486 * (60/C)`

At `I = 60` and `C = 300` that is 151 bytes per row and minute; 2 MiB/min (2,097,152 bytes) is reached at about
**13,900 locked rows** (about 38,800 if full-page images are left out). Put the production `C` into the formula; `wal_compression` was off
on the copy, and the figures rest on five ticks without and one tick with full-page images. **Below the threshold:
go on. Above it: do not switch the flag on;** either bring forward the follow-up "Slot reconcile: plan before
locking rows (dry run)" (#411) or raise `SevenTv:ResyncIntervalSeconds` (a raise helps only the 54-byte part).


1. **Check the #406 precondition (read-only).** The UTF-8 name order of #403/#406 must be deployed, and no settled
   slot may exist yet while the flag is off:

   ```sh
   psql 'host=localhost port=15432 dbname=emotepurge user=emotepurge password=<PROD-PW>' -c 'BEGIN READ ONLY' \
    -c 'SELECT count(*) FILTER (WHERE NOT "SeedUnsettled") AS own, count(*) FILTER (WHERE NOT "SeedUnsettled" AND NOT "IsArchived") AS own_active FROM "EmoteSlots"' \
    -c 'ROLLBACK'
   ```

   `own = 0` is what counts. (PR #406 adds a second query for active own slots whose names straddle the UTF-16/UTF-8
   boundary; it filters on own active slots and therefore returns no row when `own = 0`.) Anything else: stop and
   compare the names by hand under both orders.
2. **Measure before (read-only).** Fix a date `D0` (the UTC date of the measurement; do not measure between 23:55 and
   00:05 UTC) and write the result of each population below to a file, **as id lists** (settle ids; unchanged ids with
   `md5(s::text)`; all slots with `md5(s::text)`), plus `pg_current_wal_lsn()` with the time and the
   number of reconcile targets (the emote rows of the active sets of active channels, which one tick locks).
3. **Back up** with `scripts/backup-postgres.sh`.
4. **Stop worker and api** (both, before either starts with the flag on); `SELECT count(*) FROM pg_stat_activity WHERE datname = 'emotepurge' AND pid <> pg_backend_pid();` returns 0 (Npgsql leaves `application_name` empty, so a filter on `EmotePurge%` matches nothing; count by database and leave out your own psql session).
5. **Set** `SEVENTV_SLOT_RECONCILE_ENABLED=true` in your stack's environment and bring both up together. Note the time.
6. **Check the effective configuration in both containers**, not the stack setting: `printenv SevenTv__SlotReconcile__Enabled`
   in the api container and in the worker container must print `true`. Anything else: stop and correct before going on.
7. **Verify within five minutes**, against the populations below:
   - the **settle population** has no unsettled seed left, except channels whose worker log says
     `settle deferred: v4 dates missing for N emotes` (not a lasting state; if it has not cleared after three ticks, investigate that channel before going on);
   - the **unchanged population**, bound to the seed ids of the measurement before (see "Populations"): every id is
     byte-equal, except seeds whose classification turned, which are listed with the reason;
   - the **preservation population** is exact;
   - an emote under two aliases now shows two cards on the usage page, the old total on the original alias and a small
     number on the new one. Every query for "active slots" or for emotes with more than one slot filters
     `NOT "SeedUnsettled"` as well, because unsettled seeds of other sets are not archived. Count:
     `SELECT count(*) FROM (SELECT "EmoteId" FROM "EmoteSlots" WHERE NOT "IsArchived" AND NOT "SeedUnsettled" GROUP BY 1 HAVING count(*) > 1) t;`
     (3 for `handofblood` in the acceptance run);
   - the log line "<channel>: N active emotes are not counted until their unsettled seed of the active set is settled."
     is gone: at 0 the worker logs instead "<channel>: every active emote with a slot is counted again — no unsettled
     seed of the active set is left." (except deferred channels);
   - today's counts are growing: count the rows of today before and after a few minutes
     (`SELECT count(*), sum("UseCount") FROM "UsageStats" WHERE "Date" = '<D0>';`, with `D0` the current UTC date), and
     the worker log has no line "Usage flush failed" and no line "Usage flush has failed" (a successful flush logs
     nothing; `usage.flushed` is a Redis live event, not a log line).
8. **Day 1 (logs, read-only):** `AmbiguousDelta` reasons per kind (only with the EventAPI on; `seed-of-active-set`
   only at the start, `rename-with-siblings` is the common lasting one); WAL over ten ticks against the value before;
   `/totals` latency on the largest channel; the first real delete run of a slot card sends `alias` and `entries`, and
   `sync-deleted` answers 200; the leave observation of an emote removed after the 30-minute window.

**Populations.** Every comparison names its population; do not widen one:

- **Settle population:** unsettled seeds with usage rows of the active set of an active channel whose first sync after
  the switch **succeeded** (`LastSyncFailureReason` empty, `LastSyncedAtUtc` after the switch) and whose emote is
  active afterwards, i.e. present in the live set. Only here "unsettled → 0" is required. The count (this is the
  tested form; the acceptance run read 788 before and 0 after):

  ```sql
  SELECT count(*) FROM "EmoteSlots" s
  JOIN "Emotes" e ON e."Id" = s."EmoteId" JOIN "Channels" c ON c."Id" = e."ChannelId"
  WHERE s."SeedUnsettled" AND c."IsBotActive" AND NOT e."IsArchived"
    AND EXISTS (SELECT 1 FROM "UsageStats" u WHERE u."SlotId" = s."Id" AND u."EmoteSetId" = c."ActiveEmoteSetId");
  ```

  Right after the switch, add the channel condition for the "after" reading: `AND c."LastSyncFailureReason" IS NULL
  AND c."LastSyncedAtUtc" > '<UTC time from step 5>+00'` (the column is `timestamptz`; without the `+00` the literal is
  read in the session's time zone). Channels that fail it are not in the population; name them
  and look at their worker log. Take the "before" reading just before stopping the processes (it can drift by a
  few seeds: 788 became 790 in the acceptance run because two emotes were counted for the first time).
- **Unchanged population:** unsettled seeds that are **not** in the settle population: seeds of emotes that are absent
  (archived, with historical rows in the active set; they stay unsettled on purpose and leave no deferral line),
  seeds of other sets, seeds of channels without `IsBotActive`. **The population is the list of ids from the
  measurement before, not the predicate evaluated again afterwards:** the predicate reads mutable parent state
  (`Emotes.IsArchived`, `Channels.IsBotActive`, `Channels.ActiveEmoteSetId`), and the first sync can flip it. An emote
  that left the live set between the two readings is archived by the sync (`ArchivedAt` set) and its unsettled seed is
  left standing, so it moves from the settle to the unchanged side of the predicate; an archived emote that came back
  is un-archived (`ArchivedAt` null, `LastEnteredSetAtUtc` set) and moves the other way. Count and checksum of the
  predicate would then differ without any defect. The rules, per id:
  - an id that was **unchanged before** is byte-equal now (`md5(s::text)` per id); the only exception is a seed whose
    parent is settle-classified now (live emote, active channel, rows of the active set): it moved inward and may be
    settled;
  - an id that was in the **settle population before** and is still byte-equal (not settled) must have left the settle
    classification (emote no longer live or channel inactive: moved outward, nothing to settle) or belong to a
    deferred channel; a seed that is byte-equal, still settle-classified and not deferred is a failed settle.

  Before (in the stopped window, with the settle ids and the all-slots file of step 2):

  ```sh
  psql "$C" -X -q -v ON_ERROR_STOP=1 -At -F ' ' > unchanged_vorher.txt <<'SQL'
  BEGIN READ ONLY;
  SELECT s."Id", md5(s::text) FROM "EmoteSlots" s
  JOIN "Emotes" e ON e."Id" = s."EmoteId" JOIN "Channels" c ON c."Id" = e."ChannelId"
  WHERE s."SeedUnsettled"
    AND NOT (c."IsBotActive" AND NOT e."IsArchived"
             AND EXISTS (SELECT 1 FROM "UsageStats" u WHERE u."SlotId" = s."Id" AND u."EmoteSetId" = c."ActiveEmoteSetId"))
  ORDER BY s."Id" COLLATE "C";
  ROLLBACK;
  SQL
  ```

  After, take `slots_nachher.txt` with the same statement as the all-slots file (`SELECT s."Id", md5(s::text) FROM
  "EmoteSlots" s`), then compare on the workstation and let the database explain each id that differs; nothing is
  written on production (the ids go in as an array literal):

  ```sh
  LC_ALL=C sort slots_nachher.txt > n.txt
  LC_ALL=C comm -23 unchanged_vorher.txt n.txt | cut -d' ' -f1 > a_ids.txt     # unchanged before, differs or gone now
  LC_ALL=C sort slots_vorher.txt | grep -F -f settle_ids_vorher.txt | LC_ALL=C comm -12 - n.txt | cut -d' ' -f1 > b_ids.txt   # settle before, still byte-equal
  for g in A B; do
  psql "$C" -X -v ON_ERROR_STOP=1 -v grp=$g -v ids="{$(paste -sd, ${g,,}_ids.txt)}" <<'SQL'
  BEGIN READ ONLY;
  SELECT CASE WHEN s."Id" IS NULL THEN 'DEFECT: slot gone'
              WHEN :'grp' = 'A' AND t.settle_class_now THEN 'explained: moved inward (parent live again)'
              WHEN :'grp' = 'A' THEN 'DEFECT: changed without a settle classification'
              WHEN t.settle_class_now THEN 'NOT SETTLED: fine only for a deferred channel'
              ELSE 'explained: moved outward (parent archived or channel inactive)' END AS verdict,
         :'grp' AS grp, i.id, ch."ChannelName", e."Name" AS emote, s."SeedUnsettled" AS unsettled_now,
         e."IsArchived", e."ArchivedAt", e."LastEnteredSetAtUtc", ch."IsBotActive", ch."ActiveEmoteSetId", t.settle_class_now
  FROM unnest(:'ids'::text[]) AS i(id)
  LEFT JOIN "EmoteSlots" s ON s."Id" = i.id
  LEFT JOIN "Emotes" e ON e."Id" = s."EmoteId"
  LEFT JOIN "Channels" ch ON ch."Id" = e."ChannelId"
  LEFT JOIN LATERAL (SELECT ch."IsBotActive" AND NOT e."IsArchived"
                            AND EXISTS (SELECT 1 FROM "UsageStats" u WHERE u."SlotId" = s."Id" AND u."EmoteSetId" = ch."ActiveEmoteSetId") AS settle_class_now) t ON true
  ORDER BY 1, i.id;
  ROLLBACK;
  SQL
  done
  ```

  Required: no row whose verdict starts with `DEFECT`; `NOT SETTLED` only for channels with a deferral line in the
  worker log (same count); the `explained` rows are listed in the report with their channel (they are the emotes that
  entered or left the live set between the two readings). Tested on a copy of the development database with a vanished
  emote (outward), a returning emote (inward) and a changed seed outside the settle population (reported as `DEFECT`).
  The old count and checksum of the predicate may stay as a summary but are no longer the check: they differ whenever a
  seed moves between the sides. For a stricter check, diff the whole table: only settle-population seeds and newly
  created slots may differ (791 changed rows and 172 new ones in the acceptance run, nothing else).

- **Preservation population:** `UsageStats` rows with `"Date" < D0`. Per `("EmoteId", "EmoteSetId")` the row count and
  the sums of `UseCount`, `BotUseCount` and `SharedChatUseCount`, before and after, must be equal (three counters per
  pair). The contract is per `(EmoteId, EmoteSetId)`. The per-slot form is the stricter second check: seeds keep
  their slot id when settled in place, so it was equal in the acceptance run as well; a difference there that
  the per-pair form does not show is a seed that moved its rows onto an existing target and needs a look:

  ```sql
  SELECT "EmoteId", "EmoteSetId", count(*), sum("UseCount"), sum("BotUseCount"), sum("SharedChatUseCount")
  FROM "UsageStats" WHERE "Date" < '<D0>' GROUP BY 1, 2 ORDER BY 1, 2;

  SELECT "SlotId", "EmoteSetId", count(*), sum("UseCount"), sum("BotUseCount"), sum("SharedChatUseCount")
  FROM "UsageStats" WHERE "Date" < '<D0>' GROUP BY 1, 2 ORDER BY 1, 2;
  ```

  The flush only writes today's row, the backfill is off, and retention deletes `UsageStats` rows only through a channel
  cascade (a channel removed during the check is dropped from the comparison and named). Today's growth (`"Date" = D0`)
  is watched separately and not compared.

**Rollback (phase 1).** If the backfill was switched on after phase 1, take it down **first**, in the order of
admission: stop the api (a temporary lock only, it closes the enqueue while you drain), empty the open runs (cancel them
or close them with the statement of "Rolling back" under "Chat-log backfill", step 5), and with both processes stopped
count the open runs and the seeds without rows (steps 3 and 4 above): both must be 0. Then, **before any process
starts with the slot flag off**, set `CHAT_LOG_BACKFILL_ENABLED=false` in the stack environment (it reaches api and
worker; stopping the api is not enough, because the enqueue endpoint checks only `ChatLogBackfill:Enabled`, so an api
restarted with the backfill flag still true would accept runs again while the slot flag is off). Only then switch the
slot flag off: stop worker and api; set `SEVENTV_SLOT_RECONCILE_ENABLED=false`; bring both up; check with `printenv` in
both containers that `SevenTv__SlotReconcile__Enabled` is `false` **and** `ChatLogBackfill__Enabled` is `false`. The backfill stays off as long as
the slot flag is off (admission order above), so nothing is enqueued while the flag is off. The numbers
stay right: slots stay settled, the flag-off counting writes to the oldest slot with rows of the (emote, set) pair and
the readers sum per emote. The slot inventory goes stale while off and heals on the next run with the flag on, by the
add date. One consequence: with the flag on, two slots of one emote can have a row on the same day, so the `Down` of
the usage-key migration (M2) and of the snapshot-key migration (M3) need their collapse statements first, as described
below; do not expect a lossless `Down` after a phase with the flag on. This rollback exists only while the running
image still reads the flag; a rollback to such an image after part B is "Upgrade and rollback" point 3.

**Known limits** (accepted for the switch-on; documented so they are not rediscovered as bugs):

- Re-adding an entry whose slot is still active on our side (a delete report that never succeeded and a restore after
  it, a lagging 7TV REST, a fan-out entry without a slot id in a channel where the set is active) yields a new slot at
  0; the old slot is archived and keeps its history, but the card does not continue it.
- `/totals` and `/series` read the slot snapshot and the aggregates in separate read-committed statements: a settle or
  rebind that commits in between can show one emote with 0 uses in a single response; the next response is right. Follow-up: #412.
- A report that fans out into a channel holding only a seed of the emote, or only archived slots, loses its leave inside
  the credibility window.
- The write-ahead-log cost of the 60-second tick (a row lock on every reconcile target, also when nothing changes) is
  carried while the locked-row count stays below the threshold of the WAL check above; compare the measured WAL with
  the figure before the switch. The follow-up #411 (plan before locking) removes it.

### Sweeping row-less unsettled seeds (#379, part B)

Run this once, after an image from part B runs (admission order above done). On production that is after
phase 2, on any other database after its update. The flag-off path of the images before part B created unsettled seeds
(`"SeedUnsettled"`) for active emotes; some of them never received a usage row and are inert now (no numbers, no cards, no 409). The sweep
deletes exactly those, in every set and for every `Source`. Unsettled seeds that carry rows and settled empty seeds
stay, and so does a seed that the snapshot of a queued, running or paused backfill run points at: an overlooked run
(see "Fail-closed for an overlooked run" above) must keep failing closed instead of losing counts silently when its
seed is gone. The admission order still comes first and leaves no active run, so on a correctly prepared database
that conjunct changes nothing; if the sweep spared a seed, run it again after the run has ended. The only foreign key on `"EmoteSlots"` is `"UsageStats"."SlotId"`, so nothing cascades; a snapshot row of a
finished backfill run may keep pointing at a deleted slot, which is harmless. The expected count on production is 0.
Run it in one transaction, note the first number, and check that the second is 0 (`psql "$C"` as above):

```sql
BEGIN;
SELECT count(*) FROM "EmoteSlots" s
WHERE s."SeedUnsettled" AND NOT EXISTS (SELECT 1 FROM "UsageStats" u WHERE u."SlotId" = s."Id")
  AND NOT EXISTS (
      SELECT 1 FROM "ChatLogBackfillRunEmotes" re JOIN "ChatLogBackfillRuns" r ON r."Id" = re."RunId"
      WHERE re."SlotId" = s."Id" AND r."Status" IN ('queued', 'running', 'paused'));
DELETE FROM "EmoteSlots" s
WHERE s."SeedUnsettled" AND NOT EXISTS (SELECT 1 FROM "UsageStats" u WHERE u."SlotId" = s."Id")
  AND NOT EXISTS (
      SELECT 1 FROM "ChatLogBackfillRunEmotes" re JOIN "ChatLogBackfillRuns" r ON r."Id" = re."RunId"
      WHERE re."SlotId" = s."Id" AND r."Status" IN ('queued', 'running', 'paused'));
SELECT count(*) FROM "EmoteSlots" s
WHERE s."SeedUnsettled" AND NOT EXISTS (SELECT 1 FROM "UsageStats" u WHERE u."SlotId" = s."Id")
  AND NOT EXISTS (
      SELECT 1 FROM "ChatLogBackfillRunEmotes" re JOIN "ChatLogBackfillRuns" r ON r."Id" = re."RunId"
      WHERE re."SlotId" = s."Id" AND r."Status" IN ('queued', 'running', 'paused'));
COMMIT;
```

The statements are `EmoteSlotRollbackSql.CountRowlessUnsettledSeedsSql` and
`EmoteSlotRollbackSql.SweepRowlessUnsettledSeedsSql`; keep both texts identical.

### Usage key switch (M2, #373)

From #373 on, usage is counted per slot: the flush writes `UsageStats.SlotId` and upserts on
`(SlotId, EmoteSetId, Date)`; it never writes a row without a slot. The migration
`SwitchUsageStatsToSlotKey` (M2) first gives every row still without a slot the slot of its
(emote, set) pair — rows that images from before #373 wrote after `AddEmoteSlots` — then makes
`UsageStats.SlotId` NOT NULL and swaps the unique key from `(EmoteId, EmoteSetId, Date)` to
`(SlotId, EmoteSetId, Date)`; the emote index stays as a non-unique index. It refuses to run, and
changes nothing, if a check fails: a row without a slot after the attach step, a row on a slot of
another emote, two unsettled seeds carrying rows of the same (emote, set), an unsettled seed carrying
rows of two sets, or a queued, running or paused backfill run with a snapshot row without a slot.

Every image from #373 on needs M2 (api and worker refuse to start with a pending migration). **Never
run an image from before #373 against a database with M2**: its flush targets the old unique key, which
no longer exists, so every flush fails. The slot flag of the images before part B is independent of M2; from part B on
there is no flag.

**Window**, short, with the first deploy that contains #373:

1. Announce it outside big streams. Stop **worker and api**; `SELECT count(*) FROM pg_stat_activity WHERE datname = 'emotepurge' AND pid <> pg_backend_pid();` returns 0 (Npgsql leaves `application_name` empty, so a filter on `EmotePurge%` matches nothing; count by database and leave out your own psql session).
2. Back up with `scripts/backup-postgres.sh`.
3. Precondition: `SELECT count(*) FROM "ChatLogBackfillRuns" WHERE "Status" IN ('queued', 'running', 'paused');`
   returns 0. Otherwise cancel those runs first (see "Chat-log backfill"); M2 would abort on them.
4. Measure the window: `SELECT count(*) FROM "UsageStats";` and
   `SELECT count(*) FROM "UsageStats" WHERE "SlotId" IS NULL;` (the rows the attach step has to place).
5. Tunnel to the database as described under "Prod-Migration" in `CLAUDE.md`, then list:

   ```sh
   dotnet ef migrations list --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api \
     --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<DB-PASSWORD>;Command Timeout=600'
   ```

   Expect exactly `20261012111509_SwitchUsageStatsToSlotKey` as `(Pending)`. More than that means the database is
   behind by more than this release: review those migrations first.
6. Apply it, naming the target:

   ```sh
   dotnet ef database update 20261012111509_SwitchUsageStatsToSlotKey --project src/EmotePurge.Infrastructure \
     --startup-project src/EmotePurge.Api \
     --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<DB-PASSWORD>;Command Timeout=600'
   ```

   `Command Timeout=600` because the attach and the index rebuild run as single statements over the
   whole table. The migration takes `ACCESS EXCLUSIVE` on `UsageStats` with a 5-second
   `lock_timeout`; a lock timeout means a session is still open (step 1).
7. `migrations list` again: nothing pending. Note the time (`SELECT now();`), then bring **both new
   images up together**.
8. Check within five minutes: no flush errors in the worker log, `usage.flushed` events, today's row
   count grows (`SELECT count(*) FROM "UsageStats" WHERE "Date" = current_date;`), and only a few new
   seeds (`SELECT count(*) FROM "EmoteSlots" WHERE "SeedUnsettled" AND "LastSyncedAt" > '<time from step 7>';`
   — one per (emote, set) counted for the first time). Take the time after `database update`, not
   the window start: M2's attach step also creates seeds, stamped with the migration's time, for the
   pairs first counted between `AddEmoteSlots` and M2 (the rows without a slot from step 4), and those
   are expected however many there are.

**Rollback.** M2's `Down` is lossless only while at most one slot per (emote, set) carries rows, which holds on a
database that never ran the slot behaviour. After a phase with the slot behaviour on (every database that runs an image
from part B, and production since 2026-10-13) it is the normal case, not an edge case, that two slots of one emote
counted on the same day: **run the collapse statement below before `Down`.** Stop worker and api, back up, then

```sh
dotnet ef database update 20261011184206_AddEmoteSlots --project src/EmotePurge.Infrastructure \
  --startup-project src/EmotePurge.Api \
  --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<DB-PASSWORD>;Command Timeout=600'
```

and bring up the images from before #373. Slot ids and the seeds created since stay; the rows the old
images write without a slot are attached again by a later M2. `Down` refuses while an
(emote, set, day) has more than one row — two slots of one emote counted on the same day, which the slot behaviour
produces. Then run the collapse statement first, in one transaction, and repeat
`Down`; it keeps the row of the oldest slot (earliest `AddedAt`, then name, ordinal) and sums the three
counters onto it:

```sql
WITH ranked AS (
    SELECT u."Id",
           ROW_NUMBER() OVER w AS rn,
           COUNT(*) OVER g AS cnt,
           SUM(u."UseCount") OVER g AS use_sum,
           SUM(u."BotUseCount") OVER g AS bot_sum,
           SUM(u."SharedChatUseCount") OVER g AS shared_sum,
           MIN(u."Source") OVER g AS min_source
    FROM "UsageStats" u
    LEFT JOIN "EmoteSlots" s ON s."Id" = u."SlotId"
    WINDOW g AS (PARTITION BY u."EmoteId", u."EmoteSetId", u."Date"),
           w AS (g ORDER BY s."AddedAt" ASC NULLS LAST, s."Name" COLLATE "C" ASC NULLS LAST,
                            u."SlotId" ASC NULLS LAST, u."Id")
),
updated AS (
    UPDATE "UsageStats" t
    SET "UseCount" = r.use_sum,
        "BotUseCount" = r.bot_sum,
        "SharedChatUseCount" = r.shared_sum,
        "Source" = r.min_source
    FROM ranked r
    WHERE t."Id" = r."Id" AND r.rn = 1 AND r.cnt > 1
    RETURNING t."Id"
)
DELETE FROM "UsageStats" t
USING ranked r
WHERE t."Id" = r."Id" AND r.rn > 1;
```

The statement is `EmoteSlotRollbackSql.CollapseUsageStatsSql`; keep both texts identical.

### Backfill snapshot key (M3, #375)

From #375 on, the snapshot of a backfill run is keyed per slot: the migration
`SwitchBackfillSnapshotToSlotKey` (M3) makes `ChatLogBackfillRunEmotes.SlotId` NOT NULL and swaps the
primary key from `(RunId, EmoteId)` to `(RunId, SlotId)`, so a run can hold one snapshot row per set
entry (two aliases of one emote are two rows). The index on `(RunId, SevenTvEmoteId)` stays. It runs in
one transaction and takes `ACCESS EXCLUSIVE` on the snapshot table with a 5-second `lock_timeout`; an
abort changes nothing.

Rows without a slot only exist in runs enqueued before #373. M3 **refuses** while a queued, running or
paused run has one (cancel those runs, then repeat). A finished run's row without a slot is attached to
the slot of its emote that carries usage rows of the run's set — the oldest one (earliest `AddedAt`,
then name, ordinal), which is where the run's imported rows are — and keeps `CreatedSlot` false. If no
slot of the emote carries rows of that set, the run never wrote anything for the emote and the row is
deleted; no seed is created for it. A deleted row's `CreatedRow` is lost with it, so a placeholder it
marked stays behind in step 3 of "Rolling back" under "Chat-log backfill", like the placeholders of a
run that retention already removed. The migration reports both counts as a notice.

Images from before #375 keep working against a database with M3: they write one snapshot row per emote,
always with a slot, which fits the new key. Every image from #375 on needs M3 (api and worker refuse to
start with a pending migration).

**Window**, short and of its own, with the first deploy that contains #375:

1. Announce it. Stop **worker and api**; `SELECT count(*) FROM pg_stat_activity WHERE datname = 'emotepurge' AND pid <> pg_backend_pid();` returns 0 (Npgsql leaves `application_name` empty, so a filter on `EmotePurge%` matches nothing; count by database and leave out your own psql session).
2. Back up with `scripts/backup-postgres.sh`.
3. Precondition: `SELECT count(*) FROM "ChatLogBackfillRuns";` and
   `SELECT count(*) FROM "ChatLogBackfillRunEmotes";` both return 0 on production (the backfill has never
   been switched on there). If not, stop and look first: the rule above is meant for development
   databases.
4. Tunnel to the database as described under "Prod-Migration" in `CLAUDE.md`, then list:

   ```sh
   dotnet ef migrations list --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api \
     --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<DB-PASSWORD>;Command Timeout=600'
   ```

   Expect exactly `20261013092051_SwitchBackfillSnapshotToSlotKey` as `(Pending)`. More than that means the database
   is behind by more than this release: review those migrations first.
5. Apply it, naming the target:

   ```sh
   dotnet ef database update 20261013092051_SwitchBackfillSnapshotToSlotKey --project src/EmotePurge.Infrastructure \
     --startup-project src/EmotePurge.Api \
     --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<DB-PASSWORD>;Command Timeout=600'
   ```

   A lock timeout means a session is still open (step 1).
6. `migrations list` again: nothing pending. Bring **both new images up together**; the slot flag and
   the backfill stay as they were.
7. Check: `GET /api/health` answers 200, the worker logs `Chat-log backfill disabled.` (with the backfill
   off) and flushes as before.

**Rollback.** Going back to the images from before #375 needs no `Down` (see above) as long as every
run has one snapshot row per emote, which holds for runs that an image before part B enqueued with the flag off. To revert
the migration itself, stop worker and api, back up, then

```sh
dotnet ef database update 20261012111509_SwitchUsageStatsToSlotKey --project src/EmotePurge.Infrastructure \
  --startup-project src/EmotePurge.Api \
  --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<DB-PASSWORD>;Command Timeout=600'
```

Rows that `Up` deleted do not come back. `Down` refuses while a run holds more than one row of an emote
— one per slot, which every enqueue since the slot behaviour is on produces (all of them from part B). Then run the collapse statement
first, in one transaction, and repeat `Down`; per run and emote it keeps the row of the oldest slot
(earliest `AddedAt`, then name, ordinal) and sets `CreatedRow` on it if any row of the emote had it, so
step 3 of the backfill rollback still finds the placeholders the run created:

```sql
WITH ranked AS (
    SELECT e.ctid AS row_id,
           ROW_NUMBER() OVER w AS rn,
           BOOL_OR(e."CreatedRow") OVER g AS any_created_row
    FROM "ChatLogBackfillRunEmotes" e
    LEFT JOIN "EmoteSlots" s ON s."Id" = e."SlotId"
    WINDOW g AS (PARTITION BY e."RunId", e."EmoteId"),
           w AS (g ORDER BY s."AddedAt" ASC NULLS LAST, s."Name" COLLATE "C" ASC NULLS LAST,
                            e."SlotId" ASC NULLS LAST)
),
updated AS (
    UPDATE "ChatLogBackfillRunEmotes" t
    SET "CreatedRow" = true
    FROM ranked r
    WHERE t.ctid = r.row_id AND r.rn = 1 AND r.any_created_row AND NOT t."CreatedRow"
    RETURNING t."RunId"
)
DELETE FROM "ChatLogBackfillRunEmotes" t
USING ranked r
WHERE t.ctid = r.row_id AND r.rn > 1;
```

The statement is `EmoteSlotRollbackSql.CollapseBackfillSnapshotSql`; keep both texts identical.

## Database backup and restore

[`scripts/backup-postgres.sh`](../scripts/backup-postgres.sh) dumps the database and rotates
old dumps. It runs on the **host**, not in a container: it shells out to `docker exec` against
the running Postgres container, so `docker` must be on `PATH` and the invoking user allowed to
run `docker exec`/`docker inspect`. It writes `<prefix>-<date>_<time>.sql.gz` into
`BACKUP_DIR`, dumping into a `.tmp` file first and renaming it with an atomic `mv` only after
`pg_dump`'s exit code and a non-empty size check both pass. That is the point of the script: a
plain `pg_dump | gzip > file.gz` exits 0 even when `pg_dump` failed midway, leaving a
valid-looking but truncated archive. Rotation only ever deletes files matching the script's own
name pattern.

| Variable | Default | Note |
|---|---|---|
| `POSTGRES_CONTAINER` | `emotepurge-postgres` | `container_name` in `docker-compose.prod.yml` |
| `POSTGRES_USER` | `emotepurge` | `POSTGRES_USER` in `.env.example` |
| `POSTGRES_DB` | `emotepurge` | hardcoded as `POSTGRES_DB` in both compose files |
| `BACKUP_DIR` | `/var/backups/emotepurge` | operational choice, not defined elsewhere in the repo |
| `RETENTION_DAYS` | `14` | operational choice, not defined elsewhere in the repo |
| `BACKUP_FILE_PREFIX` | `emotepurge` | also the pattern rotation matches against |
| `OFFSITE_ENABLED` | `0` | optional `rclone` copy, off by default |
| `OFFSITE_RCLONE_REMOTE` | *(empty)* | e.g. `b2:my-bucket/emotepurge` |

A dump contains user records including stored Twitch tokens; restrict the target directory
(`chmod 700`). The optional off-site step copies the finished dump with
[`rclone`](https://rclone.org/) and only logs a warning if `rclone` is missing or the remote
unset, because a broken off-site leg must never mark a successful local backup as failed. A
dump on the same machine as the database it protects guards only against mistakes *inside* that
system — deletion, volume corruption, a failed upgrade — never against loss of the machine.

A single nightly cron entry is enough; backup and rotation happen in the same run. Two things
about `/etc/cron.d` files bite reliably, neither with an error message: those files do **not**
inherit `PATH` from `/etc/crontab` (cron gives them only `/usr/bin:/bin`, so the script aborts
nightly at its own `command -v docker` check if `docker` lives elsewhere), and cron silently
ignores files there that are group- or world-writable or not owned by root. Run the script by
hand once before scheduling it, and verify the schedule by moving it a couple of minutes into
the future instead of waiting for the real time — which is server time.

### Restore drill

Checking that the dumps are worth anything, without touching the live database and without
stopping API or worker: load one into a throwaway database on the same container, then drop it.

```sh
docker exec emotepurge-postgres psql -U emotepurge -d postgres \
  -c 'CREATE DATABASE emotepurge_restoretest OWNER emotepurge;'
gunzip -c <dump>.sql.gz \
  | docker exec -i emotepurge-postgres psql -U emotepurge -d emotepurge_restoretest -v ON_ERROR_STOP=1 -q
echo "exit code: $?"
docker exec emotepurge-postgres psql -U emotepurge -d postgres \
  -c 'DROP DATABASE emotepurge_restoretest;'
```

**Without `ON_ERROR_STOP=1` the drill is worthless:** `psql` otherwise skips past errors and
still exits 0, so a restore missing tables would look successful. Expect exit code 0 and no
output. Comparing row counts against the live database is a useful second step, but differences
are normal: `UsageStats` and `Votes` keep growing (the flush runs every 30 seconds), and
`__EFMigrationsHistory` is behind if a migration was added after the dump was taken.

### Restoring for real

Both real restore paths start by stopping the writers — API and worker write continuously
(chat-match flush, 7TV resync, join/leave), and leaving either running invites race conditions.
`pg_dump --format=plain`, which the script uses, contains no `DROP` statements, so replaying
into a non-empty database fails with `already exists`. Drop and recreate first, connected to
the `postgres` maintenance database because a database cannot drop itself:

```sh
docker stop emotepurge-api emotepurge-worker
docker exec -i emotepurge-postgres psql -U emotepurge -d postgres -c "DROP DATABASE emotepurge;"
docker exec -i emotepurge-postgres psql -U emotepurge -d postgres -c "CREATE DATABASE emotepurge OWNER emotepurge;"
gunzip -c <dump>.sql.gz | docker exec -i emotepurge-postgres psql -U emotepurge -d emotepurge
docker start emotepurge-api emotepurge-worker
```

A successful run is an unbroken series of `CREATE TABLE`/`COPY`/`ALTER TABLE` confirmations —
scroll the output for `ERROR:` lines.

**After the volume is gone** the sequence differs. With `postgres-data` missing, bringing the
stack up makes the Postgres image create a fresh empty volume and an empty `emotepurge`
database from the `POSTGRES_DB`/`POSTGRES_USER` variables — without schema, since the EF Core
migrations do not run at app start. Start Postgres alone, wait for its healthcheck to report
healthy, then replay the dump straight into that empty database: no drop/recreate is needed,
because a plain-format dump carries the full schema **and** the EF Core migration history. Then
start API and worker. Only if migrations were added after the dump was taken does
`dotnet ef database update` (see `CLAUDE.md`, "EF Core Migrationen") have to follow.

`postgres-data` is not the only stateful volume: `dataprotection-keys` holds the ASP.NET Core
Data Protection keyring, and losing it signs every user out (see `docker-compose.prod.yml`).

## Testing on a device in your own network

To check the app on a real phone — touch behaviour, `pointer: coarse` layouts, sheet dialogs —
without deploying and without a cable. There is deliberately no staging stage: this *is* the
local development environment under a different name, with the same database, the same test
data, hot reload and a real Twitch login.

```
docker compose up -d postgres redis
dotnet run --project src/EmotePurge.Api --launch-profile lan
__VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS=your.host.example npm --prefix web run start:lan
```

**Two things carry your hostname, and neither of them is a tracked file.** The repository is
public, so it holds no hostname at all.

- **API:** the `lan` profile sets only the flag `EMOTEPURGE_LAN`. The hostname itself lives in
  `src/EmotePurge.Api/appsettings.Lan.json`, which is gitignored. Create it once with
  `cp src/EmotePurge.Api/appsettings.Lan.json.example src/EmotePurge.Api/appsettings.Lan.json`
  and put your hostname in both values. Without the file the profile still starts and falls
  back to the `localhost` redirect URI, so login fails visibly rather than silently doing
  something else. The file is excluded from `dotnet publish` and from the Docker build context,
  so it cannot be baked into an image.
- **Dev server:** pass your hostname in `__VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS`, as above.
  The `lan` configuration deliberately does **not** set `allowedHosts`, which leaves it as the
  empty array Vite needs before it will read that variable. Do not reach for
  `ng serve --allowed-hosts` instead: the Angular CLI exposes that option only as a boolean, so
  it turns host checking off altogether and disables Vite's protection against DNS rebinding —
  a page you open in any browser could then resolve its own name to your machine and talk to
  the dev server. The variable is Vite-internal (hence the two underscores) and could disappear
  on a major upgrade; if it does, the fallback is a local, uncommitted `allowedHosts` entry in
  `angular.json`.

Both `lan` variants are purely additive — plain `dotnet run` and `npm --prefix web start`
behave exactly as before, and `appsettings.Development.json` is untouched. They differ from the
everyday start in two ways: the Angular dev server listens on all network interfaces instead of
`localhost` only, and the API's Twitch redirect URI and post-login redirect point at the
hostname you reach the machine under instead of `localhost`. Reaching that from a phone takes
three things, whatever software you use for them:

1. A hostname inside your own network resolving to the development machine, and a proxy in
   front of it terminating TLS and forwarding to the dev server on `:4200`. HTTPS is not
   optional: the auth cookie is `Secure`-only, so the app does not work over plain `http://`.
2. That proxy passing `X-Forwarded-Proto: https` through, and supporting WebSockets. Without
   the header the OAuth state cookie loses its `Secure` flag and login fails with
   `InvalidOAuthState`; without WebSockets the Vite HMR socket cannot connect and the phone
   stops live-reloading (the environment stays usable — reload by hand).
3. The same redirect URI registered in the Twitch application you develop against, character
   for character: scheme, host and path, no trailing slash. A mismatch is answered by Twitch,
   not by this app, so the error can surface on a completely different machine.

Only `/` goes through that proxy. `/api` stays on the Angular dev proxy
(`web/proxy.conf.json` → `:5151`), which keeps the topology identical to working at the desk
and everything same-origin: no CORS, no `withCredentials`. The worker is not started by the
three commands above; run it separately if you need chat counting or 7TV sync.

Four failures worth recognising:

- **"Blocked request" from the dev server.** The Vite-based dev server rejects `Host` headers
  it was not told to accept, and by default only `localhost`/`.localhost`/IPs are allowed. The
  host check stays on for `start:lan` as well, so this means your hostname did not reach it:
  check that `__VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS` is set in the environment you started the
  command in, and that it matches the name the phone actually requests, character for
  character. Note that the variable is read only when it is exported for that process — setting
  it in another shell does nothing.
- **Assets served stale, or a lazy chunk failing with `504`.** If the proxy caches assets, a
  moment where the dev server was down puts those errors into the cache with an expiry, and the
  affected chunk stays dead while others keep working. Asset caching is wrong in front of a dev
  server whose chunk hashes change on every rebuild — turn it off.
- **The phone shows an old version even after a hard reload.** Same cause one layer out: those
  assets came with a `Cache-Control` max-age and now live in the *browser*, so turning the proxy
  option off does not recall them. Clear the site data on the device. When a change refuses to
  show up, put a visible probe in the page (a colour, a border) before measuring behaviour.
- **Test data missing.** Then `dotnet run` points at a different database than the compose
  stack: the password in `appsettings.json` has to match `POSTGRES_PASSWORD` in `.env`.
