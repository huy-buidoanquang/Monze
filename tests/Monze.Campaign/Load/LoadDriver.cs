using System.Diagnostics;
using System.Globalization;
using Monze.Simulator;
using Monze.Ui;
using Npgsql;

namespace Monze.Campaign.Load;

/// <summary>
/// The open-loop traffic of a load stage, also used as background load by
/// chaos and soak runs: one generator thread (chat messages, commands, help
/// clicks, joins and voice changes), an outbox writer that inserts due
/// announcements with a unique nonce, and a sweeper that frees answered
/// command channels. Traffic runs from <c>start</c> to <c>end</c> (Stopwatch
/// ticks); events scheduled from <c>measureStart</c> on are measured.
/// </summary>
public sealed class LoadDriver
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _generator;
    private readonly Task _outbox;
    private readonly Task _sweeper;

    private LoadDriver(SimulatedMonzeHost host, LoadWorld world, LoadWorkload workload, int seed, long start, long measureStart, long end)
    {
        Tracker = new LoadTracker(world);
        Tracker.SetMeasureWindow(measureStart, end);
        host.Recorder.Recorded += Tracker.OnRecorded;
        var random = new Random(seed * 7919 + world.Registered);
        _generator = new Thread(() => Generate(host, world, Tracker, workload, random, start, measureStart, end, Errors, _stop.Token))
        {
            IsBackground = true,
            Name = "monze-load-generator"
        };
        _outbox = WriteOutboxAsync(host, world, Tracker, workload, new Random(seed * 31 + world.Registered), start, measureStart, end, Errors, _stop.Token);
        _sweeper = SweepAsync(Tracker, _stop.Token);
        _generator.Start();
    }

    public LoadTracker Tracker { get; }

    public LoadErrors Errors { get; } = new();

    public static LoadDriver Start(SimulatedMonzeHost host, LoadWorld world, LoadWorkload workload, int seed, long start, long measureStart, long end)
        => new(host, world, workload, seed, start, measureStart, end);

    /// <summary>Waits until the generator reached the end of its schedule (or was stopped).</summary>
    public void WaitForSchedule() => _generator.Join();

    /// <summary>Stops all traffic; the tracker keeps correlating late answers until the host stops.</summary>
    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        _generator.Join();
        await _outbox;
        await _sweeper;
    }

    private static void Generate(SimulatedMonzeHost host, LoadWorld world, LoadTracker tracker, LoadWorkload workload, Random random, long start, long measureStart, long measureEnd, LoadErrors errors, CancellationToken cancellationToken)
    {
        var zipf = ZipfTable(world.Clans.Count);
        var streams = new (double Rate, Action<long, bool> Fire)[]
        {
            (workload.MessagesPerSecond, (due, measured) =>
            {
                var clan = Pick(world.Clans, zipf, random);
                var user = clan.Members[random.Next(clan.Members.Count)];
                errors.Watch(host.Inbound.SayAsync(clan.Id, clan.Chat[random.Next(clan.Chat.Count)], user, "tin nhắn tải " + due.ToString(CultureInfo.InvariantCulture)));
            }),
            (workload.CommandsPerSecond, (due, measured) =>
            {
                if (!tracker.TryTakeCommandChannel(out var slot, measured))
                {
                    return;
                }

                var clan = world.Clans.First(candidate => candidate.Id == slot.Clan);
                var user = random.NextDouble() < LoadWorkload.SpamShare ? clan.Spammer : clan.Members[random.Next(clan.Members.Count)];
                var text = PickCommand(random);
                tracker.CommandSent(slot.Clan, slot.Channel, user, text, due, measured);
                errors.Watch(host.Inbound.SayAsync(slot.Clan, slot.Channel, user, text));
            }),
            (workload.ClicksPerSecond, (due, measured) =>
            {
                if (tracker.TryTakeHelpMessage(out var help, measured))
                {
                    tracker.ClickSent(help.Message, due, measured);
                    errors.Watch(host.Inbound.ClickButtonAsync(help.Clan, help.Channel, help.Message, help.User, MonzeButtonId.HelpMeeting));
                }
            }),
            (workload.JoinsPerSecond, (due, measured) =>
            {
                var clan = Pick(world.Clans, zipf, random);
                var user = world.World.NextId();
                tracker.JoinSent(user, due, measured);
                errors.Watch(host.Inbound.UserAddedAsync(clan.Id, user));
            }),
            (workload.VoiceChangesPerSecond, (due, measured) =>
            {
                var clan = Pick(world.Clans, zipf, random);
                var voice = clan.Voice[random.Next(clan.Voice.Count)];
                var user = clan.Members[random.Next(clan.Members.Count)];
                errors.Watch(host.World.VoiceOccupants(voice).Contains(user)
                    ? host.Inbound.VoiceLeaveAsync(clan.Id, voice, user)
                    : host.Inbound.VoiceJoinAsync(clan.Id, voice, user));
            })
        };
        var intervals = streams.Select(static stream => Stopwatch.Frequency / stream.Rate).ToArray();
        var next = intervals.Select((interval, i) => start + (long)(interval * (i + 1) / streams.Length)).ToArray();
        while (true)
        {
            var index = 0;
            for (var i = 1; i < next.Length; i++)
            {
                if (next[i] < next[index])
                {
                    index = i;
                }
            }

            var due = next[index];
            if (due >= measureEnd || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            while (Stopwatch.GetTimestamp() < due)
            {
                Thread.Yield();
            }

            var measured = due >= measureStart;
            if (measured)
            {
                tracker.GeneratorLag.Record(TimeSpan.FromTicks((Stopwatch.GetTimestamp() - due) * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
            }

            try
            {
                streams[index].Fire(due, measured);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            next[index] = due + (long)intervals[index];
        }
    }

    /// <summary>Inserts due announcements every 100 ms, each carrying a unique nonce in its body.</summary>
    private static async Task WriteOutboxAsync(SimulatedMonzeHost host, LoadWorld world, LoadTracker tracker, LoadWorkload workload, Random random, long start, long measureStart, long measureEnd, LoadErrors errors, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(host.Database.ConnectionString);
        var zipf = ZipfTable(world.Clans.Count);
        var perBatch = Math.Max(1, (int)Math.Round(workload.OutboxPerSecond / 10));
        long sequence = 0;
        var nextBatch = start;
        while (!cancellationToken.IsCancellationRequested && nextBatch < measureEnd)
        {
            var wait = TimeSpan.FromTicks((nextBatch - Stopwatch.GetTimestamp()) * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            var clans = new long[perBatch];
            var channels = new long[perBatch];
            var nonces = new string[perBatch];
            for (var i = 0; i < perBatch; i++)
            {
                var clan = Pick(world.Clans, zipf, random);
                channels[i] = clan.Chat[random.Next(clan.Chat.Count)];
                clans[i] = clan.Id;
                nonces[i] = LoadStage.OutboxNoncePrefix + (++sequence).ToString(CultureInfo.InvariantCulture);
            }

            try
            {
                await using var command = dataSource.CreateCommand("""
                    INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body)
                    SELECT clan, channel, 'Announcement', 'load-outbox:' || nonce, 'Thông báo tải ' || nonce
                    FROM unnest(@clans, @channels, @nonces) AS row(clan, channel, nonce);
                    """);
                command.Parameters.AddWithValue("clans", clans);
                command.Parameters.AddWithValue("channels", channels);
                command.Parameters.AddWithValue("nonces", nonces);
                await command.ExecuteNonQueryAsync(cancellationToken);
                var committed = Stopwatch.GetTimestamp();
                foreach (var nonce in nonces)
                {
                    tracker.OutboxRowInserted(nonce, committed, nextBatch >= measureStart);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                errors.Add(error);
            }

            nextBatch += Stopwatch.Frequency / 10;
        }
    }

    private static async Task SweepAsync(LoadTracker tracker, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            tracker.Sweep();
            try
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static double[] ZipfTable(int count)
    {
        var cumulative = new double[count];
        var total = 0.0;
        for (var i = 0; i < count; i++)
        {
            total += 1 / Math.Pow(i + 1, 1.1);
            cumulative[i] = total;
        }

        for (var i = 0; i < count; i++)
        {
            cumulative[i] /= total;
        }

        return cumulative;
    }

    private static LoadClan Pick(IReadOnlyList<LoadClan> clans, double[] zipf, Random random)
    {
        var index = Array.BinarySearch(zipf, random.NextDouble());
        return clans[Math.Min(index < 0 ? ~index : index, clans.Count - 1)];
    }

    private static string PickCommand(Random random)
    {
        var roll = random.Next(100);
        foreach (var (text, weight) in LoadWorkload.CommandMix)
        {
            if (roll < weight)
            {
                return text;
            }

            roll -= weight;
        }

        return LoadWorkload.CommandMix[0].Text;
    }
}
