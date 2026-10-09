using System.Net;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Hosting;

/// <summary>
/// Regression for DEF-08: the Agent SSE body is ended when a stream that has
/// sent keepalives goes silent, never when the server sends none, and a
/// reconnect resumes after the last dispatched event with Last-Event-ID.
/// </summary>
public sealed class AgentSseResumeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    [Req("REQ-MTG-003", "REQ-CONN-001")]
    public async Task A_stream_that_sent_keepalives_is_ended_after_three_keepalive_gaps_of_silence()
    {
        var time = new ManualTimeProvider(Start);
        var inner = new ScriptedStream();
        var logger = new CountingLogger();
        await using var stream = new AgentSseIdleStream(inner, new AgentSseResumeState(TimeSpan.FromSeconds(2)), time, logger);
        var buffer = new byte[256];

        inner.Send(": keepalive\n\n");
        await ReadSomeAsync(stream, buffer);
        Assert.Null(stream.IdleTimeout);
        time.Advance(TimeSpan.FromSeconds(1));
        inner.Send(": keepalive\n\n");
        await ReadSomeAsync(stream, buffer);
        Assert.Equal(TimeSpan.FromSeconds(3), stream.IdleTimeout);

        var silent = stream.ReadAsync(buffer).AsTask();
        time.Advance(TimeSpan.FromSeconds(2.9));
        Assert.False(silent.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(0.2));

        Assert.Equal(0, await silent.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    [Req("REQ-MTG-003", "REQ-CONN-001")]
    public async Task A_stream_with_fewer_than_two_keepalives_is_never_timed_out()
    {
        var time = new ManualTimeProvider(Start);
        var inner = new ScriptedStream();
        await using var stream = new AgentSseIdleStream(inner, new AgentSseResumeState(TimeSpan.FromSeconds(2)), time, new CountingLogger());
        var buffer = new byte[256];
        inner.Send("event: connected\ndata: {}\n\n: keepalive\n\n");
        await ReadSomeAsync(stream, buffer);

        var waiting = stream.ReadAsync(buffer).AsTask();
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100);

        Assert.False(waiting.IsCompleted);
        inner.Send("event: room_started\ndata: {}\n\n");
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)) > 0);
    }

    [Fact]
    [Req("REQ-MTG-003", "REQ-CONN-001")]
    public async Task The_last_event_id_is_the_id_of_the_last_dispatched_event()
    {
        var state = new AgentSseResumeState(TimeSpan.FromSeconds(30));
        var inner = new ScriptedStream();
        await using var stream = new AgentSseIdleStream(inner, state, new ManualTimeProvider(Start), new CountingLogger());
        var buffer = new byte[256];

        inner.Send("id: 7\nevent: room_started\ndata: {\"id\":\"x\"}\n\n");
        await ReadSomeAsync(stream, buffer);
        Assert.Equal("7", state.LastEventId);

        inner.Send("id: 8\r\nevent: room_ended\r\ndata: {}\r\n");
        await ReadSomeAsync(stream, buffer);
        Assert.Equal("7", state.LastEventId);
        inner.Send("\r\n");
        await ReadSomeAsync(stream, buffer);
        Assert.Equal("8", state.LastEventId);
    }

    [Fact]
    [Req("REQ-MTG-003", "REQ-CONN-001")]
    public async Task A_reconnect_carries_the_last_event_id_and_gets_a_watched_body()
    {
        var state = new AgentSseResumeState(TimeSpan.FromSeconds(30));
        var inner = new RecordingHandler();
        using var http = new HttpClient(new AgentSseResumeHandler(state, new ManualTimeProvider(Start), new CountingLogger()) { InnerHandler = inner });

        using (var first = await http.GetAsync("http://127.0.0.1/api/sse/metadata", HttpCompletionOption.ResponseHeadersRead))
        {
            // The body passes through the watcher: the dispatched event's id is recorded.
            using var reader = new StreamReader(await first.Content.ReadAsStreamAsync());
            await reader.ReadToEndAsync();
        }

        Assert.Equal("42", state.LastEventId);
        using (await http.GetAsync("http://127.0.0.1/api/sse/metadata", HttpCompletionOption.ResponseHeadersRead))
        {
        }

        Assert.Equal(new string?[] { null, "42" }, inner.LastEventIds);
    }

    private static async Task ReadSomeAsync(Stream stream, byte[] buffer)
        => Assert.True(await stream.ReadAsync(buffer) > 0);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string?> LastEventIds { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastEventIds.Add(request.Headers.TryGetValues("Last-Event-ID", out var values) ? values.Single() : null);
            var content = new StringContent(": keepalive\n\nid: 42\nevent: room_started\ndata: {}\n\n", Encoding.UTF8, "text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ScriptedStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Send(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var chunk = await _chunks.Reader.ReadAsync(cancellationToken);
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingLogger : ILogger
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}
