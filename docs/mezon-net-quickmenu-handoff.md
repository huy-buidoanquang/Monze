# Mezon.Net quick menu handoff

## Current Monze status

Monze does not provision or execute quick-menu actions. The intended menu set remains:

- `AI summary`
- `AI translate`
- `AI composer`
- `AI simplify`

Enabling the feature requires a bounded provisioning worker, reconciliation by clan
and channel, authenticated actor checks, AI budget/rate limits, source-message scope,
and durable idempotency. Those product changes are outside the current retained
command implementation.

## SDK 1.6.2 prerequisite status

The SDK limitation recorded for 1.6.0 is resolved in the reviewed 1.6.2 surface:

- `MezonClient.QuickMenuReceivedData` exposes a typed
  `QuickMenuReceivedEventData` callback while retaining the parameterless event for
  compatibility.
- The payload preserves `MenuName`, source `Message`, `HasMessage`, `SenderId`,
  `MessageSenderId`, `MessageId`, `ClanId`, and `ChannelId`.
- `MezonClient.ListQuickMenuAccessAsync` is public alongside add, update, and delete.
- Client tests cover payload dispatch, malformed/empty data, handler isolation, and
  compatibility delivery in the reviewed source tree.

Monze restore and Release build consume the published 1.6.2 package. The Monze SDK
surface test checks the public list method; live quick-menu delivery has not been
verified against the current dev backend and web bundle.

## Remaining implementation and acceptance gate

Before Monze enables AI quick menus:

1. Reconcile existing menus with `ListQuickMenuAccessAsync`; do not blindly add on
   every startup.
2. Subscribe to `QuickMenuReceivedData` and reject events without a source message,
   positive clan/channel/message IDs, or an authenticated actor contract.
3. Verify the source message belongs to the event clan/channel and remains visible to
   the caller.
4. Apply the existing AI concurrency, rate, and budget checks before provider calls.
5. Persist a dedupe key so replayed events cannot spend budget or post twice.
6. Bound provisioning and execution queues, retries, payload size, and cancellation.

The live canary must select each AI menu from a real source message and prove that
menu name, source message ID, authenticated user, clan, and channel reach Monze
together. PostgreSQL usage and the visible result must each occur once under replay.
Package compilation or a callback without those fields is insufficient evidence.
