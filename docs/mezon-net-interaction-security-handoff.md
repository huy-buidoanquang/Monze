# Interaction actor authentication and ephemeral delivery handoff

## Evidence

- `mezon-api/server/api_interactive_message.go` forwards `MessageButtonClicked`
  with the incoming `UserId` unchanged. `DropdownBoxSelected` replaces the
  incoming user ID with the authenticated request user ID.
- `Mezon.Net/src/Mezon.Net.Sdk/Interactions/InteractionRouter.cs` constructs
  `ButtonInteraction` with `InteractionActorTrust.ClientSupplied`.
- The same router marks `SelectInteraction` as
  `InteractionActorTrust.ServerAuthenticated`.
- `Mezon.Net` 1.6.1 exposes `Channel.SendEphemeralAsync`,
  `Channel.UpdateEphemeralAsync`, and `Channel.DeleteEphemeralAsync`. It sends an
  `ephemeral_message_send` envelope through the socket and returns the server
  acknowledgement.
- Live Monze 1.6.1 tests in clan `2104288434238525440` proved recipient
  isolation for command responses: the actor saw the help and meeting
  responses in Chrome, while a second user in the same channel saw only their
  own command and no private response.
- The current dev backend did not produce a new Monze response when either
  Chrome user clicked a visible help or schedule button. This is an unresolved
  deployment-contract result, not evidence that the button handler is safe or
  functional in production.
- A later 1.6.1 live run reproduced an empty `ChannelMessageAckResponse` for
  some ephemeral operations. Reading `MessageId` from that default value threw
  in Monze before the guard was added. The guard now treats an empty ACK as
  message ID `0`; the regression test passes, but an empty ACK still prevents
  reliable loading-message update/delete because there is no message ID.

The source path distinguishes the two directions. `mezon-api/server/core_channel.go`
contains the user-to-bot `sendEphemeralMessageToBot` path, while the realtime
server handles the bot-to-user envelope with message code `12` and the supplied
recipient IDs. The live test above validates the latter path for the published
1.6.1 package.

## Monze behavior

Monze sends every response containing components through
`Channel.SendEphemeralAsync`. Private avatar responses also use that path.
When a user operates a component, Monze sends a new private response and binds
the returned message ID to the same actor. SDK 1.6.1 exposes public
ephemeral update/delete methods, so Monze does not use a standard-message update
as a privacy fallback.

Every registered Monze button route requires a server-authenticated actor at
the SDK router boundary, and the handler repeats the ownership check using the
bound clan, channel, message and user IDs. It rejects an interaction when the
actor is missing, mismatched, or not server authenticated. Dropdown interaction
continues only when the SDK marks it server authenticated.

The workspace now contains the matching upstream patch: `mezon-api` replaces
the incoming button `UserId` with the authenticated request user, and the SDK
marks the decoded button actor server authenticated. The SDK build and its
four actor-trust tests pass. The API regression test is present. It was run
with a temporary local replace for `mezon-cache`, but the `server` package is
currently blocked by pre-existing compile errors in `api_channel.go`
(`ValidateChannelTopic`) and `consumer.go` (undefined `err`); those files were
already dirty and were not changed by this patch. The published 1.6.1 package
and the dev backend have not been replaced by these source changes, so the
Chrome button flow remains unverified until both services are deployed.

State-changing welcome, role, and delegate writes also repeat the owner/admin
predicate inside PostgreSQL. Meeting schedule listing and cancellation derive
the same predicate in their SQL instead of trusting an `includeAll` flag that
was computed by an earlier authorization query. This closes the revocation
race between a permission check and the write or cancellation.

## Required upstream contract follow-up for button flows

1. Derive `UserId` from the authenticated request context in
   `ApiServer.MessageButtonClick`, matching `DropdownBoxSelected`.
2. Add a regression test that sends a mismatched incoming user ID and asserts
   the emitted notification contains the authenticated caller ID.
3. Mark the decoded button actor server authenticated in Mezon.Net only after
   the backend contract is deployed.
4. Publish the SDK change, update Monze, and live-test cross-user and
   cross-clan button clicks with the exact published package.

## Required upstream contract follow-up for ephemeral responses

Keep the public ephemeral update and delete operations covered by tests with an
authenticated bot sender, recipient ID, message ID, and recipient isolation.
The bot-to-user ephemeral send, update, and delete operations exist in 1.6.1,
but the live empty-ACK case still needs a reliable server acknowledgement with
the created message ID.

The acceptance test for the remaining SDK work must use two real users in one
dev clan: the sender must observe its own response, the second user must not
observe it, and update/delete must affect only the sender's response. A
successful acknowledgement on the bot socket alone is insufficient.
