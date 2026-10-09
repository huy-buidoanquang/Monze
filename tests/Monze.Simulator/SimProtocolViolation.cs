namespace Monze.Simulator;

/// <summary>
/// A frame or request that does not match the SDK 1.6.2 wire contract
/// (malformed envelope, api index that does not match the api name, call
/// to a host the simulator does not own, ...). Tests assert none occurred.
/// </summary>
public sealed record SimProtocolViolation(
    DateTimeOffset At,
    int SessionId,
    string Operation,
    string Detail);
