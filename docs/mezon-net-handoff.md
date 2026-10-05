# Mezon.Net handoff

This Monze change does not modify upstream repositories. Monze currently consumes
the published `Mezon.Net.Sdk`, `Mezon.Net.Sdk.Caching.Redis` and
`Mezon.Net.Sdk.Caching.Sqlite` packages at `1.6.2`. The following items still need
a separate upstream implementation and release before claiming end-to-end zero
allocation or complete 1,000-clan recovery:

1. Preserve protobuf frame length; do not trim valid trailing 0x00 bytes.
2. Bound realtime dispatch and outbound send queues while preserving required ordering.
3. Fix pooled buffer ownership and disposal on reconnect and cancellation.
4. Remove fixed response buffer allocations and redundant outbound protobuf copies.
5. Cancel correlation timeout timers after successful completion.
6. Fix ChannelSendQueue prune/dispose races.
7. Bound SQLite write pumps and verify message cache lifetime.
8. Keep Agent SSE reconnect/dispose bounded and await the active loop; repeat the
   reconnect soak against each published package rather than relying on local source.
9. Avoid eager nested message decoding when the consumer does not need it.
10. Add benchmarks for frame decode, event bursts, cache hits, reconnect soak and idle memory.
11. Expose a clan-wide voice occupancy snapshot, or a bounded equivalent, so meeting selection does not need one API request per voice channel after reconnect.

Historical builder work completed before the current `1.6.2` package:

- `MessageContentBuilder` and `MessageContentCodec` cover the typed message roots used by the current Mezon UI: text and markers, embeds, action rows, poll metadata, canvas metadata, call logs, message flags and upload metadata.
- Embed fields preserve `shape`, field buttons and unknown field extensions. Component builders accept typed components and `UnknownMessageComponent` for forward-compatible payloads.
- SDK tests cover canonical poll answer objects, call-log metadata, canvas values, legacy marker roots, typed embed inputs, radio `extraData`, embed controls, unknown components, awaited Agent SSE disposal and interaction actor provenance. `MessagePollBuilder` and `MessageCallLogBuilder` now provide typed construction for the remaining root codecs, while `MessageEmbedBuilder.AddInputField` covers the radio/select/input embed-field path used by Mezube. The current focused Release verification is **81 SDK tests** and **61 client tests** passed. The SDK test project still reports existing xUnit analyzer warnings for `ConfigureAwait(false)` usage; they do not fail the test run.

Builder extensions now reject names already owned by typed root, embed, field or markdown properties. The serializer also rejects duplicate typed properties from direct model construction. This prevents duplicate JSON keys while retaining forward compatibility for unknown names. Regression tests cover the builder and direct-model rejection paths.
- Unknown future root properties remain available through the raw extension path. This is intentional so the 1.5.1 builder does not silently discard server fields that were not present in the reviewed UI contract.

The current package gate is satisfied at restore/build level: all Monze package
references and lock files resolve to `1.6.2`. Live canary and full-stack performance
remain separate gates.

## SDK 1.6.2 interaction actor boundary

Current source inspection supersedes the older 1.6.0 note. In the reviewed backend,
both button and dropdown handlers replace the client-supplied user ID with the
authenticated request-context user. The reviewed SDK source used for 1.6.2 marks
routed button and dropdown actors as `InteractionActorTrust.ServerAuthenticated`.
Monze additionally binds private interaction messages to the initiating user before
running a handler.

This establishes the reviewed source contract; it is not a substitute for a final
browser replay. Any future backend or SDK change to actor provenance must retain a
test that a forged `user_id` is replaced before the Monze handler is invoked.

Monze measures only the callback-to-enqueue segment. SDK allocations and the complete
browser to backend to socket path must still be measured separately against the
published package.

## Clan roster bot identity boundary

The canonical `api.User` used by `ClanUserList` has no `is_bot` field. The backend
`ListClanUsers` query returns at most `MAX_USER_CHANNEL` recent members and does not
join the `apps` table. By contrast, realtime `UserProfileRedis` carries `is_bot`, so
Monze can safely skip bot accounts on a live `ClanUserAdded` event but cannot prove
the same property during a periodic tenure scan.

Required upstream contract, in preference order:

1. Add a backward-compatible `is_bot` field to the canonical clan-user response and
   populate it from the authoritative app relation; or
2. Expose a bounded clan app-ID snapshot through the SDK facade so Monze can exclude
   those IDs without one request per member.

Required tests before Monze can close this gap:

- a clan roster containing a normal user and an app returns distinct bot identity;
- pagination or an equivalent complete snapshot covers clans above the current cap;
- periodic role automation skips the app while still evaluating eligible users;
- reconnect and cache refresh cannot turn an unknown member into a trusted non-bot.
