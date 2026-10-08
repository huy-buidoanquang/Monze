namespace Monze.Simulator;

/// <summary>
/// Receipt of one inbound push: how many bot sessions were eligible (joined
/// the clan, or addressed directly), how many had the frame handed to the SDK,
/// the message id for chat pushes and the push fault that was applied.
/// </summary>
public sealed record SimPush(
    long Sequence,
    SimPushKind Kind,
    int TargetSessions,
    int DeliveredSessions,
    long MessageId,
    SimFaultKind? Fault);
