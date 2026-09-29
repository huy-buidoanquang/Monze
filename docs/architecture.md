# Monze architecture

## Layer boundaries

- `Monze.Domain` contains domain values, parsers, state rules and calculations. It does not reference PostgreSQL, Redis, Mezon.Net or hosting.
- `Monze.Application` contains command orchestration and ports. Each persistence capability has a focused repository interface under `Repositories`.
- `Monze.Infrastructure` contains PostgreSQL repositories, cache adapters, migration code and external clients. PostgreSQL is the business source of truth.
- The host root contains Mezon.Net event adapters and bounded workers. `MonzeBot` receives the repository ports it needs instead of a composite persistence store.

## Persistence ports

The runtime registers one concrete repository for each application port:

| Port | PostgreSQL implementation | Transaction responsibility |
| --- | --- | --- |
| `IClanRegistryRepository` | `PostgresClanRegistryRepository` | Registry scan and discovery state |
| `IAuthorizationRepository` | `PostgresAuthorizationRepository` | Owner, delegate, welcome and role policy writes |
| `IMeetingRepository` | `PostgresMeetingRepository` | Meeting state, claims, inbox and summaries |
| `ISchedulingRepository` | `PostgresSchedulingRepository` | Durable schedule lease and occurrence state |
| `IOutboxRepository` | `PostgresOutboxRepository` | Delivery claim, lease and completion |
| `IAiUsageRepository` | `PostgresAiUsageRepository` | Atomic token budget |
| `IMessageHistoryRepository` | `PostgresMessageHistoryRepository` | Message persistence policy and gap state |
| `ICommandInboxRepository` | `PostgresCommandInboxRepository` | Command message idempotency lease |

The former `IMonzeStore` aggregate was removed. This keeps dependency direction explicit, makes transaction ownership visible at the port boundary and prevents unrelated modules from depending on one large persistence surface. Outbox delivery uses partial files only to separate closely related SQL concerns; each file still declares one type.

Scheduled meeting delivery has a separate `IScheduledMeetingRepository` because one occurrence spans the schedule lease, `meeting_session`, `voice_claim` and announcement outbox. `PostgresScheduledMeetingRepository` commits those rows in one PostgreSQL transaction and checks the schedule lease token first. A voice claim conflict rolls back the session and outbox together; a replay after the schedule lease is cleared returns false.

Meeting notifications use `meeting_session.text_channel_id` as their delivery channel. The selected voice channel is included as the destination link in the invitation, while invitations, Agent status updates and summaries stay in the text channel that started the meeting. Cross-channel reply targets are discarded so an old voice notification cannot be reused by a text-channel summary.

## Runtime wiring

`MonzeBot` consumes clan, authorization, meeting, scheduling, outbox and message-history ports. `MonzeApp` consumes authorization, meeting, scheduling, AI usage and message-history ports. `MeetingMaintenanceWorker` consumes only the meeting repository and transcript client.

Every command handler claims `(clan_id, channel_id, message_id)` through `ICommandInboxRepository` before executing. A fresh lease prevents concurrent duplicate execution, a completed row suppresses replay, an exception releases the lease for retry, and a scheduled purge bounds retention. This idempotency check runs in the worker path after the realtime callback, so it does not add database work to the zero-allocation ingress callback.

Realtime callbacks remain bounded adapters. They enqueue typed events and do not call a repository, Redis, SQLite or serializer on the callback thread. Workers own I/O and cancellation. Sensitive decisions read PostgreSQL or Mezon directly; L1/L2 cache is an optimization for read models only.

Outbox delivery claims all clans by default. The repository also accepts an optional `clan_id` predicate for isolated maintenance and integration checks without changing the production worker's global throughput path. A claim changes `pending` or an expired `sending` row to `sending` with a lease token. Completion updates the row only when that token matches, so a stale worker cannot acknowledge or reschedule a reclaimed delivery. A retry without an external message ID returns to `pending` with bounded exponential backoff; a delivery failure whose result is unknown becomes `uncertain` and is left for operational reconciliation rather than blind resend. The PostgreSQL integration test covers concurrent claim, wrong-token completion, backoff, reclaim and external-ID precedence.

Outbox delivery runs in its own worker loop instead of sharing the scheduling loop. A loop drains successive 256-row claims while work is available, then waits for the configured poll interval (`Monze:Outbox:PollMilliseconds`, default 250 ms). Scheduling latency therefore does not directly delay due outbox claims, while the existing per-batch concurrency limit and PostgreSQL lease tokens remain authoritative.

Agent SSE payload extraction is an application value parser. It parses a payload once in the Agent worker and returns room, voice channel and clan identifiers together; the realtime callback only enqueues the SDK event. This keeps JSON work off the callback boundary and avoids parsing the same payload again for the meeting bind decision.

Voice selection uses a bounded per-clan async gate around the occupancy refresh and candidate scan. Concurrent `meeting` requests for different clans remain parallel, while requests for one clan cannot clear and scan the shared occupancy map at the same time. The gate is released before the durable meeting claim and announcement transaction, so PostgreSQL remains the cross-process authority.

## Verification rule

Any repository extraction must preserve the existing SQL transaction and lease semantics. The Release build must use the explicit `net10.0` project path through `scripts/build-release.ps1`, then run the full Monze tests, the PostgreSQL SQL contract test, the authorized Chrome smoke and `scripts/inspect-db.ps1 -Assert`.

## Ingress port

`IEventIngressQueue<T>` is the application port for realtime event buffering. `PartitionedIngressQueue<T>` is the bounded in-process adapter used by the current bot. `MonzeBot` depends on the port, so queue implementation and test doubles can be changed without coupling the bot lifecycle to channel construction. The adapter preserves 16 partitions, total capacity 8,192, stable per-key ordering and `TryWrite` semantics at the callback boundary.

## Runtime observability

Ingress depth is tracked with counters incremented only after a successful bounded enqueue and decremented when a worker receives an item. Observable gauges are registered once during bot construction, so metrics collection does not add allocation to the SDK callback. Outbox metrics cover claimed items, in-flight attempts, due-to-attempt lag, attempt duration, successful delivery, retryable failure and uncertain delivery.
