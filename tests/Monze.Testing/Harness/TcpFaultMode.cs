namespace Monze.Testing.Harness;

/// <summary>What a <see cref="TcpFaultProxy"/> does with traffic.</summary>
public enum TcpFaultMode
{
    /// <summary>Forward bytes, after <see cref="TcpFaultProxy.Latency"/> per chunk.</summary>
    Pass,

    /// <summary>
    /// A network partition: connections stay open (new ones are accepted) but
    /// no byte is forwarded in either direction. Bytes already read are held,
    /// not dropped, and flow again when the mode returns to <see cref="Pass"/>.
    /// </summary>
    Blackhole,

    /// <summary>New connections are accepted and reset at once; open ones keep working.</summary>
    Refuse
}
