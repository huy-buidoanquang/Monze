using System.Net;
using System.Net.Sockets;
using Monze.Testing;
using Monze.Testing.Harness;
using Xunit;

namespace Monze.Tests.Harness;

/// <summary>Drives a real throwaway Redis container through the chaos operations.</summary>
public sealed class DockerControlTests
{
    [DockerFact]
    [Req("REQ-HARN-002")]
    public async Task Pause_kill_and_restart_a_campaign_container_and_refuse_foreign_ones()
    {
        var docker = new DockerControl(CampaignEnvironment.Id);
        var name = $"monze-campaign-{CampaignEnvironment.Id}-harness-{Guid.NewGuid().ToString("N")[..12]}";
        var ping = new[] { "redis-cli", "ping" };
        await docker.RunAsync(new DockerContainerSpec(name, "redis:7-alpine")
        {
            Ports = [(FreeLoopbackPort(), 6379)],
            Command = ["redis-server", "--save", "", "--appendonly", "no"]
        });
        try
        {
            await docker.WaitUntilReadyAsync(name, ping, TimeSpan.FromSeconds(30));
            Assert.Equal("running", await docker.StatusAsync(name));

            await docker.PauseAsync(name);
            Assert.Equal("paused", await docker.StatusAsync(name));
            await docker.UnpauseAsync(name);
            Assert.Equal(0, await docker.ExecAsync(name, ping));

            await docker.KillAsync(name);
            Assert.Equal("exited", await docker.StatusAsync(name));
            await docker.StartAsync(name);
            await docker.WaitUntilReadyAsync(name, ping, TimeSpan.FromSeconds(30));

            var other = new DockerControl("other-" + Guid.NewGuid().ToString("N"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.PauseAsync(name));
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.RemoveAsync(name));
            await Assert.ThrowsAsync<InvalidOperationException>(() => docker.KillAsync("monze-missing-" + Guid.NewGuid().ToString("N")));
            Assert.Equal("running", await docker.StatusAsync(name));
        }
        finally
        {
            await docker.RemoveAsync(name);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => docker.StatusAsync(name));
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
