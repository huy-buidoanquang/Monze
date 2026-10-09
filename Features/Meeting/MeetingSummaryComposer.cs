using System.Globalization;
using Monze.Application;
using Monze.Ui;

namespace Monze;

public sealed class MeetingSummaryComposer(IUserProfileRepository userProfiles, TimeProvider? timeProvider = null)
{
    private static readonly TimeZoneInfo VietnamTimeZone = ResolveVietnamTimeZone();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<MeetingSummaryDelivery?> ComposeAsync(
        AgentSummaryResult result,
        MeetingSummaryContext context,
        CancellationToken cancellationToken)
    {
        var identities = CollectIdentities(result);
        var labels = await ResolveLabelsAsync(context.ClanId, identities, cancellationToken);
        var presentation = BuildPresentation(result, context, labels, _time.GetUtcNow());
        var replyToMessageId = context.NotificationChannelId == context.TextChannelId
            ? context.SourceMessageId ?? context.NotificationMessageId
            : null;
        var messages = MonzeMessageBuilder.MeetingSummaryMessages(
            presentation,
            replyToMessageId);
        return new MeetingSummaryDelivery(messages.SummaryContentJson, messages.ActionItemsContentJson);
    }

    public async Task<MeetingSummaryDelivery?> ComposeAsync(
        MeetingSummaryRecord record,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.RoomId)
            || string.IsNullOrWhiteSpace(record.FullTranscriptJson)
            || !AgentSummaryParser.TryParse(record.FullTranscriptJson, record.RoomId, out var result))
        {
            return null;
        }

        var context = new MeetingSummaryContext(
            record.SessionId,
            record.ClanId,
            record.VoiceChannelId,
            record.TextChannelId,
            record.RoomId,
            record.MeetingTitle,
            record.VoiceChannelLabel,
            record.StartedAt,
            record.EndedAt,
            record.NotificationMessageId,
            null);
        return await ComposeAsync(result, context, cancellationToken);
    }

    private async Task<Dictionary<string, string>> ResolveLabelsAsync(
        long clanId,
        IReadOnlyList<string> identities,
        CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var fallbackIndex = 0;
        for (var i = 0; i < identities.Count; i++)
        {
            var identity = identities[i];
            if (long.TryParse(identity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
                && userId > 0)
            {
                var profile = await userProfiles.GetByIdAsync(clanId, userId, cancellationToken);
                var profileLabel = profile is null ? null : GetProfileLabel(profile);
                if (profileLabel is not null)
                {
                    labels[identity] = profileLabel;
                    continue;
                }
            }

            fallbackIndex++;
            labels[identity] = $"Thành viên {fallbackIndex}";
        }

        return labels;
    }

    private static MeetingSummaryPresentation BuildPresentation(
        AgentSummaryResult result,
        MeetingSummaryContext context,
        IReadOnlyDictionary<string, string> labels,
        DateTimeOffset now)
    {
        var start = result.CreatedAt ?? context.StartedAt;
        var end = result.FinalizedAt ?? context.EndedAt;
        if (start is null)
        {
            start = end ?? now;
        }

        if (end is null || end < start)
        {
            end = start;
        }

        var label = CleanLabel(context.VoiceChannelLabel);
        var title = $"TÓM TẮT HỘI THOẠI #{context.SessionId} (🔊 {label})";
        var actionTitle = $"CÁC ĐẦU MỤC CÔNG VIỆC #{context.SessionId} (🔊 {label})";
        var transcriptUrl = $"https://mezon.ai/developers/transcript-calls/{Uri.EscapeDataString(result.RoomId)}";
        var description = FormatDescription(start.Value, end.Value);
        var participants = FormatParticipants(result, labels);
        var content = Limit(result.Summary, 3800);
        var actions = result.ActionItems
            .Where(group => !MezonAgentIdentity.IsAgent(group.ParticipantIdentity))
            .Select(group => new MeetingSummaryActionItem(
                labels.TryGetValue(group.ParticipantIdentity, out var name) ? name : "Thành viên",
                FormatActions(group.Items)))
            .Take(25)
            .ToArray();

        return new MeetingSummaryPresentation(
            title,
            actionTitle,
            description,
            participants,
            content,
            transcriptUrl,
            actions.Length == 0
                ? [new MeetingSummaryActionItem(string.Empty, "Chưa có đầu mục công việc.")]
                : actions);

        string FormatActions(IReadOnlyList<string> items)
        {
            var lines = new List<string>(Math.Min(items.Count, 20));
            for (var i = 0; i < items.Count && i < 20; i++)
            {
                lines.Add($"{i + 1}. {Limit(items[i], 400)}");
            }

            return string.Join('\n', lines);
        }
    }

    private static string FormatParticipants(
        AgentSummaryResult result,
        IReadOnlyDictionary<string, string> labels)
    {
        var durations = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in result.SpeechDurations)
        {
            if (MezonAgentIdentity.IsAgent(item.ParticipantIdentity))
            {
                continue;
            }

            durations[item.ParticipantIdentity] = durations.GetValueOrDefault(item.ParticipantIdentity) + Math.Max(0, item.DurationSeconds);
        }

        var identities = durations
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var participant in result.Participants)
        {
            if (!MezonAgentIdentity.IsAgent(participant)
                && !identities.Contains(participant, StringComparer.Ordinal))
            {
                identities.Add(participant);
            }
        }

        if (identities.Count == 0)
        {
            return "Chưa có dữ liệu người tham gia.";
        }

        var totalSpeech = durations.Values.Sum();
        var lines = new List<string>(Math.Min(identities.Count, 25));
        for (var i = 0; i < identities.Count && i < 25; i++)
        {
            var identity = identities[i];
            var name = labels.TryGetValue(identity, out var label) ? label : "Thành viên";
            if (!durations.TryGetValue(identity, out var seconds) || totalSpeech <= 0)
            {
                lines.Add($"- {name}: chưa có dữ liệu thời lượng");
                continue;
            }

            var percentage = seconds / totalSpeech * 100;
            lines.Add($"- {name}: {FormatSpeechDuration(seconds)} ({percentage:0.##}%)");
        }

        return string.Join('\n', lines);
    }

    private static string FormatDescription(DateTimeOffset start, DateTimeOffset end)
    {
        var localStart = TimeZoneInfo.ConvertTime(start, VietnamTimeZone);
        var localEnd = TimeZoneInfo.ConvertTime(end, VietnamTimeZone);
        var duration = end - start;
        var durationText = FormatDuration(duration);
        var date = FormatVietnameseDate(localStart);
        var time = $"{FormatClock(localStart)} - {FormatClock(localEnd)}";
        if (localStart.Date != localEnd.Date)
        {
            date = $"{date} - {FormatVietnameseDate(localEnd)}";
            time = $"{FormatClock(localStart)} ({FormatShortDate(localStart)}) - {FormatClock(localEnd)} ({FormatShortDate(localEnd)})";
        }

        return $"{date}\nCuộc hội thoại diễn ra trong {durationText}. {time}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalMinutes = duration <= TimeSpan.Zero
            ? 0L
            : Math.Max(1L, (long)Math.Ceiling(duration.TotalMinutes));
        if (totalMinutes < 60)
        {
            return $"{totalMinutes} phút";
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return minutes == 0
            ? $"{hours} giờ"
            : $"{hours} giờ {minutes} phút";
    }

    private static string FormatVietnameseDate(DateTimeOffset value)
    {
        var weekday = value.DayOfWeek switch
        {
            DayOfWeek.Sunday => "Chủ nhật",
            DayOfWeek.Monday => "Thứ 2",
            DayOfWeek.Tuesday => "Thứ 3",
            DayOfWeek.Wednesday => "Thứ 4",
            DayOfWeek.Thursday => "Thứ 5",
            DayOfWeek.Friday => "Thứ 6",
            _ => "Thứ 7"
        };
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}, ngày {1:00} tháng {2:00} năm {3}",
            weekday,
            value.Day,
            value.Month,
            value.Year);
    }

    private static string FormatClock(DateTimeOffset value)
        => value.ToString("hh:mm tt", CultureInfo.InvariantCulture);

    private static string FormatShortDate(DateTimeOffset value)
        => value.ToString("dd/MM", CultureInfo.InvariantCulture);

    private static string FormatSpeechDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (duration == TimeSpan.Zero)
        {
            return "0 giây";
        }

        return duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes} phút {duration.Seconds} giây"
            : $"{Math.Max(1, duration.Seconds)} giây";
    }

    private static IReadOnlyList<string> CollectIdentities(AgentSummaryResult result)
    {
        var identities = new List<string>();
        foreach (var item in result.SpeechDurations)
        {
            Add(item.ParticipantIdentity);
        }

        foreach (var participant in result.Participants)
        {
            Add(participant);
        }

        foreach (var group in result.ActionItems)
        {
            Add(group.ParticipantIdentity);
        }

        return identities;

        void Add(string identity)
        {
            if (!string.IsNullOrWhiteSpace(identity)
                && !MezonAgentIdentity.IsAgent(identity)
                && !identities.Contains(identity, StringComparer.Ordinal))
            {
                identities.Add(identity);
            }
        }
    }

    private static string? GetProfileLabel(UserProfileSnapshot profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ClanNick))
        {
            return profile.ClanNick.Trim();
        }

        if (!string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            return profile.DisplayName.Trim();
        }

        return string.IsNullOrWhiteSpace(profile.Username)
            ? null
            : profile.Username.Trim();
    }

    private static string CleanLabel(string? label)
        => string.IsNullOrWhiteSpace(label) || long.TryParse(label, out _)
            ? "phòng voice"
            : label.Trim().TrimStart('#');

    private static string Limit(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 4)] + " ...";

    private static TimeZoneInfo ResolveVietnamTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        }
    }
}
