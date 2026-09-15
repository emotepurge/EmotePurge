# Security Policy

## Supported versions

Emote Purge has no versioned releases. Production (`emotepurge.app`) runs whatever is currently
built from `main` and published as GHCR images. There is no older supported line to patch — a fix
means a change to `main` followed by a deploy.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting rather than a public issue:

[github.com/emotepurge/EmotePurge/security/advisories/new](https://github.com/emotepurge/EmotePurge/security/advisories/new)

This keeps the report private between you and the maintainer while a fix is worked out.

This is a hobby project maintained by one person, so there's no SLA — but reports are taken
seriously and a response should be expected within a few days on a best-effort basis.

## Scope

In scope: the EmotePurge application itself (API, Worker, frontend) and its Docker deployment.
Out of scope: vulnerabilities in third-party services it depends on (Twitch, 7TV) — please report
those to the respective vendor instead.
