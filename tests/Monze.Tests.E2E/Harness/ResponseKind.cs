namespace Monze.Tests.E2E.Harness;

/// <summary>
/// The bot output one input (command message or click) must produce, as
/// counted by <see cref="E2EOracles"/>. A reply that is later edited, or an
/// ephemeral that is later updated or deleted by clicks, is still one response.
/// </summary>
internal enum ResponseKind
{
    /// <summary>No message, edit or delete at all (ignored or refused silently).</summary>
    None,

    /// <summary>One new ephemeral message to the actor, nothing public.</summary>
    Ephemeral,

    /// <summary>One new public message replying to the command message.</summary>
    Reply,

    /// <summary>One new public message that is not a reply (e.g. the meeting suggestion).</summary>
    Public,

    /// <summary>One public reply that is then edited in place (AI loading card, then the result).</summary>
    EditedReply,

    /// <summary>A click: exactly one update of the clicked ephemeral, sent to the actor.</summary>
    Update,

    /// <summary>A click: exactly one delete of the clicked ephemeral, sent to the actor.</summary>
    Delete,

    /// <summary>A click: one new public message plus one update of the clicked ephemeral.</summary>
    UpdateAndPublic,

    /// <summary>
    /// A command message delivered again (gateway redelivery, second
    /// instance): no output, and its single command_inbox row is left as it was.
    /// </summary>
    Duplicate,

    /// <summary>
    /// A click the bot accepted: exactly one update of the clicked message to
    /// the actor, which the platform refused (the message is not the actor's
    /// ephemeral). Used to record forged clicks Monze lets through.
    /// </summary>
    UpdateRejected
}
