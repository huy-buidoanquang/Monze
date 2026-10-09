using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Monze.Campaign.Component;
using Monze.Simulator;
using Monze.Testing.Harness;

namespace Monze.Campaign.Load;

/// <summary>
/// k6 cross-check of the load harness: Monze runs on the simulator at 10
/// registered clans, a loopback control endpoint turns each HTTP request
/// into one command and answers when Monze answered it, and k6 drives that
/// endpoint with a constant-arrival-rate of 20 requests/s. k6's p95 must agree
/// with the harness's own p95 of the same commands within 15 % (or 2 ms,
/// whichever is larger, since HTTP adds a fixed cost to a millisecond-scale
/// answer). k6 runs with usage reporting off, so it makes no network call
/// outside loopback.
/// </summary>
public static class K6CrossCheck
{
    private const double Rate = 20;
    private const double AgreementShare = 0.15;
    private const double AgreementFloorMs = 2;

    private const string Script = """
        import http from 'k6/http';
        import { check } from 'k6';

        export const options = {
          scenarios: {
            commands: {
              executor: 'constant-arrival-rate',
              rate: Number(__ENV.RATE),
              timeUnit: '1s',
              duration: __ENV.DURATION,
              preAllocatedVUs: 50,
              maxVUs: 200,
            },
          },
          summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
        };

        export default function () {
          const response = http.post(`${__ENV.TARGET}/command`, null, { timeout: '15s' });
          check(response, { answered: (r) => r.status === 200 });
        }

        export function handleSummary(data) {
          return { [__ENV.SUMMARY]: JSON.stringify(data) };
        }
        """;

    public static async Task<CampaignArtifact> RunAsync(ComponentContext context, string? k6Path)
    {
        var duration = TimeSpan.FromSeconds(context.Full ? 120 : 45);
        var artifact = new CampaignArtifact("k6", "K6-CROSSCHECK", $"k6 constant-arrival-rate {Rate} lệnh/s trong {duration.TotalSeconds:0} s qua endpoint loopback, so với đo nội bộ");
        if (k6Path is null || !File.Exists(k6Path))
        {
            return artifact.Block("Không tìm thấy k6 (PATH, MONZE_K6 hoặc --k6).");
        }

        var world = LoadWorld.Create(10);
        await using var host = await SimulatedMonzeHost.StartAsync(world.World, context.Server, "k6", new SimulatedMonzeHostOptions
        {
            Logs = new HostLogSink(LogLevel.Warning, capacity: 20_000),
            SeedAsync = world.SeedAsync,
            StartTimeout = TimeSpan.FromMinutes(2)
        });
        host.Recorder.RetainLimit = 5_000;
        var tracker = new LoadTracker(world);
        host.Recorder.Recorded += tracker.OnRecorded;
        var internalLatency = new LatencyHistogram();
        var random = new Random(context.Seed);
        long busy = 0;
        long timedOut = 0;
        using var stop = new CancellationTokenSource();
        var sweeper = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                tracker.Sweep();
                await Task.Delay(50);
            }
        });

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapPost("/command", async () =>
        {
            var started = Stopwatch.GetTimestamp();
            if (!tracker.TryTakeCommandChannel(out var slot, measured: true))
            {
                Interlocked.Increment(ref busy);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var clan = world.Clans.First(candidate => candidate.Id == slot.Clan);
            long user;
            string text;
            lock (random)
            {
                user = clan.Members[random.Next(clan.Members.Count)];
                text = LoadWorkload.CommandMix[random.Next(LoadWorkload.CommandMix.Count)].Text;
            }

            var answer = tracker.CommandSentAsync(slot.Clan, slot.Channel, user, text, started, measured: true);
            await host.Inbound.SayAsync(slot.Clan, slot.Channel, user, text);
            if (await Task.WhenAny(answer, Task.Delay(TimeSpan.FromSeconds(10))) != answer)
            {
                Interlocked.Increment(ref timedOut);
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }

            internalLatency.Record(Stopwatch.GetElapsedTime(started));
            return Results.Ok();
        });
        await app.StartAsync();
        var target = app.Urls.First();

        var work = Directory.CreateTempSubdirectory("monze-k6-");
        try
        {
            var scriptPath = Path.Combine(work.FullName, "commands.js");
            var summaryPath = Path.Combine(work.FullName, "summary.json");
            await File.WriteAllTextAsync(scriptPath, Script);
            var exitCode = await RunK6Async(k6Path, scriptPath, target, summaryPath, duration);
            await stop.CancelAsync();
            await sweeper;
            if (!File.Exists(summaryPath))
            {
                return artifact.Invariant("k6-ran", "k6 chạy và ghi summary", $"exit {exitCode}, không có summary", pass: false);
            }

            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(summaryPath));
            var metrics = summary.RootElement.GetProperty("metrics");
            var duration95 = metrics.GetProperty("http_req_duration").GetProperty("values").GetProperty("p(95)").GetDouble();
            var duration99 = metrics.GetProperty("http_req_duration").GetProperty("values").GetProperty("p(99)").GetDouble();
            var requests = metrics.GetProperty("http_reqs").GetProperty("values").GetProperty("count").GetDouble();
            var checks = metrics.GetProperty("checks").GetProperty("values");
            var passes = checks.GetProperty("passes").GetDouble();
            var fails = checks.GetProperty("fails").GetDouble();
            var internal95 = internalLatency.ValueAtPercentile(95) / 1000.0;
            var allowed = Math.Max(internal95 * AgreementShare, AgreementFloorMs);
            artifact.Metric("k6P95Ms", duration95)
                .Metric("internalP95Ms", internal95)
                .Metric("differenceMs", Math.Abs(duration95 - internal95))
                .Metric("k6P99Ms", duration99)
                .Metric("internalP99Ms", internalLatency.ValueAtPercentile(99) / 1000.0)
                .Metric("requests", (long)requests)
                .Metric("answered", (long)passes)
                .Metric("busy", Interlocked.Read(ref busy))
                .Metric("timedOut", Interlocked.Read(ref timedOut))
                .Metric("k6ExitCode", exitCode)
                .Invariant("rate", $"k6 gửi ≥ 95 % của {Rate * duration.TotalSeconds:0} request", $"{requests:0}", requests >= Rate * duration.TotalSeconds * 0.95)
                .Invariant("answered", "mọi request được Monze trả lời", $"{passes:0} trả lời, {fails:0} không", fails == 0 && passes > 0)
                .Invariant(
                    "p95-agreement",
                    string.Create(CultureInfo.InvariantCulture, $"|p95 k6 − p95 nội bộ| ≤ max(15 %, {AgreementFloorMs} ms)"),
                    string.Create(CultureInfo.InvariantCulture, $"k6 {duration95:0.###} ms, nội bộ {internal95:0.###} ms, cho phép {allowed:0.###} ms"),
                    Math.Abs(duration95 - internal95) <= allowed);
            return artifact;
        }
        finally
        {
            await stop.CancelAsync();
            await app.StopAsync();
            try
            {
                work.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Finds k6: an explicit path, MONZE_K6, PATH, then the default Windows install folder.</summary>
    public static string? Locate(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        if (Environment.GetEnvironmentVariable("MONZE_K6") is { Length: > 0 } configured)
        {
            return configured;
        }

        var executable = OperatingSystem.IsWindows() ? "k6.exe" : "k6";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "k6", executable);
        return File.Exists(installed) ? installed : null;
    }

    private static async Task<int> RunK6Async(string k6Path, string scriptPath, string target, string summaryPath, TimeSpan duration)
    {
        var start = new ProcessStartInfo(k6Path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "run", "--quiet", "--no-usage-report",
            "-e", $"TARGET={target}",
            "-e", $"SUMMARY={summaryPath}",
            "-e", $"RATE={Rate.ToString(CultureInfo.InvariantCulture)}",
            "-e", $"DURATION={(int)duration.TotalSeconds}s",
            scriptPath
        })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["K6_NO_USAGE_REPORT"] = "true";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("k6 could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(duration + TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return -1;
        }

        await output;
        await error;
        return process.ExitCode;
    }
}
