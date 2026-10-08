using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CsCheck;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Hosting;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Ui;

/// <summary>
/// G24: BoundedHttpContent returns the UTF-8 text of a payload up to the limit
/// and null above it, whatever the chunking, and rejects early on a declared
/// Content-Length above the limit.
/// G25: OpenAiCompatibleProvider returns the first choice's content or null
/// for every non-success shape, sends the model and both messages with the
/// bearer key, and never writes the key, the instruction or the input to logs.
/// Known gap CAND-14: a JSON array root or a non-object choice throws
/// InvalidOperationException instead of returning null.
/// G30: connection retry delays double from the clamped initial delay up to
/// the clamped maximum; queue partitions round up to a power of two in 1..64.
/// </summary>
public sealed class G24G25G30HttpAndRetryProperties
{
    private static readonly string[] Shapes =
        ["ok", "ok-untyped", "empty-choices", "no-choices", "blank-content", "number-content", "html", "status", "invalid-json", "array-root", "non-object-choice", "oversized", "transport", "timeout"];

    [Fact]
    [Req("REQ-AI-001", "REQ-MTG-004")]
    public void Bounded_reads_return_text_up_to_the_limit()
    {
        var cases =
            from limit in Gen.OneOfConst(1, 7, 64, 4_096)
            from lengthMode in Gen.OneOfConst("near", "random")
            from delta in Gen.Int[-2, 2]
            from random in Gen.Int[0, 3 * 4_096]
            from textual in Gen.Bool
            from chunk in Gen.Int[1, 9_000]
            from declared in Gen.OneOfConst("none", "exact", "smaller", "larger")
            select (limit, lengthMode, length: Math.Max(0, lengthMode == "near" ? limit + delta : random % ((3 * limit) + 1)), textual, chunk, declared);
        PropertyRun.Run(
            "G24",
            Gen.Select(cases, Gen.Int[0, int.MaxValue]),
            static value =>
            {
                var ((limit, mode, length, textual, chunk, declared), seed) = value;
                var payload = Payload(length, textual, seed);
                long? header = declared switch
                {
                    "exact" => payload.Length,
                    "smaller" => Math.Max(0, payload.Length - 1),
                    "larger" => payload.Length + 1,
                    _ => null
                };
                using var content = new ChunkedContent(payload, chunk, header);
                var actual = Monze.BoundedHttpContent.ReadStringAsync(content, limit, CancellationToken.None).GetAwaiter().GetResult();
                var expected = header > limit || payload.Length > limit ? null : Encoding.UTF8.GetString(payload);
                var tags = new Dictionary<string, string>
                {
                    ["length"] = mode,
                    ["declared"] = declared,
                    ["fits"] = payload.Length <= limit ? "yes" : "no"
                };
                return PropertyResult.Check(actual == expected, $"limit={limit} length={payload.Length} chunk={chunk} declared={header}", tags, () => $"expected {(expected is null ? "null" : $"{expected.Length} chars")}, got {(actual is null ? "null" : $"{actual.Length} chars")}");
            },
            iterations: 20_000,
            declare: static ledger => ledger.Dimension("length", "near", "random").Dimension("declared", "none", "exact", "smaller", "larger").Dimension("fits", "yes", "no"));
    }

    [Fact]
    [Req("REQ-AI-001", "REQ-SEC-102")]
    [Covers("http:v1/chat/completions")]
    [Covers("port:IAiProvider.CompleteAsync")]
    public void Ai_provider_maps_every_response_and_keeps_secrets_out_of_logs()
    {
        var cases = Gen.Select(
            Gen.Frequency(Shapes.Select(static shape => (shape == "oversized" ? 1 : 8, (IGen<string>)Gen.Const(shape))).ToArray()),
            Gen.OneOfConst(HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable),
            Gen.Bool,
            Gen.Int[0, 1_000_000]);
        PropertyRun.Run(
            "G25",
            cases,
            static value =>
            {
                var (shape, status, declareLength, salt) = value;
                var key = $"sk-test-{salt:x8}-{Guid.NewGuid():N}";
                var instruction = $"instruction-{salt}-bí mật";
                var input = $"input-{salt}-nội dung riêng";
                var logger = new CapturingLogger();
                var handler = new ScriptedHandler(shape, status, declareLength);
                using var http = new HttpClient(handler);
                var provider = new Monze.OpenAiCompatibleProvider(http, "https://ai.test/base", key, "model-test", logger);
                var tags = new Dictionary<string, string> { ["shape"] = shape };
                string? result;
                try
                {
                    result = provider.CompleteAsync(instruction, input, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex) when (shape is "array-root" or "non-object-choice")
                {
                    return PropertyResult.Known("CAND-14", shape, tags, ex.Message);
                }

                var expected = shape is "ok" or "ok-untyped" ? "trả lời" : null;
                var leaks = logger.Text.Contains(key, StringComparison.Ordinal)
                    || logger.Text.Contains(instruction, StringComparison.Ordinal)
                    || logger.Text.Contains(input, StringComparison.Ordinal);
                var request = handler.Request;
                var requestOk = request is not null
                    && request.Method == HttpMethod.Post
                    && request.Uri == "https://ai.test/base/v1/chat/completions"
                    && request.Authorization == $"Bearer {key}"
                    && request.Model == "model-test"
                    && request.System == instruction
                    && request.User == input;
                var correct = result == expected && !leaks && requestOk;
                var note = $"result={(result is null ? "null" : $"'{result}'")} leaks={leaks} request={requestOk}";
                if (shape is "array-root" or "non-object-choice")
                {
                    return correct ? PropertyResult.Pass(shape, tags, "CAND-14") : PropertyResult.Fail(shape, tags, note);
                }

                return PropertyResult.Check(correct, shape, tags, () => note);
            },
            iterations: 6_000,
            declare: static ledger => ledger.Dimension("shape", Shapes),
            knownDefects: ["CAND-14"]);
    }

    [Fact]
    [Req("REQ-CONN-001", "REQ-ING-001")]
    [Covers("cfg:Monze:Connection:InitialRetrySeconds")]
    [Covers("cfg:Monze:Queues:MessagePartitions")]
    public void Retry_delays_double_up_to_the_clamped_maximum()
    {
        var nextRetry = typeof(Monze.MonzeBot).GetMethod("NextRetryDelay", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("MonzeBot.NextRetryDelay not found.");
        var normalize = typeof(Monze.MonzeBot).GetMethod("NormalizePartitionCount", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MonzeBot.NormalizePartitionCount not found.");
        PropertyRun.Run(
            "G30",
            Gen.Select(Gen.Int[-5, 70], Gen.OneOf(Gen.Int[-5, 5], Gen.Int[0, 400]), Gen.Int[-10, 200]),
            value =>
            {
                var (initial, maximum, partitions) = value;
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Monze:Connection:InitialRetrySeconds"] = initial.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Monze:Connection:MaxRetrySeconds"] = maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }).Build();
                var options = Monze.MonzeConnectionRetryOptions.From(configuration);
                var expectedInitial = TimeSpan.FromSeconds(Math.Clamp(initial, 1, 60));
                var expectedMax = TimeSpan.FromSeconds(Math.Clamp(maximum, (int)expectedInitial.TotalSeconds, 300));
                var bot = Bot(configuration, options);
                var delay = options.InitialDelay;
                var steps = new List<TimeSpan> { delay };
                for (var i = 0; i < 12; i++)
                {
                    delay = (TimeSpan)nextRetry.Invoke(bot, [delay])!;
                    steps.Add(delay);
                }

                var expectedDelay = expectedInitial;
                var sequenceOk = true;
                foreach (var step in steps)
                {
                    sequenceOk &= step == expectedDelay;
                    expectedDelay = TimeSpan.FromTicks(Math.Min(expectedMax.Ticks, Math.Max(expectedDelay.Ticks * 2, expectedInitial.Ticks)));
                }

                var bounded = Math.Clamp(partitions, 1, 64);
                var expectedPartitions = 1;
                while (expectedPartitions < bounded)
                {
                    expectedPartitions *= 2;
                }

                var actualPartitions = (int)normalize.Invoke(null, [partitions])!;
                var tags = new Dictionary<string, string>
                {
                    ["initial"] = initial < 1 ? "below" : initial > 60 ? "above" : "within",
                    ["maximum"] = maximum < expectedInitial.TotalSeconds ? "below-initial" : maximum > 300 ? "above" : "within"
                };
                return PropertyResult.Check(
                    options.InitialDelay == expectedInitial && options.MaxDelay == expectedMax && sequenceOk && steps[^1] == expectedMax && actualPartitions == expectedPartitions,
                    $"initial={initial} max={maximum} partitions={partitions}",
                    tags,
                    () => $"options {options.InitialDelay}/{options.MaxDelay}, steps {string.Join(",", steps.Select(static s => s.TotalSeconds))}, partitions {actualPartitions}");
            },
            iterations: 5_000,
            declare: static ledger => ledger.Dimension("initial", "below", "within", "above").Dimension("maximum", "below-initial", "within", "above"));
    }

    private static byte[] Payload(int length, bool textual, int seed)
    {
        if (!textual)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        var text = new StringBuilder();
        var random = new Random(seed);
        string[] pieces = ["a", "ệ", "🎯", " ", "{", "\"", "Họp"];
        while (Encoding.UTF8.GetByteCount(text.ToString()) < length)
        {
            text.Append(pieces[random.Next(pieces.Length)]);
        }

        return Encoding.UTF8.GetBytes(text.ToString())[..length];
    }

    private static Monze.MonzeBot Bot(IConfiguration configuration, Monze.MonzeConnectionRetryOptions retry)
        => new(
            configuration,
            app: null!,
            clans: null!,
            authorization: null!,
            welcome: null!,
            meeting: null!,
            scheduledMeeting: null!,
            scheduling: null!,
            outbox: null!,
            messageHistory: null!,
            userProfiles: null!,
            commandInbox: null!,
            interactionInbox: null!,
            welcomeSetupDrafts: null!,
            transcript: null!,
            summaryComposer: null!,
            readModelCache: new DisabledReadModelCache(),
            policyCache: null!,
            commandOptions: MonzeCommandOptions.Default,
            connectionRetryOptions: retry,
            commandRateLimiter: null!,
            readiness: new StartupReadiness(),
            time: TimeProvider.System,
            timings: Monze.MonzeWorkerTimings.From(configuration),
            logger: NullLogger<Monze.MonzeBot>.Instance);

    private sealed class ChunkedContent(byte[] payload, int chunk, long? declared) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(payload).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new ChunkedStream(payload, chunk));

        protected override bool TryComputeLength(out long length)
        {
            length = declared ?? 0;
            return declared is not null;
        }
    }

    private sealed class ChunkedStream(byte[] payload, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => payload.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(Math.Min(count, chunk), payload.Length - _position);
            Array.Copy(payload, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? Authorization, string? Model, string? System, string? User);

    private sealed class ScriptedHandler(string shape, HttpStatusCode errorStatus, bool declareLength) : HttpMessageHandler
    {
        public RecordedRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var messages = body.RootElement.GetProperty("messages");
            Request = new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                body.RootElement.GetProperty("model").GetString(),
                messages[0].GetProperty("content").GetString(),
                messages[1].GetProperty("content").GetString());
            return shape switch
            {
                "transport" => throw new HttpRequestException("connection refused"),
                "timeout" => throw new TaskCanceledException("timed out"),
                "status" => Json(errorStatus, "{\"error\":\"x\"}"),
                "html" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>", Encoding.UTF8, "text/html") },
                "ok-untyped" => Untyped("{\"choices\":[{\"message\":{\"content\":\"trả lời\"}}]}"),
                "empty-choices" => Json(HttpStatusCode.OK, "{\"choices\":[]}"),
                "no-choices" => Json(HttpStatusCode.OK, "{\"id\":\"x\"}"),
                "blank-content" => Json(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"   \"}}]}"),
                "number-content" => Json(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":5}}]}"),
                "invalid-json" => Json(HttpStatusCode.OK, "{\"choices\":["),
                "array-root" => Json(HttpStatusCode.OK, "[{\"choices\":[]}]"),
                "non-object-choice" => Json(HttpStatusCode.OK, "{\"choices\":[5]}"),
                "oversized" => Oversized(),
                _ => Json(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"trả lời\"}}]}")
            };
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Untyped(string body)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.ContentType = null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private HttpResponseMessage Oversized()
        {
            var bytes = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"" + new string('x', (2 * 1024 * 1024) + 16) + "\"}}]}");
            var content = new ChunkedContent(bytes, 64 * 1024, declareLength ? bytes.Length : null);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class CapturingLogger : ILogger<Monze.OpenAiCompatibleProvider>
    {
        private readonly StringBuilder _text = new();

        public string Text => _text.ToString();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _text.AppendLine(formatter(state, exception)).AppendLine(exception?.ToString());
    }
}
