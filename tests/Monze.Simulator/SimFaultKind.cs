namespace Monze.Simulator;

/// <summary>Scripted misbehaviour of the simulated platform.</summary>
public enum SimFaultKind
{
    /// <summary>Handle the operation only after a delay.</summary>
    Delay,

    /// <summary>Answer with an error status (API code, Error envelope or HTTP status) and change nothing.</summary>
    Error,

    /// <summary>Apply the operation but never send the acknowledgement or response.</summary>
    DropResponse,

    /// <summary>Close the socket instead of handling the operation.</summary>
    CloseSocket,

    /// <summary>Refuse the socket handshake.</summary>
    RefuseConnect,

    /// <summary>Deliver a push twice.</summary>
    DuplicatePush,

    /// <summary>Hold a push and deliver it after the next push.</summary>
    ReorderPush,

    /// <summary>Never deliver a push.</summary>
    DropPush
}
