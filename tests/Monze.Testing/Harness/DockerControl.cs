using System.Diagnostics;

namespace Monze.Testing.Harness;

/// <summary>
/// Drives the docker CLI for chaos scenarios: run, pause, stop, kill and
/// remove campaign containers. Every container it runs carries the label
/// monze.campaign=&lt;campaign id&gt; (the orchestrator removes containers by
/// that label), images are never pulled, and every other operation first
/// checks that label, so it cannot touch a container the campaign does not
/// own, such as mezube-redis-1.
/// </summary>
public sealed class DockerControl
{
    public const string CampaignLabel = "monze.campaign";
    public const string NamePrefix = "monze-";

    public DockerControl(string campaignId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        CampaignId = campaignId;
    }

    public string CampaignId { get; }

    /// <summary>The docker run arguments for <paramref name="spec"/>, after validating it.</summary>
    public IReadOnlyList<string> RunArguments(DockerContainerSpec spec)
    {
        if (!spec.Name.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Campaign container names start with '{NamePrefix}'.", nameof(spec));
        }

        var arguments = new List<string> { "run", "-d", "--pull", "never", "--name", spec.Name, "--label", $"{CampaignLabel}={CampaignId}" };
        foreach (var (hostPort, containerPort) in spec.Ports)
        {
            if (hostPort is <= 1024 or > 65535 or 5432 or 6379)
            {
                throw new ArgumentException($"Host port {hostPort} is not a campaign port.", nameof(spec));
            }

            arguments.AddRange(["-p", $"127.0.0.1:{hostPort}:{containerPort}"]);
        }

        foreach (var name in spec.Environment.Keys.Order(StringComparer.Ordinal))
        {
            arguments.AddRange(["-e", name]);
        }

        foreach (var path in spec.Tmpfs)
        {
            arguments.AddRange(["--tmpfs", path]);
        }

        arguments.Add(spec.Image);
        arguments.AddRange(spec.Command);
        return arguments;
    }

    public async Task<string> RunAsync(DockerContainerSpec spec, CancellationToken cancellationToken = default)
    {
        await DockerAsync(RunArguments(spec), spec.Environment, cancellationToken);
        return spec.Name;
    }

    public async Task PauseAsync(string name, CancellationToken cancellationToken = default)
        => await OwnedAsync(name, ["pause", name], cancellationToken);

    public async Task UnpauseAsync(string name, CancellationToken cancellationToken = default)
        => await OwnedAsync(name, ["unpause", name], cancellationToken);

    public async Task StopAsync(string name, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await OwnedAsync(name, ["stop", "-t", ((int)timeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), name], cancellationToken);

    public async Task StartAsync(string name, CancellationToken cancellationToken = default)
        => await OwnedAsync(name, ["start", name], cancellationToken);

    public async Task KillAsync(string name, string signal = "KILL", CancellationToken cancellationToken = default)
        => await OwnedAsync(name, ["kill", "-s", signal, name], cancellationToken);

    public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
        => await OwnedAsync(name, ["rm", "-f", name], cancellationToken);

    /// <summary>The container state: created, running, paused, restarting, exited or dead.</summary>
    public async Task<string> StatusAsync(string name, CancellationToken cancellationToken = default)
    {
        await EnsureOwnedAsync(name, cancellationToken);
        return (await DockerAsync(["inspect", "-f", "{{.State.Status}}", name], null, cancellationToken)).Trim();
    }

    /// <summary>Runs a command in the container and returns its exit code (its output is discarded).</summary>
    public async Task<int> ExecAsync(string name, IReadOnlyList<string> command, CancellationToken cancellationToken = default)
    {
        await EnsureOwnedAsync(name, cancellationToken);
        var (exitCode, _, _) = await RunProcessAsync(["exec", name, .. command], null, cancellationToken);
        return exitCode;
    }

    /// <summary>Repeats <paramref name="command"/> until it exits with 0, e.g. pg_isready or redis-cli ping.</summary>
    public async Task WaitUntilReadyAsync(string name, IReadOnlyList<string> command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = Stopwatch.StartNew();
        while (await ExecAsync(name, command, cancellationToken) != 0)
        {
            if (deadline.Elapsed > timeout)
            {
                throw new TimeoutException($"Container {name} was not ready after {timeout.TotalSeconds:0} s.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private async Task OwnedAsync(string name, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await EnsureOwnedAsync(name, cancellationToken);
        await DockerAsync(arguments, null, cancellationToken);
    }

    private async Task EnsureOwnedAsync(string name, CancellationToken cancellationToken)
    {
        var (exitCode, output, _) = await RunProcessAsync(
            ["inspect", "-f", $"{{{{index .Config.Labels \"{CampaignLabel}\"}}}}", name],
            null,
            cancellationToken);
        if (exitCode != 0 || !string.Equals(output.Trim(), CampaignId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Container {name} does not carry {CampaignLabel}={CampaignId}; refusing to touch it.");
        }
    }

    private static async Task<string> DockerAsync(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        var (exitCode, output, error) = await RunProcessAsync(arguments, environment, cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"docker {arguments[0]} exited with {exitCode}: {error.Trim()}");
        }

        return output;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("docker could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }
}
