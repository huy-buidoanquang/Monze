namespace Monze.Application;

public sealed record PendingMeetingSummary(string RoomId, int Attempts, string LeaseToken);
