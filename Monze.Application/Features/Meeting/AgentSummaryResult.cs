namespace Monze.Application;

public sealed record AgentSummaryResult(
    string RoomId,
    string Summary,
    string? FullText,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? FinalizedAt,
    IReadOnlyList<string> Participants,
    IReadOnlyList<AgentSpeechDuration> SpeechDurations,
    IReadOnlyList<AgentActionItemGroup> ActionItems,
    string FullTranscriptJson);
