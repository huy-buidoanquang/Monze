using Mezon.Net.Core;

namespace Monze.Simulator;

/// <summary>
/// One scripted fault: what to do (<see cref="Kind"/>) to which operation or
/// push kind (<see cref="Target"/>), with the delay or status code it uses.
/// </summary>
public sealed record SimFault(
    SimFaultKind Kind,
    string Target,
    TimeSpan Delay,
    MezonStatusCode Code,
    string? Detail);
