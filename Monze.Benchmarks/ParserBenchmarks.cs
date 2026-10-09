using BenchmarkDotNet.Attributes;
using Monze.Application;
using Monze.Domain;
using Monze.Ui;

/// <summary>Command, form, schedule and Agent summary parsers on typical input.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ParserBenchmarks
{
    private const string ScheduleForm = """
        {
          "monze_meeting_schedule_name": { "value": "Sprint Review" },
          "monze_meeting_schedule_date": { "value": "2026-10-01" },
          "monze_meeting_schedule_time": { "value": "18:30" },
          "monze_meeting_schedule_frequency": { "values": ["weekly"] }
        }
        """;

    private const string AgentSummary = """
        {
          "status":"ok",
          "data":{
            "room_id":"room-1",
            "created_at":"2026-10-01T10:00:00Z",
            "finalized_at":"2026-10-01T10:30:00Z",
            "participants":["100","200"],
            "speech_durations":[
              {"participant_identity":"100","duration":900},
              {"participant_identity":"200","duration":300}
            ],
            "summary_data":{
              "summary":"đã xong",
              "action_items":{"100":["Việc một","Việc hai"],"200":["Việc ba"]}
            },
            "full_text":"toàn bộ nội dung"
          }
        }
        """;

    private static readonly string[] ScheduleCommand = ["Sync", "weekly", "12/10/2026", "10:00"];
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);

    [Benchmark]
    public bool MeetingCommandSchedule()
        => MeetingCommandParser.TryParse(ScheduleCommand, out _);

    [Benchmark]
    public bool MeetingScheduleForm()
        => MeetingScheduleFormParser.TryRead(ScheduleForm, out _, out _, out _, out _, out _);

    [Benchmark]
    public bool ScheduleNextDaily()
        => MeetingScheduleCalculator.TryGetNext(
            MeetingScheduleKind.Daily,
            "18:00",
            "Asia/Ho_Chi_Minh",
            Now,
            out _,
            out _);

    [Benchmark]
    public bool AgentSummaryPayload()
        => AgentSummaryParser.TryParse(AgentSummary, "room-1", out _);
}
