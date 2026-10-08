using Google.Protobuf;
using Mezon.Net.Internal.Api;

namespace Monze.Simulator;

/// <summary>
/// One entry of the <see cref="SimRecorder"/> log: an outbound bot action as
/// the simulated platform saw it on the wire (or an inbound push / server
/// close, see <see cref="SimActionKind"/>), with the decoded target ids,
/// content JSON, reply target, mentions, ephemeral receivers, the status the
/// simulator answered with and the fault it applied.
/// </summary>
public sealed record SimAction
{
    /// <summary>Position in the log; strictly increasing.</summary>
    public long Sequence { get; init; }

    public DateTimeOffset At { get; init; }

    public required SimActionKind Kind { get; init; }

    /// <summary>Wire operation (<see cref="SimOperations"/>) or push kind name.</summary>
    public required string Operation { get; init; }

    /// <summary>The simulated socket session (0 for REST and pushes).</summary>
    public int SessionId { get; init; }

    public long ClanId { get; init; }

    public long ChannelId { get; init; }

    public long MessageId { get; init; }

    /// <summary>Ephemeral receiver, role holder or event subject.</summary>
    public long TargetUserId { get; init; }

    public long RoleId { get; init; }

    public string? ContentJson { get; init; }

    public long? ReplyToMessageId { get; init; }

    public IReadOnlyList<MessageMention> Mentions { get; init; } = [];

    /// <summary>User ids of <see cref="Mentions"/> (the @here marker is a special user id).</summary>
    public IReadOnlyList<long> MentionUserIds => Mentions.Where(static mention => mention.UserId > 0).Select(static mention => mention.UserId).ToList();

    public bool MentionEveryone { get; init; }

    public IReadOnlyList<long> ReceiverIds { get; init; } = [];

    public IReadOnlyList<long> AddedUserIds { get; init; } = [];

    public IReadOnlyList<long> RemovedUserIds { get; init; } = [];

    /// <summary>Message code on the wire (12/14/15 for ephemeral send/update/delete).</summary>
    public int Code { get; init; }

    /// <summary>Status the simulator answered with (MezonStatusCode value; 0 is success).</summary>
    public int ResponseCode { get; init; }

    /// <summary>Sessions a push targeted.</summary>
    public int Sessions { get; init; }

    public SimFaultKind? Fault { get; init; }

    /// <summary>The decoded request (or pushed envelope) for detailed assertions.</summary>
    public IMessage? Request { get; init; }

    /// <summary>Whether this is something the bot sent (not a push or server close).</summary>
    public bool IsOutbound => Kind is not (SimActionKind.Push or SimActionKind.ServerClose);

    public override string ToString()
        => $"#{Sequence} {Kind} {Operation} s{SessionId} clan={ClanId} channel={ChannelId} message={MessageId} target={TargetUserId} code={Code} status={ResponseCode}{(Fault is null ? string.Empty : $" fault={Fault}")}";
}
