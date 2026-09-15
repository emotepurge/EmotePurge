# ARCHITECTURE & SPECIFICATION: Emote Purge

> **Project name:** Emote Purge  
> **Repository:** `emote-purge`  
> **Backend API:** `EmotePurge.Api` (.NET 10)  
> **Worker Bot:** `EmotePurge.Worker` (.NET 10)  
> **Message Broker / Cache:** Redis 7.2  
> **Database:** PostgreSQL (EF Core)

---

## 1. System overview & guiding principles

**Emote Purge** is a cross-platform web application for analysing, community-rating and cleaning up 7TV emote sets on Twitch.

### Architecture principles:

1. **Single Source of Truth (PostgreSQL):** The PostgreSQL database permanently stores which channels are active (`IsBotActive = true`), which emotes exist and what the chat statistics look like.
2. **Decoupled real-time control (Redis Pub/Sub):** Web API and worker service are strictly separated. When a streamer has the bot join from the dashboard, the API writes this to PostgreSQL and publishes an event via Redis (`channel:bot:commands`). The worker receives that event in real time (< 5ms) and joins the chat.
3. **Automatic recovery on restarts:** On startup the worker service reads all active channels from PostgreSQL, automatically re-establishes the Twitch IRC chat connections and runs one full 7TV sync for every channel. After that the hybrid 7TV sync takes over ongoing operation (see A.3): EventAPI WebSocket for live deltas (behind a feature flag) plus the periodic `SevenTvPeriodicResyncWorker` as reconciliation.
4. **Zero-knowledge for write tokens:** 7TV access tokens with write permissions remain _exclusively_ in the admin's browser. The backend never stores or processes 7TV tokens at any point.
5. **Dynamic role caching:** Roles (sub, VIP, mod) are not stored permanently in the database; they are queried live via the Twitch API and cached briefly in Redis / MemoryCache.
6. **High-performance analytics:** The chat bot processes high chat volumes economically through in-memory buffering (`ConcurrentDictionary`) and performs a batch flush into PostgreSQL every 30 seconds.

---

## 2. Tech stack & infrastructure

| Layer              | Technology             | Description & purpose                                                                          |
| :----------------- | :--------------------- | :-------------------------------------------------------------------------------------------- |
| **Backend API**    | .NET 10 (ASP.NET Core) | REST API for auth, dashboard, voting engine and the Redis publisher.                          |
| **Worker Service** | .NET 10 Worker Service | Background bot for the Twitch IRC chat listener & the hybrid 7TV sync: EventAPI WebSocket (live deltas, feature flag) + periodic REST resync as reconciliation (see A.3). |
| **Message Broker** | Redis 7.2 (Alpine)     | Decouples API & worker via Pub/Sub; caching for Twitch roles. Pinned to 7.2, the last BSD-licensed Redis version before the licence change to RSALv2/SSPL from 7.4 onwards. |
| **Database**       | PostgreSQL 16+         | Relational persistence for channels, emotes, stats and vote sessions via EF Core (Npgsql).    |
| **Frontend**       | Angular + Tailwind CSS | Single page application with virtual scrolling (`CdkVirtualScrollViewport`) for 1,000+ emotes. |
| **Deployment**     | Docker Compose         | Containerisation of API, worker service and Redis with persistent volumes.                    |

---

## 3. Inter-service communication (Pub/Sub + recovery)

[ Angular Dashboard ]
│
│ HTTP POST /api/channels/{name}/join
▼
[ ASP.NET Core API ] ───────── (1) Save Status ─────────► [ PostgreSQL ]
│ ▲
│ (2) PUBLISH "JOIN:montanablack" │
▼ │ (4) Recovery On Boot
[ Redis Pub/Sub Broker ] │
│ │
│ (3) Realtime Signal │
▼ │
[ .NET Worker Service ] ──────────────────────────────────────┘
│
├─► Twitch IRC: Join Channel
├─► 7TV EventAPI (WSS): live dispatches (feature flag, see A.3)
└─► 7TV REST: full sync (initial + periodic reconciliation, see A.3)

**Return channel (live updates, since 2026-07-31):** Worker and Api publish thin notification events (`{type, channel, sessionId?}` — never data) to the Redis channel `live:events` (contract: `Core/Messaging/LiveEvents.cs`). For this the Api is itself a Redis subscriber for the first time: `RedisLiveEventStream` (Infrastructure, singleton, lazy subscribe on the first client) fans the events out to open **Server-Sent Events** connections (`GET /api/channels/{name}/live`, `GET /api/admin/live` — native `TypedResults.ServerSentEvents`, no SignalR). The browser then refetches through the normal REST endpoints (notify-and-refetch). Because every Api replica subscribes itself, the mechanism works without a backplane and without sticky sessions even with several replicas. Rationale and operating contract (broker heartbeat 15 s, 10-minute connection cap, connection limits instead of a rate limit, proxy requirements) are in the DECISIONS entry of 2026-07-31; since 2026-09-11 the Api additionally writes its own keepalive at least every 5 s so proxies do not time out an idle connection. Prompt release of an abandoned stream's slot does **not** come from that keepalive — production measurement showed the proxy chain (Cloudflare → nginx) holds the slot for 15–25 s regardless of write frequency — but from the SPA explicitly calling `DELETE /api/live/connections/{id}` when it closes a stream on purpose (DECISIONS entry of 2026-09-11 correcting the same-day diagnosis, #128).

Publishing sites per event type (as of 2026-08-01):

| Event | Publisher |
|---|---|
| `usage.flushed` | Worker: `UsageFlushWorker` after a successful flush |
| `vote.changed` | Api: `VoteSessionEndpoints` (success arms of the vote POST/DELETE) |
| `channel.synced` | Worker: `Worker` (JOIN/RESYNC command **unconditional**, boot recovery only on change), `SevenTvPeriodicResyncWorker` and `SevenTvEventClient` (delta plus follow-up/gap-fill resyncs) **only on change** · Api: `EmoteEndpoints` `POST .../emotes/sync-deleted` and `.../sync-restored` respectively, when ≥1 emote was newly archived or newly un-archived |
| `worker.health` | Worker: `WorkerHealthPublisher` |
| `worker.roster` | Worker: `WorkerRosterPublisher` (60 s cadence, one third of the health frequency) |

"Only on change" means: `SevenTvSyncResult.HasChanges` or `SevenTvDeltaOutcome.Applied` — see the DECISIONS entry of 2026-08-01.

---

## 4. Module specifications

### Module A: Twitch chat bot & analytics engine (Worker Service)

> **Implementation status:** A.1 (basic flow + spam protection/emote matching), A.2 (in-memory aggregator + batch flush) and A.3 (7TV sync, hybrid: EventAPI WebSocket + REST reconciliation — see below) are fully implemented. `EmotePurge.Worker` connects anonymously/read-only via `TwitchLib.Client` (no bot account, no OAuth token), joins/leaves channels on demand via Redis (`channel:bot:commands`, messages `JOIN:<name>`/`LEAVE:<name>`) and on startup automatically joins all `IsBotActive=true` channels from Postgres (boot recovery, principle 3). Every chat message received is matched against the active 7TV emotes of the respective channel (`IEmoteMatchCache`, `channelName → {EmoteName → Emote.Id}`) and hits are counted at most once per message in `IEmoteUsageCounter` (spam protection against copypasta), separated into human and bot in the channel's own room plus a third category for everything from foreign rooms of a Twitch shared-chat session (`UseCount`/`BotUseCount`/`SharedChatUseCount`, see DECISIONS 2026-09-01 and 2026-09-06 respectively); a separate `UsageFlushWorker` hosted service drains this counter every 30 seconds and upserts the counts into `UsageStat` via `IUsageStatFlushService`. Controlled through Minimal API endpoints in `EmotePurge.Api`: `POST /api/channels/{channelName}/join` upserts the `Channel` in Postgres (principle 1) and publishes `JOIN:<name>`; `DELETE /api/channels/{channelName}` hard-deletes the row (not merely deactivating it — see the decision log in CLAUDE.md) and publishes `LEAVE:<name>`; `GET /api/channels/{channelName}/usage-stats` returns the current `UsageStat` rows for debugging; `GET /api/channels/{channelName}/usage-stats/totals?from=&to=` returns, per emote, the `UseCount` summed over a freely chosen time range (the basis for the usage-stats dashboard as well as for the manager context column in Module C's voting results — no longer part of the score since 2026-08-01, see docs/DECISIONS.md). On every join the active 7TV emote set is resolved and fully synchronised into Postgres (`ISevenTvSyncService`, which also refreshes `IEmoteMatchCache` — and, since 2026-09-08, pre-warms it from the active Postgres rows **before** the first 7TV call whenever it is empty for that channel, so that a channel counts from the join onwards even when 7TV happens not to answer; see the DECISIONS entry of 2026-09-08); after that the hybrid 7TV sync keeps the inventory current — live deltas over the EventAPI WebSocket (`SevenTvEventWorker`/`SevenTvEventClient`, feature flag `SevenTv:EventApi:Enabled`) plus a `SevenTvPeriodicResyncWorker` that periodically repeats the same full sync for all active channels as reconciliation (cadence `SevenTv:ResyncIntervalSeconds`, default 60 s — see A.3). Since 2026-08-03 a `TwitchLivePollWorker` additionally polls `GET /helix/streams` for all active channels (batches of 100 via `user_login`, app access token by client credentials through `ITwitchAppTokenProvider`, cadence `Twitch:LivePollIntervalSeconds`, default 300 s) and writes live coverage per channel/UTC day to `ChannelLiveDay` — the data basis for marking stream days in the emote drilldown and for A10 stage 2 (see the DECISIONS entry of 2026-08-03).

#### A.1 IRC chat listener & spam protection

- Connects via `TwitchLib.Client` to all active Twitch channels.
- Messages are split on spaces (`string.Split(' ')`) and matched against a `HashSet<string>`.
- **Spam protection:** Every emote that occurs is counted **at most once per chat message** (prevents distortion by spam copypastas).
- **Connecting and reconnecting.** Three roles are cleanly separated: the transport (`TwitchChatManager`) holds exactly one `TwitchClient`, performs joins under a throttle and makes no timing decision of its own. TwitchLib events and a periodic tick only deliver a signal when the connection is gone or suspect — several signals in quick succession coalesce into a single rebuild. A dedicated rebuild loop (`TwitchConnectionWatchdog`) waits for that signal or the tick, replaces the client where needed instead of repairing it and afterwards rejoins under a throttle outside the read loop; TwitchLib no longer reconnects by itself. Alongside that, a watchdog net detects a connection that has gone quiet when no IRC frame has arrived for a while, even when TwitchLib reports nothing — the numbers behind this (backoff curve, thresholds) are in `docs/DECISIONS.md`, not here.

#### A.2 In-Memory Aggregator & Batch Flush

- Counts are incremented in a `ConcurrentDictionary<string, int>` (key: `EmoteId`).
- A timer performs a batch flush into the PostgreSQL database every **30 seconds**.

#### A.3 7TV sync engine (hybrid: EventAPI WebSocket live deltas + periodic REST reconciliation)

> **Deviation from the original specification — history:** Originally implemented as **one shared** WebSocket connection to `wss://events.7tv.io/v3` (`ISevenTvEventClient`/`SevenTvEventClient`) with `emote_set.update` subscriptions per channel on a single connection. Investigated systematically on 2026-07-24/25 across several live tests (channels `vassilly`, `sensitron`, among others): dispatches demonstrably did **not** arrive reliably — sometimes delayed by several minutes, sometimes not at all (e.g. an emote "REITEN" added live that never arrived by dispatch, although another 7TV client (DankChat) received the update correctly). The subscriptions themselves were registered correctly on the server side (confirmed by `Ack` frame, `subscription_limit` far from exhausted). An analysis of the official 7TV browser extension source code (github.com/SevenTV/Extension, `src/worker/worker.http.ts`) revealed two differences — wildcard subscription type `emote_set.*` instead of `emote_set.update`, plus an additional channel-scoped subscription (`condition: {ctx: "channel", platform: "TWITCH", id: <TwitchChannelId>}`, survives set changes) — both retrofitted on a trial basis without measurably improving reliability. Since the REST full sync (`ISevenTvSyncService.SyncChannelAsync`) was reliable in every test, the entire WebSocket logic was removed and replaced by a periodic REST resync (**decision**, see docs/DECISIONS.md for the full course of events).
>
> **Addendum 2026-07-30:** The re-investigation [Untersuchung-7TV-WebSocket-2026-07-30.md](Untersuchung-7TV-WebSocket-2026-07-30.md) **refuted** the attribution "demonstrably unreliable on 7TV's side": the cause was two implementation errors of our own (resubscribe before the connection was established; the parser read `added`/`removed` instead of the actual wire format `pushed`/`pulled`), and the channel-scoped subscription is a presence scope on the server side that structurally does not deliver channel set updates. The WebSocket was thereupon reintroduced as a **supplement** (entry "7TV-EventAPI-WebSocket wieder eingeführt" in docs/DECISIONS.md): `SevenTvEventWorker`/`SevenTvEventClient` deliver live deltas (`emote_set.*` + `user.*`, each `{object_id}`), the periodic REST resync stays in place as mandatory reconciliation — the EventAPI has no resume/replay and drops every connection after a TTL of about 1 h. Feature flag `SevenTv:EventApi:Enabled` (default off), resync cadence `SevenTv:ResyncIntervalSeconds` (default 60 s, manually stretchable once WS operation has proven itself).
>
> **API version:** the backend (REST + read-only GQL for the Twitch→7TV user resolution + EventAPI) stays on 7TV v3, not v4. v4 exists as a GraphQL API but has no event channel (no `events.7tv.io/v4`, GQL schema without subscriptions) — the v3 EventAPI is the only live path and is not deprecated (as of 2026-07-30). **Exception since 2026-09-10 (#149):** the frontend's write surface (module D, mass-delete engine: import, restore, delete) talks to `https://7tv.io/v4/gql`, because `v3`'s alias validator rejects umlauts and `v4` fixed that — see section "Module D" and DECISIONS.md.
>
> **Resolving channel → emote set:** 7TV's REST endpoint (`/v3/users/twitch/{twitchUserId}`) accepts only the numeric Twitch user ID, not the username. `ISevenTvApiClient` resolves the Twitch username through 7TV's own GraphQL user search instead (`/v3/gql`, `users(query: ...)`, filtered to an exact hit in `connections[]` with `platform=="TWITCH"`). That already populates `Channel.TwitchChannelId` today (not only through the future Module B) — semantically the same numeric ID, only populated by a different route. **This path is older than the Twitch app registration and its stated rationale has expired:** it used to read "since no Twitch Helix API/app registration is used by design", which stopped being true on 2026-08-03, when `ITwitchAppTokenProvider` arrived with the live-coverage poll. Helix is used in several places today — `TwitchLivePollWorker`, `ChannelIdentityService`, `MyChannelsService` and the live role checks of §B.2. The 7TV resolution path itself is unchanged; only the reason given for it was wrong.

- On every join: a one-off full sync (`SyncChannelAsync`) — resolves Twitch username → 7TV emote set, reconciles all `Emote` rows (add/update/archive) against Postgres, refreshes `IEmoteMatchCache` and registers the set plus the 7TV user ID as the desired EventAPI subscriptions (`SevenTvSubscriptionRegistry`, desired-state-first).
- **Live path (feature flag):** `SevenTvEventWorker` holds exactly one EventAPI connection (`SevenTvEventClient`); subscriptions are rebuilt from the registry after **every** hello (deduplicated per `(type, object_id)` — shared sets yield one subscription), dispatches are processed strictly sequentially and applied as deltas via `ApplyEmoteSetUpdateAsync` under the `ChannelSyncGate` (followed by a full `IEmoteMatchCache` reload, no incremental cache patching). `user.update` detects set changes; heartbeat watchdog (3× the interval), op-4/7 reconnects and the ~1 h server TTL are normal cases with a gap-filling full sync after every reconnect.
- `SevenTvPeriodicResyncWorker` (its own `BackgroundService`) periodically repeats the same full sync for **all** `IsBotActive=true` channels (default 60 s) — mandatory as reconciliation alongside the WebSocket (no resume/replay at 7TV), catches set/account changes and missed dispatches and converges the subscriptions (`EnsureSubscribed` on every tick). A sync that fails for one channel is logged and skipped without affecting the other channels or the worker host.
- Cost: one 7TV REST request per active channel and resync tick plus one standing WebSocket connection — negligible at the current/foreseeable channel count, no rate limiting observed. Health: `GET /api/worker/health` returns the EventAPI state as a `sevenTv` sub-object (`disabled/disconnected/stale/connected`, staleness measured on heartbeat frames).

### Module B: Auth & dynamic role system

#### B.1 Authentication

- Twitch OAuth2 flow via the web API: `/api/auth/twitch/login` and `/api/auth/twitch/callback`.
- Requests only the basic identity (`user:read:email` or basic profile).

#### B.2 Live role check

- Twitch roles are not stored persistently in PostgreSQL.
- On a vote request the backend checks the user's roles live via the Twitch Helix API.
- Results are cached in Redis (`Auth:ModCheckCacheTtlMinutes`, default **10 minutes**) to conserve rate limits.

#### B.3 Roles and authorization filters — the binding overview

There is **no** `Role` column and no role enum. "Role" here means: one of four check methods fires. Authorization runs exclusively through `IEndpointFilter` classes in `src/EmotePurge.Api/Auth/`, never through ASP.NET Core policies (rule 6).

**The four role sources** (`Infrastructure/Services/ChannelAccessService.cs`):

| Role | Where from | Particularity |
|---|---|---|
| **Global Admin** | Config `Auth:AdminTwitchLogins` (a comma-separated scalar from env/user secret **beats** the JSON array from `appsettings.json`) | channel-independent |
| **Broadcaster** | `Channel.TwitchChannelId` against `principal.TwitchUserId` | Login comparison only as a fallback, as long as the ID has never been resolved. If the login matches but the ID does not → **rejected plus a warning in the log**: Twitch releases names again after a rename |
| **Moderator** | Helix `GetModeratedChannelLogins`, via `IModRoleCache` | cached positively as well as negatively; an `/unmod` takes up to 10 minutes to take effect |
| **7TV editor** | 7TV's `editor_of` relation | **only** read access to usage stats plus `sync-deleted`, never channel management |

**Precedence:** `CanManageChannelAsync` = admin → broadcaster → moderator. `CanViewUsageStatsAsync` = *exactly that* plus 7TV editor. Thus `CanManageChannelAsync ⊂ CanViewUsageStatsAsync` holds strictly; the only difference is the editor.

**The five filters:**

| Filter | Lets through | Rejection |
|---|---|---|
| `GlobalAdminAuthorizationFilter` | admin only | 401 without a principal, otherwise 403 |
| `ChannelManagementAuthorizationFilter` | admin, broadcaster, moderator | 400 `invalid_channel_name` · 401 · 403 |
| `UsageStatsAccessAuthorizationFilter` | + 7TV editor | as above |
| `VoteEligibilityFilter` (casting a vote) | admin/broadcaster/mod **always**, otherwise per `AllowedRoles` | 404 `vote_session_not_found` · **409 `vote_session_ended`** · 403 |
| `VoteAudienceFilter` (viewing results) | the same role logic | 404 · 403 — **no 409**: ended sessions stay visible to their target audience |

`ChannelNameValidationFilter` lives in `Validation/`, not `Auth/`: it only checks the format (`^[a-z0-9_]{4,25}$` after `ChannelName.Normalize`) and applies only where the route actually carries a `channelName`.

**Endpoint → filter mapping** (group filters resolved):

| Group | Filter of the group | Deviations of individual endpoints |
|---|---|---|
| `/api/channels` | Auth + ChannelNameValidation | `GET /{name}`, `GET /{name}/audit-log`, `POST /{name}/join`, `DELETE /{name}` → additionally ChannelManagement · **`POST /{name}/resync` → UsageStatsAccess** (deliberately the wider filter, see the decision log) with its own policy `ChannelResync` **plus** a per-channel cooldown · **`DELETE /{name}/purge` → GlobalAdmin** (the only admin endpoint outside `/api/admin`) · `GET /{name}/permissions` and `GET /mine` → **deliberately without** an authorization filter |
| `/api/channels/{name}/emotes` | Auth + ChannelNameValidation + **UsageStatsAccess** + `ExternalApi` | `POST /sync-deleted` and `POST /sync-restored` use `Bookkeeping` instead of `ExternalApi` — together with `GET /{name}/audit-log` the three endpoints with that policy |
| `/api/channels/{name}/usage-stats` | Auth + ChannelNameValidation + UsageStatsAccess + `ExternalApi` | — |
| `/api/channels/{name}/vote-sessions` | Auth + ChannelNameValidation | `POST`, `POST /{id}/end`, `DELETE /{id}` → ChannelManagement · `GET /{id}/results` → VoteAudience · `POST`/`DELETE .../votes` → VoteEligibility · `GET` (list) → **no filter**, filtered per session in the handler |
| `/api/admin` | Auth + **GlobalAdmin** | no rate limit (deliberately) and **no `ChannelNameValidationFilter`** — `POST /channels/{name}/resync` therefore returns no 400 for an invalid name |
| `/api/auth` | none | `login`, `callback`, `logout` are public; `logout` deliberately so, so that an expired session can still delete its cookie |

**Authenticated, but open to everyone logged in** — that is intent, not a gap: `GET /{name}/permissions` (reports itself what the caller would be allowed to do), `GET /channels/mine`, `GET /vote-sessions/mine`, `GET /auth/me`, the vote-session list (filtered per row in the handler) and `GET /{name}/live` (SSE events are pure "something has changed" pings without payload).

**Public without login:** `GET /api/worker/health` (minimal payload, feeds the header badge) and, since 2026-08-05, `GET /api/health` (payload-free, status code only: 200 on `connected`, otherwise 503 — the target of the container HEALTHCHECKs and of the external uptime monitor, rate-limit policy `PublicHealth`). This completes finding **Z1** from wave E in full: admin detail behind `GET /api/admin/health` (since 2026-07-31), badge payload minimal, machine endpoint payload-free.

**`AllowedRoles` in practice:** `Everyone` (1) short-circuits immediately. `Subs` (2) triggers a Helix sub check. `Mods` (8) and `Broadcaster` (16) are **never evaluated explicitly** — they are already covered by the `CanManageChannelAsync` short circuit, which gives managers voting rights independently of the flags. **`VIPs` (4) is defined but unusable**: session creation rejects it with `vips_not_supported`, because Twitch has no endpoint through which a user can report their own VIP status.

### Module C: Voting engine & net vote score

- Voting venue: voting takes place exclusively in the web dashboard (not in chat).
- Parallel sessions: allow flexible votes (e.g. "July monthly clean-up").
- Target-audience restriction (`AllowedRoles`, `[Flags]`): who may vote can be specified — `Everyone = 1`, `Subs = 2`, `VIPs = 4`, `Mods = 8`, `Broadcaster = 16`.
- **Emote subset per session (since 2026-08-01):** The creator can give the session an explicit ballot (`VoteSessionEmote` join table, `emoteIds` at creation time). Without a selection the session dynamically covers all non-archived channel emotes (the pre-existing behaviour, no join rows). A curated ballot is fixed from creation onwards; if a member drops out of the 7TV set mid-session it stays visible with a badge (votes preserved), further votes on it are blocked.
- The score (since 2026-08-01, previously `f(Chat-Nutzung) + (Keep − Delete)`):

$$\text{Score} = \text{Keep-Votes} - \text{Delete-Votes}$$

  Chat usage **no longer** enters into it — the mods already consume the usage data while curating the ballot, and normalised 0–100 usage points dominated raw ±N votes. Usage stays available to managers as a context column (`TotalUseCount`, `null` for non-managers). Results sort ascending (delete candidates first), tiebreaker: more total votes. `VoterCount` (distinct voters) qualifies thin participation in the UI.

### Module D: Angular dashboard (overview, usage stats, voting UI, mass-delete engine)

> **Implementation status:** Fully implemented (2026-07-26) — full details/gotchas in the decision log in CLAUDE.md, only the concretised specification here.

- **Pages/routes (`web/src/app/app.routes.ts`):** `/welcome` (public, guard-less landing page — the entry point for anonymous visitors, e.g. via a shared link), `/login`, `/` (overview, `homeGuard` — anonymous visitors end up on `/welcome` instead of the login form; when logged in: one's own tracked **and** untracked moderated channels, `GET /api/channels/mine` — the former admin section "all tracked channels + join form" on the same page was removed on 2026-07-31 in favour of the `/admin` area), `/admin/*` (global admin area, `adminGuard`: monitoring, channel list, audit log), `/my-votings` (one's own cross-channel voting history, `authGuard` — deliberately a sibling route of the channel workspace routes, not nested underneath them, since it is not bound to any single `channelName` route value), `/channels/:channelName/usage-stats` (`usageStatsAccessGuard` — a real permission for the channel, not just a login: admin/broadcaster/live moderator or 7TV editor), `/channels/:channelName/vote-sessions` (list, `authGuard` — login required only, the list itself has no session-specific role restriction), `/channels/:channelName/vote-sessions/:sessionId` (detail, `voteSessionAccessGuard` — login **and** genuine membership of the target audience of this particular session). **Anonymous share links were removed on 2026-07-27** (an explicit user decision, reversing an earlier design decision): voting pages were originally reachable without a login requirement by design, but now require a login throughout — see the decision log in CLAUDE.md for the full course of events.
- **Grid instead of list:** With up to ~1,000 emotes per channel a single-column list would be impractically long to scroll. Usage stats and voting results therefore render the emotes as a responsive grid (2–8 columns depending on window width) — `CdkVirtualScrollViewport` virtualises **rows** of several cards each (row chunking), not individual emotes; the column count reacts to resizing live.
- Multi-selection (checkbox + shift-click range selection) identical on both grid pages.
- Virtual scrolling: use of the Angular CDK `CdkVirtualScrollViewport` for smooth rendering.
- Direct GraphQL execution: write tokens stay locally in the browser (`sessionStorage`).
- Batch delete queue: on the delete command the frontend sends the mutation directly from the browser to `https://7tv.io/v4/gql` (`v3` until 2026-09-10, see #149 and DECISIONS.md — `v4` dropped the `action` enum and gives each operation its own field under `emoteSets { emoteSet(id:) { … } }`):

```graphql
mutation RemoveEmote($setId: Id!, $emoteId: Id!) {
  emoteSets {
    emoteSet(id: $setId) {
      removeEmote(id: { emoteId: $emoteId }) {
        id
      }
    }
  }
}
```

- **Rate limiting: sequential, self-regulating (since 2026-08-01).** Starts at a delay of ~275 ms between requests; 7TV's actual quota for the `emote_set_change` bucket is not public (it lives in 7TV's database, not in the open-source tree), so it is learned at runtime from the first rejection. A rate-limited mutation does **not** count as failed: 7TV answers with HTTP 200 and the details in `errors[0].extensions` (`code: "RATE_LIMIT_EXCEEDED"`, `headers["x-ratelimit-emote_set_change-*"]`), the emote is retried after the reported `reset` (max. 5 wait cycles) and the pacing of the rest of the run is set to `window / quota × 1.1`. The identically named response headers are **not** readable via CORS (`Access-Control-Expose-Headers` lists only `x-access-token`, `x-request-id`, `x-auth-failure`), so proactive pacing is impossible in the browser — details and sources in [DECISIONS.md](DECISIONS.md).
- **Backend sync — deviation from the original specification:** The frontend reports deleted IDs to the C# API via `POST /api/channels/{channelName}/emotes/sync-deleted` (channel-scoped), not via the originally sketched global path `POST /api/emotes/sync-deleted` — consistent with every other channel-related endpoint. The route group sits behind `UsageStatsAccessAuthorizationFilter` (not `ChannelManagementAuthorizationFilter` — 7TV editors of the channel may archive as well, see that class's comment for the current endpoint list). It marks the affected `Emote` rows as `IsArchived = true` (soft archive, no hard delete — see the decision log in CLAUDE.md); the 1-minute `SevenTvPeriodicResyncWorker` remains the actual safety net.
- **Voting UI:** Thumbs up/down per emote (keep/delete), one's own vote is highlighted (`MyVote`, delivered along with the results — since the login requirement of 2026-07-27 always a real user, no anonymous `null` case any more); session creation/ending/deletion is visible only to managers (`ChannelManagementAuthorizationFilter`), the results view sits behind `VoteAudienceFilter` (login + membership of the session's target audience, still visible to the original target audience after the session has ended). **Two creation entry points (since 2026-08-01):** the inline form on the voting list (produces "whole set" sessions) and "Put up for vote" from the multi-selection of the usage-stats grid (`CreateVoteSessionDialog`, which passes the selection on as a fixed ballot).
- **Internationalisation (i18n):** Transloco (`@jsverse/transloco`), two languages (`de`/`en`), locale files under `web/public/i18n/{de,en}.json`. `web/src/app/core/i18n/language.service.ts` (`LanguageService`) holds the active language as a signal, persists the choice in `localStorage` and, without a stored preference, falls back to the browser language or German (the primary target audience). Switching at runtime without a reload.
- **Stable error-code contract:** On errors the Api returns exclusively language-neutral codes (`{ errorCode = "..." }`), never finished text — translation happens exactly once, in the frontend. The chain: `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` (constants) → `web/src/app/core/i18n/api-error.ts` (`apiErrorTranslationKey`, maps `errorCode` to an `errors.api.<code>` translation key, with a status-code fallback for responses without a body, e.g. a bare `Forbid()`) → `errors.api.*` entries in both locale files. The contract is kept in sync manually across these three places by design (no generator); `web/src/app/core/i18n/api-error.spec.ts` reconciles the known codes against both locale files.
- **Pagination:** `PagedResult<T>` (`src/EmotePurge.Core/Services/PagedResult.cs`, a `record` with `Items`/`Page`/`PageSize`/`TotalCount`/computed `TotalPages`) as the generic paging envelope for list endpoints (among others the vote-session lists). `web/src/app/shared/pagination/pager.ts` (the `Pager` component) renders next/previous + "page X of Y" on top of it, reused on all paginated list pages.

### Module Admin: Global admin area

Not part of the original specification, but in scope a module of its own: a vertical slice from the entity through to the page, reachable under `/admin/*` behind the `adminGuard`. Access is governed exclusively by the allowlist `Auth:AdminTwitchLogins` — channel-independent, no Twitch role.

**Eight endpoints**, all in the `/api/admin` group behind `GlobalAdminAuthorizationFilter`:

| Endpoint | Purpose |
|---|---|
| `GET /health` | Worker health snapshot, the authenticated sister of the public `/api/worker/health` |
| `GET /live` | its own SSE stream (implemented in `LiveEndpoints.OpenAdminAsync`, registered in `AdminEndpoints` — only that way does it inherit the group's admin filter) |
| `GET /channels` | all tracked channels together with aggregates (emote and vote-session counts) |
| `POST /channels/{name}/resync` | trigger a 7TV full sync for one channel |
| `GET /users` | all users with a derived token status |
| `POST /users/{id}/revoke-sessions` | sets `User.SessionsValidFromUtc` — invalidates existing cookies on the server side |
| `POST /users/{id}/invalidate-role-cache` | deletes the user's `modcheck:`/`subcheck:`/`7tveditor:` keys from Redis without waiting out the 10-minute TTL |
| `GET /audit-log` | paginated history of privileged actions |

**Audit log.** Every privileged action writes an `AuditLogEntry` row with the actor, `Action` (one of the `AuditActions` constants), an optional channel reference, a target and free-form `DetailsJson`. Logged are channel join/leave/purge/resync, channel rename and merge, vote-session creation/ending/deletion, the three emote sync actions (`emotes.syncDeleted`, `emotes.syncRestored`, `emotes.syncImported`), session revoke and role-cache invalidation.

**`DELETE /api/channels/{name}/purge` is the only admin endpoint outside the group** and carries its `GlobalAdminAuthorizationFilter` individually. It deletes the channel along with the cascade (emotes, usage stats, vote sessions) and deliberately does **not** sit behind `ChannelManagementAuthorizationFilter`: that filter's moderator branch depends on a cache up to 10 minutes old, and a freshly de-modded user could thereby still destroy an entire channel.

**Two known deviations:** The `/api/admin` group registers **no** `RequireRateLimiting` (deliberately — admins are a closed, small set) and **no** `ChannelNameValidationFilter`. The latter means: for a formally invalid channel name `POST /channels/{name}/resync` does not answer with `400 invalid_channel_name` as everywhere else, but runs into the normal not-found path.

## 5. Database model (Entity Framework Core schema)

> **Deviation from the original specification (`Emote`):** The 7TV ObjectID is **no longer** the primary key; it lives in `SevenTvEmoteId`. Reason: one 7TV emote can be active in several channels at the same time; since `Emote` is a row per channel (`ChannelId` column), the 7TV ID as a global PK would have caused a primary-key collision for shared emotes. Instead `Id` is an internal Guid PK, and a unique index on `(ChannelId, SevenTvEmoteId)` ensures uniqueness per channel. `UsageStat.EmoteId` references this internal PK.
>
> In addition, `UsageStat` has a unique index on `(EmoteId, Date)`, so that the 30-second batch flush maintains exactly one aggregated row per emote and day instead of many individual rows. `Date` is typed as `DateOnly`/Postgres `date` (not `DateTime`/`timestamptz`) — that makes the "UTC calendar day" character of the column type-safe instead of merely a comment, and the index carries `UseCount` as an include column so that time-range sum queries (`SUM(UseCount) WHERE Date BETWEEN from AND to`) can in principle run as an index-only scan — `BotUseCount` (see DECISIONS 2026-09-01) is deliberately **not** in the include, because no aggregate query reads it. `SharedChatUseCount` (see DECISIONS 2026-09-06) is **not** in the include for the same reason. Since move 2 of #73 (DECISIONS 2026-09-08) the productive read queries in `UsageStatQueryService` again sum and filter exclusively over `UseCount`, so that the index-only scan takes effect again by itself; the transitional period in which the sum ran over `UseCount + SharedChatUseCount` is thereby over, and the index had to be neither extended nor rolled back for it. This daily granularity is deliberately the basis for flexible dashboard time ranges (day/week/month/custom) — a time range is simply a sum over the matching daily rows, no finer granularity or rollup table needed (see the decision log in CLAUDE.md).
>
> **The entities are implemented:** `Channel`, `Emote`, `UsageStat`, `User`, `VoteSession`, `VoteSessionEmote`, `Vote`, `AuditLogEntry`, `ChannelLiveDay` (plus `AllowedRoles`/`VoteType` as enums and `AuditActions` as a constants class) live completely under `src/EmotePurge.Core/Entities/` and have been applied through migrations — the state that held until 2026-07-25 ("only module 1 implemented") is obsolete. In addition, `ChannelName.cs` lives there — not an entity but a static normalisation helper class (`Normalize(string) => value.Trim().ToLowerInvariant()`) that pins the silent invariant "`Channel.ChannelName` is always lowercase/trimmed in the DB" down in one place.
>
> **`VoteSessionEmote`** (migration `20260801005055`) is the membership row of the explicit ballot: `(VoteSessionId, EmoteId)` as a composite key. The semantics are deliberately asymmetric — a session **without** such rows dynamically covers all non-archived channel emotes (the behaviour before the subset redesign and at the same time the "whole set" mode), a session **with** rows has a ballot fixed at creation time that is never edited afterwards. As with `Vote`, `EmoteId` is the internal `Emote` Guid, not the 7TV ID.
>
> **`AuditLogEntry`** (migrations `20260731101655` and `20260731134345` for the `ChannelName` index) logs privileged actions: `OccurredAtUtc`, `ActorTwitchUserId`/`ActorLogin`, `Action` (one of the ten values from `AuditActions`, e.g. `channel.purge`, `voteSession.delete`, `user.revokeSessions`), optionally `ChannelName`, `TargetType`/`TargetId` and a free-form `DetailsJson`.
>
> **Columns that were added later and are easily overlooked:**
>
> | Entity | Column | Migration | Purpose |
> |---|---|---|---|
> | `VoteSession` | `HideResultsUntilEnd` | `20260801120155` | Secret ballot — tallies are withheld on the server side until the session ends, not merely hidden in the frontend |
> | `User` | `TwitchRefreshToken`, `TwitchAccessToken`, `TwitchAccessTokenExpiresAtUtc`, `TwitchTokenScopes` | `20260730160215` | server-side token refresh; the two token columns are stored **encrypted** (`AesGcmTokenCipher`, key from `Auth:Twitch:TokenEncryptionKey`) |
> | `User` | `SessionsValidFromUtc` | `20260729222651` | logout / session revoke that takes effect on the server side: older cookies count as invalid |
> | `Channel` | `ActiveEmoteSetCapacity` | `20260801183949` | slot limit of the active 7TV set, `null` = 7TV reported none (never assume 1000 — subscribers have larger sets). Written only together with `ActiveEmoteSetId` in the REST full sync, never in the EventAPI delta |
> | `Channel` | `TrackingResumedAt` | `20260801183949` | point in time of the last join that **reactivated** the channel. `CreatedAt` overstates the coverage because `LeaveAsync` keeps the row — "we have been counting since" is `TrackingResumedAt ?? CreatedAt` |
> | `Channel` | `LastSyncedAtUtc` | `20260801195038` | when a REST full sync last **ran through successfully**, regardless of whether it changed anything. Deliberately separate from `MAX(Emote.LastSyncedAt)` (= last inventory change): emote rows are stamped only on a real change, so a channel syncing successfully every minute with a static set would otherwise read as "last synchronised three days ago". Written only in the REST path, never in the delta path, and never part of change detection |
> | `Emote` | `FirstSeenAt` | `20260801191203` | when the emote entered the 7TV set, since 2026-08-03 taken from `EmoteSetEmote.addedAt` of the v4 GraphQL API (the v3 `timestamp` turned out to be the emote's upload date) — which also makes it retroactively correct for pre-existing rows, unlike a "first seen" stamp. `null` = unknown, **never** "new". Only the REST sync writes it (correct-on-deviation, `null` never overwrites; the dispatch path decides via the ChangeTracker and stamps `UtcNow` only on `push`), and the correction does not count as an inventory change |
>
> `AllowedRoles` is a `[Flags]` enum with **five** values: `Everyone = 1`, `Subs = 2`, `VIPs = 4`, `Mods = 8`, `Broadcaster = 16`.
>
> **Second deviation (`Channel.TwitchChannelId`):** it is `string?` (nullable) instead of non-nullable — since the column has a unique index, a non-nullable default (`""`) would have triggered a unique-constraint violation on the second channel created (an empty string counts for unique indexes, NULL does not). It stays `null` until it can be resolved. It is meanwhile (module A.3) populated at channel join through 7TV's GraphQL user search, not only through the future module B (Twitch OAuth) — see the module A.3 section.

The complete field lists for all entities are directly in `src/EmotePurge.Core/Entities/` (`Channel.cs`, `Emote.cs`, `UsageStat.cs`, `ChannelLiveDay.cs` — live coverage per channel per UTC day, since 2026-08-03, see DECISIONS —, `User.cs`, `VoteSession.cs`, `Vote.cs`) — deliberately not mirrored here, since that was exactly the reason this section went stale last time. What you have to know when reading the database are the two invariants described above:

```csharp
public class Emote
{
    public string Id { get; set; } = Guid.NewGuid().ToString(); // internal PK, NOT the 7TV ObjectID

    // 7TV ObjectID (24-hex string). Unique only per channel via the unique index
    // on (ChannelId, SevenTvEmoteId) — the same 7TV emote can be active in
    // several channels at the same time.
    public string SevenTvEmoteId { get; set; } = string.Empty;
    // ... ChannelId, Name, ImageUrl, IsArchived, LastSyncedAt, Channel, UsageStats
}

public class UsageStat
{
    public long Id { get; set; }
    public string EmoteId { get; set; } = string.Empty; // FK to Emote.Id (internal PK)

    // UTC calendar day, Postgres `date` (not `timestamptz`) — unique index (include UseCount)
    // together with EmoteId.
    public DateOnly Date { get; set; }
    public int UseCount { get; set; }
    public int BotUseCount { get; set; } // bots in the channel's own room, see DECISIONS 2026-09-01
    public int SharedChatUseCount { get; set; } // foreign rooms of a shared-chat session, see DECISIONS 2026-09-06
    // ... Emote
}
```

## 6. Docker topology

> **Deviations from the original specification:** `redis:7-alpine` → `redis:7.2-alpine` (licence reason, see section 2). `depends_on` uses `condition: service_healthy` instead of a plain list, along with healthchecks for `postgres`/`redis` — without that, `api`/`worker` would start before the database accepts connections at all and crash on the first access. The concrete Dockerfiles live under `src/EmotePurge.Api/Dockerfile` and `src/EmotePurge.Worker/Dockerfile` (multi-stage build: SDK image for build/publish, slim runtime image for `final`, for the Api additionally a `web-build` Node stage for the Angular build, see module D).

There are two compose files, and no YAML is mirrored here any more — the embedded copy went stale for exactly the reason that it was a copy. For the complete, current content the file itself is the source of truth in each case.

### 6a. Local (`docker-compose.yml`)

For local development/tests: `docker compose up -d --build` builds `api`/`worker` from the repo state (`build:` section, no prebuilt image). Started with `redis`, `postgres`, `api`, `worker` in the shared `emotepurge-network` bridge network. A fifth service, `harness` (#69), carries `profiles: ["harness"]` and therefore never starts with `up` — only deliberately via `docker compose --profile harness run --rm harness <channel> [--days <n>] [--diagnostic]`, or, to recompute an existing run's report against the current code and a current live snapshot without touching the archive (#119), `docker compose --profile harness run --rm harness <channel> --report-only <file>` (see DECISIONS).

### 6b. Production (`docker-compose.prod.yml` + `.github/workflows/publish.yml`)

Runs on a VPS next to an existing, independent app, imported as a Portainer stack (`docker-compose.prod.yml` is the file that lives on GitHub for that purpose). On every push to `main` (after a green `test` and `test-web` job) `.github/workflows/publish.yml` builds both images and pushes them to `ghcr.io/emotepurge/emotepurge-{api,worker}:latest` (additionally tagged with the commit SHA); a redeploy of the Portainer stack pulls `:latest` again.

**Differences local vs. production:**

| Aspect | Local (`docker-compose.yml`) | Production (`docker-compose.prod.yml`) |
| :--- | :--- | :--- |
| `api`/`worker` images | `build:` from the local repo state | `image: ghcr.io/emotepurge/emotepurge-{api,worker}:latest`, built by CI |
| Host port `api` | `127.0.0.1:8080:8080` | `127.0.0.1:4300:8080` — port 8080 on the VPS is already taken by the other app |
| Host port `postgres` | `127.0.0.1:5432:5432` | `127.0.0.1:5433:5432` — likewise, its own isolated Postgres instance instead of sharing the other app's |
| Host port `redis` | `127.0.0.1:6379:6379` | `127.0.0.1:6380:6379` |
| TLS/reverse proxy | none, direct HTTP access to `localhost:8080` | A host-native (not containerised) reverse proxy in front of the loopback port terminates TLS for `emotepurge.app` and sets `X-Forwarded-Proto`/`-For`; `ForwardedHeadersMiddleware` in `Program.cs` trusts it with empty `KnownIPNetworks`/`KnownProxies`, since the container is reachable exclusively through the locally bound port |
| `Auth:Twitch:RedirectUri` | `http://localhost:8080/api/auth/twitch/callback` | `https://emotepurge.app/api/auth/twitch/callback` |
| `dataprotection-keys` volume | present — deliberate parity with prod, so that this path is tested locally at all | present — without a persisted key ring every container restart would throw all logged-in users out of the cookie session |

Deliberately **not** different: `redis` runs in both files with `--maxmemory 256mb --maxmemory-policy allkeys-lru`. Besides the role/health cache, Redis here also carries `channel:bot:commands`; an uncontrollably growing Redis would therefore take the bot control down with it, and a locally unlimited Redis would have left exactly the path that matters untested.

Configuration is done in both cases through a `.env` file at the repo root (`POSTGRES_USER`, `POSTGRES_PASSWORD`, `REDIS_PASSWORD`, `TWITCH_CLIENT_ID`, `TWITCH_CLIENT_SECRET`) — template in `.env.example`, `.env` itself is git-ignored.

`docker-compose.prod.yml` also carries the `harness` service from 6a, here on the same worker image (`ghcr.io/emotepurge/emotepurge-worker:latest`) instead of a second image, likewise reachable only via `--profile harness run --rm harness <channel> [--days <n>] [--diagnostic]` (or `--report-only <file>`, #119) — never through the normal stack redeploy.

## 7. Local development & debugging

For debugging `EmotePurge.Api`/`EmotePurge.Worker` directly in VS Code (breakpoints, F5):

- `.vscode/launch.json` contains the `coreclr` launch configs `Api` and `Worker` as well as a compound config `Api + Worker` for debugging both processes together; `.vscode/tasks.json` builds beforehand in each case (`build-api`/`build-worker`).
- These configs use the `localhost` connection strings from `appsettings.json`, so `docker compose up -d postgres redis` must be running locally first.
- The `Api` launch config binds to `http://0.0.0.0:8080` (`ASPNETCORE_URLS`), matching the port that the productive `api` container exposes. This does **not** match the locally registered Twitch OAuth redirect (`http://localhost:5151/api/auth/twitch/callback`) — for login/frontend work, run `dotnet run --project src/EmotePurge.Api` instead (port `5151`).
