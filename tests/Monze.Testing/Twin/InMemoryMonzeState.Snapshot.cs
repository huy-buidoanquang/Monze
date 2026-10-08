using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Monze.Testing.Twin;

public sealed partial class InMemoryMonzeState
{
    /// <summary>
    /// Renders every table and sequence of the twin as canonical text, one
    /// row per line in key order, so two snapshots are equal exactly when the
    /// business state is equal.
    /// </summary>
    public string Snapshot()
    {
        var text = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;
        lock (_gate)
        {
            foreach (var (clanId, clan) in _clans.OrderBy(static pair => pair.Key))
            {
                text.Append(culture, $"clan_registry clan={clanId} owner={clan.OwnerId} inactive_reason={Quote(clan.InactiveReason)}").Append('\n');
            }

            foreach (var (clanId, userId) in _admins.Order())
            {
                text.Append(culture, $"clan_admin clan={clanId} user={userId}").Append('\n');
            }

            foreach (var (clanId, row) in _settings.OrderBy(static pair => pair.Key))
            {
                text.Append(culture, $"clan_settings clan={clanId} version={row.Version} welcome_enabled={Flag(row.WelcomeEnabled)} welcome_text={Quote(row.WelcomeText)} welcome_embed={Quote(row.WelcomeEmbedJson)} role_enabled={Flag(row.RoleEnabled)}").Append('\n');
            }

            foreach (var (key, rule) in _roleRules
                .OrderBy(static pair => pair.Key.ClanId)
                .ThenBy(static pair => pair.Key.RoleId)
                .ThenBy(static pair => pair.Key.Kind, StringComparer.Ordinal))
            {
                text.Append(culture, $"role_rule clan={key.ClanId} role={key.RoleId} kind={Quote(key.Kind)} enabled={Flag(rule.Enabled)} condition_value={Quote(rule.ConditionValue)} version={rule.Version} updated_at={Time(rule.UpdatedAt)}").Append('\n');
            }

            foreach (var (key, grantedAt) in _roleGrants.OrderBy(static pair => pair.Key))
            {
                text.Append(culture, $"role_grant clan={key.ClanId} role={key.RoleId} user={key.UserId} granted_at={Time(grantedAt)}").Append('\n');
            }

            foreach (var (key, claimedAt) in _welcomeDeliveries.OrderBy(static pair => pair.Key))
            {
                text.Append(culture, $"welcome_delivery clan={key.ClanId} user={key.UserId} claimed_at={Time(claimedAt)}").Append('\n');
            }

            foreach (var (key, tokens) in _aiUsage.OrderBy(static pair => pair.Key))
            {
                text.Append(culture, $"ai_usage clan={key.ClanId} user={key.UserId} usage_day={key.Day.ToString("yyyy-MM-dd", culture)} tokens={tokens}").Append('\n');
            }

            foreach (var (key, row) in _channelPolicies.OrderBy(static pair => pair.Key))
            {
                text.Append(culture, $"channel_policy clan={key.ClanId} channel={key.ChannelId} persist_messages={Flag(row.PersistMessages)} last_message_id={Number(row.LastMessageId)} has_gap={Flag(row.HasGap)}").Append('\n');
            }

            foreach (var (id, row) in _schedules)
            {
                text.Append(culture, $"meeting_schedule id={id} clan={row.ClanId} channel={row.ChannelId} requester={row.RequesterId} title={Quote(row.Title)} kind={Quote(row.Kind)} when_text={Quote(row.WhenText)} timezone={Quote(row.TimeZoneId)} next_run_at={Time(row.NextRunAt)} status={Quote(row.Status)} locked_until={Time(row.LockedUntil)} lease_token={Quote(row.LeaseToken)} last_error={Quote(row.LastError)}").Append('\n');
            }

            foreach (var (id, row) in _sessions)
            {
                text.Append(culture, $"meeting_session id={id} clan={row.ClanId} text_channel={row.TextChannelId} voice_channel={Number(row.VoiceChannelId)} requester={row.RequesterId} status={Quote(row.Status)} room_id={Quote(row.RoomId)} claim_until={Time(row.ClaimUntil)} created_at={Time(row.CreatedAt)} started_at={Time(row.StartedAt)} ended_at={Time(row.EndedAt)} notification_message_id={Number(row.NotificationMessageId)} root_session_id={Number(row.RootSessionId)} context_closed_at={Time(row.ContextClosedAt)} voice_channel_label={Quote(row.VoiceChannelLabel)} meeting_title={Quote(row.MeetingTitle)}").Append('\n');
            }

            foreach (var (key, claim) in _voiceClaims.OrderBy(static pair => pair.Key))
            {
                text.Append(culture, $"voice_claim clan={key.ClanId} voice_channel={key.VoiceChannelId} session={claim.SessionId} expires_at={Time(claim.ExpiresAt)}").Append('\n');
            }

            foreach (var (sessionId, summary) in _summaries)
            {
                text.Append(culture, $"meeting_summary session={sessionId} summary_text={Quote(summary.SummaryText)} full_transcript={Quote(summary.FullTranscriptJson)} posted={Flag(summary.Posted)}").Append('\n');
            }

            text.Append(culture, $"sequence meeting_schedule={_scheduleSequence} meeting_session={_sessionSequence} schedule_lease={_leaseSequence}").Append('\n');
        }

        return text.ToString();
    }

    // JSON string literals keep the text unambiguous; the relaxed encoder
    // leaves non-ASCII text readable in test failure diffs.
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Quote(string? value)
        => value is null ? "null" : JsonSerializer.Serialize(value, SnapshotJson);

    private static string Flag(bool value)
        => value ? "true" : "false";

    private static string Number(long? value)
        => value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "null";

    private static string Time(DateTimeOffset? value)
        => value is { } instant
            ? instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            : "null";
}
