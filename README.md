# Emote Purge

Cross-platform web application that lets Twitch communities analyse, rate and clean up their 7TV emote sets. Chat is read live and tallied per emote, the community votes on keep/delete in vote sessions, and in the end a mass-delete engine removes the losers directly via the 7TV API.

Production: **[emotepurge.app](https://emotepurge.app)**

**Stack:** .NET 10 (Minimal API + Worker Service) · PostgreSQL via EF Core · Redis Pub/Sub · Angular 22 (Standalone + Signals, Tailwind) · Docker.

---

## Where things live

| Document | What for |
|---|---|
| **this README** | One-time setup and everyday commands |
| [`CLAUDE.md`](CLAUDE.md) (in German) | The applicable rules and conventions, condensed. Read this before your first PR. |
| [`docs/Architectur.md`](docs/Architectur.md) | The specification: modules, communication flow, DB model, Docker topology |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) (in German) | **"Why is X built this way?"** — chronological decision log. Searchable via `grep <filename> docs/DECISIONS.md`. |
| [`docs/UI-Designsprache.md`](docs/UI-Designsprache.md) | Binding for every UI change under `web/` |
| [`web/.claude/CLAUDE.md`](web/.claude/CLAUDE.md) | Frontend-specific conventions |

Anything with an outward effect is written in English: this README, `CONTRIBUTING.md`, issues and pull requests, code comments, commit messages, and log and error messages. Plans, concepts and the existing entries of the decision log stay German by design — they are the maintainer's working notes, not a public surface. The exact rule is in `CLAUDE.md` (in German) under "Sprache", and the reasoning is in `docs/DECISIONS.md` (2026-09-10).

---

## Prerequisites

| | Version | Note |
|---|---|---|
| .NET SDK | 10.0.300+ | pinned in [`global.json`](global.json) |
| Node.js | 22+ | [`web/.nvmrc`](web/.nvmrc); CI and the Docker image build on 22 |
| Docker | current | Required — also for `dotnet test` (Testcontainers starts real Postgres/Redis containers) |
| `dotnet-ef` | matching EF 10 | `dotnet tool install --global dotnet-ef` |

---

## One-time setup

The steps build on each other. **Nothing works without Twitch login** — `join`/`leave` and practically every page require an authenticated session.

### 1. Register a Twitch application

Create an application at [dev.twitch.tv/console/apps](https://dev.twitch.tv/console/apps). Enter **both** as the OAuth redirect URL, otherwise only one mode of operation will work:

```
http://localhost:5151/api/auth/twitch/callback     # lokal via dotnet run
http://localhost:8080/api/auth/twitch/callback     # via docker compose
```

Note the client ID and client secret. **The secret never belongs in the repo** (Rule 17) — it goes straight into `.env` or `dotnet user-secrets`.

### 2. Create `.env`

```bash
cp .env.example .env
```

Then fill in:

- `TWITCH_CLIENT_ID` / `TWITCH_CLIENT_SECRET` from step 1
- `AUTH_TWITCH_TOKEN_ENCRYPTION_KEY` — 32-byte key used to encrypt the Twitch tokens in Postgres:
  ```bash
  openssl rand -base64 32
  ```
- **`ADMIN_TWITCH_USER_IDS` — your own numeric Twitch user id** (look it up as described in [docs/Operations.md](docs/Operations.md) "Global admins"; `ADMIN_TWITCH_LOGINS` with your login still works as a transitional fallback). Without either, the entire admin area (`/admin/*`) returns a blank 403, with no hint as to why.
- You can leave the Postgres/Redis passwords as they are for local use.

### 3. Migrate the database

**Migrations do not run automatically on startup** — in any environment. So before the first start:

```bash
docker compose up -d postgres redis
dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
```

### 4. Start

**Option A — everything in Docker** (closest to production; Angular is built into the Api image and served from there):

```bash
docker compose up -d --build
# → http://localhost:8080
```

**Option B — local with hot reload** (for frontend work):

```bash
docker compose up -d postgres redis
dotnet run --project src/EmotePurge.Api        # Terminal 1, Port 5151
dotnet run --project src/EmotePurge.Worker     # Terminal 2
npm --prefix web install                       # einmalig
npm --prefix web start                         # Terminal 3 → http://localhost:4200
```

`ng serve` proxies `/api` to `:5151` ([`web/proxy.conf.json`](web/proxy.conf.json)), so everything stays same-origin and the session cookies flow without CORS configuration.

> **Do not** use the VS Code launch config `Api` for frontend work: it binds hard to `:8080` and thereby breaks the locally registered redirect to `:5151`.

### 5. Log in and track a channel

Log in in the browser, then join a Twitch channel. **Only after that is there any data at all** — emotes come from the 7TV sync, usage numbers only from chat that has been read live. There are deliberately no seed data: the application lives on real chat traffic, and a fixture would only fake that.

Anyone who wants to try it as a non-admin can empty `ADMIN_TWITCH_USER_IDS` and `ADMIN_TWITCH_LOGINS` and restart the stack.

---

## Everyday commands

```bash
# Build
dotnet build EmotePurge.slnx

# Tests (the backend suite needs Docker running — Testcontainers)
dotnet test EmotePurge.slnx
npm --prefix web test -- --watch=false      # Vitest
npm --prefix web run e2e                    # Playwright, /api/** gemockt

# Formatting and lint — the same checks CI runs
dotnet format EmotePurge.slnx
npm --prefix web run format
npm --prefix web run lint

# Rebuild the stack (after backend changes --build is mandatory, see rule 15)
docker compose up -d --build
docker compose logs -f api
```

Recommended once, so that `git blame` skips the pure formatting commits:

```bash
git config blame.ignoreRevsFile .git-blame-ignore-revs
```

---

## What surprises first-time contributors

Four things that are deliberately this way and still trip people up:

**A new backend capability costs three places.** Interface in `EmotePurge.Core/Services/`, implementation in `EmotePurge.Infrastructure/Services/`, registration in `AddEmotePurgeInfrastructure`. `AppDbContext` and `IConnectionMultiplexer` are forbidden from API handlers (Rule 4). The interfaces are never mocked — they carry the layer separation, not testability. This is ceremony by intent, not by accident.

**Endpoints live in `src/EmotePurge.Api/Endpoints/*.cs`, never in `Program.cs`** (Rule 6), and authorization runs via `IEndpointFilter` classes in `Auth/`, not ASP.NET policies. Which filter applies to which endpoint is documented as a matrix in `docs/Architectur.md`.

**On errors, the API returns only language-neutral codes** (`ApiErrorCodes`), never finished text. A new code needs the same key in `web/src/app/core/i18n/api-error.ts` **and** in both locale files — otherwise `api-error.spec.ts` fails.

**Almost nothing is configurable at runtime.** Flush interval, join throttling, rate limits, watchdog thresholds and delete pacing are named constants in the code, not settings. This is a deliberate, consistently upheld decision — but it means an operational problem requires a code change plus a deploy.

---

## Contributing

- **Conventional Commits** (`feat:`, `fix:`, `chore:`, `docs:`, …), prefer several logically separate commits over one catch-all commit.
- A commit that changes a convention, a contract or a topology **includes its entry in `docs/DECISIONS.md` (in German) in the same commit**.
- Verify backend changes **live** against real Postgres/Redis/Twitch/7TV access before committing, not just `dotnet build` (Rule 16).
- The full rule list is in [`CLAUDE.md`](CLAUDE.md) (in German).
- [`CONTRIBUTING.md`](CONTRIBUTING.md) has the short English version: the test gates, the decision-log rule, formatting, and where to read on.
- Report security issues privately, not as an issue — see [`SECURITY.md`](SECURITY.md).
