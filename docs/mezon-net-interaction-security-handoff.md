# Interaction actor authentication and ephemeral delivery handoff

## Current source and package contract

- `mezon-api/server/api_interactive_message.go` copies a button event and replaces
  its incoming `UserId` with the authenticated request-context user. The dropdown
  path derives `UserId` from the same context. The backend regression test sends a
  forged button user ID and asserts that it is replaced without mutating the input.
- The reviewed SDK source marks routed button and dropdown actors as
  `InteractionActorTrust.ServerAuthenticated`. Its protected routes reject actors
  that do not carry that provenance.
- Monze consumes `Mezon.Net.Sdk`, `Mezon.Net.Sdk.Caching.Redis`, and
  `Mezon.Net.Sdk.Caching.Sqlite` 1.6.2. The published package exposes
  `Channel.SendEphemeralAsync`, `Channel.UpdateEphemeralAsync`,
  `Channel.DeleteEphemeralAsync`, and matching interaction-context update/delete
  methods.
- Restore, lock-file inspection, Release build, and the 142-test Monze suite verify
  package consumption and the public surface. They do not prove which backend or
  web bundle is currently deployed in the dev environment.

## Historical live evidence from 1.6.1

- A two-user Chrome test in clan `2104288434238525440` showed recipient isolation
  for command responses: each user saw only their own ephemeral response.
- The dev deployment used in that run did not deliver a new Monze response for a
  visible help or schedule button click. This remains historical deployment evidence,
  not evidence about the current 1.6.2 binary or current backend deployment.
- Some ephemeral operations returned an empty `ChannelMessageAckResponse`. Monze now
  treats that value as message ID `0` instead of dereferencing it. Without a message
  ID, reliable update/delete of a loading or interactive message is still impossible.

The source path has two distinct directions. `mezon-api/server/core_channel.go`
contains user-to-bot ephemeral delivery. The realtime server handles bot-to-user
ephemeral envelopes and recipient IDs. The historical two-user check covered the
bot-to-user direction only.

## Monze behavior

Only responses containing interactive components are sent as ephemeral messages.
Avatar output and Agent meeting summaries remain ordinary channel messages. When a
user operates a component, Monze updates or deletes the same private message and
binds it to the initiating clan, channel, message, and user.

Every registered state-changing route requires a server-authenticated actor at the
SDK router boundary. The Monze handler then repeats the private-message ownership
check. Owner/admin predicates for welcome, role, and delegate writes are evaluated
again in PostgreSQL. Meeting schedule listing and cancellation derive authorization
inside their repository operation rather than trusting an earlier boolean result.

## Remaining live acceptance gate

Run a fresh test with the exact 1.6.2 package, the intended backend deployment, and
two real users in one dev clan:

1. Send one interactive response to each user and prove recipient isolation.
2. Click a button with a forged client `user_id`; prove the bot receives the
   authenticated user and rejects the other user's private message.
3. Repeat for dropdown/radio input.
4. Verify send, update, and delete each return a usable message ID and affect only
   the initiating user's ephemeral message.
5. Verify PostgreSQL changes only for the authorized clan and user.

An SDK unit test, socket acknowledgement, or successful build alone does not close
this live deployment gate.
