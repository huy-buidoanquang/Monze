using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Mezon.Net.Client;
using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Mezon.Net.Sdk.Entities;
using Mezon.Net.Sdk.Interactions;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Monze.Ui;
using Xunit;
using Envelope = Mezon.Net.Internal.Realtime.Envelope;
using SdkMezonClient = Mezon.Net.Sdk.MezonClient;

namespace Monze.Tests.E2E;

/// <summary>
/// Keeps the simulator honest. The SDK calls and event subscriptions are
/// read from Monze's production source and mapped to wire operations; every
/// operation must be modelled (or deliberately excluded), every push kind
/// must raise its SDK event exactly once on a probe client, faults must
/// surface through the SDK, and a real Monze session must only use declared,
/// modelled operations.
/// </summary>
public sealed partial class SimWireCatalogTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly string[] ProductionRoots = ["Hosting", "Features", "Infrastructure", "Ui", "Program.cs"];
    private const string Socket = "socket lifecycle";

    // SDK surface Monze can reach (Mezon.Net.Sdk 1.6.2).
    private static readonly Type[] SdkSurface =
    [
        typeof(SdkMezonClient),
        typeof(Channel),
        typeof(Clan),
        typeof(Message),
        typeof(User),
        typeof(Role),
        typeof(ICommandContext),
        typeof(IInteractionContext),
        typeof(InteractionRouter),
        typeof(CommandService)
    ];

    // SDK method -> wire operations it causes at v1.6.2 (MezonClient.cs,
    // Entities/*.cs, Messaging/MessageSendHelper.cs, Clients/MezonSocketClient.cs).
    private static readonly IReadOnlyDictionary<string, string[]> SdkCallWire = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // REST login, handshake, then InitializeAfterConnectedAsync: DM list,
        // ListClanDescs + ClanJoin per clan; the heartbeat loop pings.
        ["LoginAsync"] = [SimOperations.Authenticate, SimOperations.SocketConnect, SimOperations.Heartbeat, SimOperations.ListChannelDescs, SimOperations.ListClanDescs, SimOperations.ClanJoin],
        ["DisposeAsync"] = [SimOperations.SocketDisconnect],
        ["ConnectAgentSseAsync"] = [SimOperations.AgentSse],
        ["ListClanDescsAsync"] = [SimOperations.ListClanDescs],
        ["JoinClanAsync"] = [SimOperations.ClanJoin],
        ["GetClanAsync"] = [SimOperations.ListClanDescs],
        ["GetChannelAsync"] = [SimOperations.ListChannelDetail, SimOperations.ListClanDescs],
        ["GetChannelDetailAsync"] = [SimOperations.ListChannelDetail],
        ["ListChannelDescsAsync"] = [SimOperations.ListChannelDescs],
        ["LoadChannelsAsync"] = [SimOperations.ListChannelDescs],
        ["ListClanUsersAsync"] = [SimOperations.ListClanUsers],
        ["ListChannelMessagesAsync"] = [SimOperations.ListChannelMessages],
        ["ListChannelVoiceUsersAsync"] = [SimOperations.ListChannelVoiceUsers],
        ["ListRolesAsync"] = [SimOperations.ListRoles],
        ["UpdateRoleAsync"] = [SimOperations.UpdateRole],
        ["SendAsync"] = [SimOperations.ChannelMessageSend],
        ["ReplyAsync"] = [SimOperations.ChannelMessageSend],
        ["SendEphemeralAsync"] = [SimOperations.EphemeralMessageSend],
        ["UpdateEphemeralAsync"] = [SimOperations.EphemeralMessageSend],
        ["DeleteEphemeralAsync"] = [SimOperations.EphemeralMessageSend],
        ["UpdateMessageAsync"] = [SimOperations.UpdateChannelMessage],
        ["HandleButtonAsync"] = [],
        ["HandleSelectAsync"] = []
    };

    // SDK event -> the push (or lifecycle) that raises it.
    private static readonly IReadOnlyDictionary<string, string> SdkEventPush = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ChannelMessageReceived"] = nameof(SimPushKind.ChannelMessage),
        ["MessageButtonClicked"] = nameof(SimPushKind.MessageButtonClicked),
        ["DropdownBoxSelected"] = nameof(SimPushKind.DropdownBoxSelected),
        ["ClanUserAdded"] = nameof(SimPushKind.AddClanUserEvent),
        ["VoiceJoined"] = nameof(SimPushKind.VoiceJoinedEvent),
        ["VoiceLeaved"] = nameof(SimPushKind.VoiceLeavedEvent),
        ["VoiceEnded"] = nameof(SimPushKind.VoiceEndedEvent),
        ["ChannelCreated"] = nameof(SimPushKind.ChannelCreatedEvent),
        ["ChannelUpdated"] = nameof(SimPushKind.ChannelUpdatedEvent),
        ["ChannelDeleted"] = nameof(SimPushKind.ChannelDeletedEvent),
        ["Connected"] = Socket,
        ["Disconnected"] = Socket,
        ["Reconnecting"] = Socket,
        ["AgentSessionStarted"] = SimOperations.AgentSse,
        ["AgentSessionEnded"] = SimOperations.AgentSse,
        ["AgentSessionSummaryDone"] = SimOperations.AgentSse
    };

    [Fact]
    [Req("REQ-HOST-022")]
    public void Every_sdk_call_in_monze_source_maps_to_a_modelled_wire_operation()
    {
        var sdkMethods = SdkSurface
            .SelectMany(static type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(static method => method.Name)
            .Where(static name => name.EndsWith("Async", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var called = MonzeSdkCalls(sdkMethods);

        var unmapped = called.Keys.Where(name => !SdkCallWire.ContainsKey(name)).Order().ToList();
        Assert.True(unmapped.Count == 0, "Monze calls SDK methods the simulator catalog does not map: " + string.Join(", ", unmapped.Select(name => $"{name} ({called[name]})")));
        var stale = SdkCallWire.Keys.Where(name => !called.ContainsKey(name)).Order().ToList();
        Assert.True(stale.Count == 0, "Catalog entries Monze no longer calls: " + string.Join(", ", stale));

        var operations = SdkCallWire.Values.SelectMany(static wire => wire).ToHashSet(StringComparer.Ordinal);
        var unknown = operations.Where(op => !SimOperations.Modelled.Contains(op) && !SimOperations.NotModelled.ContainsKey(op)).Order().ToList();
        Assert.True(unknown.Count == 0, "Wire operations the simulator neither models nor excludes: " + string.Join(", ", unknown));
        Assert.Equal(new[] { SimOperations.AgentSse }, operations.Where(SimOperations.NotModelled.ContainsKey).ToArray());

        // The excluded operation is refused, not silently ignored.
        var simulator = new MezonSimulator(SmokeWorld.Create());
        var options = new MezonClientOptions(SmokeWorld.BotId, SmokeWorld.BotToken) { AgentEventUrl = "http://127.0.0.1:9/agent" };
        Assert.Throws<InvalidOperationException>(() => simulator.Configure(options));

        // Names the simulator uses are real SDK wire names.
        Assert.All(SimOperations.Apis, static api => Assert.True(MezonApiMap.TryGetIndex(api, out _), $"{api} is not in MezonApiMap"));
        Assert.All(
            SimOperations.Realtime.Where(static op => op != SimOperations.Heartbeat),
            static op => Assert.True(Enum.TryParse<Envelope.MessageOneofCase>(op, out _), $"{op} is not an Envelope case"));
    }

    [Fact]
    [Req("REQ-HOST-022")]
    public void Every_sdk_event_monze_subscribes_to_has_a_simulated_push()
    {
        var subscribed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, text) in ProductionSources())
        {
            foreach (Match match in EventSubscription().Matches(text))
            {
                subscribed.Add(match.Groups["event"].Value);
            }
        }

        Assert.NotEmpty(subscribed);
        var unmapped = subscribed.Where(name => !SdkEventPush.ContainsKey(name)).ToList();
        Assert.True(unmapped.Count == 0, "Monze subscribes to SDK events the simulator cannot raise: " + string.Join(", ", unmapped));
        Assert.All(SdkEventPush.Keys, static name => Assert.NotNull(typeof(SdkMezonClient).GetEvent(name)));
        Assert.All(SdkEventPush.Values, static target => Assert.True(
            target == Socket || SimOperations.NotModelled.ContainsKey(target) || Enum.TryParse<SimPushKind>(target, out _),
            $"Unknown push target {target}"));
        foreach (var kind in Enum.GetValues<SimPushKind>())
        {
            Assert.Contains(kind.ToString(), SdkEventPush.Values);
            Assert.True(Enum.TryParse<Envelope.MessageOneofCase>(kind.ToString(), out _), $"{kind} is not an Envelope case");
        }
    }

    [Fact]
    [Req("REQ-HOST-022")]
    public async Task Every_push_kind_raises_its_client_event_exactly_once()
    {
        var world = SmokeWorld.Create();
        await using var probe = await Probe.StartAsync(world);
        var client = probe.Client;
        var channel = await client.GetChannelAsync(SmokeWorld.GeneralId);
        var botMessage = (await channel.SendAsync(MessageContent.CreateText("probe buttons"))).MessageId;
        Assert.True(botMessage > 0);
        const long newUser = 1_840_000_000_000_000_777L;
        const long newChannel = 1_840_000_000_000_006_777L;
        var text = $"probe {Guid.NewGuid():N}";
        var hits = new ConcurrentDictionary<SimPushKind, int>();
        void Hit(SimPushKind kind, bool matches)
        {
            if (matches)
            {
                hits.AddOrUpdate(kind, 1, static (_, count) => count + 1);
            }
        }

        client.ChannelMessageReceived += evt => Done(() => Hit(SimPushKind.ChannelMessage, MessageContent.Parse(((ChannelMessageResponse)evt).Content).Text == text));
        client.MessageButtonClicked += evt => Done(() => Hit(SimPushKind.MessageButtonClicked, ((MessageButtonClickedResponse)evt) is { ButtonId: "probe_button", UserId: SmokeWorld.MemberId } click && click.MessageId == botMessage));
        client.DropdownBoxSelected += evt => Done(() => Hit(SimPushKind.DropdownBoxSelected, ((DropdownBoxSelectedResponse)evt) is { SelectboxId: "probe_select" } select && select.Values.Single() == "b"));
        client.ClanUserAdded += evt => Done(() => Hit(SimPushKind.AddClanUserEvent, ((AddClanUserEventResponse)evt) is { ClanId: SmokeWorld.ClanId } added && added.User.UserId == newUser));
        client.VoiceJoined += evt => Done(() => Hit(SimPushKind.VoiceJoinedEvent, ((VoiceJoinedEventResponse)evt) is { VoiceChannelId: SmokeWorld.VoiceId, UserId: SmokeWorld.MemberId }));
        client.VoiceLeaved += evt => Done(() => Hit(SimPushKind.VoiceLeavedEvent, ((VoiceLeavedEventResponse)evt) is { VoiceChannelId: SmokeWorld.VoiceId, VoiceUserId: SmokeWorld.MemberId }));
        client.VoiceEnded += evt => Done(() => Hit(SimPushKind.VoiceEndedEvent, ((VoiceEndedEventResponse)evt).VoiceChannelId == SmokeWorld.VoiceId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        client.ChannelCreated += evt => Done(() => Hit(SimPushKind.ChannelCreatedEvent, ((ChannelCreatedEventResponse)evt) is { ChannelId: newChannel, ChannelType: (int)ChannelType.MezonVoice }));
        client.ChannelUpdated += evt => Done(() => Hit(SimPushKind.ChannelUpdatedEvent, ((ChannelUpdatedEventResponse)evt) is { ChannelId: newChannel, ChannelLabel: "renamed" }));
        client.ChannelDeleted += evt => Done(() => Hit(SimPushKind.ChannelDeletedEvent, ((ChannelDeletedEventResponse)evt).ChannelId == newChannel));

        var inbound = probe.Simulator.Inbound;
        foreach (var kind in Enum.GetValues<SimPushKind>())
        {
            var push = kind switch
            {
                SimPushKind.ChannelMessage => await inbound.SayAsync(SmokeWorld.ClanId, SmokeWorld.GeneralId, SmokeWorld.MemberId, text),
                SimPushKind.MessageButtonClicked => await inbound.ClickButtonAsync(SmokeWorld.ClanId, SmokeWorld.GeneralId, botMessage, SmokeWorld.MemberId, "probe_button"),
                SimPushKind.DropdownBoxSelected => await inbound.SelectDropdownAsync(SmokeWorld.ClanId, SmokeWorld.GeneralId, botMessage, SmokeWorld.MemberId, "probe_select", ["b"]),
                SimPushKind.AddClanUserEvent => await inbound.UserAddedAsync(SmokeWorld.ClanId, newUser),
                SimPushKind.VoiceJoinedEvent => await inbound.VoiceJoinAsync(SmokeWorld.ClanId, SmokeWorld.VoiceId, SmokeWorld.MemberId),
                SimPushKind.VoiceLeavedEvent => await inbound.VoiceLeaveAsync(SmokeWorld.ClanId, SmokeWorld.VoiceId, SmokeWorld.MemberId),
                SimPushKind.VoiceEndedEvent => await inbound.VoiceEndAsync(SmokeWorld.ClanId, SmokeWorld.VoiceId),
                SimPushKind.ChannelCreatedEvent => await inbound.ChannelCreatedAsync(SmokeWorld.ClanId, newChannel, "probe room", (int)ChannelType.MezonVoice),
                SimPushKind.ChannelUpdatedEvent => await inbound.ChannelUpdatedAsync(SmokeWorld.ClanId, newChannel, label: "renamed"),
                SimPushKind.ChannelDeletedEvent => await inbound.ChannelDeletedAsync(SmokeWorld.ClanId, newChannel),
                _ => throw new InvalidOperationException($"No probe scenario for push kind {kind}.")
            };
            Assert.Equal(kind, push.Kind);
            Assert.Equal(1, push.TargetSessions);
            Assert.Equal(1, push.DeliveredSessions);
        }

        var all = Enum.GetValues<SimPushKind>();
        await Eventually(() => all.All(kind => hits.GetValueOrDefault(kind) >= 1), "every push raised its event");
        await Task.Delay(500);
        Assert.All(all, kind => Assert.True(hits.GetValueOrDefault(kind) == 1, $"{kind} raised {hits.GetValueOrDefault(kind)} events"));
        Assert.Empty(probe.Simulator.Recorder.UnmodelledCalls);
        Assert.Empty(probe.Simulator.Recorder.ProtocolViolations);
    }

    [Fact]
    [Req("REQ-CONN-001")]
    public async Task Server_socket_close_drives_one_reconnect_and_the_clan_is_joined_again()
    {
        await using var probe = await Probe.StartAsync(SmokeWorld.Create());
        var client = probe.Client;
        var connected = 0;
        var disconnected = 0;
        var reconnecting = 0;
        client.Connected += () => Done(() => Interlocked.Increment(ref connected));
        client.Disconnected += _ => Done(() => Interlocked.Increment(ref disconnected));
        client.Reconnecting += _ => Done(() => Interlocked.Increment(ref reconnecting));
        var session = Assert.Single(probe.Simulator.Sessions);
        var mark = probe.Simulator.Recorder.LastSequence;

        Assert.Equal(1, await probe.Simulator.Inbound.CloseSocketAsync());

        await probe.Simulator.Recorder.WaitForAsync(action => action.Kind == SimActionKind.ClanJoin && action.ClanId == SmokeWorld.ClanId, Timeout, mark);
        await Eventually(() => Volatile.Read(ref connected) == 1, "the client reconnected");
        Assert.Equal(1, Volatile.Read(ref disconnected));
        Assert.Equal(1, Volatile.Read(ref reconnecting));
        Assert.Equal(2, session.ConnectCount);
        Assert.True(session.HasJoinedClan(SmokeWorld.ClanId));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ChannelMessageReceived += evt => Done(() => received.TrySetResult());
        var push = await probe.Simulator.Inbound.SayAsync(SmokeWorld.ClanId, SmokeWorld.GeneralId, SmokeWorld.MemberId, "after reconnect");
        Assert.Equal(1, push.DeliveredSessions);
        await received.Task.WaitAsync(Timeout);
        Assert.Empty(probe.Simulator.Recorder.UnmodelledCalls);
        Assert.Empty(probe.Simulator.Recorder.ProtocolViolations);
    }

    [Fact]
    [Req("REQ-HOST-022")]
    public async Task Scripted_faults_and_unmodelled_calls_surface_through_the_sdk()
    {
        await using var probe = await Probe.StartAsync(SmokeWorld.Create(), new MezonSimulatorOptions { SocketTimeoutMilliseconds = 1_500 });
        var client = probe.Client;
        var simulator = probe.Simulator;

        simulator.Faults.Fail(SimOperations.ListClanUsers, MezonStatusCode.Unavailable);
        var failure = await Assert.ThrowsAsync<MezonApiException>(() => client.ListClanUsersAsync(SmokeWorld.ClanId));
        Assert.Equal(MezonStatusCode.Unavailable, failure.StatusCode);
        Assert.Equal(3, (await client.ListClanUsersAsync(SmokeWorld.ClanId)).ClanUsers.Count);

        simulator.Faults.DropResponse(SimOperations.ListRoles);
        await Assert.ThrowsAsync<TimeoutException>(() => client.ListRolesAsync(new RoleListEventParams(clanId: SmokeWorld.ClanId)));

        simulator.Faults.Delay(SimOperations.ListChannelDetail, TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        Assert.Equal(SmokeWorld.VoiceId, (await client.GetChannelDetailAsync(SmokeWorld.VoiceId)).ChannelId);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(250), $"delay was {watch.Elapsed}");

        var unmodelled = await Assert.ThrowsAsync<MezonApiException>(() => client.ListFriendsAsync());
        Assert.Equal(MezonStatusCode.Unimplemented, unmodelled.StatusCode);
        Assert.Equal("ListFriends", Assert.Single(simulator.Recorder.UnmodelledCalls).Operation);

        var texts = new ConcurrentQueue<string>();
        client.ChannelMessageReceived += evt => Done(() => texts.Enqueue(MessageContent.Parse(((ChannelMessageResponse)evt).Content).Text ?? string.Empty));
        simulator.Faults.DuplicatePush(SimPushKind.ChannelMessage).ReorderPush(SimPushKind.ChannelMessage).DropPush(SimPushKind.ChannelMessage);
        var duplicated = await simulator.Inbound.SayAsync(SmokeWorld.ClanId, SmokeWorld.GeneralId, SmokeWorld.MemberId, "twice");
        var held = await simulator.Inbound.SayAsync(SmokeWorld.ClanId, SmokeWorld.GeneralId, SmokeWorld.MemberId, "late");
        var dropped = await simulator.Inbound.SayAsync(SmokeWorld.ClanId, SmokeWorld.GeneralId, SmokeWorld.MemberId, "never");
        var overtaking = await simulator.Inbound.SayAsync(SmokeWorld.ClanId, SmokeWorld.GeneralId, SmokeWorld.MemberId, "first");

        Assert.Equal(SimFaultKind.DuplicatePush, duplicated.Fault);
        Assert.Equal((SimFaultKind.ReorderPush, 0), (held.Fault!.Value, held.DeliveredSessions));
        Assert.Equal((SimFaultKind.DropPush, 0), (dropped.Fault!.Value, dropped.DeliveredSessions));
        Assert.Equal(1, overtaking.DeliveredSessions);
        await Eventually(() => texts.Count(static text => text == "late") == 1, "the held push was released");
        await Task.Delay(300);
        Assert.Equal(2, texts.Count(static text => text == "twice"));
        Assert.Equal(1, texts.Count(static text => text == "first"));
        Assert.DoesNotContain("never", texts);
        var pushes = simulator.Recorder.Since(held.Sequence).Where(static action => action.Kind == SimActionKind.Push).Select(static action => action.MessageId).ToList();
        Assert.True(
            pushes.IndexOf(overtaking.MessageId) < pushes.LastIndexOf(held.MessageId),
            "The held push must reach the client after the push that overtook it.");
        Assert.Empty(simulator.Recorder.ProtocolViolations);
    }

    [DbFact]
    [Req("REQ-HOST-022")]
    public async Task Wire_operations_of_a_monze_session_are_declared_and_modelled()
    {
        await using var host = await MonzeE2EHost.StartAsync(SmokeWorld.Create(), "wire");
        var member = SmokeWorld.MemberId;
        var general = SmokeWorld.GeneralId;

        await host.Inbound.SayAsync(SmokeWorld.ClanId, general, member, "*monze help");
        var card = await host.Recorder.WaitForAsync(action => action.Kind == SimActionKind.SendEphemeral && action.ChannelId == general, Timeout);
        await host.Inbound.ClickButtonAsync(SmokeWorld.ClanId, general, card.MessageId, member, MonzeButtonId.HelpMeeting);
        await host.Recorder.WaitForAsync(action => action.Kind == SimActionKind.UpdateEphemeral && action.MessageId == card.MessageId, Timeout);
        await host.Inbound.ClickButtonAsync(SmokeWorld.ClanId, general, card.MessageId, member, MonzeButtonId.HelpClose);
        await host.Recorder.WaitForAsync(action => action.Kind == SimActionKind.DeleteEphemeral && action.MessageId == card.MessageId, Timeout);
        Assert.True(host.World.FindMessage(card.MessageId)!.Deleted);
        await host.Inbound.SayAsync(SmokeWorld.ClanId, general, member, "*meeting now");
        await host.Recorder.WaitForAsync(action => action.Kind == SimActionKind.SendMessage && action.ChannelId == general && action.MentionEveryone, Timeout);

        var beforeClose = host.Recorder.LastSequence;
        Assert.Equal(1, await host.Inbound.CloseSocketAsync());
        await host.Logs.WaitForAsync(static entry => entry.Message.StartsWith("Mezon clan rejoin completed", StringComparison.Ordinal), Timeout);
        await host.Recorder.WaitForAsync(action => action.Kind == SimActionKind.ClanJoin && action.ClanId == SmokeWorld.ClanId, Timeout, beforeClose);
        var afterReconnect = host.Recorder.LastSequence;
        await host.Inbound.SayAsync(SmokeWorld.ClanId, general, member, "*monze help");
        await host.Recorder.WaitForAsync(action => action.Kind == SimActionKind.SendEphemeral && action.ChannelId == general, Timeout, afterReconnect);
        await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(5));
        await host.StopHostAsync();
        Assert.Contains(host.Recorder.Actions, static action => action.Kind == SimActionKind.Disconnect);

        var declared =SdkCallWire.Values.SelectMany(static wire => wire).ToHashSet(StringComparer.Ordinal);
        var observed = host.Recorder.ObservedOperations.ToHashSet(StringComparer.Ordinal);
        Assert.Subset(declared, observed);
        Assert.All(observed, static operation => Assert.Contains(operation, SimOperations.Modelled));
        Assert.All(
            host.Recorder.Actions.Where(static action => action.IsOutbound && action.Kind != SimActionKind.Authenticate),
            static action => Assert.True(action.ResponseCode == 0, $"Unexpected failure answer: {action}"));
        host.AssertClean(allowWarningsWithExceptions: true);
    }

    private static Dictionary<string, string> MonzeSdkCalls(IReadOnlySet<string> sdkMethods)
    {
        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, text) in ProductionSources())
        {
            foreach (Match match in AsyncInvocation().Matches(text))
            {
                var name = match.Groups["name"].Value;
                if (sdkMethods.Contains(name))
                {
                    calls.TryAdd(name, file);
                }
            }
        }

        return calls;
    }

    private static IEnumerable<(string File, string Text)> ProductionSources()
    {
        var root = RepositoryPaths.Root;
        foreach (var entry in ProductionRoots)
        {
            var path = Path.Combine(root, entry);
            var files = File.Exists(path)
                ? [path]
                : Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return (relative, File.ReadAllText(file));
            }
        }
    }

    private static Task Done(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail($"Timed out waiting until {what}.");
            }

            await Task.Delay(25);
        }
    }

    [GeneratedRegex("\\.(?<name>[A-Z]\\w*Async)\\(")]
    private static partial Regex AsyncInvocation();

    [GeneratedRegex("\\b_?client\\.(?<event>[A-Z]\\w*)\\s*\\+=")]
    private static partial Regex EventSubscription();

    /// <summary>A bare SDK client logged in to a fresh simulator (no Monze host).</summary>
    private sealed class Probe : IAsyncDisposable
    {
        private Probe(MezonSimulator simulator, SdkMezonClient client)
        {
            Simulator = simulator;
            Client = client;
        }

        public MezonSimulator Simulator { get; }

        public SdkMezonClient Client { get; }

        public static async Task<Probe> StartAsync(SimWorld world, MezonSimulatorOptions? options = null)
        {
            var simulator = new MezonSimulator(world, options);
            var clientOptions = new MezonClientOptions(world.Bot.Id, world.Bot.Token)
            {
                TransportType = TransportType.WebSocket,
                SocketHandlerTimeoutInMilliseconds = null
            };
            simulator.Configure(clientOptions);
            var client = new SdkMezonClient(clientOptions);
            try
            {
                Assert.True(await client.LoginAsync().WaitAsync(Timeout));
                return new Probe(simulator, client);
            }
            catch
            {
                await client.DisposeAsync();
                await simulator.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Simulator.DisposeAsync();
        }
    }
}
