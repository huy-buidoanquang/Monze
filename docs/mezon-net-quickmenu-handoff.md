# Mezon.Net quick menu handoff

## Current Monze status

Monze does not activate quick menu provisioning yet. The published SDK exposes
the add operation, and the backend currently de-duplicates an identical
bot/clan/channel/menu/type row. The wrapper still lacks the public read operation
needed to reconcile drift, so provisioning must be bounded and tied to a verified
bot-join or clan-registration event instead of running an unbounded startup loop.
The menu set that should be provisioned is:

- `AI summary`
- `AI translate`
- `AI composer`
- `AI simplify`

The intended provisioning uses the backend's verified quick menu type and action
contract for AI actions, provisions a newly confirmed clan once, and bounds API
calls with a small worker queue. The exact menu type, action message, and menu
scope still require a live canary before enabling the feature at 1,000-clan scale.

## SDK 1.6.0 limitation

The published SDK exposes:

```csharp
event Func<Task> QuickMenuReceived;
```

The client event loop currently sees `Envelope.MessageOneofCase.QuickMenuEvent`
but invokes that event without passing the envelope payload. The payload contains
the fields Monze needs to execute an AI action safely:

- selected `menu_name`;
- source message content and metadata;
- source `message_id`;
- source `message_sender_id`.

The canonical web client sends those fields through `writeQuickMenuEvent` when a
user chooses a right-click quick menu. Because SDK 1.6.0 discards them, Monze
cannot determine which message to summarize, which user initiated the action, or
which clan/channel must be used. Registering a parameterless callback and guessing
the last message would create a cross-user and cross-message authorization bug.

The loss is visible in `Mezon.Net/src/Mezon.Net.Client/MezonClient.EventHandling.cs`:
the `QuickMenuEvent` case invokes the parameterless event only. The upstream
payload is defined in `mezon-proto-server/proto/realtime.proto` as
`QuickMenuDataEvent` and includes `menu_name`, `message`, `sender_id` and
`message_sender_id`. The low-level SDK API already has
`ListQuickMenuAccessAsync`; the public `MezonClient` wrapper currently exposes
only add, update and delete operations. The backend `AddQuickMenu` path checks
for an existing row with the same menu name, type, clan and channel before
inserting, so repeated adds are not by themselves a correctness reason to
duplicate rows. A public list wrapper is still needed for drift detection and
cleanup.

## Required SDK change before AI quick menu execution

Publish the next SDK patch with a typed event preserving the decoded payload and
the authenticated event context, for example:

```csharp
event Func<QuickMenuDataEventResponse, Task> QuickMenuReceived;
```

The change must be made in the SDK event model and event dispatch path, not in
generated protobuf files. Required tests:

1. Decode a quick menu envelope and preserve menu name, message id, sender id,
   channel/clan and message metadata.
2. Dispatch the payload to every subscriber without a shared mutable buffer.
3. Verify cancellation, reconnect and handler exception isolation.
4. Verify a malformed payload is rejected without invoking Monze's AI handler.
5. Benchmark the event dispatch after warmup and record allocation/op.

The SDK patch also needs a public `ListQuickMenuAccessAsync` wrapper so Monze can
reconcile existing menus and remove drift. After that package is published,
Monze should subscribe to the typed event, validate the event clan/channel and
authenticated sender, apply the AI rate/budget checks, and use the supplied source
message as explicit input. No quick menu AI result is considered implemented until
those checks pass in a live Chrome test.

The live acceptance test must select each AI menu from a real source message and
prove that the selected menu name, source message ID, authenticated user, clan
and channel reach Monze together. A callback that fires without those values is
not sufficient for enabling the feature.
