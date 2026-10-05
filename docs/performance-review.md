# Monze performance review

Entries below the current cleanup baseline may mention community commands that were exercised before migration `018`. Those commands are historical evidence only and are not part of the current Monze source, database or command surface.

## Current baseline

- One bot session and one process are the target topology.
- The production workload gate is 1,000 registered clans, 100 active clans, 200 message events/s, 20 commands/s, 100 meetings and 200 outbox deliveries/s.
- PostgreSQL is the business source of truth. Redis is bounded L2 cache/invalidation. SQLite is only SDK message history.
- Realtime callbacks do not perform database, Redis, API, SQLite, JSON serialization or logging work. They enqueue into bounded channels.
- Monze now restores and builds against the synchronized Mezon.Net 1.6.2 package set. Source and historical runtime entries below may describe older package behavior and are not evidence for the current deployment.

## Hot path rules

- TryWrite only in ingress callbacks.
- Fixed queue capacities and explicit counters for dropped work.
- Critical Agent and state events require a separate lane from best-effort message persistence.
- No LINQ, ToArray, string.Join, closures or anonymous payloads in ingress code.
- Allocation measurement starts at the Monze callback and ends after enqueue. SDK allocations are a separate handoff.

## Cache and event consistency review

- L1 loads use compare-by-instance removal so a completed waiter cannot remove a newer single-flight load.
- Redis invalidation messages carry the row version. A delayed lower-version message cannot evict a newer local snapshot; the generation check rejects an L2 result that completed after invalidation.
- Agent inbox keys prefer the `event_id`/`eventId` supplied by the Agent payload and fall back to a SHA-256 payload identity for legacy deployments.
- Meeting voice candidates are indexed by clan. A fallback scan now visits only the candidate channels for the requested clan instead of scanning the process-wide 1,000-clan set.
- Reconnect clears both occupancy and event-discovered voice candidates before clan rejoin, preventing stale rooms from surviving a disconnected session.
- Monze subscribes to SDK disconnect and reconnect notifications. Disconnect invalidates live occupancy immediately; reconnect is counted and the existing `Connected` handler rejoins the stored clan registry with bounded parallelism.
- Meeting selection treats the PostgreSQL `voice_claim` transaction as authoritative. A failed claim now returns a failed result, cancels the orphan request and prevents a success announcement; scheduled runs retry after one minute.
- Event signup serializes on the event row and uses an index for confirmed and waitlisted entries. Capacity decisions stay in PostgreSQL and are never made from L1/L2 state.
- Each live clan-wide voice refresh clears the prior occupancy for that clan's known voice candidates before applying the new snapshot. This prevents an omitted empty channel from retaining a stale occupied count.
- AI and transcript HTTP responses are bounded before materialization (`2 MiB` for AI and `512 KiB` for transcript). STT retries only timeout/rate-limit/5xx responses once with a short backoff; AI requests do not retry automatically because an ambiguous provider timeout may already have been charged.
- Leaderboard display labels load one clan member snapshot and use the bounded L1 policy cache for 15 seconds, avoiding one `ListClanUsers` request per leaderboard row. The snapshot is display-only and is never used for authorization.
- Automatic role rules are persisted in PostgreSQL and scanned with one bounded member snapshot per clan. The scan is serialized per clan, deduplicates point thresholds, skips already assigned roles, and retries failed assignments on the next interval. The current `ListClanUsers` contract is capped at 1,000 members, so large-clan role reconciliation remains bounded but is not a complete discovery guarantee until upstream pagination is available.
- Meeting voice fallback filters event-discovered candidates with the same empty occupancy check as the primary channel list. Summary reads include `clan_id` in the repository predicate.

## Capacity acceptance

- 0 B/op in Monze callback-to-enqueue after warmup.
- No critical event drops.
- p99 internal enqueue under 5 ms.
- No monotonic heap or RSS growth during a two-hour soak.
- L1 Monze cache remains under 64 MiB.

The benchmark project is a smoke gate. Final CPU, RSS, database, Redis and Mezon rate-limit results require a live canary with the workload above.

## Command rate limiting

- The in-process limiter keys windows by `clan_id`, `user_id` and normalized command name. AI, meeting, admin and user commands keep separate policy limits without making unrelated commands consume the same window.
- Agent summary completion is durable: a short bounded fetch retry is followed by `summary_pending`, PostgreSQL backoff state and a maintenance worker retry capped at eight attempts. Inbox claims are released only when processing cannot complete, so a transient STT or database failure is not treated as a successful event.
- Monze defaults to the SDK WebSocket transport. TCP remains an explicit compatibility override through `Mezon:Transport: "Tcp"`; this keeps the known abridged-TCP trailing-zero risk out of the default production path.
- The limiter has a fixed entry cap and removes expired entries before rejecting a new key. It is a local guard; a multi-instance deployment needs a shared admission layer before claiming cluster-wide rate-limit enforcement.

## Local smoke result

On 2026-09-28, BenchmarkDotNet ShortRun on .NET 10 x64 measured `BoundedIngressTryWriteRead` at 28.871 ns/op and `WheelIndex` at 0.710 ns/op. Both reported no managed allocation and 0 Gen0/Gen1 collections in the measured operations. This covers the Monze queue and wheel micro paths only; it does not prove SDK, network, database, Redis, RSS or 1,000-clan capacity.

## Redis L2 failure containment and final restart (08:45–08:50)

- Redis registration now replaces the disabled cache descriptor deterministically, so a configured Redis connection selects `MonzeReadModelCache` without depending on descriptor ordering.
- Redis connection options use bounded connect, sync and async timeouts, limited initial retries and exponential reconnect delay. Cache reads fall back to PostgreSQL on Redis exceptions; writes and invalidations remain best effort. A five-second cooldown prevents repeated Redis failures from becoming a request storm.
- Payload CAS now requires a strictly newer version. A tombstone cannot be overwritten by a stale payload with the same version.
- Migration `012_meeting_request_cleanup` adds `created_at`; maintenance cancels `requested` sessions older than five minutes so a crash between session creation and voice claim cannot accumulate unbounded orphan requests.
- Release build passed with 0 warnings and 0 errors; Monze.Tests passed 32/32.
- Final process started at `2026-09-28 08:49:42.520 +07:00`; startup logged `Read model cache selected: MonzeReadModelCache`, WebSocket login, 5 discovered clans and 5 successful clan rejoins.
- Chrome verified `*monze help` and `*monze points` on the final process. The points response remained idempotent and returned the human-facing balance.
- PostgreSQL `inspect-db.ps1 -Assert` exited 0 for the authorized clan/channel. The previously retained meeting smoke row remains unchanged and is not deleted without explicit approval.

## Redis live evidence (08:58–09:00)

- Final startup logged `Read model cache selected: MonzeReadModelCache, RedisConnected=True`.
- Chrome sent `*monze welcome off`; the command returned the expected response and the cache invalidation log reported `Redis cache invalidation committed`.
- A local Redis RESP probe then found the Monze welcome tombstone with a positive TTL (`3503 ms` at probe time). This confirms the configured Monze L2 path is writing to Redis; the exact remaining TTL depends on probe timing.
- The live probe inspected only reachability, key existence and TTL. It did not print payload, token, connection string or user content.

## Dropdown actor contract correction (2026-09-28)

- Source review found that the web `MessageSelect` component used `messages/clickButtonMessage`, even though Mezon exposes the authenticated `DropdownBoxSelected` endpoint for select controls. This caused a visible selection with no `SelectInteraction` reaching Monze.
- The web store now has a dedicated `clickDropdownBoxSelected` thunk, and `MessageSelect` sends the current selection through that endpoint. Multi-select values are sent as the complete selected value list; embed state remains updated locally.
- `nx build chat --prod --baseHref=/` passed after the change. Direct ESLint on both changed files reported no errors; the remaining warnings are pre-existing in `messages.slice.ts` plus the existing fragment warning in `MessageSelect`.
- The current dev site bundle was not republished during this local Monze run. Chrome console evidence still shows the deployed page dispatching `messages/clickButtonMessage`, and PostgreSQL remained `WelcomeEnabled=False;Version=22` after the attempted selection. No privileged button fallback was enabled.
- End-to-end acceptance remains pending the web bundle deployment followed by a fresh Chrome selection and a PostgreSQL state assertion. The Monze route already requires `ServerAuthenticated` actor provenance.

## Historical SDK actor provenance and live UI verification (09:13–09:15)

- Interaction model types are now split one public type per file. `IInteraction` remains source compatible; actor provenance is exposed through `IInteractionActor`.
- In the package/source snapshot used by this historical run, `HandleButtonAsync` marked the actor as `ClientSupplied` and `HandleSelectAsync` marked it as `ServerAuthenticated`.
- In that snapshot, `RequireServerAuthenticatedActor()` rejected a protected button route before its handler. The reviewed 1.6.2 source supersedes this behavior: the backend derives both actors from authenticated request context and the SDK marks both as `ServerAuthenticated`.
- Release verification passed: Mezon.Net SDK tests 74/74, Monze tests 32/32. The full Mezon.Net solution passed 233 tests with loopback permissions enabled; the sandbox-only run had 11 `HttpListenerException` failures.
- The rebuilt process started at `2026-09-28 09:13:12.540 +07:00`, selected Redis L2 with `RedisConnected=True`, connected over WebSocket, discovered 5 clans and rejoined 5 clans.
- Chrome verified the current process with `*monze help`, the embed field page, the `Lệnh` navigation button and the welcome `Cách dùng` button. Welcome mutation remains command-only until the web UI sends a server-authenticated actor event end to end.
- `inspect-db.ps1 -Assert` exited 0 after the smoke. A credential assignment scan over five runtime logs found 0 potential credential assignments; stack trace parameter names such as `token` were not treated as secrets.

## Durable meeting request cleanup and current smoke (09:33–09:34)

- Added append-only migration `012_meeting_request_cleanup`: `meeting_session.created_at` and a partial cleanup index.
- Maintenance now cancels `requested` sessions older than five minutes. This bounds the crash window between session creation and voice claim without deleting meeting history.
- Migration command completed successfully against the configured development PostgreSQL database; `inspect-db.ps1 -Assert` exited 0 and includes migration `012`.
- Release rebuild passed with 0 warnings and 0 errors. The new process started at `09:33:31`, selected Redis L2 with `RedisConnected=True`, connected over WebSocket, discovered 5 clans and rejoined 5 clans.
- Chrome verified `*monze help`, the `Lệnh` embed navigation button and `*meeting help` against the new process. No warning or error appeared in the new startup segment.

## Command input validation and rate-limit normalization (09:39–09:43)

- `MonzeCommandRateLimiter` now normalizes whitespace and casing before choosing the policy bucket. The regression test proves an uppercase/whitespace `welcome` command cannot bypass the admin window.
- Empty `announce` now returns its command-specific help without enqueueing an empty outbox payload.
- Empty `info` now returns its command-specific help without querying FAQ storage with an empty search string.
- Invalid `outbox resend` syntax now returns help instead of silently falling through to the uncertain list operation.
- Release build passed with 0 warnings and 0 errors; Monze.Tests passed 32/32.
- The restarted process at `09:42:28` selected Redis L2, connected over WebSocket, discovered 5 clans and rejoined 5 clans. Chrome verified all three invalid-input branches and received the expected embed help fields.
- `inspect-db.ps1 -Assert` exited 0 for the authorized clan/channel. The retained meeting smoke row was not deleted.

## Final partitioned ingress restart and live smoke (10:31, 2026-09-28)

- The final Release binary started with `Message ingress configured: Partitions=16, Capacity=8192`, `RedisConnected=True`, WebSocket connected, 5 clans discovered and 5 clans rejoined.
- The final process answered `*monze help` in the authorized Chrome clan/channel after restart. Chrome reported no console errors. The broader 18-command smoke was run against the same code generation before the final queue capacity rounding correction; the final correction affects queue sizing only and the final help path was rechecked after restart.
- `inspect-db.ps1 -Assert` remained successful for clan `2104288434238525440` and channel `2104288438869037056`. The retained meeting smoke row was not deleted.

## Partitioned realtime ingress benchmark (2026-09-28)

- Message ingress now uses `PartitionedIngressQueue<ChannelMessageEventData>` with a fixed capacity and 16 power-of-two lanes. A message callback reads the SDK readonly payload, selects the lane by `clan_id`, and calls `TryWrite`; it does not await, serialize, access storage or log.
- Each lane has one consumer, preserving ordering within a clan while allowing unrelated clans to be processed concurrently. Agent and welcome events remain on their dedicated bounded lanes, so critical event handling is not coupled to message-history persistence.
- BenchmarkDotNet ShortRun on .NET 10 x64 measured `PartitionedIngressTryWrite` at **26.239 ns/op**, with no managed allocation and 0 Gen0/Gen1 collections. `WheelIndex` measured **0.825 ns/op**, also with no allocation.
- The capacity benchmark includes a fixed in-memory registry with 1, 10, 100 or 1,000 clan entries and 100 active clans. `ActiveClanIngressTryWrite` measured **31.959 ns/op** at 1 clan, **30.274 ns/op** at 10, **30.090 ns/op** at 100 and **30.169 ns/op** at 1,000; all runs reported no managed allocation and 0 Gen0/Gen1 collections.
- These numbers cover the bounded ingress data structure and registry lookup only. They do not prove end-to-end throughput, p99 queue latency, database QPS, Redis hit rate, RSS stability, SDK allocation behavior, upstream rate limits or 1,000-clan production capacity. Those remain live canary and soak gates.

## Meeting lease, claim recovery and current live verification (10:46–10:53, 2026-09-28)

- Added migration `013_meeting_summary_leases` with a lease token and expiry for pending summary work. `ListPendingSummariesAsync` claims rows with `FOR UPDATE SKIP LOCKED`; store and retry operations only clear or complete a row when the lease token still matches. This prevents two maintenance workers from fetching or posting the same summary concurrently.
- Meeting room binding now selects the newest matching suggested/live session, makes the corresponding voice claim non-expiring when the Agent confirms the room, and records unmatched Agent rooms without creating an orphan session. Expired suggested sessions release their claim during takeover.
- Welcome delivery claims are released when the channel send fails, so a transient Mezon failure cannot permanently suppress the member's welcome.
- `Monze migrate` completed successfully. `scripts/inspect-db.ps1 -Assert` passed with migrations through `013_meeting_summary_leases` for the authorized development clan/channel.
- The current Release process started at `2026-09-28 10:46:40.590 +07:00`, selected Redis L2 with `RedisConnected=True`, connected over WebSocket, discovered 5 clans and rejoined 5 clans. The latest startup segment contains no hosting, ingress-drop, Agent-worker or meeting-maintenance failure.
- Chrome smoke after that restart sent the complete 19-command help/read-only set, plus welcome on/off and `*meeting now`. Every command rendered a response and Chrome reported no console errors. The meeting smoke selected `monze-test-voice`; its database row is intentionally retained for inspection.
- The current capacity benchmark measured `ActiveClanIngressTryWrite` at 32.525 ns/op for 1 clan, 29.932 ns/op for 10 clans, 30.666 ns/op for 100 clans and 30.656 ns/op for 1,000 clans. All cases reported no managed allocation and 0 Gen0/Gen1 collections.
- These measurements still cover only the bounded ingress path and in-memory registry. End-to-end 200 message/s, 20 command/s, 100 meeting, 200 outbox/s, RSS, reconnect and two-hour soak gates remain open.

## Command dispatch allocation reduction (11:04–11:06, 2026-09-28)

- Replaced `Skip(1).ToArray()` and the direct-command argument copy with `CommandArguments`, a readonly indexed view that supports prefix and slice operations without allocating an intermediate array or iterator.
- Command module normalization now returns existing canonical constants instead of calling `ToLowerInvariant()` for known commands. Argument joining uses a single `string.Create` result when text materialization is required.
- Added allocation regression coverage. The focused test passed 2/2, including a 10,000-iteration slice/single-item join loop with 0 bytes allocated after warmup. The full Monze test suite passed 37/37.
- BenchmarkDotNet reported 0 B/op for the isolated single-item command argument path. The result is treated as a micro path signal only; command response building, SDK dispatch, network and persistence remain outside this measurement.
- The rebuilt process started at `2026-09-28 11:04:42.444 +07:00`; Chrome received `*monze help` at 11:05 and the browser error console remained empty.

## Typed bounded L1 cache (11:12–11:18, 2026-09-28)

- `MonzeReadModelCache` now uses `MonzeL1Cache`, a typed `ConcurrentDictionary` cache with absolute expiry, a 64 MiB byte budget, a 64 KiB entry limit, version-aware replacement and bounded eviction tokens. Redis key materialization remains on the L2 path only.
- An initial benchmark using `MemoryCache` with the typed key measured **40 B/op** because the `MemoryCache` object-key API boxed the struct. That implementation was rejected and replaced before the live restart; the result is retained here as the reason for the custom L1 implementation.
- BenchmarkDotNet ShortRun on .NET 10 x64 measured `L1TypedKeyHit` at **29.69 ns/op**, with `-` allocated per operation and 0 Gen0/Gen1 collections. This is the actual typed L1 implementation after warmup, measured in isolation.
- Monze Release build passed with 0 warnings and 0 errors; Monze.Tests passed **42/42**, including L1 version, expiry and typed-key tests. The 1,000-clan end-to-end capacity gate remains open until the full workload and soak profile is measured.

## Meeting bind SQL correction (11:21–11:27, 2026-09-28)

- The first process after the L1 correction exposed a real PostgreSQL syntax error in the Agent meeting path. Both meeting bind queries were missing the comma between the `updated` and `claimed` CTEs.
- The CTEs were corrected and both statements were parsed by PostgreSQL with `EXPLAIN (COSTS OFF)` against the configured development database; the probe did not execute updates.
- The corrected Release process started successfully, rejoined 5 clans, and its latest startup segment has no hosting, Agent-worker, ingress-drop or meeting-maintenance failure. Chrome verified `*meeting help` and `*monze help` with no browser console errors.

## Meeting query contract extraction and final live check (11:28–11:38, 2026-09-28)

- Extracted the two meeting bind statements into `PostgresMeetingQueries`, so the store uses one reviewed SQL contract instead of maintaining duplicated inline CTE text.
- Added a PostgreSQL integration contract test gated by `MONZE_RUN_DB_TESTS=1`. It executes `EXPLAIN (COSTS OFF)` for both bind statements against the configured development database without mutating business rows; the test passed 1/1.
- The complete Monze Release test suite passed **43/43** after the extraction and the secrets-config fallback in the DB test harness. Release build remains 0 warnings and 0 errors.
- The Release process started at `2026-09-28 11:36:28.713 +07:00`, selected Redis L2 with `RedisConnected=True`, configured 16 ingress partitions with capacity 8,192, connected over WebSocket, discovered 5 clans and rejoined 5 clans.
- Chrome verified `*monze help`, `*meeting help` and `*meeting now` in clan `2104288434238525440`; the last command suggested `monze-test-voice`. Chrome error logs were empty and the PostgreSQL assertion passed after the meeting smoke.
- The latest startup segment contains zero `fail:`, `Hosting failed`, `Unhandled`, ingress-drop, Agent-worker or meeting-maintenance failure markers. The append-only log still retains failures from earlier pre-fix runs, which are historical evidence rather than a clean-log claim.

## Typed rate-limit key and command dispatch allocation check (11:44–11:51, 2026-09-28)

- Replaced the rate limiter's interpolated string bucket key and per-call `Trim().ToLowerInvariant()` with a typed key containing numeric clan/user IDs, a bucket enum and canonical command constants. Unknown commands use one bounded canonical key and cannot create unbounded distinct entries.
- The Monze application now accepts `CommandArguments` by value on the bot ingress path. The compatibility overload for existing `IReadOnlyList<string>` callers wraps the view without changing command behavior; the internal dispatch no longer boxes the struct.
- The allocation regression test passed inside the full suite. BenchmarkDotNet ShortRun measured `CommandRateLimitKnownKey` at **21.65 ns/op**, with `0 B/op` and no managed allocation reported after warmup.
- Release build passed with 0 warnings and 0 errors; Monze.Tests passed **44/44**. The final process started at `2026-09-28 11:50:10.631 +07:00`, connected over WebSocket and rejoined 5 stored clans.
- Chrome verified `*monze help` and `*monze points` on the final binary. The points response stayed idempotent, Chrome error logs were empty, and the PostgreSQL assertion plus meeting SQL contract test passed.
- The benchmark covers the limiter and dispatch view in isolation. It does not establish zero allocation for the complete SDK callback, command response, database or network path.

## Command parser allocation cleanup and final restart (11:52–11:55, 2026-09-28)

- Welcome text parsing now uses `CommandArguments.Join` instead of LINQ `Skip`, meeting parsing compares command names case-insensitively without lowercasing a new string, and help topic lookup reuses canonical command constants.
- Added a regression for uppercase meeting schedule commands with multiple arguments. Release build passed with 0 warnings and 0 errors; Monze.Tests passed **45/45**.
- The final process started at `2026-09-28 11:53:44.483 +07:00`. Chrome verified `*meeting help` and `*monze welcome off`; both replies rendered expected embed fields, Chrome error logs were empty, and the PostgreSQL assertion passed.
- These changes remove avoidable command path work. They do not turn response construction, SDK serialization or database I/O into zero-allocation operations.

## Explicit framework Release build and live smoke (12:02–12:06, 2026-09-28)

- Added `scripts/build-release.ps1`, which builds `Monze.csproj` and optionally `Monze.Tests.csproj` with an explicit `net10.0` framework, `Release` configuration, project references and disabled parallel project builds. It also compares the copied application SDK hash with the local Release SDK output.
- The helper built Monze and the test project with 0 warnings and 0 errors. The copied `Mezon.Net.Sdk.dll` matched the Release SDK output with SHA256 `F7EB7BA6064E302BCE2E1B72FE606AC29EEEEEB7FA490088E135A2C87EB02438`.
- The rebuilt binary passed the full Monze suite **45/45** and the PostgreSQL meeting SQL contract **1/1**.
- The process started at `2026-09-28 12:02:35.022 +07:00`, selected Redis with `RedisConnected=True`, configured 16 ingress partitions with capacity 8,192, connected over WebSocket, discovered 5 clans and rejoined 5 clans.
- Chrome exercised root help, all three help navigation buttons, points, leaderboard, spin, topic, info, event recap, setup help, role help, outbox, `*meeting now`, `*summary`, welcome on/off and the welcome `Cách dùng` interaction. Every check returned a response; the Chrome error console was empty.
- The current startup segment had zero `fail:`, hosting, ingress-drop, Agent-worker or meeting-maintenance failure markers. The append-only log still contains historical failures from earlier runs and is not treated as a clean all-time log.

## Persistence repository extraction and live verification (12:13–12:18, 2026-09-28)

- Replaced the composite `IMonzeStore` registration with focused PostgreSQL repository implementations for clan registry, authorization, meeting, scheduling, outbox, community, AI usage and message history. `MonzeBot` and `MeetingMaintenanceWorker` now receive only the ports they use.
- The repository extraction preserved the existing SQL files and transaction boundaries. Release build completed with 0 warnings and 0 errors; Monze.Tests passed **45/45** and the PostgreSQL meeting SQL contract passed **1/1**.
- The restarted process connected through WebSocket, selected Redis L2, configured 16 partitions with capacity 8,192, discovered 5 clans and rejoined 5 clans.
- Chrome verified root help, points, `*meeting now` and `*monze welcome off` after the new DI wiring. Meeting returned the expected occupied-room guard because the retained smoke session still held `monze-test-voice`; the database query confirmed the live suggested session and its voice claim. Chrome error logs were empty and the database assertion passed.
- This extraction improves dependency clarity and test isolation. It does not change the open end-to-end 1,000-clan capacity gate.

## Command idempotency lease and live verification (12:21–12:28, 2026-09-28)

- Added `ICommandInboxRepository` and `PostgresCommandInboxRepository` with a unique `(clan_id, channel_id, message_id)` key, a 60-second processing lease, lease-token scoped complete/release, completed-row retention and hourly cleanup.
- The first DB test exposed an `ON CONFLICT` condition that reclaimed a fresh processing row; the condition was corrected to reclaim only an expired lease. A second DB test exposed a schema mismatch when completion set a `NOT NULL` lease column to `NULL`; completion now leaves a timestamp while changing status to `completed`. These defects were fixed before live restart.
- PostgreSQL integration testing now proves sequential duplicate suppression, concurrent same-message claims yielding one winner, completed replay suppression, wrong-token protection and retry after the correct release. Full Release tests passed **46/46** with `MONZE_RUN_DB_TESTS=1`.
- Migration `014_command_inbox` is applied. The live database contains three completed command claims from the current Chrome smoke, and `inspect-db.ps1 -Assert` passes.
- The restarted Release process connected through WebSocket, rejoined 5 clans and reported Redis L2 connected. Chrome verified help, points and welcome after the idempotency wiring; the browser error console was empty and the current startup segment contained zero warnings or failure markers.

## Outbox lease and retry verification (2026-09-28)

- Added a clan-filtered claim predicate to `IOutboxRepository` for isolated checks. The production dispatcher continues to call the global claim path and retains the 256-row batch limit.
- PostgreSQL integration test passed **1/1** for concurrent claim ownership, lease-token protected completion, retry backoff, due-row reclaim, external message ID precedence, uncertain delivery listing, cross-clan resend rejection and explicit resend recovery.
- The test uses a unique dedupe key and removes its rows in `finally`; it does not migrate, delete or rewrite unrelated outbox rows.
- This closes the local outbox correctness gate. It does not prove 200 deliveries/second, Mezon API rate-limit headroom, delivery p99, or process RSS under the 1,000-clan workload.

## Agent payload parsing and current meeting smoke (2026-09-28)

- Added `AgentEventPayload` as a single parse boundary for Agent room, voice channel and clan metadata. `ProcessAgentAsync` no longer parses the same raw JSON once for the room and again for the voice bind.
- Added coverage for nested room payloads, flat payloads, numeric and string IDs, and malformed JSON. The full DB-enabled Monze suite passed **49/49**.
- Release startup at `12:44:48.758` selected Redis (`RedisConnected=True`), configured 16 partitions with capacity 8,192, connected through WebSocket and rejoined 5 clans. The current startup segment has zero warning/failure markers.
- Chrome verified `*meeting help`, `*meeting now`, `*summary` and `*monze welcome off`; the room suggestion rendered `monze-test-voice`, the no-summary state rendered, and browser error logs were empty. `inspect-db.ps1 -Assert` passed.

## Scheduled meeting atomic commit (2026-09-28)

- Added `IScheduledMeetingRepository` and `PostgresScheduledMeetingRepository`. A scheduled occurrence now commits its schedule completion, suggested meeting session, voice claim and announcement outbox in one PostgreSQL transaction.
- The integration test passed **1/1**: the first commit created exactly one session, claim and outbox row; replay with the cleared lease returned false; a second occurrence hitting the occupied voice claim rolled back without leaving a session or outbox row.
- Chrome rechecked `*meeting help`, `*meeting now`, `*summary`, `*monze outbox` and `*monze welcome off` after restart. The retained voice claim correctly produced the busy-room response, and browser errors remained empty.

## Final Release restart and Chrome smoke (12:53–12:54, 2026-09-28)

- Final `scripts/build-release.ps1 -BuildTests` completed with 0 warnings and 0 errors; the full DB-enabled suite passed **50/50**.
- The final process started at `12:53:47.468`, selected Redis L2, configured 16 partitions with capacity 8,192, connected through WebSocket and rejoined 5 clans. The current segment has 0 `fail:`, 0 `warn:`, 0 hosting failure and 0 Agent/meeting worker failure markers.
- Chrome verified `*monze help`, `*meeting help`, `*meeting now`, `*summary`, `*monze outbox` and `*monze welcome off`; all responses rendered and Chrome error logs were empty. `inspect-db.ps1 -Assert` passed.

## Per-clan voice selection gate and final live check (13:18–13:21, 2026-09-28)

- Added a per-clan `SemaphoreSlim` gate around voice occupancy refresh and candidate selection. This prevents concurrent requests in one clan from interleaving the clear-and-rebuild snapshot sequence, while preserving parallel selection across clans. The durable PostgreSQL voice claim remains authoritative after the gate.
- Release build completed with 0 warnings and 0 errors; normal and DB-enabled Monze suites passed **51/51** each.
- After the final lifecycle cleanup, the restarted process at 13:21 selected Redis L2, connected through WebSocket and rejoined 5 clans. The clean current startup segment has 23 lines, 0 warning markers and 0 failure markers.
- Chrome verified `*meeting help` and `*meeting now`; the named empty room `monze-test-voice` was suggested. `inspect-db.ps1 -Assert` passed and Chrome error logs were `[]`.

## Typed embed input codec and SDK sync (13:30, 2026-09-28)

- Extended the SDK message model with typed embed-field `inputs` components and radio `extraData`, matching the UI contract used by Mezube. `MessageEmbedBuilder.AddInputField` now builds this shape without hand-written JSON.
- The full `Mezon.Net.sln` Release build passed with 0 errors across netstandard2.1/net6/net8/net9/net10. Existing SDK test analyzer warnings remain; focused tests passed **78/78 SDK** and **61/61 Client**.
- Monze was rebuilt against the updated local SDK; Release build passed with 0 warnings and 0 errors, and both normal and DB-enabled Monze suites passed **51/51**.
- The restarted bot at 13:30 connected through WebSocket, Redis and 5 clans. Chrome verified `*monze help`; the browser error console was `[]`, and `inspect-db.ps1 -Assert` passed.

## SDK extension-key validation and current Release smoke (13:37–13:50, 2026-09-28)

- SDK builders now reject extension names that collide with typed message, embed, embed-field or markdown properties. Unknown extension names remain supported for forward compatibility, and focused regression tests cover root, markdown, embed and field collisions.
- Full Mezon.Net Release build completed with 0 errors. Focused tests passed **81/81 SDK** and **61/61 Client**; the solution retains 3 existing nullable warnings in the Redis test project.
- Monze `scripts/build-release.ps1 -BuildTests` completed with **0 warnings and 0 errors** and copied SDK SHA256 `36FFB8C5C4455ACADBBFC0683CF2E0D7941A6C4D7C521471076CAFEB1DFDD080`.
- Normal and DB-enabled Monze suites passed **51/51** each. `scripts/inspect-db.ps1 -Assert` passed for the authorized clan and channel.
- The final Release process started at `13:49:32.964 +07:00`, selected Redis with `RedisConnected=True`, configured 16 ingress partitions with capacity 8,192, connected over WebSocket, discovered 5 clans and rejoined 5 clans. The clean current segment has 23 lines, 0 warning markers, 0 failure markers, 0 ingress drops and 0 Agent/meeting worker failures.
- Chrome checked help for setup, welcome, event, role, meeting and summary; points, leaderboard, spin, topic and FAQ lookup; and the welcome embed help button. The dev site's welcome dropdown was exercised but did not persist a state change because the served bundle still uses the client-supplied button event; the mutation remained blocked by the server-authenticated actor guard. The owner command path changed `welcome_enabled` to `True` and back to `False` in PostgreSQL, with versions 40 and 41, and Chrome error logs remained empty.

## Hotpath benchmark rerun (13:52–13:53, 2026-09-28)

- BenchmarkDotNet ShortRun on .NET 10.0.12 x64 measured `PartitionedIngressTryWrite` at **27.974 ns/op**, `WheelIndex` at **0.710 ns/op**, `CommandArgumentsSingleItem` at **0.533 ns/op** and `CommandRateLimitKnownKey` at **22.396 ns/op**.
- All four cases reported `Allocated = -` (0 B/op) and 0 Gen0/Gen1 collections. The harness ran with 3 warmup/3 measurement iterations and one launch.
- These are isolated Monze data-structure and command-key measurements. They do not establish zero allocation for SDK callbacks, serialization, network, PostgreSQL, Redis, RSS stability or the 1,000-clan workload.

## Current Release restart and embed-field UI check (14:00–14:03, 2026-09-28)

- `scripts/build-release.ps1 -BuildTests` completed with 0 warnings and 0 errors after moving the welcome selector into a typed embed field input. Normal and DB-enabled Monze suites passed **51/51** each.
- The Release process started at `14:00:15.274 +07:00`, selected Redis (`RedisConnected=True`), configured 16 ingress partitions with capacity 8,192, connected over WebSocket, discovered 5 clans and rejoined 5 clans. After the live command, the clean current segment has 25 lines with 0 failure, warning, ingress-drop, Agent-worker or meeting-worker markers.
- Chrome sent `*monze welcome off` to the authorized clan. The rendered message has separate `Kết quả` and `Điều chỉnh welcome` embed fields; the second field contains the typed selector with placeholder `Chọn trạng thái`, while `Cách dùng` remains a separate action button. The selector was opened and `Tắt lời chào` was selected; PostgreSQL remained `welcome_enabled=False` and `inspect-db.ps1 -Assert` passed.
- Chrome console errors remained `[]`. This verifies the new Monze payload and current FE rendering. Privileged selector mutation remains guarded by server-authenticated actor requirements; no client-supplied interaction path was trusted.

## Leaderboard label cache admission and final restart (14:07–14:08, 2026-09-28)

- Leaderboard label snapshots now estimate their UTF-16 payload size and are admitted to the bounded policy cache only when the estimate is at most 64 KiB. This keeps the existing 64 MiB cache budget from treating one large dictionary as a small count-based entry.
- Release build completed with 0 warnings and 0 errors; normal and DB-enabled Monze suites passed **51/51** each.
- The final process started at `14:08:33.991 +07:00`, selected Redis, connected over WebSocket, discovered 5 clans and rejoined 5 clans. The clean segment has 23 lines with zero failure, warning, ingress-drop, Agent-worker or meeting-worker markers. `inspect-db.ps1 -Assert` passed and Chrome error logs remained `[]`.

## Capacity ingress benchmark rerun (14:10–14:12, 2026-09-28)

- BenchmarkDotNet ShortRun on .NET 10.0.12 x64 measured `ActiveClanIngressTryWrite` with `ActiveClanCount=100` at **30.43 ns/op** for 1 clan, **31.61 ns/op** for 10 clans, **30.34 ns/op** for 100 clans and **30.04 ns/op** for 1,000 clans.
- All four cases reported **0 B/op** and zero GC collections. The measured operation is a fixed-capacity partitioned ingress write, followed by a local read and registry lookup.
- This confirms the in-process queue operation does not scale with registry size in this workload. It does not measure SDK decode, DB/Redis I/O, message persistence, external delivery, RSS, or the 1,000-clan end-to-end gate.

## Dedicated outbox worker and final runtime check (14:19–14:20, 2026-09-28)

- Outbox flushing now runs in a separate worker loop from scheduled meeting maintenance. It drains successive due batches while backlog exists and polls at a bounded configurable interval, defaulting to 250 ms.
- Release build completed with 0 warnings and 0 errors; normal and DB-enabled Monze suites passed **51/51** each.
- Final startup at `14:19:56.443 +07:00` selected Redis, connected over WebSocket, discovered 5 clans and rejoined 5 clans. The clean 23-line segment has 0 failure, warning, ingress-drop, Agent-worker, meeting-maintenance or outbox-worker failure markers; `inspect-db.ps1 -Assert` passed.
- This improves scheduling/outbox isolation and backlog drain behavior. It does not by itself prove 200 external deliveries/second or p99 delivery latency.

## Runtime queue and outbox observability (14:27–14:28, 2026-09-28)

- Added bounded runtime gauges for message, Agent and welcome ingress depth plus outbox in-flight attempts. The gauges read monotonic counters and are registered during construction; the realtime callback only performs the existing bounded `TryWrite` and one `Interlocked` update.
- Added outbox counters for claimed, delivered, failed and uncertain items, plus histograms for due-to-attempt lag and attempt duration. The claim projection now carries `due_at`, so lag is measured from PostgreSQL data rather than inferred from worker timing.
- Release build completed with 0 warnings and 0 errors. Normal and DB-enabled Monze suites passed **51/51** each.
- The new Release process started at `14:27:29.855 +07:00`, selected Redis, connected through WebSocket, discovered 5 clans and rejoined 5 clans. `inspect-db.ps1 -Assert` passed and Chrome error logs were `[]`.
- This closes the instrumentation wiring check. It does not close SDK callback allocation, external outbox throughput, p99, RSS or the two-hour soak gate; those require a collector and sustained workload.

## Metrics lifecycle and reproducible configuration (14:35–14:36, 2026-09-28)

- Metric providers now use a weak reference to the bot instance. A stopped host can therefore release the bot even though the process-wide `Meter` remains registered.
- `appsettings.example.json` now documents the measured runtime knobs: SDK request limits, command bucket limits, queue partitions/capacity, outbox poll interval and delivery concurrency.
- Release build completed with 0 warnings and 0 errors; normal and DB-enabled Monze suites passed **51/51** each.
- Startup at `14:35:23.183 +07:00` selected Redis, connected through WebSocket, discovered 5 clans and rejoined 5 clans. `inspect-db.ps1 -Assert` passed and Chrome error logs were `[]`.
- The updated objective accepts reliable measurement in place of a physical 1,000-clan test. The current evidence remains a bounded 1,000-clan capacity benchmark plus live 5-clan smoke; no end-to-end 1,000-clan claim is made.

## Fresh 1,000-clan bounded ingress benchmark (14:37–14:38, 2026-09-28)

- BenchmarkDotNet ShortRun on .NET 10.0.12 x64 measured `ActiveClanIngressTryWrite` with 100 active clans at **29.76 ns/op** for 1 registered clan, **30.28 ns/op** for 10, **31.46 ns/op** for 100 and **31.76 ns/op** for 1,000.
- All four cases reported `Allocated = -` and zero GC collections. The benchmark exercises fixed-capacity partitioned ingress, local read and registry lookup; it does not perform SDK decode, PostgreSQL, Redis or external Mezon I/O.
- This is the current reliable measurement for the accepted 1,000-clan boundary. It supports registry-size independence of this hotpath, while end-to-end throughput, RSS and two-hour soak remain separate measurements.

## Final live command smoke after benchmark (14:40, 2026-09-28)

- Chrome sent `*monze help` in the authorized clan/channel after the latest Release restart. The response rendered the `Trợ giúp` embed with separate result content and `Lệnh`, `Sự kiện`, `Meeting` buttons.
- PostgreSQL assertion remained green, the running process stayed connected, and Chrome error logs were `[]`.

## SDK 1.6.2, message-gap durability and interaction replay gate (2026-10-02)

- Runtime, infrastructure, tests and all lock files resolve the synchronized
  Mezon.Net package set at `1.6.2`.
- The accepted message callback retains the existing fixed-capacity partitioned
  `TryWrite` path. When that queue is full, a value-type marker enters a separate
  bounded single-reader queue; the worker coalesces at most 256 channel keys and
  stores only the highest dropped message ID. PostgreSQL updates only channels that
  already opted into message persistence.
- During shutdown, canceled in-flight messages and unread bounded-queue entries are
  coalesced by clan/channel and persisted as monotonic gap markers before the SQLite
  history store is flushed. This shutdown-only dictionary does not change the
  measured callback allocation path.
- `meeting now` and valid schedule-submit interactions use PostgreSQL leases keyed
  by clan, channel, source message and action. This work runs after SDK dispatch,
  outside the measured realtime callback boundary.
- Release build completed with zero warnings and errors. After removing two
  caller-free meeting bind APIs and their syntax-only SQL test, the Redis and
  PostgreSQL enabled suite passed `173/173`; it includes 26 PostgreSQL tests and one real
  two-instance Redis test.
- The final repository-port sweep also removed caller-free
  `LatestPostedSummaryAsync` and `SetWelcomeEmbedAsync`; the suite remained
  `173/173` in `docs/test-artifacts/20261002-repository-port-cleanup-full.trx`.
- A later transcript timeout hardening pass removed the nested Agent retry loop and
  applies one configured HTTP timeout across authentication, retries and response
  reading. This bounds one `session_done` transcript operation to 30 seconds instead
  of a possible roughly 4.5 minutes. That PostgreSQL and Redis enabled checkpoint was
  `176/176` in `docs/test-artifacts/20261002-transcript-timeout-full.trx`.
- BenchmarkDotNet ShortRun measured `PartitionedIngressTryWrite` at `27.4787 ns/op`,
  `CommandArgumentsSingleItem` at `0.7986 ns/op` and
  `CommandRateLimitKnownKey` at `17.7406 ns/op`. Each reported no managed allocation
  and no GC collections in the measured operation.
- A fresh 30-second bounded profile produced 6,000 messages, 600 commands and 6,000
  outbox items for 1,000 registered/100 active clans with zero drops or rejects.
  Maximum ingress and outbox depth were 4 and 5 of 8,192; maximum managed heap was
  2,867,528 bytes, working set 38,387,712 bytes and
  Gen0/Gen1/Gen2 counts were 0/0/0.
- These measurements exclude SDK decode, PostgreSQL/Redis I/O, Mezon delivery and
  Agent traffic. They do not prove whole-stack zero allocation or production
  capacity for 1,000 clans.
- After the final Chrome/AI/meeting/schedule workload, a 20-sample process series over
  19 seconds recorded working set `110,358,528–111,030,272` bytes, private bytes
  `36,511,744–37,146,624`, 25–26 threads and `0.015625` CPU seconds of change. This is
  a short post-workload observation only; artifact:
  `docs/test-artifacts/20261002-final-live-resource-sample.json`.
- After the final port cleanup, PID `23212` connected Redis/WebSocket, rejoined five
  clans and produced empty stderr. A 20-sample idle observation over 19 seconds
  recorded working set `88,104,960–97,460,224` bytes, private bytes
  `26,050,560–35,471,360`, 24–25 threads and `0.109375` CPU seconds of change;
  artifact: `docs/test-artifacts/20261002-port-cleanup-resource-sample.json`.
- The critical PostgreSQL meeting/Agent/outbox group passed 20 consecutive runs,
  280 case executions in total with zero failures, while the live worker was stopped
  to avoid fixture interference. The same Release binary then restarted as PID
  `19072`, connected Redis/WebSocket, rejoined five clans and passed the database
  assertion with empty stderr.
- The eight transcript client cases and 14 critical PostgreSQL cases then passed 20
  combined runs, 440 case executions in total with zero failures. PID `13064` runs the
  resulting Release binary; its current startup log shows Redis/WebSocket connected,
  five clans rejoined, no warning/failure marker and empty stderr. This is bounded
  correctness and short runtime evidence, not a full-stack soak.
- A fresh 20-sample, 19-second observation of PID `13064` recorded working set
  `95,625,216–96,366,592` bytes, private bytes `31,576,064–32,559,104`, 26–27
  threads, 605–608 handles and `0.03125` CPU seconds of change. Artifact:
  `docs/test-artifacts/20261002-summary-timeout-resource-sample.json`.
- The summary maintenance worker no longer leases up to 32 rows before sequential
  HTTP processing. It claims one row immediately before work and retains the existing
  32-item cycle cap, preventing later rows from expiring before processing starts.
  The red test observed a single claim limit of 32; the green test observed
  `[1, 1, 1]` for two rows and the terminating empty claim. That suite passed
  `177/177`; the transcript, lease and critical PostgreSQL group passed 20 combined
  runs, 460 case executions with zero failures.
- PID `39592` runs the resulting binary. A 20-sample, 19-second observation recorded
  working set `85,319,680–86,343,680` bytes, private bytes
  `24,547,328–25,513,984`, 37–38 threads, 674–679 handles and `0.015625` CPU
  seconds of change. Artifact:
  `docs/test-artifacts/20261002-summary-lease-resource-sample.json`.
- Startup clan joins now retain the successful first-pass ID set and send the
  post-discovery batch only for failed or newly discovered clans. This removes one
  complete Monze join pass at the 1,000-clan target while retaining full-registry
  reconnect behavior. The current suite passed `178/178`; PID `21972` logged one
  Monze startup batch of five successful joins and no second batch after discovery.
  SDK-internal seed joins are outside that Monze batch count.
- A 20-sample, 19-second observation of PID `21972` recorded working set
  `85,204,992–88,215,552` bytes, private bytes `23,572,480–26,578,944`, 36–37
  threads, 668–671 handles and `0.046875` CPU seconds of change. Artifact:
  `docs/test-artifacts/20261002-clan-join-resource-sample.json`.
- Summary maintenance now has four fixed lanes instead of one sequential lane. Each
  lane claims one lease just before external work, and a shared atomic counter keeps
  the existing 32-item cycle cap. The focused worker plus real PostgreSQL claim test
  passed `3/3`; four concurrent `SKIP LOCKED` calls returned distinct rooms and lease
  tokens. That DB/Redis checkpoint passed `180/180`, and the 25-case critical group
  passed 20 runs (`500/500`) without an intermittent failure.
- Final PID `30088` connected Redis/WebSocket, joined five clans in one Monze batch,
  and produced empty stderr. Its 20-sample, 19-second observation recorded working
  set `84,996,096–88,375,296` bytes, private bytes
  `23,773,184–26,861,568`, 36–37 threads, 671–676 handles and `0.0625` CPU seconds
  of change. This remains a short idle observation, not a full-stack soak.
- Global Agent SSE traffic is now scoped before any PostgreSQL inbox claim. The
  callback accepts only a resolved voice channel in the durable known-clan set,
  validates an optional payload clan, uses the SDK channel cache before one direct
  channel-detail lookup and retries only transient lookup failures. Old unmatched
  Agent rows were removed by append-only migration `028_agent_event_scope`; a unique
  `(room_id, event_type)` index prevents concurrent duplicate pending transitions.
- The post-build PostgreSQL and real two-instance Redis suite passed `196/196`. The
  43-case transcript, worker, Agent-scope and PostgreSQL meeting/outbox group passed
  20 independent processes (`860/860`) without an intermittent failure. The final
  database state had zero Agent inbox rows, zero pending Agent rows and zero duplicate
  pending keys.
- The 2026-10-04 two-account continuation increased the current PostgreSQL/Redis suite
  to `200/200`. Voice-profile event construction plus bounded queue roundtrip measured
  0 B after warmup and passed 20 independent test processes (`40/40`). Live session
  `3620` completed with two sent reply-bound summary messages; the runtime sample and
  participant correction are recorded in
  `docs/test-artifacts/20261004-multispeaker-agent-summary-verification.md`.
- PID `27144` connected Redis and the dev WebSocket and joined five clans with zero
  warning, failure or stderr records in the captured canary. Its 20-sample,
  19-second observation recorded working set `94,851,072–104,038,400` bytes, private
  bytes `32,137,216–40,382,464`, 24–28 threads, 613–637 handles and `0.234375` CPU
  seconds of change. The working set was non-monotonic and the sample remains too
  short for a leak or capacity conclusion.

## Ingress port extraction and final runtime check (14:14, 2026-09-28)

- Added and wired `IEventIngressQueue<T>`; `PartitionedIngressQueue<T>` remains the fixed-capacity adapter and `MonzeBot` now consumes the application port.
- Release build completed with 0 warnings and 0 errors. Normal and DB-enabled Monze suites passed **51/51** each.
- Final startup at `14:14:15.940 +07:00` selected Redis, connected through WebSocket, discovered 5 clans and rejoined 5 clans. The clean 23-line segment has zero failure, warning, ingress-drop, Agent-worker or meeting-worker markers; `inspect-db.ps1 -Assert` passed.
