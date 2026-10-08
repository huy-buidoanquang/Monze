using System.Collections.Concurrent;
using System.Threading.Channels;
using Mezon.Net.Core;
using Mezon.Net.Core.Abstractions;

namespace Monze.Simulator;

/// <summary>
/// The simulated gateway socket, installed through
/// <c>MezonClientOptions.NetworkTransportProvider</c>. It decodes every frame
/// the SDK sends (socket API calls as <c>Api</c> frames carrying an
/// <c>Envelope.api_request_event</c>, realtime envelopes, heartbeats), answers
/// them from the <see cref="SimWorld"/>, records them in the
/// <see cref="SimRecorder"/> and delivers server frames to the SDK in order
/// from one receive pump per connection, like the real WebSocket receive loop
/// (src/Mezon.Net.Transport/WebSocket/MezonNetworkWebSocketTransporter.cs at
/// v1.6.2). Unknown APIs and envelopes fail closed. Scripted faults
/// (<see cref="SimFaultPlan"/>) delay, fail, drop or close.
/// </summary>
public sealed partial class SimTransporter : IMezonNetworkTransporter
{
    private readonly MezonSimulator _simulator;
    private readonly object _gate = new();
    private Connection? _connection;
    private IReadOnlyDictionary<string, string> _headers = new Dictionary<string, string>();
    private int _connects;
    private bool _disposed;

    internal SimTransporter(MezonSimulator simulator, TransportType transportType, int sessionId)
    {
        _simulator = simulator;
        TransportType = transportType;
        SessionId = sessionId;
    }

    /// <summary>Identifies this SDK client's socket in the recorder.</summary>
    public int SessionId { get; }

    /// <summary>The transport type the SDK asked for (WebSocket for Monze).</summary>
    public TransportType TransportType { get; }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _connection is { Open: true };
            }
        }
    }

    /// <summary>Successful handshakes so far (reconnects included).</summary>
    public int ConnectCount => Volatile.Read(ref _connects);

    /// <summary>Clans joined with ClanJoin on the current connection.</summary>
    public IReadOnlyCollection<long> JoinedClans
    {
        get
        {
            lock (_gate)
            {
                return _connection is { Open: true } connection ? connection.JoinedClans.Keys.Order().ToList() : [];
            }
        }
    }

    /// <summary>Headers the SDK set for the handshake.</summary>
    public IReadOnlyDictionary<string, string> Headers => _headers;

    public Func<MezonMessageType, int, int, ReadOnlyMemory<byte>, ValueTask>? MessageReceived { get; set; }

    public Func<Task>? Opened { get; set; }

    public Func<Exception?, Task>? Closed { get; set; }

    public Func<Exception, Task>? ErrorOccurred { get; set; }

    public bool HasJoinedClan(long clanId)
    {
        lock (_gate)
        {
            return _connection is { Open: true } connection && connection.JoinedClans.ContainsKey(clanId);
        }
    }

    public void SetHeader(IDictionary<string, string> headers)
        => _headers = headers is null ? new Dictionary<string, string>() : new Dictionary<string, string>(headers);

    public void SetCancelToken(CancellationToken cancellationToken)
    {
        // The real transporter links this token to its loops; the simulated
        // socket is closed explicitly by DisconnectAsync instead.
    }

    public async Task ConnectAsync(string host, int? port = 443, string? token = null, bool? useSsl = false, bool? createStatus = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Connection? previous;
        lock (_gate)
        {
            previous = _connection;
            _connection = null;
        }

        // The real transporter disconnects (and raises Closed) before a new handshake.
        if (previous is not null && previous.Close())
        {
            await InvokeClosedAsync().ConfigureAwait(false);
        }

        var (delay, terminal) = _simulator.Faults.TakeOperation(SimOperations.SocketConnect);
        if (delay is not null)
        {
            await Task.Delay(delay.Delay).ConfigureAwait(false);
        }

        var options = _simulator.Options;
        if (!string.Equals(host, options.Host, StringComparison.OrdinalIgnoreCase) || port != options.Port)
        {
            _simulator.Recorder.RecordViolation(SessionId, SimOperations.SocketConnect, $"Handshake to {host}:{port}, which the simulator does not serve.");
            await FailConnectAsync(MezonStatusCode.Unavailable, null, new NetworkTransportException($"The simulated platform does not serve {host}:{port}.")).ConfigureAwait(false);
        }

        if (terminal is not null)
        {
            await FailConnectAsync(MezonStatusCode.Unavailable, terminal.Kind, new NetworkTransportException("The simulated gateway refused the handshake.")).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(token) || !_simulator.IsSessionToken(token))
        {
            await FailConnectAsync(MezonStatusCode.Unauthenticated, null, new NetworkTransportUnauthorizationException()).ConfigureAwait(false);
        }

        var connection = new Connection();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connection = connection;
        }

        Interlocked.Increment(ref _connects);
        connection.Pump = Task.Run(() => PumpAsync(connection));
        _simulator.Recorder.Record(new SimAction
        {
            Kind = SimActionKind.Connect,
            Operation = SimOperations.SocketConnect,
            SessionId = SessionId
        });
        if (Opened is { } opened)
        {
            await opened().ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync(int closeCode = 1000, string? reason = null)
    {
        Connection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection is null || !connection.Close())
        {
            return;
        }

        _simulator.Recorder.Record(new SimAction
        {
            Kind = SimActionKind.Disconnect,
            Operation = SimOperations.SocketDisconnect,
            SessionId = SessionId,
            Code = closeCode
        });

        // MezonNetworkWebSocketTransporter.DisconnectInternalAsync raises Closed(null).
        await InvokeClosedAsync().ConfigureAwait(false);
    }

    public ValueTask SendAsync(MezonMessageType type, int cid, ReadOnlyMemory<byte> data)
    {
        Connection? connection;
        lock (_gate)
        {
            connection = _disposed ? null : _connection;
        }

        if (connection is not { Open: true })
        {
            return ValueTask.FromException(new InvalidOperationException("Cannot send on the simulated socket: it is not connected."));
        }

        var payload = data.ToArray();
        try
        {
            switch (type)
            {
                case MezonMessageType.Api:
                    HandleApiFrame(connection, cid, payload);
                    break;
                case MezonMessageType.Realtime:
                    HandleRealtimeFrame(connection, cid, payload);
                    break;
                case MezonMessageType.Heartbeat when TransportType == TransportType.Tcp:
                    HandleHeartbeatFrame(connection, cid);
                    break;
                default:
                    _simulator.Recorder.RecordViolation(SessionId, type.ToString(), $"{type} frame on a {TransportType} socket.");
                    return ValueTask.FromException(new InvalidOperationException($"Unsupported {TransportType} message type '{type}'."));
            }
        }
        catch (Exception ex)
        {
            // Malformed frames (or a simulator bug) fail the SDK call at once
            // instead of leaving it to time out.
            _simulator.Recorder.RecordViolation(SessionId, type.ToString(), $"Frame handling failed: {ex.GetType().Name}: {ex.Message}");
            return ValueTask.FromException(new InvalidOperationException("The simulated platform could not handle the frame.", ex));
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        Connection? connection;
        lock (_gate)
        {
            _disposed = true;
            connection = _connection;
            _connection = null;
        }

        connection?.Close();
    }

    /// <summary>Closes the socket from the server side; the SDK sees Closed(null) and reconnects.</summary>
    internal async Task<bool> CloseFromServerAsync(string reason)
    {
        Connection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection is null || !connection.Close())
        {
            return false;
        }

        _simulator.Recorder.Record(new SimAction
        {
            Kind = SimActionKind.ServerClose,
            Operation = reason,
            SessionId = SessionId
        });

        // The real receive loop ends and DisconnectAsync raises Closed(null).
        await InvokeClosedAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Queues a server push; completes with true once the SDK receive handler ran.</summary>
    internal Task<bool> EnqueuePush(byte[] envelope)
    {
        Connection? connection;
        lock (_gate)
        {
            connection = _connection;
        }

        return connection is { Open: true }
            ? connection.Enqueue(new Frame(MezonMessageType.Realtime, 0, 0, envelope, Tracked: true))
            : Task.FromResult(false);
    }

    private async Task FailConnectAsync(MezonStatusCode status, SimFaultKind? fault, Exception error)
    {
        _simulator.Recorder.Record(new SimAction
        {
            Kind = SimActionKind.Connect,
            Operation = SimOperations.SocketConnect,
            SessionId = SessionId,
            ResponseCode = (int)status,
            Fault = fault
        });
        if (ErrorOccurred is { } errorOccurred)
        {
            await errorOccurred(error).ConfigureAwait(false);
        }

        throw error;
    }

    private async Task InvokeClosedAsync()
    {
        if (Closed is { } closed)
        {
            await closed(null).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(Connection connection)
    {
        await foreach (var frame in connection.Frames.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (!connection.Open)
            {
                frame.Delivered?.TrySetResult(false);
                continue;
            }

            var handler = MessageReceived;
            try
            {
                if (handler is not null)
                {
                    await handler(frame.Type, frame.Cid, frame.Code, frame.Payload).ConfigureAwait(false);
                }

                frame.Delivered?.TrySetResult(handler is not null);
            }
            catch (Exception ex)
            {
                _simulator.Recorder.RecordViolation(SessionId, "MessageReceived", $"SDK receive handler threw {ex.GetType().Name}: {ex.Message}");
                frame.Delivered?.TrySetResult(true);
            }
        }
    }

    /// <summary>
    /// Runs an operation through the fault plan: an optional delay, then
    /// close / error / dropped response, then the handler's reply frames.
    /// </summary>
    private void Run(Connection connection, string operation, Func<SimFault?, Reply> handle)
    {
        var (delay, terminal) = _simulator.Faults.TakeOperation(operation);
        var wait = delay?.Delay ?? TimeSpan.Zero;
        if (operation != SimOperations.Heartbeat && _simulator.Options.ResponseLatency is { } latency)
        {
            wait += latency(operation);
        }

        if (wait <= TimeSpan.Zero)
        {
            RunNow(connection, operation, terminal, handle);
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(wait).ConfigureAwait(false);
            try
            {
                RunNow(connection, operation, terminal, handle);
            }
            catch (Exception ex)
            {
                _simulator.Recorder.RecordViolation(SessionId, operation, $"Delayed handling failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private void RunNow(Connection connection, string operation, SimFault? terminal, Func<SimFault?, Reply> handle)
    {
        if (!connection.Open)
        {
            return;
        }

        if (terminal?.Kind == SimFaultKind.CloseSocket)
        {
            _ = Task.Run(() => CloseFromServerAsync($"fault:{operation}"));
            return;
        }

        var reply = handle(terminal);
        if (terminal?.Kind != SimFaultKind.DropResponse)
        {
            foreach (var frame in reply.Frames)
            {
                _ = connection.Enqueue(frame);
            }
        }

        reply.After?.Invoke();
    }

    private void HandleHeartbeatFrame(Connection connection, int cid)
        => Run(connection, SimOperations.Heartbeat, fault =>
        {
            RecordHeartbeat(fault);
            return fault?.Kind == SimFaultKind.Error
                ? Reply.None
                : new Reply([new Frame(MezonMessageType.Heartbeat, cid, 0, [], Tracked: false)]);
        });

    private void RecordHeartbeat(SimFault? fault)
        => _simulator.Recorder.Record(new SimAction
        {
            Kind = SimActionKind.Heartbeat,
            Operation = SimOperations.Heartbeat,
            SessionId = SessionId,
            ResponseCode = fault?.Kind == SimFaultKind.Error ? (int)fault.Code : 0,
            Fault = fault?.Kind
        });

    private sealed record Frame(MezonMessageType Type, int Cid, int Code, byte[] Payload, bool Tracked)
    {
        public TaskCompletionSource<bool>? Delivered { get; } = Tracked
            ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
    }

    private sealed record Reply(IReadOnlyList<Frame> Frames, Action? After = null)
    {
        public static Reply None { get; } = new([]);
    }

    private sealed class Connection
    {
        private int _open = 1;

        public Channel<Frame> Frames { get; } = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        public ConcurrentDictionary<long, byte> JoinedClans { get; } = new();

        public bool Open => Volatile.Read(ref _open) == 1;

        public Task? Pump { get; set; }

        public bool Close()
        {
            if (Interlocked.Exchange(ref _open, 0) == 0)
            {
                return false;
            }

            Frames.Writer.TryComplete();
            return true;
        }

        public Task<bool> Enqueue(Frame frame)
        {
            if (!Open || !Frames.Writer.TryWrite(frame))
            {
                frame.Delivered?.TrySetResult(false);
                return frame.Delivered?.Task ?? Task.FromResult(false);
            }

            return frame.Delivered?.Task ?? Task.FromResult(true);
        }
    }
}
