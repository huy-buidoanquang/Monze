# Upstream handoff from the test campaign

The test campaign (`scripts/run-test-campaign.ps1`, registry
`tests/traceability/expected-gaps.json`) found defects that Monze cannot close
on its own. Each entry names the upstream source, the evidence, the Monze test
that reproduces it, what Monze does in the meantime and when the Monze side can
change. Sources were read at Mezon.Net tag `v1.6.2` (the package Monze uses),
mezon-api `bcc6e172` and the mezon-proto-server working tree of 2026-10-09.

## 1. Button click actor is client-supplied (CAND-19, P0, mezon-api)

- **Source:** `mezon-api/server/api_interactive_message.go:10-23`.
  `MessageButtonClick` forwards the client's `MessageButtonClicked` unchanged,
  `user_id` included. `DropdownBoxSelected` (lines 25-45) replaces `user_id`
  with the session user.
- **Impact:** any member can click as another user. Monze accepts a private
  click only on the exact ephemeral message it sent to that user (block 1).
  The remaining hole needs the owner's ephemeral message id: a forged owner id
  on the owner's own card.
- **Reproduction:**
  - `tests/Monze.Tests.E2E/Areas/ForgedActorTests.cs`
  - `PrivateInteractionDecisionTableTests` (G32, row "R2 message binding, owner id forged")
- **Upstream fix:** set `UserId` from `ctx.UserValue(ctxUserIDKey{})` as the
  dropdown handler does.
- **Mezon.Net side:** commit `6f267e7` (tag `v1.7.0`; Monze still uses
  `1.6.2`) already marks button actors as client-supplied, so
  `RequireServerAuthenticatedActor()` rejects them. Once the server overwrites
  the id, a release can mark them server-authenticated again.
- **Monze change after release:** bump the SDK, require a server-authenticated
  actor for every state-changing click, and flip the CAND-19 expectation in G32.

## 2. Clan discovery returns at most 100 clans (DEF-06, mezon-api)

- **Source:**
  - `mezon-api/server/core_clan_desc.go:48-67`: `ClanDescsList` ignores
    `limit` and `cursor` from `ListClanDescRequest`, and appends `LIMIT 100`
    without `ORDER BY`, so the subset is not deterministic.
  - `server/api_clan_desc.go:38-55`: the result is cached per user regardless
    of the request.
  - `ClanDescList` has no next-cursor field.
- **Impact:** a bot in more than 100 clans never discovers the rest. Monze now
  registers every clan the list does contain (it used to register none of a
  capped list) and logs a warning when the list hits the cap. Clans that are
  already registered are rejoined at start-up whatever discovery returns.
- **Reproduction:**
  - `tests/Monze.Tests.E2E/Workflows/ScaleDefectWorkflowTests.cs` `A_bot_in_150_clans_registers_the_100_listed_and_never_sees_50_DEF_06`
  - capacity `BP-8-DEF06`
- **Upstream fix:** order by clan id and honour `limit` and `cursor`. Return
  the next cursor (a new `ClanDescList` field) and include the cursor in the
  cache key.
- **Monze change after release:** read every page in `RefreshClansAsync`.

## 3. Bot send rate versus the documented workload (DEF-13, platform)

- **Source:**
  - `mezon-proto-server/include/ratelimit.h:22-62`: a fixed 1 s window and a
    fixed 60 s window per connection, counting channel and ephemeral message
    sends.
  - `src/config.c:19-20`: the defaults are 60/s and 200/min (keys
    `rate_limit.per_second` and `rate_limit.per_minute`, where 0 disables a
    window). `dev-config.yml` sets 100/s and 500/min.
  - Over the limit, the send is answered with error 429 on the same cid
    (`src/pipeline_channel.c:184-188`).
  - No production value is in the repository.
- **Impact:** the documented workload needs about 240 requests per second
  (200 outbox messages, 20 commands, about 16 reads). The campaign measured
  this in load stage `L-V3-S10`, metrics `requests.*PerSecond`. One bot
  connection may send 200 to 500 messages per minute.
- **Monze behaviour:**
  - Monze paces its 500/min budget evenly; `Mezon:RateLimit:RequestsPerSecond`
    defaults to a sixtieth of it.
  - Bulk outbox delivery uses at most half of the budget.
  - Commands are shed beyond 64 in flight.
  - A 429 on an outbox send is retried with backoff.
- **Needed:**
  - The production `rate_limit` for bot connections. Set
    `Mezon:RateLimit:RequestsPerMinute` no higher than it.
  - Either a higher limit for bot connections, or a decision to shard the
    workload over several bot connections.
- **Until then:** no capacity claim against the documented workload (load
  stages V1 stay KNOWN_GAP DEF-13).

## 4. Agent SSE: no idle detection, no Last-Event-ID (DEF-08, Mezon.Net)

- **Source:** `src/Mezon.Net.Sdk/Agent/AgentSseManager.cs`.
  - `ReadOnceAsync` reads on an `HttpClient` with `Timeout.InfiniteTimeSpan`
    and no idle timeout.
  - The request never carries `Last-Event-ID`.
  - `MezonClient.ConnectAgentSseAsync` gives no way to supply an HttpClient.
- **Impact:** a half-open stream loses every Agent event until a restart.
- **Monze behaviour:** Monze runs its own `AgentSseManager` on its own
  `HttpClient` (`Infrastructure/Agent/AgentEventStream.cs`).
  - After two keepalives, a stream silent for three keepalive gaps (at least
    `Mezon:AgentSse:IdleTimeoutSeconds`, default 45) is ended.
  - Reconnects send the id of the last dispatched event. The id is also
    saved next to the message store once every event handed over was
    processed, so the first connect after a restart resumes after it.
  - A server that sends no keepalives is never timed out.
  - Regression: `AgentSseWorkflowTests` `A_half_open_stream_is_replaced_and_resumed_after_the_last_event`, `An_event_published_while_the_bot_is_down_is_replayed_after_a_restart`, `AgentSseResumeTests`.
- **Still unverified on the live Agent server:**
  - its keepalive interval;
  - whether it writes `id:` fields and resumes after `Last-Event-ID`.
  The 2026-10-01 probe saw `connected` and then `disconnect`.
- **Upstream fix:** an idle timeout derived from keepalives and Last-Event-ID
  resume in `AgentSseManager`, or an `HttpClient` hook on
  `ConnectAgentSseAsync`.
- **Monze change after release:** return to `ConnectAgentSseAsync` once the
  SDK behaves the same.

## 5. SQLite write pump stops after one failed batch (CAND-18, Mezon.Net)

- **Source:** `src/Mezon.Net.Sdk.Caching.Sqlite/Internal/BatchWritePump.cs`.
  - `DrainQueueAsync` rolls back a failed batch and rethrows (line 132).
  - `RunAsync` (lines 64-79) catches only cancellation, so the pump ends.
  - Later writes queue without bound and are never stored.
  - `FlushAsync` never completes, and `DisposeAsync` waits for it.
- **Reproduction:** `Monze.Tests/SqliteMessageStoreFaultTests.cs` (xfail
  CAND-18).
- **Monze behaviour:** Monze only writes message history.
  - Shutdown bounds flush and close to 5 s each and logs the loss
    (`MonzeBot.CloseMessageStoreAsync`).
  - Regression: `MessageStoreCloseTests`.
- **Upstream fix:** catch per batch, complete or fail that batch's flush
  targets, log, and keep the loop running.

## 6. One message in flight per channel (CAND-20, Mezon.Net)

- **Source:** `src/Mezon.Net.Sdk/Caching/ChannelSendQueue.cs`. A
  `SemaphoreSlim(1)` per channel wraps every send, update and reply from
  `Entities/Channel.cs` and `Entities/Message.cs`, so one channel carries at
  most one message per ack round trip.
- **Evidence:** load stage `L-V3L-S10`, 10 outbox channels, ack p50 30 ms.
  - Outbox concurrency 32 gave 155/s (each attempt 151 ms).
  - Concurrency 128 gave 174/s (each attempt 492 ms).
  - The socket receive loop was busy 0.5 % of the time, and the thread pool
    was not queued.
- **Upstream fix:** pipeline several sends per channel while keeping their
  order (sequence numbers), or document the 1/RTT ceiling per channel.
- **Monze:** nothing to change. The outbox is already a pipeline.

## 7. Per-channel message cache grows with the number of channels (CAND-31, Mezon.Net)

- **Source:** `src/Mezon.Net.Sdk/Entities/Channel.cs` creates
  `Messages = new EntityCache<Message>(Options.CacheCapacity)` for every
  channel. `CacheCapacity` (default 512) also sizes the clan, channel, role
  and user caches, and nothing bounds the total.
- **Impact:** retained memory grows towards active channels × 512 messages
  (about 0.4 KB each with the protobuf). The 120-minute soak (1,000 clans,
  V3L) gained 84.5 MiB/h of heap in the baseline.
- **Evidence:** heap histograms of the soak on the fixed build, minutes 30
  and 40: +20.6 MiB in total, of which `ChannelMessage`, `Message`, the
  `EntityCache<Message>` nodes and their strings are +30,189 entries
  (about 12 MiB); Monze's own structures are flat.
- **Upstream fix:** a separate message cache capacity (Monze keeps its own
  history in `SqliteMessageStore` and needs few or none), or a global bound
  across channels.
- **Monze:** nothing to change before that. Lowering `CacheCapacity` would
  also shrink the clan and channel caches below 1,000 clans.
