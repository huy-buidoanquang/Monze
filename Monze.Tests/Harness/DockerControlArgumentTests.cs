using Monze.Testing;
using Monze.Testing.Harness;
using Xunit;

namespace Monze.Tests.Harness;

public sealed class DockerControlArgumentTests
{
    private readonly DockerControl _docker = new("run-1");

    [Fact]
    [Req("REQ-HARN-002")]
    public void Run_never_pulls_labels_the_container_and_keeps_secrets_off_the_command_line()
    {
        const string secret = "s3cret-value";
        var arguments = _docker.RunArguments(new DockerContainerSpec("monze-campaign-run-1-pg", "postgres:17-alpine")
        {
            Ports = [(55432, 5432)],
            Environment = new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = secret, ["POSTGRES_USER"] = "monze" },
            Tmpfs = ["/var/lib/postgresql/data"],
            Command = ["-c", "fsync=off"]
        });

        Assert.Equal(
            ["run", "-d", "--pull", "never", "--name", "monze-campaign-run-1-pg", "--label", "monze.campaign=run-1",
             "-p", "127.0.0.1:55432:5432", "-e", "POSTGRES_PASSWORD", "-e", "POSTGRES_USER",
             "--tmpfs", "/var/lib/postgresql/data", "postgres:17-alpine", "-c", "fsync=off"],
            arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains(secret, StringComparison.Ordinal));
    }

    [Theory]
    [Req("REQ-HARN-002")]
    [InlineData("monze-x", 5432)]
    [InlineData("monze-x", 6379)]
    [InlineData("monze-x", 80)]
    [InlineData("mezube-redis-1", 56379)]
    [InlineData("redis", 56379)]
    public void Run_rejects_names_and_ports_outside_the_campaign(string name, int hostPort)
        => Assert.Throws<ArgumentException>(() => _docker.RunArguments(new DockerContainerSpec(name, "redis:7-alpine") { Ports = [(hostPort, 6379)] }));
}
