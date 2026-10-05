# Mezon web SFU metadata handoff

Monze did not modify the `mezon` or `mezon-js` repositories. This handoff records a
browser error found while replaying real Agent start and stop cycles for Monze.

## Reproduction and observed result

In the authorized dev clan, the owner joined a voice channel and enabled the Agent.
The browser logged `JSON Parse failed completely: Object` 109 times between
`2026-10-01T20:54:01.949Z` and `2026-10-01T21:01:23.311Z`. The first error occurred
158 ms after `meet/handleAddAgentToVoice`; subsequent groups repeated every five
seconds while the SFU participant was active. The separate member browser, which did
not run the Agent flow, had zero error entries.

The sanitized browser record is
`docs/test-artifacts/20261002-agent-browser-console.json`.

## Source-level cause

The current source has two incompatible metadata contracts:

1. `mezon/apps/chat/src/app/pages/channel/MezonSfuChannelVoice.tsx:227` and
   `mezon/apps/chat/src/app/pages/meeting/index.tsx:192` produce metadata as
   `username;avatar`.
2. `mezon/libs/components/src/lib/components/MezonSfuVoiceChannel/MyVideoConference/remoteMediaLifecycle.ts:66`
   passes every non-empty `peer.metadata` value to `safeJSONParse` and expects an
   object with `username` and `avatar`.
3. `mezon-js/packages/mezon-sdk/src/utils.ts:79` emits the exact observed console
   error after the raw and newline-escaped JSON attempts fail.
4. `MezonSfuVoiceRoom.tsx:1314-1320` runs remote-media synchronization every five
   seconds, which explains the recurring error interval.

Monze is outside this producer/consumer path. It does not create meet tokens, join
the SFU, or populate peer metadata. Its Agent path subscribes to Agent SSE lifecycle
events and updates channel messages.

## Required upstream change

Keep mixed deployed versions compatible. The smallest safe change is a dedicated
peer-metadata decoder that:

- accepts the intended JSON object form;
- accepts the deployed legacy `username;avatar` form without calling
  `safeJSONParse` first;
- treats empty metadata as absent;
- returns a bounded fallback for malformed input without logging on every media
  synchronization tick.

After the tolerant consumer is deployed, producers can move to one canonical JSON
object contract in a coordinated release. Changing only the producer would leave old
clients and cached tokens exposed to the mismatch.

## Required verification before release

- Unit tests for JSON, legacy delimiter, empty and malformed metadata.
- A regression test proving repeated `mergeRemotePeerState` calls do not emit console
  errors for legacy metadata.
- Scoped chat build and lint using the repository's Yarn 1 workflow.
- Browser replay for voice join, Agent on/off and five-second media synchronization,
  with zero `JSON Parse failed completely` errors.
- A mixed-version test covering an old producer with the new consumer.

