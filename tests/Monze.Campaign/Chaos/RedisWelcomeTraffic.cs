using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Monze.Campaign.Load;
using Monze.Simulator;
using Monze.Testing.Harness;

namespace Monze.Campaign.Chaos;

/// <summary>
/// Background traffic that reads and writes Monze's Redis cache, for Redis
/// chaos scenarios. Welcome settings are the only read model in Redis and L1
/// answers repeated reads, so every second the owner of one of the last five
/// load clans changes the welcome text ("*welcome message ...", which
/// invalidates L1 and writes a tombstone to Redis) and a new member joins
/// that clan, whose welcome then reads the settings through L2 (and falls
/// back to PostgreSQL when Redis fails). Each welcome must carry the text of
/// the latest edit; the latency is measured from the join.
/// </summary>
public sealed class RedisWelcomeTraffic
{
    private readonly ChaosContext _context;
    private readonly IReadOnlyList<LoadClan> _clans;
    private readonly HashSet<long> _welcomeChannels;
    private readonly ConcurrentDictionary<long, (string Marker, long SentTicks)> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private long _edits;
    private long _editsAnswered;
    private long _joins;
    private long _joinsDuringFault;
    private long _welcomed;
    private long _stale;
    private readonly List<string> _staleExamples = [];
    private long _faultStart = long.MaxValue;
    private long _faultEnd = long.MaxValue;

    public RedisWelcomeTraffic(ChaosContext context, LoadWorld world)
    {
        _context = context;
        _clans = world.Clans.Skip(Math.Max(0, world.Clans.Count - 5)).ToList();
        _welcomeChannels = _clans.Select(static clan => clan.Welcome).ToHashSet();
    }

    public long Edits => Interlocked.Read(ref _edits);

    public long EditsAnswered => Interlocked.Read(ref _editsAnswered);

    public long Joins => Interlocked.Read(ref _joins);

    /// <summary>Joins sent while the fault held (each reads the welcome settings through the cache).</summary>
    public long JoinsDuringFault => Interlocked.Read(ref _joinsDuringFault);

    public long Welcomed => Interlocked.Read(ref _welcomed);

    /// <summary>Welcomes that did not carry the latest edited text.</summary>
    public long Stale => Interlocked.Read(ref _stale);

    /// <summary>Up to five stale welcomes: fault phase of the join, expected and received text.</summary>
    public IReadOnlyList<string> StaleExamples
    {
        get
        {
            lock (_staleExamples)
            {
                return _staleExamples.ToList();
            }
        }
    }

    public LatencyHistogram Welcomes { get; } = new();

    /// <summary>Welcomes for joins sent while the fault held.</summary>
    public LatencyHistogram WelcomesDuringFault { get; } = new();

    public LoadErrors Errors { get; } = new();

    public void SetFaultWindow(long start, long end)
    {
        Interlocked.Exchange(ref _faultEnd, end);
        Interlocked.Exchange(ref _faultStart, start);
    }

    public void Start() => _loop = Task.Run(LoopAsync);

    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        await _loop;
    }

    /// <summary>Joins sent before <paramref name="cutoffTicks"/> that were never welcomed.</summary>
    public long Missing(long cutoffTicks) => _pending.Values.Count(pending => pending.SentTicks < cutoffTicks);

    public void OnRecorded(SimAction action)
    {
        if (action.Kind != SimActionKind.SendMessage || !_welcomeChannels.Contains(action.ChannelId) || action.ContentJson is not { } content)
        {
            return;
        }

        foreach (var (user, pending) in _pending)
        {
            if (!content.Contains(user.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) || !_pending.TryRemove(user, out _))
            {
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            var latency = TimeSpan.FromTicks((now - pending.SentTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
            Interlocked.Increment(ref _welcomed);
            Welcomes.Record(latency);
            if (pending.SentTicks >= Interlocked.Read(ref _faultStart) && pending.SentTicks < Interlocked.Read(ref _faultEnd))
            {
                WelcomesDuringFault.Record(latency);
            }

            if (!content.Contains(pending.Marker, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _stale);
                var phase = pending.SentTicks < Interlocked.Read(ref _faultStart) ? "trước" : pending.SentTicks < Interlocked.Read(ref _faultEnd) ? "trong" : "sau";
                var found = System.Text.RegularExpressions.Regex.Match(content, "luot-[0-9]+x");
                lock (_staleExamples)
                {
                    if (_staleExamples.Count < 5)
                    {
                        _staleExamples.Add($"{phase} fault: chờ {pending.Marker}, nhận {(found.Success ? found.Value : "mặc định")}");
                    }
                }
            }
        }
    }

    private async Task LoopAsync()
    {
        var round = 0;
        var latest = new Dictionary<long, string>();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                // Even rounds edit a clan's text and then join it; odd rounds only join a clan
                // edited a few rounds earlier (maybe during the fault), so the read path meets
                // settings whose invalidation may have failed.
                var edit = round % 2 == 0;
                var clan = _clans[(round / 2 + (edit ? 0 : 2)) % _clans.Count];
                round++;
                var host = _context.RequireHost();
                if (edit)
                {
                    var marker = $"luot-{round}x";
                    var since = host.Recorder.LastSequence;
                    Interlocked.Increment(ref _edits);
                    await host.Inbound.SayAsync(clan.Id, clan.Chat[1], clan.Owner, $"*welcome message Chao {{user}} {marker}");
                    try
                    {
                        // The owner's ephemeral answer; background outbox announcements also land in chat channels.
                        await host.Recorder.WaitForAsync(
                            action => action.ChannelId == clan.Chat[1] && action.Kind == SimActionKind.SendEphemeral && action.ReceiverIds.Contains(clan.Owner),
                            TimeSpan.FromSeconds(10),
                            since);
                        Interlocked.Increment(ref _editsAnswered);
                        latest[clan.Id] = marker;
                    }
                    catch (TimeoutException)
                    {
                        // An unanswered edit may or may not have been stored: its clan is not checked until the next edit.
                        latest.Remove(clan.Id);
                        continue;
                    }
                }

                var user = host.World.NextId();
                _pending[user] = (latest.GetValueOrDefault(clan.Id) ?? string.Empty, Stopwatch.GetTimestamp());
                Interlocked.Increment(ref _joins);
                var sentAt = Stopwatch.GetTimestamp();
                if (sentAt >= Interlocked.Read(ref _faultStart) && sentAt < Interlocked.Read(ref _faultEnd))
                {
                    Interlocked.Increment(ref _joinsDuringFault);
                }

                await host.Inbound.UserAddedAsync(clan.Id, user);
                await Task.Delay(1_000, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                Errors.Add(error);
                await Task.Delay(500);
            }
        }
    }
}
