using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Mezon.Net.Client;
using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing.Harness;
using Npgsql;

namespace Monze.Campaign.Chaos;

/// <summary>
/// Background traffic that uses Monze's HTTP dependencies while a chaos
/// fault holds, on the <see cref="AgentClan"/>s only:
/// <list type="bullet">
/// <item>Agent meeting cycles, one at a time per clan, like the real Agent:
/// "*meeting now" by the owner, a member joins the suggested voice room, then
/// over SSE room_started; 1 s later the member leaves (the empty room closes the
/// meeting context and its voice claim, as in production), then room_ended and,
/// with the transcript stored on the fake, room_summary_done;</item>
/// <item>"*ai translate" commands from the members every
/// <see cref="AiInterval"/>, correlated by the loading card Monze edits.</item>
/// </list>
/// Scenario switches change how events are delivered (duplicated,
/// reordered, room_ended lost, oversized transcripts) or burst events. A
/// cycle is "clean" when every event it published reached at least one
/// open stream; events published while no stream is open are lost (the SDK
/// never sends Last-Event-ID). <see cref="MeasureAsync"/> joins what was sent
/// with the database and the recorded messages.
/// </summary>
public sealed class AgentAiTraffic
{
    public const string AiInput = "xin chào cả nhóm, hôm nay họp lúc ba giờ chiều";
    private const string LoadingMarker = "Đang xử lý yêu cầu";
    private const string SummaryTitle = "TÓM TẮT HỘI THOẠI #";
    private const string ActionsTitle = "CÁC ĐẦU MỤC CÔNG VIỆC #";
    private readonly SimHttpHost _http;
    private readonly ChaosContext _context;
    private readonly IReadOnlyList<AgentClan> _clans;
    private readonly NpgsqlDataSource _database;
    private readonly HashSet<long> _aiChannels;
    private readonly HashSet<long> _summaryChannels;
    private readonly ConcurrentDictionary<string, AgentCycle> _cycles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, long> _aiCalls = new();
    private readonly ConcurrentDictionary<long, long> _aiCards = new();
    private readonly ConcurrentDictionary<long, AiResult> _aiFinals = new();
    private readonly ConcurrentDictionary<string, int> _summaryMessages = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Random _random;
    private readonly List<Task> _loops = [];
    private int _rooms;
    private long _failureNotices;
    private int _delivery;
    private int _reorder;
    private int _loseEnded;
    private int _largeTranscripts;
    private long _aiIntervalTicks = TimeSpan.FromMilliseconds(1_500).Ticks;

    public AgentAiTraffic(SimHttpHost http, ChaosContext context, IReadOnlyList<AgentClan> clans, string connectionString, int seed)
    {
        _http = http;
        _context = context;
        _clans = clans;
        _database = NpgsqlDataSource.Create(connectionString);
        _random = new Random(seed * 131 + 7);
        _aiChannels = clans.Select(static clan => clan.AiChannel).ToHashSet();
        _summaryChannels = clans.SelectMany(static clan => clan.Voices.Append(clan.MeetingChannel)).ToHashSet();
    }

    public enum AiResult
    {
        Answered,
        ProviderEmpty,
        Busy,
        BudgetExceeded,
        RateLimited,
        Other
    }

    /// <summary>How Agent events are written to the stream while a scenario holds its fault.</summary>
    public SimSseDelivery Delivery
    {
        get => (SimSseDelivery)Volatile.Read(ref _delivery);
        set => Volatile.Write(ref _delivery, (int)value);
    }

    /// <summary>room_ended overtakes room_started (the start is held and released by the end).</summary>
    public bool Reorder
    {
        get => Volatile.Read(ref _reorder) == 1;
        set => Volatile.Write(ref _reorder, value ? 1 : 0);
    }

    /// <summary>room_ended never reaches Monze (lost upstream).</summary>
    public bool LoseEnded
    {
        get => Volatile.Read(ref _loseEnded) == 1;
        set => Volatile.Write(ref _loseEnded, value ? 1 : 0);
    }

    /// <summary>Transcripts stored for new cycles carry a 600 KiB full_text (above Monze's 512 KiB limit).</summary>
    public bool LargeTranscripts
    {
        get => Volatile.Read(ref _largeTranscripts) == 1;
        set => Volatile.Write(ref _largeTranscripts, value ? 1 : 0);
    }

    /// <summary>Pause between two AI commands.</summary>
    public TimeSpan AiInterval
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _aiIntervalTicks));
        set => Interlocked.Exchange(ref _aiIntervalTicks, value.Ticks);
    }

    public LatencyHistogram BindLatency { get; } = new();

    public LatencyHistogram AiLatency { get; } = new();

    public Load.LoadErrors Errors { get; } = new();

    /// <summary>Starts the Agent loop of each clan and the AI loop.</summary>
    public void Start()
    {
        foreach (var clan in _clans)
        {
            _loops.Add(Task.Run(() => AgentLoopAsync(clan)));
        }

        _loops.Add(Task.Run(AiLoopAsync));
    }

    /// <summary>Stops the loops; a cycle in progress publishes its remaining events first.</summary>
    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAll(_loops);
    }

    /// <summary>
    /// Publishes <paramref name="events"/> Agent events as fast as the stream
    /// takes them: rooms of three events (started, ended, summary done) on the
    /// burst voice rooms, every transcript stored first.
    /// </summary>
    public async Task BurstAsync(int events)
    {
        var rooms = new List<(string Room, AgentClan Clan, long Voice)>();
        for (var i = 0; i < events / 3; i++)
        {
            var clan = _clans[i % _clans.Count];
            var room = NewRoom(clan, "burst");
            rooms.Add((room, clan, clan.Voices[2 + (i / _clans.Count) % 2]));
            _http.SetSummary(room, Transcript(room));
        }

        foreach (var (room, clan, voice) in rooms)
        {
            var cycle = new AgentCycle(room, Stopwatch.GetTimestamp(), burst: true);
            _cycles[room] = cycle;
            cycle.Delivered(await _http.PublishAgentEventAsync("room_started", room, voice, clan.Id));
            cycle.Delivered(await _http.PublishAgentEventAsync("room_ended", room, voice, clan.Id));
            cycle.Delivered(await _http.PublishAgentEventAsync("room_summary_done", room, voice, clan.Id));
        }
    }

    /// <summary>
    /// Waits until no clean cycle is still on its way (live, or summary
    /// pending without a retry scheduled), at most <paramref name="timeout"/>.
    /// With <paramref name="warpRetries"/> pending retries are made due at
    /// once (a time warp on the database, for the transcript scenarios).
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout, bool warpRetries)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (warpRetries)
            {
                await WarpRetriesAsync();
            }

            var states = await StatesAsync();
            var open = _cycles.Values.Count(cycle => cycle.Clean
                && (!states.TryGetValue(cycle.Room, out var state) || state.Status is "live" or "suggested" || (state.Status == "summary_pending" && !state.RetryScheduled)));
            if (open == 0)
            {
                return;
            }

            await Task.Delay(500);
        }
    }

    /// <summary>Makes every pending summary retry of the Agent clans due now (database time warp).</summary>
    public async Task WarpRetriesAsync()
    {
        await using var command = _database.CreateCommand(
            "UPDATE meeting_session SET summary_next_attempt_at = now() WHERE clan_id = ANY(@clans) AND status = 'summary_pending' AND summary_next_attempt_at > now();");
        command.Parameters.AddWithValue("clans", _clans.Select(static clan => clan.Id).ToArray());
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Joins the cycles and AI calls with the database. Cycles started at or
    /// after <paramref name="recoveredTicks"/> count as "after recovery".
    /// </summary>
    public async Task<AgentAiReport> MeasureAsync(long recoveredTicks, long aiCutoffTicks)
    {
        var states = await StatesAsync();
        int posted = 0, failed = 0, stuck = 0, deferred = 0, missing = 0, afterRecovery = 0, afterRecoveryNotPosted = 0, lost = 0, burst = 0, burstPosted = 0;
        int stuckClean = 0, large = 0, largePosted = 0;
        foreach (var cycle in _cycles.Values)
        {
            var status = states.TryGetValue(cycle.Room, out var state) ? state.Status : null;
            var isPosted = status == "posted";
            posted += isPosted ? 1 : 0;
            large += cycle.Large ? 1 : 0;
            largePosted += cycle.Large && isPosted ? 1 : 0;
            failed += status == "summary_failed" ? 1 : 0;
            missing += status is null ? 1 : 0;
            var isStuck = status is "live" or "suggested" || (status == "summary_pending" && !state!.RetryScheduled);
            stuck += isStuck ? 1 : 0;
            deferred += status == "summary_pending" && state!.RetryScheduled ? 1 : 0;
            lost += cycle.Clean ? 0 : 1;
            stuckClean += isStuck && cycle.Clean ? 1 : 0;
            if (cycle.Burst)
            {
                burst++;
                burstPosted += isPosted ? 1 : 0;
            }
            else if (cycle.StartedTicks >= recoveredTicks)
            {
                afterRecovery++;
                afterRecoveryNotPosted += isPosted ? 0 : 1;
            }
        }

        long answered = 0, empty = 0, busy = 0, budget = 0, limited = 0, other = 0, unanswered = 0, expectedTokens = 0, answeredAfterRecovery = 0;
        foreach (var (command, sentTicks) in _aiCalls)
        {
            if (!_aiFinals.TryGetValue(command, out var final))
            {
                unanswered += sentTicks < aiCutoffTicks ? 1 : 0;
                continue;
            }

            switch (final)
            {
                case AiResult.Answered:
                    answered++;
                    answeredAfterRecovery += sentTicks >= recoveredTicks ? 1 : 0;
                    break;
                case AiResult.ProviderEmpty:
                    empty++;
                    break;
                case AiResult.Busy:
                    busy++;
                    break;
                case AiResult.BudgetExceeded:
                    budget++;
                    break;
                case AiResult.RateLimited:
                    limited++;
                    break;
                default:
                    other++;
                    break;
            }

            // MonzeApp.CompleteAiAsync spends (length + 3) / 4 tokens before
            // calling the provider; busy, over-budget and rate-limited answers spend nothing.
            if (final is AiResult.Answered or AiResult.ProviderEmpty)
            {
                expectedTokens += (AiInput.Length + 3) / 4;
            }
        }

        long actualTokens;
        await using (var command = _database.CreateCommand("SELECT COALESCE(sum(tokens), 0) FROM ai_usage WHERE clan_id = ANY(@clans);"))
        {
            command.Parameters.AddWithValue("clans", _clans.Select(static clan => clan.Id).ToArray());
            actualTokens = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        var duplicates = _summaryMessages.Values.Count(static count => count > 1);
        return new AgentAiReport(
            _cycles.Count, posted, failed, stuck, stuckClean, deferred, missing, lost, afterRecovery, afterRecoveryNotPosted, burst, burstPosted, duplicates,
            _summaryMessages.Values.Sum(), Interlocked.Read(ref _failureNotices), large, largePosted,
            _aiCalls.Count, answered, answeredAfterRecovery, empty, busy, budget, limited, other, unanswered, expectedTokens, actualTokens);
    }

    /// <summary>Correlates AI cards and summary messages; subscribe to <see cref="SimRecorder.Recorded"/>.</summary>
    public void OnRecorded(SimAction action)
    {
        if (action.ContentJson is not { } json)
        {
            return;
        }

        if (action.Kind == SimActionKind.SendMessage && _aiChannels.Contains(action.ChannelId) && action.ReplyToMessageId is long command)
        {
            var visible = Visible(json);
            if (visible.Contains(LoadingMarker, StringComparison.Ordinal))
            {
                _aiCards[action.MessageId] = command;
            }
            else
            {
                _aiFinals.TryAdd(command, Classify(visible));
            }
        }
        else if (action.Kind == SimActionKind.UpdateMessage && _aiCards.TryGetValue(action.MessageId, out var cardCommand))
        {
            if (_aiFinals.TryAdd(cardCommand, Classify(Visible(json))) && _aiCalls.TryGetValue(cardCommand, out var sentTicks))
            {
                AiLatency.Record(TimeSpan.FromTicks((Stopwatch.GetTimestamp() - sentTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
            }
        }
        else if (action.Kind == SimActionKind.SendMessage && _summaryChannels.Contains(action.ChannelId) && action.ResponseCode == 0)
        {
            var title = Title(json);
            if (title is not null && (title.StartsWith(SummaryTitle, StringComparison.Ordinal) || title.StartsWith(ActionsTitle, StringComparison.Ordinal)))
            {
                _summaryMessages.AddOrUpdate(title, 1, static (_, count) => count + 1);
            }
            else if (Visible(json).Contains(MonzeMessages.SummaryFailed, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _failureNotices);
            }
        }
    }

    private async Task AgentLoopAsync(AgentClan clan)
    {
        var n = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await CycleAsync(clan, n++);
                await Task.Delay(1_000, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                Errors.Add(error);
                await Task.Delay(1_000);
            }
        }
    }

    /// <summary>One meeting: invitation, member joins, started, member leaves, ended, summary; never cancelled half-way.</summary>
    private async Task CycleAsync(AgentClan clan, int n)
    {
        var host = _context.RequireHost();
        await host.Inbound.SayAsync(clan.Id, clan.MeetingChannel, clan.Owner, "*meeting now");
        var voice = await SuggestedVoiceAsync(clan.Id, TimeSpan.FromSeconds(5)) ?? clan.Voices[n % 2];
        var participant = clan.Members[n % clan.Members.Count];
        await host.Inbound.VoiceJoinAsync(clan.Id, voice, participant);
        var room = NewRoom(clan, "cycle");
        var cycle = new AgentCycle(room, Stopwatch.GetTimestamp(), burst: false);
        _cycles[room] = cycle;
        var reorder = Reorder;
        if (reorder)
        {
            // A held frame is written together with the next one, so it is not a loss.
            await _http.PublishAgentEventAsync("room_started", room, voice, clan.Id, delivery: SimSseDelivery.Hold);
            cycle.Delivered(await _http.PublishAgentEventAsync("room_ended", room, voice, clan.Id, delivery: Delivery));
        }
        else
        {
            cycle.Delivered(await _http.PublishAgentEventAsync("room_started", room, voice, clan.Id, delivery: Delivery));
        }

        var bound = await WaitForStatusAsync(room, TimeSpan.FromSeconds(10), "live", "summary_pending", "posted");

        // A room_started published while no instance was subscribed is replayed after the
        // reconnect: its wait measures the outage, not Monze.
        if (bound && cycle.Clean)
        {
            BindLatency.Record(TimeSpan.FromTicks((Stopwatch.GetTimestamp() - cycle.StartedTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
        }

        await Task.Delay(1_000);
        await _context.RequireHost().Inbound.VoiceLeaveAsync(clan.Id, voice, participant);
        if (!reorder)
        {
            if (LoseEnded)
            {
                cycle.Delivered(0);
            }
            else
            {
                cycle.Delivered(await _http.PublishAgentEventAsync("room_ended", room, voice, clan.Id, delivery: Delivery));
            }
        }

        await Task.Delay(500);
        _http.SetSummary(room, LargeTranscripts ? LargeTranscript(room) : Transcript(room));
        cycle.Large = LargeTranscripts;
        cycle.Delivered(await _http.PublishAgentEventAsync("room_summary_done", room, voice, clan.Id, delivery: Delivery));
    }

    private async Task AiLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                AgentClan clan;
                long user;
                lock (_random)
                {
                    clan = _clans[_random.Next(_clans.Count)];
                    user = clan.Members[_random.Next(clan.Members.Count)];
                }

                var sent = Stopwatch.GetTimestamp();
                var push = await _context.RequireHost().Inbound.SayAsync(clan.Id, clan.AiChannel, user, "*ai translate " + AiInput);
                if (push.DeliveredSessions > 0)
                {
                    _aiCalls[push.MessageId] = sent;
                }

                await Task.Delay(AiInterval, _stop.Token);
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

    private string NewRoom(AgentClan clan, string kind)
        => $"chaos-{kind}-{clan.Id % 10}-{Interlocked.Increment(ref _rooms).ToString(CultureInfo.InvariantCulture)}";

    private async Task<long?> SuggestedVoiceAsync(long clanId, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            await using var command = _database.CreateCommand(
                "SELECT voice_channel_id FROM meeting_session WHERE clan_id = @clan AND status = 'suggested' AND room_id IS NULL ORDER BY id DESC LIMIT 1;");
            command.Parameters.AddWithValue("clan", clanId);
            if (await command.ExecuteScalarAsync() is long voice)
            {
                return voice;
            }

            await Task.Delay(100);
        }

        return null;
    }

    private async Task<bool> WaitForStatusAsync(string room, TimeSpan timeout, params string[] statuses)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            await using var command = _database.CreateCommand("SELECT status FROM meeting_session WHERE room_id = @room ORDER BY id DESC LIMIT 1;");
            command.Parameters.AddWithValue("room", room);
            if (await command.ExecuteScalarAsync() is string status && statuses.Contains(status))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private async Task<Dictionary<string, (string Status, bool RetryScheduled)>> StatesAsync()
    {
        var states = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        await using var command = _database.CreateCommand("""
            SELECT room_id, status, COALESCE(summary_next_attempt_at > now() - interval '30 seconds', FALSE)
            FROM meeting_session WHERE clan_id = ANY(@clans) AND room_id IS NOT NULL;
            """);
        command.Parameters.AddWithValue("clans", _clans.Select(static clan => clan.Id).ToArray());
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            states[reader.GetString(0)] = (reader.GetString(1), reader.GetBoolean(2));
        }

        return states;
    }

    private static string Transcript(string room)
        => SimHttpHost.SummaryJson(
            room,
            "Nhóm thống nhất kế hoạch tuần tới.",
            new Dictionary<string, string[]> { ["speaker-1"] = ["Gửi biên bản"] });

    /// <summary>A valid transcript whose full_text makes the body about 600 KiB.</summary>
    private static string LargeTranscript(string room)
    {
        var json = Transcript(room);
        return json.Replace("\"full_text\":\"Bản ghi mô phỏng.\"", "\"full_text\":\"" + new string('a', 600 * 1024) + "\"", StringComparison.Ordinal);
    }

    private static AiResult Classify(string visible)
        => visible.Contains("Kết quả mô phỏng", StringComparison.Ordinal) ? AiResult.Answered
            : visible.Contains(MonzeMessages.AiProviderEmpty, StringComparison.Ordinal) ? AiResult.ProviderEmpty
            : visible.Contains(MonzeMessages.AiBusy, StringComparison.Ordinal) ? AiResult.Busy
            : visible.Contains(MonzeMessages.AiBudgetExceeded, StringComparison.Ordinal) ? AiResult.BudgetExceeded
            : visible.Contains(MonzeMessages.TitleRateLimited, StringComparison.Ordinal) ? AiResult.RateLimited
            : AiResult.Other;

    private static string Visible(string json)
    {
        try
        {
            var content = MessageContent.Parse(json);
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(content.Text))
            {
                parts.Add(content.Text);
            }

            foreach (var embed in content.Embeds ?? [])
            {
                parts.Add(embed.Title ?? string.Empty);
                parts.Add(embed.Description ?? string.Empty);
                foreach (var field in embed.Fields ?? [])
                {
                    parts.Add(field.Name);
                    parts.Add(field.Value);
                }
            }

            return string.Join('\n', parts);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? Title(string json)
    {
        try
        {
            return MessageContent.Parse(json).Embeds is { Count: > 0 } embeds ? embeds[0].Title : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class AgentCycle(string room, long startedTicks, bool burst)
    {
        private int _lost;

        public string Room { get; } = room;

        public long StartedTicks { get; } = startedTicks;

        public bool Burst { get; } = burst;

        /// <summary>Every event this cycle published reached an open stream.</summary>
        public bool Clean => Volatile.Read(ref _lost) == 0;

        public bool Large { get; set; }

        public void Delivered(int streams)
        {
            if (streams == 0)
            {
                Interlocked.Exchange(ref _lost, 1);
            }
        }
    }
}
