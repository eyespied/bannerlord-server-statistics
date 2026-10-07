# Bannerlord Server Statistics

A reusable, server-only Bannerlord statistics mod and small HTTPS ingestion backend. It collects completed live rounds, including score, kills/deaths/assists, damage and weapon styles, accuracy, team damage, first kills/deaths, MVPs and time alive. No gameplay commands, ranked system, MMR, custom maps or community-specific configuration are included. Clients do not install the mod.

The game module is named **CIStatistics** internally. Keep that folder/module ID; the repository name does not change the game's ID. Source is extracted from the existing SkirmishPlay collector, with no minimum player count and a durable whole-round outbox.

## What statistics are recorded?

| Group | Recorded values |
| --- | --- |
| Round context | Match UUID, round number, map ID, game type, UTC start/end timestamps. |
| Player context | Native platform/Steam ID, display name, team side, class group, culture. |
| Scoreboard | Per-round score, kills, deaths and assists; rounds played/won. |
| Highlights | Native MVP flag, first enemy-player kill and first player death. |
| Combat styles | Kills, deaths and damage split into on-foot melee/ranged/throwing and mounted melee/ranged/throwing. Mounted damage is mounted melee, with separate mounted ranged/throwing fields. |
| Accuracy | Non-throwing missile shots/hits/headshots and separate throwing shots/hits/headshots, including mounted attacks. Hits are positive unblocked enemy-human damage events; horse/friendly/shield hits are excluded. |
| Mounts and techniques | Horse damage, enemy horse kills, successful damaging kicks and passive/couched attacks. |
| Friendly fire | Team kills, hits/damage caused, hits/damage received and deaths by teammate. |
| Uncredited deaths | Self-kills and deaths without a qualifying player killer, stored as suicides (including environmental deaths). |
| Survival | Milliseconds alive and closed spawn intervals; surviving spawns close at round end. |
| Participation | Infantry/Ranged/Cavalry and culture per round. Horse Archer counts as Cavalry. |

Your website can derive K/D, win rate, per-round averages, MVP rate, hit/headshot percentages, class/culture breakdowns and average lifespan. Scoreboard counters are round deltas; damage is rounded to integer HP. Accuracy counts hit events, so unusual multi-hit projectiles can exceed one hit per shot. Warmup is excluded and only completed rounds upload. No MMR, rank, donation/member data or gameplay controls are collected.

Type **`/stats`** (alias `!stats`) in all or team chat for a private readiness/recording/upload status reply. The command itself is suppressed from native forwarding; other players receive neither it nor the reply. Status includes active round, warmup/waiting/config issues, queued/rejected reports and last upload result, with no credentials or internal paths. Three-second cooldown per player.

## 1. Prepare your own backend

Use your own Supabase project. Apply `backend/schema.sql` in its SQL editor as database owner. It creates isolated statistics tables and an immutable ingestion function. It does not grant anonymous/authenticated clients database access. The generic configuration collects every live day; optional scheduled dates use `ci_statistics_event_calendar` and a server's `scheduled_only=true` flag.

Install Node 22+ on your backend machine:

```sh
cd backend
npm ci
cp .env.example .env
chmod 600 .env
# Edit SUPABASE_URL and SUPABASE_SECRET_KEY in .env, then:
npm start
```

Put this service behind an HTTPS reverse proxy on your own domain. Default listen address is `127.0.0.1:3100`; set HOST/PORT for your hosting platform. Configure the proxy's request limit to 1 MiB and a rate limit on `/ingest`, with TLS verification enabled. Run it through your platform's service manager so it restarts automatically. `/health` checks that the process is running, not database readiness.

The **backend-only** Supabase secret remains on this machine. The game server receives a separate random credential with only upload access.

## 2. Issue a game-server credential

```sh
cd backend
node --env-file=.env provision.mjs "My battle server" https://stats.example.org/ingest
```

This writes `<server-uuid>-stats_config.json` with owner-only permissions; it does not print the token. Copy it to the game server as `Modules/CIStatistics/stats_config.json` and restrict its permissions. Never commit it. Provision a separate credential for every server/operator. To revoke one, set its `ci_statistics_servers.enabled=false` in your database administration tool. To replace a stolen credential, revoke it and provision another; old report history remains.

## 3. Build and install the collector

Install .NET SDK and matching Bannerlord game assemblies on the build machine. This build targets .NET 6 to match Bannerlord dedicated-server runtime; check your server version before upgrading.

```powershell
dotnet build collector/src/CIStatistics/CIStatistics.csproj -c Release
# Optional different game location:
dotnet build collector/src/CIStatistics/CIStatistics.csproj -c Release '-p:BannerlordPath=D:\Games\Bannerlord'
```

Copy `collector/dist/` into the server's `Modules/CIStatistics/`, add the private config next to `SubModule.xml`, then enable CIStatistics after Native and Multiplayer in the server's module list. Restart that instance during maintenance. Release ZIPs contain only this mod's DLL/manifest/example; no TaleWorlds DLLs. A disabled example config is included.

| Config | Meaning |
| --- | --- |
| Enabled | Collection switch, false in example. |
| IngestUrl | Your backend's HTTPS `/ingest` URL. No redirects followed. |
| IngestToken | This server's random upload credential. Not a Supabase key. |
| QueueDirectory | Writable durable outbox. Use an absolute path/persistent volume in Docker. |

Config is read once per process. Uploads retry every 15 seconds, with 20-second HTTP timeout. Completed reports are flushed to disk first and replayed on restart. Success/duplicate/ignored responses remove queued JSON; malformed/conflicting reports remain `.json.rejected` for inspection. Other errors retain the report. Monitor disk usage: the queue is uncapped. Losing its volume loses pending reports. An unfinished round is lost if the game crashes before completion.

## Results and security

Backend ingestion is `POST /ingest`, bearer token plus schema-version-1 round JSON. Only the backend talks to Supabase. Tokens are SHA-256 hashed in the database; a token cannot select arbitrary tables, read other server data, edit members or overwrite submitted rounds. Server identity is derived from the credential, and unique `(server,match UUID,round)` makes retries idempotent. Reports are bounded to 1 MiB, 600 players, typed numeric limits and timezone-qualified timestamps. Database rate limit: 120 new reports per server per minute.

`ci_statistics_rounds` contains immutable reports. `ci_statistics_event_days` provides date/round/player totals to a trusted backend. Public display is a separate integration: aggregate the report's `players` arrays into your own website, exposing only intended public stats. No public Supabase policies are required. Dates use the round's start date in Europe/London; change this consciously in your backend schema if your community uses another calendar timezone.

Anyone possessing a server token can fabricate reports for that server. This credential limits database reach, but cannot prove honest combat or protect a compromised backend/database admin. Keep tokens private, revoke leaks and add perimeter rate limits. Do not give game operators service-role keys.

## Verification and limitations

```powershell
dotnet run --project collector/tests/CollectorTests.csproj -c Release
```

Durable outbox failure/restart replay, conflict retention and per-round counter deltas are tested. The CI integration additionally tested actual PostgreSQL transactions, duplicate reports, server scope, revocation, SQL permissions, London midnight/DST and website validation/build.

Supported modes must expose `MultiplayerRoundController`; continuous siege/deathmatch requires a separate lifecycle implementation. Actual multiplayer round verification of this extracted module is still required before relying on it for production event statistics. No running game server is automatically changed by these scripts.
