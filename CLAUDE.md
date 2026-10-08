# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Monze is a .NET 10 Mezon bot covering meetings, welcome, AI, setup and roles. It is built as a modular monolith on `Mezon.Net.Sdk`.

- **Scope:** this file applies to `Monze/` and everything below it. For cross-repository work, the workspace rules in `../CLAUDE.md` stay authoritative.
- **Stay in this repo:** keep changes here unless the user explicitly asks for an upstream or cross-repository change.
- **Protect existing work:** inspect `git status` and `git diff` before editing. Don't reset, check out or rewrite unrelated work.

## Commands

Restore is locked: `Directory.Build.props` sets `RestorePackagesWithLockFile` and `RestoreLockedMode`, and the `packages.lock.json` files are committed. The build script uses `--no-restore`, so restore first. Typical Release checks:

```powershell
dotnet restore Monze.slnx
./scripts/build-release.ps1 -BuildTests
dotnet test Monze.slnx --configuration Release --no-build
pwsh scripts/run-test-campaign.ps1            # quick campaign: containers, every tier, one REPORT.md
./scripts/inspect-db.ps1 -ClanId <clan> -ChannelId <channel> -Assert
git diff --check
```

### Packages
- Changing a package version needs a lockfile update (e.g. `dotnet restore --force-evaluate`).
- Keep the SDK package set in sync (see SDK and Workspace Boundaries below).

### Tests
- Single test: `dotnet test Monze.Tests/Monze.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~CommandHelpTests"`; database tests live in `tests/Monze.Tests.Integration` (e.g. `--filter "FullyQualifiedName~PostgresMeetingCycleTests"` with `MONZE_TEST_POSTGRES` set).
- **Database tests (`[DbFact]`) only use the guarded campaign PostgreSQL in `MONZE_TEST_POSTGRES`** (loopback, port other than 5432, database `monze_*`, `cluster_name` starting `monze-test-`); `MONZE_TEST_POSTGRES_ALT` is the second server. Without it they are reported as skipped; in a strict campaign (`MONZE_CAMPAIGN_STRICT=1`) they fail. Tests never read `appsettings*.json`, and those files are stripped from every test output.
- Redis tests (`[RedisFact]`) use `MONZE_REDIS_CONNECTION` under the same guard (loopback, not port 6379). `[DockerFact]` tests run only in a strict campaign.
- Test projects: `Monze.Tests` (unit, contract, inventory), `tests/Monze.Tests.Property` (CsCheck generators), `tests/Monze.Tests.Integration` (database and Redis), `tests/Monze.Tests.E2E` (the real host on the offline Mezon simulator in `tests/Monze.Simulator`), `tests/Monze.Campaign` (component, load, k6, capacity, chaos and soak runner) and `tests/Monze.TestReport` (report builder). Shared helpers are in `tests/Monze.Testing`.
- Every known-defect marker (`KnownDefect.ExpectFailure*`, `KnownGap`, `PropertyResult.Known`) must be registered in `tests/traceability/expected-gaps.json`; `[Req]` ids must exist in `tests/traceability/requirements.json`. The inventory tests enforce both.

### Test campaign
- `pwsh scripts/run-test-campaign.ps1` runs the quick profile (about 15–30 minutes); `-Full` adds every micro benchmark, the full load matrix, k6, all chaos scenarios and a 10-minute soak; `-Full -Soak` makes the soak `-SoakMinutes` long (default 120, needed for G4); `-Tiers a,b` selects tiers; `-Deep` scales the property generators.
- It starts throwaway containers from local images (`--pull never`) labelled `monze.campaign=<id>` on ports 55432/55433/56379 and removes them at the end. It never touches the PostgreSQL service on 5432 or `mezube-redis-1`.
- The single report is `docs/test-artifacts/<stamp>-<profile>-campaign/REPORT.md` with `campaign-summary.json`; `raw/` next to it is git-ignored. Exit codes: 0 pass, 1 failure, 2 blocked or not run, 3 harness or build error, 4 redaction violation.
- Chaos runs its own durable PostgreSQL and Redis containers, because it pauses, stops and kills them.

### Scripts
- `./scripts/inspect-db.ps1`:
  - Loads `bin/Release/net10.0/Npgsql.dll`, so build Release first.
  - Reads `Monze:Postgres` from `appsettings.json`, then from `appsettings.secrets.json`.
  - `-Assert` requires migration `018_remove_community_features`.
- Other helpers: `cleanup-live-test.ps1 -ClanId -ChannelId -UserId`, `inspect-meeting-delivery.ps1`, `probe-agent-sse.ps1`, `probe-ai-contract.ps1`.

### Migrations
- Run `dotnet run --project Monze.csproj -- migrate` (launch profile "Monze (migration)") before every runtime deployment.
- Startup (`StartupSchemaValidator`) only validates the schema and never applies migrations itself.

### Benchmarks
- Hot path: `dotnet run -c Release --project Monze.Benchmarks -- --filter "*HotPath*"`.
- Soak: `dotnet run -c Release --project Monze.Benchmarks -- --soak --duration-seconds <N> --report <path>`.

## Architecture

The solution `Monze.slnx` targets net10.0 throughout.

### Projects
- **`Monze.Domain`:** domain values, parsers, rules and calculations. It must not reference PostgreSQL, Redis, Mezon.Net or hosting; today it references nothing.
- **`Monze.Application`:** use cases, service orchestration, command outcomes, ports and business policies. It references only Domain.
- **`Monze.Infrastructure`:** PostgreSQL repositories, the Redis cache, external HTTP clients, persistence adapters and migrations. The migrations are 28 embedded SQL files, each checked by SHA256 and applied under `pg_advisory_lock`.
- **`Monze.csproj` (root project):** the Generic Host, Mezon.Net event adapters, bounded workers and runtime composition.
  - `Hosting/`: the `MonzeBot` partial classes for connection, ingress, inboxes, outbox and routing.
  - `Features/`: Ai, Avatar, Meeting, Roles and Welcome.
  - `Infrastructure/` folder: the Agent transcript HTTP client and bounded HTTP helpers. This is separate from the `Monze.Infrastructure` project.
  - `Ui/`: message, embed, button and form builders, plus interaction identifiers. Keep UI text and command names centralized here.

### Supporting folders
- `Monze.Tests` and `tests/`: the test projects and campaign tooling listed under Tests.
- `Monze.Benchmarks`: BenchmarkDotNet microbenchmarks. Their results are isolated evidence, not proof of production capacity.
- `scripts/`: Release build, database inspection and approved dev cleanup helpers.
- `docs/`: architecture, performance, capacity gates, database verification, commands and SDK handoff records. Most of it is in Vietnamese.

### Runtime
- `Hosting/MonzeBot.cs` uses WebSocket transport unless `Mezon:Transport=Tcp`.
- It sets `AgentEventUrl` from `Mezon:AgentBaseUrl`.
- SDK message history is kept in `SqliteMessageStore` under `Monze:SqliteDirectory` (default `data`).
- Commands use the prefix `*` and the root `monze`. Buttons go through `router.OnButton`.
- AI uses `OpenAiCompatibleProvider` when `Monze:Ai:BaseUrl` and its API key are set (default model `gpt-4o-mini`).
- Redis is optional; without it Monze falls back to PostgreSQL.

### Configuration precedence
- `Program.cs` adds `appsettings.json`, `appsettings.{env}.local.json` and `appsettings.secrets.json` after the host defaults. JSON values therefore override environment variables such as `Monze__Postgres`.
- The default environment is Production. `appsettings.Development.local.json` loads only with `DOTNET_ENVIRONMENT=Development`.
- Transport rate limits are read from `Mezon:RateLimit:*`: 500 requests a minute by default and, unless set, a sixtieth of that per second, so the budget is paced evenly. `appsettings.example.json` puts them under `Monze:RateLimit`, where they are ignored; the defaults happen to match.
- The realtime server (`mezon-proto-server`, `rate_limit`) limits message sends per connection: built-in default 60/s and 200/min, dev config 100/s and 500/min. Beyond it a send gets error 429.
- Bulk outbox delivery uses at most `Monze:Outbox:TransportSharePercent` (default 50) of the transport budget. Commands beyond `Monze:Commands:MaxInFlight` (default 64) in flight are dropped unanswered.

### Code organization
- Prefer one primary public type per source file.
- Keep enums, constants, command names, message catalogs, button IDs and other shared declarations in dedicated files.
- Reuse an existing port, repository, service, builder or parser before introducing another abstraction.

### Product surface
- What remains: meeting, welcome, AI, setup and roles, plus the shared infrastructure that meeting needs: help, rate limiting, command inbox, cache, clan registry, reconnect, metrics, migrations and outbox delivery.
- Don't reintroduce removed community features without an explicit request and a migration plan.

## Data Ownership and Consistency

- **Sources of truth:**
  - PostgreSQL is the source of truth for Monze business state: configuration, authorization delegates, welcome, roles, schedules, meeting state, Agent events, summaries, outbox, inbox, AI usage and message-history policy.
  - The Mezon API/socket is the source of truth for platform state: owner, membership, roles, channels, permissions and voice occupancy.
- **Caches:**
  - Redis is a bounded L2 cache and invalidation channel only. L1 is bounded process memory.
  - Neither cache may authorize a user, grant a role, claim capacity, select a voice room, spend an AI budget or claim a job.
- **SQLite** belongs to the SDK message-history facade. Don't use it as Monze business storage.
- **Clan scoping:** scope every clan-owned query and key by `clan_id`. Never use `clan_id = 0` as an authorization wildcard.
- **Cached read models:**
  - When changing them, use versions, generation checks, tombstones and compare-and-set semantics.
  - A stale read must fall back to the authoritative source instead of making a sensitive decision.
- **Transactions:** put durable state transitions, leases, idempotency keys, occurrence creation, balance or budget changes, and outbox rows in one appropriate PostgreSQL transaction.

## Realtime, Performance, and Allocation

- **SDK callbacks:**
  - Keep them bounded.
  - The callback-to-dispatch boundary must not do PostgreSQL, Redis, Mezon API, SQLite, JSON serialization or unbounded logging work.
- **Queues:**
  - Enqueue typed events with `TryWrite` into fixed-capacity queues.
  - Preserve ordering by clan or channel where required.
  - Don't introduce unbounded tasks, queues or retries.
- **Allocation:**
  - The 0 B/op goal is a measured boundary: callback ingress through internal enqueue, after warmup.
  - Measure SDK allocations separately. Never claim whole-stack zero allocation from a microbenchmark.
  - On measured hot paths, avoid LINQ, closure capture, temporary arrays, `ToArray`, `string.Join`, boxing and unnecessary `Task` creation.
  - Use `ValueTask` only when the operation can complete synchronously and its lifetime semantics are clear.
- **Slow work** belongs in workers. Bound HTTP payloads, concurrency, retries, timeouts and cancellation.
- **What to record:** queue depth, lag, allocations, CPU, GC, heap, RSS, database QPS, Redis hit/miss, reconnects and upstream rate limits.
- **Capacity claims:** don't claim support for 1,000 clans, or for the stated message/command/meeting/outbox workload, without repeatable live benchmark and soak evidence.

## Authorization, Security, and UI

- **Authorization:**
  - Verify owner/admin/delegate permissions against PostgreSQL and Mezon immediately before every state-changing operation.
  - Don't trust a client-supplied `user_id` or the actor identity from generic button events. State-changing interactions require a server-authenticated actor contract.
- **User-facing labels:**
  - Don't show raw clan, channel or user IDs.
  - Prefer channel labels and clan names. For users, prefer `clan_nick`, then `display_name`, then `user_name`.
- **Secrets:**
  - Keep secrets in ignored local settings, environment variables or an approved secret store.
  - Never print tokens, passwords, DSNs, API keys, prompts, transcripts or credential-bearing URLs in logs, tests, screenshots or reports.
  - **The working copy of the tracked `appsettings.json` contains real values that are not in the committed version.** Never commit, print or copy it. Put secrets in `appsettings.secrets.json` or local files.
- **Logging:**
  - Redact sensitive payloads in logs.
  - Use stable IDs only in internal diagnostics when necessary, and keep them out of normal user messages.
- **Welcome and meeting output:** resolve and validate labels, mentions, channels, roles and recipients against current Mezon state before sending.

## Database and Migrations

- **Applied migrations:** migrations are append-only after deployment. Don't edit or delete an applied migration.
- **New migrations:** add an idempotent migration with a unique ordered name. Use the advisory locking and checksum validation the migration layer already provides.
- **Deployment order:**
  - Run `Monze migrate` before runtime deployment.
  - Runtime startup validates the schema and must not silently apply migrations.
- **Testing:**
  - Test migration upgrade and retry behavior against a real PostgreSQL instance.
  - Record backup and rollout requirements before destructive schema changes.
- **Time values:** use UTC `timestamptz` for stored instants, and keep IANA timezone information for recurring schedules.

## SDK and Workspace Boundaries

- **Package set:**
  - Keep the published packages in sync at version `1.6.2`: `Mezon.Net.Sdk`, `Mezon.Net.Sdk.Caching.Redis` and `Mezon.Net.Sdk.Caching.Sqlite`.
  - Commit `packages.lock.json` and keep it consistent.
  - The `UseLocalMezonSdk` property in two csproj files is not read by anything.
- **Contract references:** treat `Mezon.Net`, `mezon-api`, `mezon-proto-server`, `mezon`, `mezon-desktop`, `Mezube` and `pm-assistant-bot` as contract references. Change them only if the user explicitly authorizes it.
- **Generated bindings:** never hand-edit generated protobuf, OpenAPI or other generated bindings. Change the canonical source and regenerate in the repository that owns it.
- **SDK limitations:**
  - Write a handoff with the exact path, contract evidence, reproduction, test and publish condition.
  - Don't silently work around an authorization or ownership flaw with a less secure fallback.

## Verification Workflow

- **Scope and reporting:**
  - Start with focused checks, then broaden as risk requires.
  - Report the commands actually run.
  - Keep static, unit, database, live Chrome and performance evidence separate.
- **UI or live integration changes:**
  - Use the authorized Chrome dev environment only when the task requires it.
  - Verify the visible response, the runtime log and the database state.
  - For state-changing tests, restore intentional test data, or document exactly what state was kept.
  - A command attempt, an HTTP 200 or a process that stays alive is not proof of business success.
- **Performance work:**
  - Run the relevant BenchmarkDotNet project.
  - Record allocation, Gen0/Gen1, CPU, heap/RSS, queue, database, Redis, reconnect and rate-limit evidence.
  - Results from isolated benchmarks must state their limits.
- **Blocked checks:** when a check is blocked by a locked process, a missing service, an unavailable upstream contract or a stale deployed web bundle, name the blocker. Don't replace it with an invented result.
- **Empty `Monze/Monze` directory:** it exists, and the test config lookup also searches `Monze/appsettings.json`.
