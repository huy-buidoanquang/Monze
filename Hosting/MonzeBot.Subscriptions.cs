using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Agent;

namespace Monze;

public sealed partial class MonzeBot
{
    private void SubscribeRealtimeEvents(MezonClient client)
    {
        client.ChannelMessageReceived += EnqueueMessageAsync;
        client.ChannelCreated += OnChannelCreatedAsync;
        client.ChannelUpdated += OnChannelUpdatedAsync;
        client.ChannelDeleted += OnChannelDeletedAsync;
        client.ClanUserAdded += EnqueueWelcomeAsync;
        client.VoiceJoined += OnVoiceJoinedAsync;
        client.VoiceLeaved += OnVoiceLeavedAsync;
        client.VoiceEnded += OnVoiceEndedAsync;
    }

    /// <summary>Routes an Agent SSE event the way MezonClient does (room_started, room_ended, room_summary_done).</summary>
    private Task RouteAgentEventAsync(AgentSseSessionEvent evt)
        => evt.EventType switch
        {
            "room_started" => EnqueueAgentAsync(evt, AgentEventKind.Started),
            "room_ended" => EnqueueAgentAsync(evt, AgentEventKind.Ended),
            "room_summary_done" => EnqueueAgentAsync(evt, AgentEventKind.SummaryDone),
            _ => Task.CompletedTask
        };

    private void SubscribeConnectionEvents(MezonClient client)
    {
        client.Connected += () => OnClientConnectedAsync(client);
        client.Disconnected += OnClientDisconnectedAsync;
        client.Reconnecting += OnClientReconnectingAsync;
    }
}
