using Microsoft.Extensions.DependencyInjection;
using Monze.Simulator;

namespace Monze.Tests.E2E.Harness;

/// <summary>
/// Steps of an area scenario: start Monze on <see cref="AreaWorld"/> with
/// the secret canaries, send a command and wait until Monze finished it,
/// click and wait for the answer, and find the bot output an input caused.
/// Waiting for each step keeps the oracle's per-input windows exact.
/// </summary>
internal static class E2EActions
{
    /// <summary>
    /// Starts Monze with the canaries and generous rate-limit buckets (the
    /// rate-limit scenario overrides them through <paramref name="configuration"/>).
    /// </summary>
    public static Task<MonzeE2EHost> StartAsync(
        string tag,
        SimWorld? world = null,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? services = null,
        bool seedDelegate = false,
        Action<SimFaultPlan>? faults = null,
        Func<MonzeWorkerTimings, MonzeWorkerTimings>? timings = null,
        MezonSimulatorOptions? simulatorOptions = null,
        SimHttpHost? http = null,
        bool agent = true,
        bool ai = true,
        Monze.Testing.Postgres.CampaignDatabase? database = null,
        MezonSimulator? simulator = null,
        DirectoryInfo? dataDirectory = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Monze:RateLimit:UserLimit"] = "100",
            ["Monze:RateLimit:AdminLimit"] = "100",
            ["Monze:RateLimit:MeetingLimit"] = "100",
            ["Monze:RateLimit:AiLimit"] = "100"
        };
        foreach (var (key, value) in E2ECanaries.Configuration)
        {
            settings[key] = value;
        }

        if (http is not null)
        {
            if (agent)
            {
                settings["Mezon:AgentBaseUrl"] = http.BaseUrl;
            }

            if (ai)
            {
                settings["Monze:Ai:BaseUrl"] = http.BaseUrl;
            }
        }

        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        return MonzeE2EHost.StartAsync(
            simulator?.World ?? world ?? AreaWorld.Create(),
            tag,
            simulatorOptions: simulatorOptions,
            faults: faults,
            configuration: settings,
            services: services,
            seed: seedDelegate ? AreaWorld.SeedDelegateAsync : null,
            timings: timings,
            database: database,
            simulator: simulator,
            dataDirectory: dataDirectory,
            http: http);
    }

    /// <summary>
    /// A fake for Monze's HTTP dependencies that accepts the area bot's
    /// credentials and the AI key canary.
    /// </summary>
    public static Task<SimHttpHost> HttpAsync(Func<SimAiRequest, string>? aiReply = null)
        => SimHttpHost.StartAsync(new SimHttpHostOptions
        {
            BotId = AreaWorld.BotId,
            BotToken = E2ECanaries.MezonToken,
            AiApiKey = E2ECanaries.AiApiKey,
            AiReply = aiReply ?? (static request => $"Kết quả mô phỏng ({request.Input?.Length ?? 0} ký tự).")
        });

    /// <summary>
    /// Sends a command that every connected bot session receives (two
    /// instances on one simulator) and waits until its command_inbox row left
    /// 'processing'.
    /// </summary>
    public static async Task<SimPush> CommandAnySessionAsync(
        MonzeE2EHost host,
        long channelId,
        long userId,
        string text,
        long clanId = AreaWorld.ClanId)
    {
        var push = await host.Inbound.SayAsync(clanId, channelId, userId, text);
        if (push.DeliveredSessions < 1)
        {
            throw new InvalidOperationException($"Command '{text}' reached no bot session.");
        }

        await host.WaitForCommandStatusAsync(clanId, channelId, push.MessageId, E2EOracles.Timeout);
        return push;
    }

    /// <summary>Polls <paramref name="condition"/> until it holds.</summary>
    public static async Task WaitUntilAsync(MonzeE2EHost host, Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? E2EOracles.Timeout);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.{Environment.NewLine}{host.Recorder.Describe(40)}{Environment.NewLine}{host.Logs.Describe(40)}{Environment.NewLine}{host.Http?.Describe() ?? string.Empty}");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>Sends a command and waits until its command_inbox row left 'processing'.</summary>
    public static async Task<SimPush> CommandAsync(
        MonzeE2EHost host,
        long channelId,
        long userId,
        string text,
        IReadOnlyList<long>? mentions = null,
        long clanId = AreaWorld.ClanId)
    {
        var push = await host.Inbound.SayAsync(clanId, channelId, userId, text, mentions: mentions);
        if (push.DeliveredSessions != 1)
        {
            throw new InvalidOperationException($"Command '{text}' reached {push.DeliveredSessions} bot sessions.");
        }

        await host.WaitForCommandStatusAsync(clanId, channelId, push.MessageId, E2EOracles.Timeout);
        return push;
    }

    /// <summary>Sends a message Monze should ignore and waits until the bot stayed quiet.</summary>
    public static async Task<SimPush> IgnoredAsync(MonzeE2EHost host, Func<Task<SimPush>> send)
    {
        var push = await send();
        await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
        return push;
    }

    /// <summary>
    /// A click from <paramref name="userId"/>'s client on a message. Unless
    /// <paramref name="allowInvisible"/>, the simulator refuses clicks on an
    /// ephemeral the user cannot see. Waits until Monze finished dispatching it.
    /// </summary>
    public static async Task<SimPush> ClickAsync(
        MonzeE2EHost host,
        long channelId,
        long messageId,
        long userId,
        string buttonId,
        string? extraData = null,
        bool allowInvisible = false)
    {
        var before = DispatchCount(host, messageId, buttonId);
        var push = await host.Inbound.ClickButtonAsync(AreaWorld.ClanId, channelId, messageId, userId, buttonId, extraData, allowInvisible);
        await WaitForDispatchAsync(host, messageId, buttonId, before);
        return push;
    }

    /// <summary>
    /// A click event as a malicious client can put it on the wire (any user
    /// id, any message id, extra_data of its choice); waits until Monze
    /// finished dispatching it.
    /// </summary>
    public static async Task<SimPush> ForgeClickAsync(
        MonzeE2EHost host,
        long channelId,
        long messageId,
        long forgedUserId,
        string buttonId,
        string? extraData = null)
    {
        var before = DispatchCount(host, messageId, buttonId);
        var push = await host.Inbound.ForgeButtonClickAsync(channelId, messageId, host.World.Bot.Id, forgedUserId, buttonId, extraData);
        await WaitForDispatchAsync(host, messageId, buttonId, before);
        return push;
    }

    /// <summary>
    /// Waits for MonzeBot's "Button interaction dispatch completed|failed" log
    /// line of this click: the router has run the handler to the end.
    /// </summary>
    private static async Task WaitForDispatchAsync(MonzeE2EHost host, long messageId, string buttonId, int before)
    {
        var deadline = DateTimeOffset.UtcNow + E2EOracles.Timeout;
        while (DispatchCount(host, messageId, buttonId) <= before)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Monze did not dispatch the click {buttonId} on message {messageId}.{Environment.NewLine}{host.Logs.Describe()}");
            }

            await Task.Delay(20);
        }

        await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));
    }

    private static int DispatchCount(MonzeE2EHost host, long messageId, string buttonId)
        => host.Logs.Entries.Count(entry => entry.Message.StartsWith("Button interaction dispatch", StringComparison.Ordinal)
            && entry.Message.Contains($"Message={messageId},", StringComparison.Ordinal)
            && (entry.Message.Contains($"CustomId={buttonId},", StringComparison.Ordinal)
                || entry.Message.EndsWith($"CustomId={buttonId}.", StringComparison.Ordinal)));

    /// <summary>Waits until at least <paramref name="count"/> host log entries match.</summary>
    public static async Task WaitForLogsAsync(MonzeE2EHost host, Func<HostLogEntry, bool> predicate, int count)
    {
        var deadline = DateTimeOffset.UtcNow + E2EOracles.Timeout;
        while (host.Logs.Entries.Count(predicate) < count)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Expected {count} matching host log entries.{Environment.NewLine}{host.Logs.Describe()}");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>The first new message (send or ephemeral) the bot produced in the channel after the input.</summary>
    public static SimAction NewMessageAfter(MonzeE2EHost host, SimPush input, long channelId)
        => host.Recorder.Since(input.Sequence)
            .First(action => action.ChannelId == channelId
                && action.Kind is SimActionKind.SendMessage or SimActionKind.SendEphemeral);

    /// <summary>The last update of an ephemeral after the input.</summary>
    public static SimAction LastUpdateAfter(MonzeE2EHost host, SimPush input, long messageId)
        => host.Recorder.Since(input.Sequence)
            .Last(action => action.MessageId == messageId && action.Kind == SimActionKind.UpdateEphemeral);
}
