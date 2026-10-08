namespace Monze.Simulator;

/// <summary>
/// Platform events the simulator can push. Each name equals the
/// <c>Envelope.MessageOneofCase</c> the SDK dispatches
/// (src/Mezon.Net.Client/MezonClient.EventHandling.cs at v1.6.2).
/// </summary>
public enum SimPushKind
{
    /// <summary>ChannelMessage, raised as MezonClient.ChannelMessageReceived.</summary>
    ChannelMessage,

    /// <summary>MessageButtonClicked, raised as MezonClient.MessageButtonClicked.</summary>
    MessageButtonClicked,

    /// <summary>DropdownBoxSelected, raised as MezonClient.DropdownBoxSelected.</summary>
    DropdownBoxSelected,

    /// <summary>AddClanUserEvent, raised as MezonClient.ClanUserAdded.</summary>
    AddClanUserEvent,

    /// <summary>VoiceJoinedEvent, raised as MezonClient.VoiceJoined.</summary>
    VoiceJoinedEvent,

    /// <summary>VoiceLeavedEvent, raised as MezonClient.VoiceLeaved.</summary>
    VoiceLeavedEvent,

    /// <summary>VoiceEndedEvent, raised as MezonClient.VoiceEnded.</summary>
    VoiceEndedEvent,

    /// <summary>ChannelCreatedEvent, raised as MezonClient.ChannelCreated.</summary>
    ChannelCreatedEvent,

    /// <summary>ChannelUpdatedEvent, raised as MezonClient.ChannelUpdated.</summary>
    ChannelUpdatedEvent,

    /// <summary>ChannelDeletedEvent, raised as MezonClient.ChannelDeleted.</summary>
    ChannelDeletedEvent
}
