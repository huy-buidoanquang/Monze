using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Monze.Testing.Harness;

/// <summary>
/// A loopback TCP proxy in front of a campaign container (PostgreSQL, Redis)
/// that chaos scenarios switch between <see cref="TcpFaultMode"/>s at run
/// time, slow down with <see cref="Latency"/>, or cut with
/// <see cref="ResetConnections"/>. It only forwards to a loopback port other
/// than 5432 and 6379, so it can never sit in front of the development
/// database or a Redis that is not the campaign's.
/// </summary>
public sealed class TcpFaultProxy : IAsyncDisposable
{
    private const int ChunkBytes = 16 * 1024;

    private readonly TcpListener _listener;
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<long, Connection> _connections = new();
    private readonly Task _acceptLoop;
    private readonly Lock _modeGate = new();
    private TaskCompletionSource _open = Opened();
    private TcpFaultMode _mode = TcpFaultMode.Pass;
    private long _nextConnection;
    private long _accepted;
    private long _latencyTicks;

    private TcpFaultProxy(IPEndPoint target, int listenPort)
    {
        _target = target;
        _listener = new TcpListener(IPAddress.Loopback, listenPort);
        _listener.Start();
        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
        _acceptLoop = AcceptLoopAsync();
    }

    public IPEndPoint Endpoint { get; }

    public long AcceptedConnections => Interlocked.Read(ref _accepted);

    public int OpenConnections => _connections.Count;

    /// <summary>
    /// Added before each forwarded chunk in each direction, so a
    /// request/response round trip gains about twice this value.
    /// </summary>
    public TimeSpan Latency
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _latencyTicks));
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            Interlocked.Exchange(ref _latencyTicks, value.Ticks);
        }
    }

    public TcpFaultMode Mode
    {
        get
        {
            lock (_modeGate)
            {
                return _mode;
            }
        }
        set
        {
            lock (_modeGate)
            {
                if (value == _mode)
                {
                    return;
                }

                if (value == TcpFaultMode.Blackhole)
                {
                    _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else if (_mode == TcpFaultMode.Blackhole)
                {
                    _open.TrySetResult();
                }

                _mode = value;
            }
        }
    }

    /// <summary>Starts a proxy on 127.0.0.1:<paramref name="listenPort"/> (0 picks a free port).</summary>
    public static TcpFaultProxy Start(IPEndPoint target, int listenPort = 0)
    {
        if (!IPAddress.IsLoopback(target.Address) || target.Port is 5432 or 6379)
        {
            throw new InvalidOperationException("The fault proxy only forwards to a campaign container port on loopback.");
        }

        return new TcpFaultProxy(target, listenPort);
    }

    /// <summary>Aborts every open connection with a TCP reset, in both directions.</summary>
    public void ResetConnections()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Abort();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        lock (_modeGate)
        {
            _open.TrySetResult();
        }

        ResetConnections();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }

        _stopping.Dispose();
    }

    private static TaskCompletionSource Opened()
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        open.SetResult();
        return open;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_stopping.Token);
            }
            catch (Exception error) when (IsDisconnect(error))
            {
                return;
            }

            Interlocked.Increment(ref _accepted);
            if (Mode == TcpFaultMode.Refuse)
            {
                client.LingerState = new LingerOption(true, 0);
                client.Close();
                continue;
            }

            var id = Interlocked.Increment(ref _nextConnection);
            _ = RunConnectionAsync(id, client);
        }
    }

    private async Task RunConnectionAsync(long id, Socket client)
    {
        var server = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        client.NoDelay = true;
        var connection = new Connection(client, server);
        _connections[id] = connection;
        try
        {
            await server.ConnectAsync(_target, _stopping.Token);
            await Task.WhenAll(
                PumpAsync(client, server, connection, _stopping.Token),
                PumpAsync(server, client, connection, _stopping.Token));
            connection.Close();
        }
        catch (Exception error) when (IsDisconnect(error))
        {
            connection.Abort();
        }
        finally
        {
            _connections.TryRemove(id, out _);
        }
    }

    private async Task PumpAsync(Socket source, Socket destination, Connection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[ChunkBytes];
        try
        {
            while (true)
            {
                var read = await source.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
                Task open;
                lock (_modeGate)
                {
                    open = _open.Task;
                }

                // A partition holds the bytes (and the FIN) until it heals.
                await open.WaitAsync(cancellationToken);
                if (read == 0)
                {
                    destination.Shutdown(SocketShutdown.Send);
                    return;
                }

                var latency = Latency;
                if (latency > TimeSpan.Zero)
                {
                    await Task.Delay(latency, cancellationToken);
                }

                var sent = 0;
                while (sent < read)
                {
                    sent += await destination.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None, cancellationToken);
                }
            }
        }
        catch (Exception error) when (IsDisconnect(error))
        {
            connection.Abort();
        }
    }

    private static bool IsDisconnect(Exception error)
        => error is OperationCanceledException or SocketException or ObjectDisposedException or IOException;

    private sealed class Connection(Socket client, Socket server)
    {
        public void Close()
        {
            client.Dispose();
            server.Dispose();
        }

        public void Abort()
        {
            Reset(client);
            Reset(server);
        }

        private static void Reset(Socket socket)
        {
            try
            {
                socket.LingerState = new LingerOption(true, 0);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
            }

            socket.Dispose();
        }
    }
}
