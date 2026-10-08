namespace Monze.Testing.Harness;

/// <summary>
/// A throwaway campaign container. Ports are always published on 127.0.0.1;
/// <see cref="Environment"/> values are handed to docker through its own
/// environment (only the names appear on the command line), so a password
/// never shows in a process listing or an error message.
/// </summary>
public sealed record DockerContainerSpec(string Name, string Image)
{
    public IReadOnlyList<(int HostPort, int ContainerPort)> Ports { get; init; } = [];

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> Tmpfs { get; init; } = [];

    /// <summary>Arguments after the image, e.g. "-c", "fsync=off".</summary>
    public IReadOnlyList<string> Command { get; init; } = [];
}
