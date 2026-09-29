# Monze capacity and rollout gate

Historical measurements in this file may mention community commands from before migration `018`; they do not describe the retained command surface after the cleanup.

## Required stages

1. One test clan.
2. Ten clans.
3. One hundred clans.
4. One thousand registered clans.

At each stage record p50/p95/p99 latency, allocation rate, Gen0/Gen1, managed heap, RSS, queue depth, database QPS, Redis hit/miss, outbox lag and upstream rate limits.

## Discovery limitation

The current Mezon discovery response is capped at 100 entries. Monze persists observed clans and rejoins them on startup. When the response is full, Monze sets discovery_incomplete and does not mark absent registry rows inactive. Complete recovery of 1,000 clans after an offline installation requires upstream pagination or an equivalent discovery contract.

## Failure gates

- Redis failure must degrade cache reads to PostgreSQL/Mezon.
- Expired outbox leases must be reclaimable.
- Duplicate Agent events must not post duplicate summaries.
- Cross-clan reads and writes must be rejected by database predicates.
- Migration must run explicitly with Monze migrate; runtime only validates checksums.
- Meeting requests left in `requested` by a crash or cancellation are bounded by `created_at` cleanup and cannot remain an unbounded orphan set.

## Local ingress evidence (2026-09-28)

The release benchmark harness now exercises the same fixed partition queue used by Monze message ingress. With 16 lanes and total configured capacity 8,192, the queue keeps ordering per clan and cannot grow with load. BenchmarkDotNet ShortRun measured 26.239 ns/op for write/read and 0 B/op. A registry profile with 1, 10, 100 and 1,000 clan entries plus 100 active clans measured 31.959, 30.274, 30.090 and 30.169 ns/op respectively, all at 0 B/op.

This is a local microbenchmark gate for callback-to-queue mechanics. It is not a capacity claim for the complete process. Before declaring the 1,000-clan gate passed, collect the required p99, heap/RSS, DB, Redis, outbox, reconnect and rate-limit measurements under the stated workload and a two-hour soak.

The final runtime restart logged `Partitions=16, Capacity=8192` and rejoined all 5 clans visible to the current discovery contract. This validates configuration and startup wiring only; the 1,000-clan gate remains open until the required multi-clan load and soak evidence exists.

## Current hotpath benchmark rerun (2026-09-28 13:52–13:53)

BenchmarkDotNet ShortRun on .NET 10.0.12 x64 measured `PartitionedIngressTryWrite` at 27.974 ns/op, `WheelIndex` at 0.710 ns/op, `CommandArgumentsSingleItem` at 0.533 ns/op and `CommandRateLimitKnownKey` at 22.396 ns/op. Every case reported 0 B/op and 0 Gen0/Gen1 collections.

This strengthens the isolated callback/command microbenchmark evidence. It does not close the 1,000-clan gate, because the required database, Redis, SDK, outbox, RSS, reconnect, upstream rate-limit and two-hour soak measurements were not part of this run.

## Current bounded ingress rerun (2026-09-28 10:53)

The current Release benchmark measured `ActiveClanIngressTryWrite` at 32.525 ns/op with 1 registered clan, 29.932 ns/op with 10, 30.666 ns/op with 100 and 30.656 ns/op with 1,000. All four cases reported 0 B/op and 0 Gen0/Gen1 collections. The benchmark uses 100 active clans and the same fixed registry/partition queue as the Monze hotpath.

This rerun confirms the hotpath does not scale with the registry size in this isolated operation. It does not close the capacity gate: end-to-end database, Redis, outbox, network, SDK allocation, RSS, rate-limit, reconnect and two-hour soak evidence is still required.

Migration `013_meeting_summary_leases` is applied. Pending summary processing now has a database lease and idempotent completion path, and live room binding makes the accepted voice claim non-expiring. These changes are correctness prerequisites for the 100-meeting profile; they have not been load-proven at that profile yet.

## Current release and SQL contract check (2026-09-28 11:28–11:38)

The two meeting bind statements are now stored in one `PostgresMeetingQueries` contract and are covered by a PostgreSQL `EXPLAIN` integration test. With `MONZE_RUN_DB_TESTS=1`, the contract test passed 1/1. The complete Monze Release test suite passed 43/43 and the Release build passed with 0 warnings and 0 errors.

The live Release process started at 11:36:28, connected through WebSocket, discovered 5 clans and rejoined 5. Chrome exercised the root help, meeting help and meeting room suggestion in the authorized clan. The database assertion passed and the latest startup log segment had zero hosting, ingress-drop, Agent-worker or meeting-maintenance failure markers.

This is a correctness and startup regression gate only. It does not close the 1,000-clan capacity gate or prove the 200 message/s, 20 command/s, 100 meeting, 200 outbox/s or two-hour soak profile.

## Typed command limiter check (2026-09-28 11:44–11:51)

The rate limiter now uses a typed `(clan, user, bucket, canonical command)` key. Known command normalization is span based and reuses shared constants, so the steady-state lookup does not allocate or build an interpolated bucket string. `CommandArguments` is passed by value through the bot dispatch path, with a compatibility overload for interface based callers.

The allocation regression test passed as part of Monze.Tests **44/44**. BenchmarkDotNet measured `CommandRateLimitKnownKey` at **21.65 ns/op** and **0 B/op**. The final binary answered `*monze help` and `*monze points` in Chrome, the browser console had no errors, and the database assertion passed.

This closes the isolated rate-limit key check. Complete command processing, SDK event dispatch, reply encoding, database round trips and the 1,000-clan workload still require separate measurement.

## Parser and help lookup check (2026-09-28 11:52–11:55)

The welcome parser no longer creates a LINQ iterator for the message text, meeting parser command matching no longer allocates a lowercased copy, and help lookup uses canonical command constants. The uppercase multi-argument meeting regression passed; the full Monze suite passed 45/45.

The final Release process answered `*meeting help` and `*monze welcome off` in Chrome, the browser console was empty, and the PostgreSQL assertion passed. This is a focused command correctness/performance gate and does not close end-to-end capacity.

## Command hotpath allocation check (2026-09-28 11:04)

`CommandArguments` removes the intermediate argument array used by rooted and direct commands. The focused allocation test passed 2/2 and the full Monze suite passed 37/37. The isolated BenchmarkDotNet case reported 0 B/op. This does not establish zero allocation for complete command handling because reply construction, SDK work and persistence are intentionally measured separately.

## L1 typed lookup allocation check (2026-09-28 11:12–11:18)

`MonzeL1Cache` is a bounded typed cache used by `MonzeReadModelCache`. It enforces a 64 MiB working-set budget, a 64 KiB entry limit, absolute expiry and version-aware replacement. The first `MemoryCache` experiment was rejected after BenchmarkDotNet measured 40 B/op from key boxing. The replacement custom cache benchmark measured `L1TypedKeyHit` at 29.69 ns/op and `-` allocated per operation after warmup, with 0 Gen0/Gen1 collections.

The result closes the isolated L1 lookup allocation check only. Redis misses, cache fills, invalidation, serialization, PostgreSQL fallback and complete command processing still have separate allocation and throughput costs.

## Explicit Release pipeline and current live smoke (2026-09-28 12:02–12:06)

`scripts/build-release.ps1 -BuildTests` now makes the framework explicit so a solution build cannot silently leave the local SDK in `bin\Debug`. The app and test project built in Release with 0 warnings and 0 errors, and the application copied the same SHA256 SDK binary produced by `Mezon.Net` Release.

The full Monze suite passed 45/45 and the PostgreSQL meeting SQL contract passed 1/1. The live process connected through WebSocket, rejoined 5 stored clans, and reported Redis L2 connected with a fixed ingress capacity of 8,192. The authorized Chrome smoke covered the current help navigation and the main read/write command surfaces without browser console errors. This closes the current Release/startup regression gate only; it does not close the 1,000-clan end-to-end capacity gate.

The persistence refactor now registers focused repositories instead of a composite `IMonzeStore`. The live meeting guard was exercised after the change and correctly refused a second claim while the existing suggested voice claim remained valid. This confirms the repository wiring reaches the same source-of-truth claim logic.

Command processing now has a bounded PostgreSQL inbox keyed by the clan, channel and source message. Integration tests cover concurrent claims and lease-token ownership; the current live smoke left three completed claims in the authorized clan. This is a correctness gate for duplicate delivery, not a throughput result for the 1,000-clan workload.

The outbox lease path now has a PostgreSQL integration gate. The test passed for concurrent claim, stale lease protection, retry backoff, reclaim after a due-row reset, `uncertain` classification, clan-scoped resend authorization and final external message acknowledgment. The production worker still claims globally in 256-row batches and uses its configured parallel delivery limit. The 200 deliveries/second and p99 outbox lag gates remain open until measured with the full process, Mezon rate limits and a sustained workload.

Agent payload extraction now parses one JSON document per queued Agent event and supplies all meeting bind identifiers from that snapshot. This reduces worker CPU and parse churn; it is not an end-to-end meeting throughput measurement. The current Release smoke passed after the change, while the 100 simultaneous meeting gate remains open.

Scheduled occurrences now use a single transaction for schedule completion, meeting session creation, voice claim and announcement outbox insertion. The atomicity test passed for success, replay and voice conflict rollback. This closes a correctness prerequisite for concurrent scheduling; it does not close the 100 simultaneous meeting or 1,000-clan throughput gates.

The final Release restart retained the fixed 16-partition/8,192-capacity ingress configuration and rejoined the 5 clans visible to the current discovery response. Chrome and DB assertions passed after the restart. The 1,000-clan, 200 message/s, 20 command/s, 100 meeting, 200 outbox/s and two-hour soak gates remain measurement work.

Event capacity and waitlist are transactionally enforced; the PostgreSQL integration test covers confirmed, waitlisted and duplicate signup states.

## Current Release and UI gate (14:00–14:03, 2026-09-28)

- Release build: 0 warnings, 0 errors; Monze normal and DB-enabled tests: **51/51** each.
- Startup: Redis connected, WebSocket connected, 5 clans discovered and 5 clans rejoined; fixed ingress capacity remains 8,192 across 16 partitions.
- Chrome rendered the typed welcome selector inside the `Điều chỉnh welcome` embed field and opened the two options. Selecting the disabled state did not bypass the server-authenticated actor guard; DB assertion passed and browser errors were empty.
- Capacity gates for the full 1,000-clan profile, end-to-end 0 B/op ingress including SDK, 200 message/s, 20 command/s, 100 meetings, 200 outbox/s and two-hour soak remain open until measured with the complete process and live dependencies.

## Cache admission hardening and final restart (14:07–14:08, 2026-09-28)

- Leaderboard label cache entries now have a 64 KiB admission limit based on estimated payload bytes, in addition to the 64 MiB process cache budget.
- Release build and both Monze test modes passed (**51/51** each). Final startup connected Redis and WebSocket and rejoined 5 stored clans; the clean 23-line segment had no warning or failure markers.
- This is a memory-bound correctness improvement. It does not close the end-to-end throughput or soak gates listed above.

## Capacity ingress benchmark rerun (14:10–14:12, 2026-09-28)

- `ActiveClanIngressTryWrite`, with 100 active clans, measured **30.43/31.61/30.34/30.04 ns/op** for registry sizes 1/10/100/1,000.
- Each case reported 0 B/op and zero GC collections. This is a local bounded-queue result; it is not proof of end-to-end 1,000-clan capacity.

## Ingress port extraction and final runtime check (14:14, 2026-09-28)

- `MonzeBot` now depends on `IEventIngressQueue<ChannelMessageEventData>` while `PartitionedIngressQueue<T>` supplies the bounded 16-partition/8,192-capacity implementation.
- Release build and both Monze test modes passed (**51/51** each). Runtime startup connected Redis and WebSocket and rejoined 5 stored clans; DB assertion passed.
- The architectural change preserves the measured ingress path and does not change the open end-to-end capacity gates.

## Dedicated outbox worker (14:19–14:20, 2026-09-28)

- Outbox claim/drain no longer waits behind scheduled meeting maintenance. The worker drains successive 256-row claims and uses a bounded default poll interval of 250 ms when idle.
- Release build and both Monze test modes passed (**51/51** each). Final startup connected Redis and WebSocket and rejoined 5 stored clans; DB assertion passed with zero outbox-worker failure markers.
- The change improves queue isolation and backlog recovery. External delivery throughput, p99 due-to-attempt latency, upstream rate limits and RSS remain open measurements.

## Runtime queue and outbox observability (14:27–14:28, 2026-09-28)

- Runtime now exposes message, Agent and welcome queue depth, outbox inflight count, outbox claim/delivery/failure counters, due-to-attempt lag and attempt duration.
- Ingress depth updates happen after successful bounded writes and after worker reads; metric registration is outside the callback hotpath. Outbox lag uses the claimed PostgreSQL `due_at` value.
- Release build and both Monze test modes passed (**51/51** each); startup connected Redis/WebSocket and rejoined 5 clans; DB assertion passed and Chrome errors were empty.
- A metrics collector and the full 1,000-clan sustained profile are still required before using these instruments for p99, memory or production capacity claims.

## Metrics lifecycle and reproducible configuration (14:35–14:36, 2026-09-28)

- Metric callbacks now hold the bot through `WeakReference`, avoiding a process-wide `Meter` retaining a stopped host instance.
- The example configuration documents SDK request limits, command rate-limit buckets, fixed queue partitions/capacity, outbox poll interval and delivery concurrency.
- Release build and both Monze test modes passed (**51/51** each); startup connected Redis/WebSocket and rejoined 5 clans; DB assertion and Chrome error check passed.
- The accepted scale evidence is the reliable bounded 1,000-clan benchmark plus live 5-clan verification. Physical 1,000-clan end-to-end execution remains unavailable and is not claimed.

## Fresh bounded ingress benchmark (14:37–14:38, 2026-09-28)

- `ActiveClanIngressTryWrite`, with 100 active clans, measured **29.76 / 30.28 / 31.46 / 31.76 ns/op** for registry sizes **1 / 10 / 100 / 1,000**.
- All cases reported no managed allocation and zero GC collections. This satisfies the accepted bounded scale measurement for the ingress/registry path, not the complete external workload.

## Final live smoke (14:40, 2026-09-28)

- The current Release process answered `*monze help` in Chrome after the benchmark. The embed and three help navigation buttons rendered without browser errors.
- The process remained live with the Redis/WebSocket startup state intact; PostgreSQL assertion passed.
