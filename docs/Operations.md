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
  deployment's chain holds them for at most 60 days, which is also what the dialog tells the
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
whether any backup exists. `DataRetentionWorker`, the tenth hosted service in `EmotePurge.Worker`,
enforces it once a day by default.

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

"Last activity" is the later of a login and the daily "last seen" stamp `OnValidatePrincipal`
writes at most once per 24 hours — without it, a user who never logs out again (the session
cookie slides for 14 days) but never re-authenticates either would look inactive by `LastLogin`
alone. Active channels and their statistics are never touched. A channel's emote tags (#201)
have no period of their own: the six tables behind them (tags, entries, placements, activations,
operations and the sync's leave observations) go with the channel's purge, through the
foreign-key cascades. These periods are deliberately **not configurable** — a privacy
policy quotes them (issue #247), and an environment variable that could silently change one
would turn that text into a lie. What is configurable is only
whether the job writes and how often it runs (below).

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
Retention dry run: nothing was deleted, Retention:Enforce is false. tokens cleared: 3; accounts deleted: 0, still active: 0, not found: 0, failed: 0, cap reached: False, votes deleted: 12 (4 in open sessions), audit entries pseudonymised: 5; vote sessions deleted: 2, votes deleted: 34, ballot entries deleted: 2; audit log entries deleted: 118; channels restamped: 1, purged: 0, still active: 0, not found: 0, failed: 0, emotes deleted: 0, usage rows deleted: 0, live days deleted: 0, vote sessions deleted: 0, votes deleted: 0
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
up on the next pass.

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
