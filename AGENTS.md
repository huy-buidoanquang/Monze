# Monze Repository Guidelines

## Scope and precedence

- This file applies to `F:/projects/mezon/Monze` and its descendants.
- Follow the nearest `AGENTS.md`. The workspace-level guide remains authoritative for cross-repository rules; this file adds Monze-specific rules.
- Keep Monze changes scoped to this repository unless the user explicitly requests an upstream or cross-repository change.
- Preserve unrelated dirty-worktree changes. Inspect `git diff` and `git status` before editing; do not reset, checkout, or rewrite unrelated work.
- Work in this order: understand, search, trace, reason, plan, change, verify, review.

## Behavioral guidelines

These guidelines reduce common LLM coding mistakes. Merge them with project-specific instructions as needed.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

### 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:

- State assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them; do not pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop, name what is confusing, and ask.

### 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- Do not add features beyond the request.
- Do not add abstractions for single-use code.
- Do not add flexibility or configurability that was not requested.
- Do not add error handling for impossible scenarios.
- If a change is much longer than necessary, simplify it before committing.

Ask: “Would a senior engineer say this is overcomplicated?” If yes, simplify.

### 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:

- Do not improve adjacent code, comments, or formatting without a direct reason.
- Do not refactor code that is not broken or required by the request.
- Match the existing style.
- If unrelated dead code is found, mention it; do not delete it unless asked.

When a change creates orphans:

- Remove imports, variables, and functions made unused by your change.
- Do not remove pre-existing dead code unless requested.

Every changed line should trace directly to the request, a required correctness fix, or a verification requirement.

### 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Turn requests into observable goals:

- “Add validation” means write tests for invalid inputs, then make them pass.
- “Fix the bug” means reproduce it with a test or live evidence, then make the regression pass.
- “Refactor X” means verify tests before and after.

For multi-step work, state a short plan:

    1. [Step] -> verify: [check]
    2. [Step] -> verify: [check]
    3. [Step] -> verify: [check]

Strong success criteria support independent verification. Weak criteria such as “make it work” require clarification.

These guidelines are working when diffs contain fewer unnecessary changes, rewrites caused by overcomplication decrease, and clarification happens before implementation mistakes.

## Monze architecture

The solution is a .NET 10 modular monolith:

- `Monze.Domain`: domain values, parsers, rules, and calculations. It must not reference PostgreSQL, Redis, Mezon.Net, or hosting.
- `Monze.Application`: use cases, service orchestration, command outcomes, ports, and business policies.
- `Monze.Infrastructure`: PostgreSQL repositories, Redis cache, migrations, external HTTP clients, and persistence adapters.
- Root project: Generic Host, Mezon.Net event adapters, bounded workers, and runtime composition.
- `Ui`: message/embed/button/form builders and interaction identifiers. Keep UI text and command names centralized.
- `Monze.Tests`: unit and integration tests.
- `Monze.Benchmarks`: BenchmarkDotNet microbenchmarks. Treat microbenchmarks as isolated evidence, not production capacity proof.
- `scripts`: explicit Release build, database inspection, and approved dev cleanup helpers.
- `docs`: architecture, performance, capacity gates, database verification, commands, and SDK handoff records.

Prefer one primary public type per source file. Keep enums, constants, command names, message catalogs, button IDs, and other shared declarations in dedicated files. Reuse an existing port, repository, service, builder, or parser before introducing another abstraction.

The retained product surface is meeting, welcome, AI, setup, role, and shared infrastructure such as help, rate limiting, command inbox, cache, clan registry, reconnect, metrics, migrations, and outbox delivery required by meeting. Do not reintroduce removed community features without an explicit request and a migration plan.

## Data ownership and consistency

- PostgreSQL is the source of truth for Monze business state: configuration, authorization delegates, welcome, roles, schedules, meeting state, Agent events, summaries, outbox, inbox, AI usage, and message-history policy.
- Mezon API/socket is the source of truth for platform state: owner, membership, roles, channels, permissions, and voice occupancy.
- Redis is bounded L2 cache and invalidation only. L1 is bounded process memory. Neither cache may authorize a user, grant a role, claim capacity, select a voice room, spend an AI budget, or claim a job.
- SQLite belongs to the SDK message-history facade. Do not use it as Monze business storage.
- Scope every clan-owned query and key by `clan_id`. Never use `clan_id = 0` as an authorization wildcard.
- Use versions, generation checks, tombstones, and compare-and-set semantics when changing cached read models. A stale read must fall back to the authoritative source rather than make a sensitive decision.
- Durable state transitions, leases, idempotency keys, occurrence creation, balance or budget changes, and outbox rows belong in one appropriate PostgreSQL transaction.

## Realtime, performance, and allocation

- Keep SDK callbacks bounded. The callback-to-dispatch boundary must not perform PostgreSQL, Redis, Mezon API, SQLite, JSON serialization, or unbounded logging work.
- Enqueue typed events with `TryWrite` into fixed-capacity queues. Preserve ordering by clan or channel where required. Do not introduce unbounded tasks, queues, or retries.
- Treat the 0 B/op goal as a measured boundary: callback ingress through internal enqueue after warmup. Measure SDK allocations separately; never claim whole-stack zero allocation from a microbenchmark.
- Avoid LINQ, closure capture, temporary arrays, `ToArray`, `string.Join`, boxing, and unnecessary `Task` creation on measured hot paths. Use `ValueTask` only when the operation can complete synchronously and the lifetime semantics are clear.
- Keep slow work in workers. Bound HTTP payloads, concurrency, retries, timeouts, and cancellation. Record queue depth, lag, allocations, CPU, GC, heap, RSS, database QPS, Redis hit/miss, reconnects, and upstream rate limits.
- Do not claim support for 1,000 clans or the stated message/command/meeting/outbox workload without repeatable live benchmark and soak evidence.

## Authorization, security, and UI

- Verify owner/admin/delegate permissions from PostgreSQL and Mezon immediately before every state-changing operation.
- Do not trust client-supplied `user_id` or actor identity from generic button events. State-changing interactions require a server-authenticated actor contract.
- Do not display raw clan, channel, or user IDs in user-facing UI. Prefer channel labels, clan names, and user labels in the order `clan_nick`, `display_name`, `user_name`.
- Keep secrets in ignored local settings, environment variables, or an approved secret store. Never print tokens, passwords, DSNs, API keys, prompts, transcripts, or credential-bearing URLs in logs, tests, screenshots, or reports.
- Redact sensitive payloads in logs. Use stable IDs only in internal diagnostics when necessary and keep them out of normal user messages.
- For welcome and meeting output, resolve and validate labels, mentions, channels, roles, and recipients against current Mezon state before sending.

## Database and migrations

- Migrations are append-only after deployment. Do not edit or delete an applied migration.
- Add an idempotent migration with a unique ordered name for schema changes. Use advisory locking and checksum validation already provided by the migration layer.
- Run `Monze migrate` before runtime deployment. Runtime startup validates schema; it must not silently apply migrations.
- Test migration upgrade and retry behavior against a real PostgreSQL instance. Record backup and rollout requirements before destructive schema changes.
- Use UTC `timestamptz` for stored instants and preserve IANA timezone information for recurring schedules.

## SDK and workspace boundaries

- Keep the current published package set synchronized at version `1.6.2`: `Mezon.Net.Sdk`, `Mezon.Net.Sdk.Caching.Redis`, and `Mezon.Net.Sdk.Caching.Sqlite`, with `packages.lock.json` committed and consistent.
- Treat `Mezon.Net`, `mezon-api`, `mezon-proto-server`, `mezon`, `mezon-desktop`, `Mezube`, and `pm-assistant-bot` as contract references unless the user explicitly authorizes changes there.
- Never hand-edit generated protobuf, OpenAPI, or other generated bindings. Change the canonical source and regenerate in its owning repository.
- For SDK limitations, create a handoff with the exact path, contract evidence, reproduction, test, and publish condition. Do not silently work around an authorization or ownership flaw with a less secure fallback.

## Verification workflow

Start with focused checks, then broaden as risk requires. Report commands actually run and separate static, unit, database, live Chrome, and performance evidence.

Typical Release checks:

    ./scripts/build-release.ps1 -BuildTests
    dotnet test Monze.slnx --configuration Release --no-build
    $env:MONZE_RUN_DB_TESTS = '1'
    dotnet test Monze.slnx --configuration Release --no-build
    ./scripts/inspect-db.ps1 -ClanId <clan> -ChannelId <channel> -Assert
    git diff --check

For UI or live integration changes, use the authorized Chrome dev environment only when the task requires it. Verify the visible response, runtime log, and database state. For state-changing tests, restore intentional test data or document the exact retained state. Do not treat a command attempt, an HTTP 200, or a process that remains alive as proof of business success.

For performance work, run the relevant BenchmarkDotNet project and record allocation, Gen0/Gen1, CPU, heap/RSS, queue, database, Redis, reconnect, and rate-limit evidence. Isolated benchmark results must state their limits.

When a check is blocked by a locked process, missing service, unavailable upstream contract, or stale deployed web bundle, identify the blocker and do not replace it with an invented result.
