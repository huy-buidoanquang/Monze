using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Monze.Testing;
using Monze.Testing.Harness;
using Xunit;

namespace Monze.Tests.Harness;

public sealed class TcpFaultProxyTests : IAsyncLifetime
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private readonly TcpListener _echo = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private Task _echoLoop = Task.CompletedTask;
    private TcpFaultProxy _proxy = null!;

    public Task InitializeAsync()
    {
        _echo.Start();
        _echoLoop = EchoLoopAsync();
        _proxy = TcpFaultProxy.Start((IPEndPoint)_echo.LocalEndpoint);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        await _stop.CancelAsync();
        _echo.Stop();
        await _echoLoop;
        _stop.Dispose();
    }

    [Fact]
    [Req("REQ-HARN-002")]
    public async Task Pass_forwards_both_directions_and_latency_delays_the_round_trip()
    {
        using var client = await ConnectAsync();
        Assert.Equal("ping", await RoundTripAsync(client, "ping"));

        _proxy.Latency = TimeSpan.FromMilliseconds(150);
        var watch = Stopwatch.StartNew();
        Assert.Equal("slow", await RoundTripAsync(client, "slow"));
        Assert.True(watch.ElapsedMilliseconds >= 280, $"round trip took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(1, _proxy.AcceptedConnections);
    }

    [Fact]
    [Req("REQ-HARN-002")]
    public async Task Blackhole_holds_bytes_until_the_partition_heals()
    {
        using var client = await ConnectAsync();
        Assert.Equal("before", await RoundTripAsync(client, "before"));

        _proxy.Mode = TcpFaultMode.Blackhole;
        await client.SendAsync("held"u8.ToArray(), SocketFlags.None);
        using var late = await ConnectAsync();
        await late.SendAsync("late"u8.ToArray(), SocketFlags.None);
        Assert.Null(await TryReceiveAsync(client, Quiet));
        Assert.Null(await TryReceiveAsync(late, Quiet));

        _proxy.Mode = TcpFaultMode.Pass;
        Assert.Equal("held", await TryReceiveAsync(client, TimeSpan.FromSeconds(5)));
        Assert.Equal("late", await TryReceiveAsync(late, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    [Req("REQ-HARN-002")]
    public async Task Refuse_resets_new_connections_and_keeps_open_ones()
    {
        using var open = await ConnectAsync();
        Assert.Equal("a", await RoundTripAsync(open, "a"));

        _proxy.Mode = TcpFaultMode.Refuse;
        using var refused = await ConnectAsync();
        Assert.True(await IsClosedAsync(refused));
        Assert.Equal("b", await RoundTripAsync(open, "b"));
    }

    [Fact]
    [Req("REQ-HARN-002")]
    public async Task Reset_aborts_every_open_connection()
    {
        using var first = await ConnectAsync();
        using var second = await ConnectAsync();
        Assert.Equal("1", await RoundTripAsync(first, "1"));
        Assert.Equal("2", await RoundTripAsync(second, "2"));

        _proxy.ResetConnections();

        Assert.True(await IsClosedAsync(first));
        Assert.True(await IsClosedAsync(second));
        using var after = await ConnectAsync();
        Assert.Equal("3", await RoundTripAsync(after, "3"));
    }

    [Theory]
    [Req("REQ-HARN-002")]
    [InlineData("127.0.0.1", 5432)]
    [InlineData("127.0.0.1", 6379)]
    [InlineData("10.0.0.5", 55432)]
    public void Refuses_targets_outside_the_campaign(string address, int port)
        => Assert.Throws<InvalidOperationException>(() => TcpFaultProxy.Start(new IPEndPoint(IPAddress.Parse(address), port)));

    private async Task<Socket> ConnectAsync()
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(_proxy.Endpoint);
        return socket;
    }

    private static async Task<string> RoundTripAsync(Socket socket, string text)
    {
        await socket.SendAsync(System.Text.Encoding.ASCII.GetBytes(text), SocketFlags.None);
        return await TryReceiveAsync(socket, TimeSpan.FromSeconds(5)) ?? throw new TimeoutException("no echo");
    }

    private static async Task<string?> TryReceiveAsync(Socket socket, TimeSpan timeout)
    {
        var buffer = new byte[256];
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, cancel.Token);
            return System.Text.Encoding.ASCII.GetString(buffer, 0, read);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<bool> IsClosedAsync(Socket socket)
    {
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await socket.ReceiveAsync(new byte[16], SocketFlags.None, cancel.Token) == 0;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private async Task EchoLoopAsync()
    {
        var connections = new List<Task>();
        try
        {
            while (true)
            {
                var socket = await _echo.AcceptSocketAsync(_stop.Token);
                connections.Add(EchoAsync(socket));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        await Task.WhenAll(connections);
    }

    private async Task EchoAsync(Socket socket)
    {
        using (socket)
        {
            var buffer = new byte[256];
            try
            {
                int read;
                while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None, _stop.Token)) > 0)
                {
                    await socket.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, _stop.Token);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException)
            {
            }
        }
    }
}
