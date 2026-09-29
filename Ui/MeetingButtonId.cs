namespace Monze.Ui;

public static class MeetingButtonId
{
    public const string Refresh = "monze_meeting_refresh";
    public const string StartNow = "monze_meeting_now";
    public const string Schedule = "monze_meeting_schedule";
    public const string ScheduleSubmit = "monze_meeting_schedule_submit";
    public const string ScheduleCancel = "monze_meeting_schedule_cancel";
    public const string ScheduleName = "monze_meeting_schedule_name";
    public const string ScheduleDate = "monze_meeting_schedule_date";
    public const string ScheduleTime = "monze_meeting_schedule_time";
    public const string ScheduleFrequency = "monze_meeting_schedule_frequency";
    public const string Help = "monze_meeting_help";
    public const string CancelPrefix = "monze_meeting_cancel:";

    public static string CancelFor(long scheduleId)
        => CancelPrefix + scheduleId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryReadCancelId(string buttonId, out long scheduleId)
    {
        scheduleId = 0;
        return buttonId.StartsWith(CancelPrefix, StringComparison.Ordinal)
            && long.TryParse(
                buttonId.AsSpan(CancelPrefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out scheduleId)
            && scheduleId > 0;
    }
}
