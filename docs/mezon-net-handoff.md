# Mezon.Net handoff

This Monze change does not modify upstream repositories. The following items need a separate Mezon.Net implementation and release before claiming end-to-end zero allocation:

1. Preserve protobuf frame length; do not trim valid trailing 0x00 bytes.
2. Bound realtime dispatch and outbound send queues while preserving required ordering.
3. Fix pooled buffer ownership and disposal on reconnect and cancellation.
4. Remove fixed response buffer allocations and redundant outbound protobuf copies.
5. Cancel correlation timeout timers after successful completion.
6. Fix ChannelSendQueue prune/dispose races.
7. Bound SQLite write pumps and verify message cache lifetime.
8. Make Agent SSE reconnect/dispose await the active loop. The local SDK now implements awaited async disposal and `MezonClient.DisposeAsync` awaits it; publish and package-restore verification are still required for the 1.5.1 gate.
9. Avoid eager nested message decoding when the consumer does not need it.
10. Add benchmarks for frame decode, event bursts, cache hits, reconnect soak and idle memory.
11. Expose a clan-wide voice occupancy snapshot, or a bounded equivalent, so meeting selection does not need one API request per voice channel after reconnect.

Completed in the local SDK work for the planned 1.5.1 release:

- `MessageContentBuilder` and `MessageContentCodec` cover the typed message roots used by the current Mezon UI: text and markers, embeds, action rows, poll metadata, canvas metadata, call logs, message flags and upload metadata.
- Embed fields preserve `shape`, field buttons and unknown field extensions. Component builders accept typed components and `UnknownMessageComponent` for forward-compatible payloads.
- SDK tests cover canonical poll answer objects, call-log metadata, canvas values, legacy marker roots, typed embed inputs, radio `extraData`, embed controls, unknown components, awaited Agent SSE disposal and interaction actor provenance. `MessagePollBuilder` and `MessageCallLogBuilder` now provide typed construction for the remaining root codecs, while `MessageEmbedBuilder.AddInputField` covers the radio/select/input embed-field path used by Mezube. The current focused Release verification is **81 SDK tests** and **61 client tests** passed. The SDK test project still reports existing xUnit analyzer warnings for `ConfigureAwait(false)` usage; they do not fail the test run.

Builder extensions now reject names already owned by typed root, embed, field or markdown properties. The serializer also rejects duplicate typed properties from direct model construction. This prevents duplicate JSON keys while retaining forward compatibility for unknown names. Regression tests cover the builder and direct-model rejection paths.
- Unknown future root properties remain available through the raw extension path. This is intentional so the 1.5.1 builder does not silently discard server fields that were not present in the reviewed UI contract.

Release gate for 1.5.1: run the full Mezon.Net solution checks, publish the package, change Monze `UseLocalMezonSdk` to `false`, pin all three SDK package references to the published version, restore from NuGet and rerun the Monze build, tests and live smoke checks.

## SDK 1.6.0 interaction boundary

Monze now consumes `Mezon.Net.Sdk`, `Mezon.Net.Sdk.Caching.Redis` and `Mezon.Net.Sdk.Caching.Sqlite` 1.6.0 from NuGet. The published SDK can serialize and render radio inputs in embed fields, but its interaction router still creates button interactions with `InteractionActorTrust.ClientSupplied`; only dropdown events are server-authenticated. Monze therefore renders the interactive message but rejects button interactions before any form update or PostgreSQL write. A future SDK release must add a server-authenticated button/form event, or an equivalent signed interaction contract, before Monze can safely process button interactions.

Monze measures only the callback-to-enqueue segment. The upstream SDK segment must be measured separately from the published 1.6.0 package. Monze's package references are pinned to 1.6.0; a future SDK update must be restored from NuGet before the corresponding live smoke checks.

Interactive authorization remains a contract handoff: `MessageButtonClick` currently forwards the client supplied `user_id`, and the current web select control also calls that button path. Although the separate dropdown endpoint binds the actor from the authenticated request context, it is not the path used by the current web message select. Monze therefore uses chat commands for privileged mutations during this phase. Welcome renders an embed and a non-mutating help button until the UI and upstream event carry a server authenticated actor field end-to-end.

The local web source now corrects this mismatch: `MessageSelect` dispatches a dedicated `clickDropdownBoxSelected` store action, which calls the existing `mezon-js` `dropdownBoxSelected` method. The production chat bundle build passed, but the authorized dev site was still serving the earlier bundle during the Chrome smoke. Republish the web bundle, then verify that the browser request reaches `DropdownBoxSelected`, the SDK emits `SelectInteraction` with `ServerAuthenticated`, and the welcome settings row changes. Do not replace this with a privileged `MessageButtonClick` fallback.

The local SDK now carries actor provenance through `IInteractionActor`. `HandleButtonAsync` marks the actor as `ClientSupplied`; `HandleSelectAsync` marks the actor as `ServerAuthenticated` because the reviewed backend dropdown handler replaces `UserId` from the authenticated request context. Routes that change state can call `RequireServerAuthenticatedActor()`, which rejects a button event before invoking the handler. Monze has not enabled a welcome mutation route because the current web UI still sends its select through `MessageButtonClick`; the upstream and frontend handoff must be completed before enabling it.

The current Monze verification consumes the published NuGet 1.6.0 package. The package release gate for the actor-authentication fix remains: publish a new SDK version, keep Monze on the new package references, and rerun the SDK, Monze, database-contract and Chrome checks.
