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
`<script>` typed into the source file cannot execute — it is escaped like any other text. Both
`/api/legal/availability` (tells the frontend which links to show) and the document endpoints are
anonymous and IP-partitioned like `GET /api/health`, but behind their own `PublicLegal` policy
(`RateLimiting:PublicLegal`, 60/min by default) rather than a share of `PublicHealth`'s — the two
have unrelated legitimate callers (browser visitors vs. two machines on fixed cadences) and must
not be able to exhaust each other's budget.

`.env.example`-style template: none is checked in, because every line would either be empty or a
placeholder path with nothing to demonstrate — `Legal:ContentPath` is documented here instead,
the way `BACKUP_DIR`/`RETENTION_DAYS` above are.

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
