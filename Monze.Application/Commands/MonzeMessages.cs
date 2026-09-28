using System.Globalization;
using Monze.Domain;

namespace Monze.Application.Commands;

public static class MonzeMessages
{
    public const string TitleMonze = "Monze";
    public const string TitleMeeting = "Meeting";
    public const string TitleSummary = "Summary";
    public const string TitleHelp = "Hướng dẫn";
    public const string TitleWelcome = "Welcome";
    public const string TitleClanSetup = "Cấu hình clan";
    public const string TitleWelcomeSetup = "Cấu hình welcome";
    public const string TitleRateLimited = "Giới hạn thao tác";
    public const string AdminOnly = "Chỉ owner hoặc người được ủy quyền.";
    public const string OwnerOnly = "Chỉ owner của clan được thay đổi người được ủy quyền.";
    public const string NoFaq = "Không thấy mục nào.";
    public const string NoTopic = "Chưa có chủ đề.";
    public const string TopicAdded = "Đã thêm chủ đề.";
    public const string TopicAlreadyExists = "Chủ đề này đã có trong clan.";
    public const string TopicRemoved = "Đã xóa chủ đề.";
    public const string TopicNotFound = "Không tìm thấy chủ đề này.";
    public const string NoEvent = "Không có sự kiện đang mở.";
    public const string NoVoiceRoom = "Không còn phòng voice trống.";
    public const string AiNotConfigured = "Chưa cấu hình AI provider.";
    public const string AiProviderEmpty = "Provider không trả nội dung.";
    public const string AiBudgetExceeded = "Hết ngân sách AI trong ngày hoặc chưa bật provider.";
    public const string AiInputEmpty = "Hãy nhập nội dung cần xử lý.";
    public const string AiInputTooLong = "Nội dung AI vượt quá giới hạn cho phép của clan.";
    public const string AiBusy = "AI đang xử lý các yêu cầu khác. Hãy thử lại sau.";
    public const string OwnerAlreadyAdmin = "Owner đã có quyền quản trị sẵn.";
    public const string DelegateAdded = "Đã thêm người được ủy quyền.";
    public const string DelegateRemoved = "Đã xóa người được ủy quyền.";
    public const string DelegateAlreadyExists = "Người này đã được ủy quyền trước đó.";
    public const string DelegateNotFound = "Người này không có trong danh sách ủy quyền.";
    public const string VoiceClaimConflict = "Phòng voice vừa được giữ bởi meeting khác. Hãy thử lại sau.";
    public const string InvalidTime = "Thời điểm không hợp lệ.";
    public const string NoSummary = "Chưa có kết quả summary cho phòng này.";
    public const string WelcomeEnabled = "Đã bật welcome.";
    public const string WelcomeDisabled = "Đã tắt welcome.";
    public const string WelcomeEmbedSaved = "Đã lưu mẫu welcome.";
    public const string WelcomeDraftInvalid = "Mã mẫu đã hết hạn, đã dùng hoặc không thuộc kênh này. Hãy mở lại welcome setting.";
    public const string WelcomePreview = "Bản xem trước welcome";
    public const string EventJoined = "Đã ghi nhận đăng ký.";
    public const string EventWaitlisted = "Sự kiện đã đủ chỗ. Bạn đã được đưa vào danh sách chờ.";
    public const string EventAlreadyJoined = "Bạn đã đăng ký sự kiện này trước đó.";
    public const string EventNotFound = "Chưa có sự kiện.";
    public const string OutboxQueued = "Đã đưa vào hàng gửi.";
    public const string OutboxResent = "Đã xếp gửi lại.";
    public const string OutboxUncertainEmpty = "Không có mục uncertain.";
    public const string OutboxUncertainNotFound = "Không thấy mục uncertain đó.";
    public const string FaqSeparatorMissing = "Thiếu dấu | giữa câu hỏi và trả lời.";
    public const string FaqSaved = "Đã lưu FAQ.";
    public const string NoPoints = "Chưa có điểm.";
    public const string SpinCooldown = "Bạn đã quay gần đây. Hãy thử lại sau một phút.";
    public const string RoleGatewayNotReady = "Role gateway chưa sẵn sàng.";
    public const string RoleNotFound = "Không tìm thấy role đang hoạt động trong clan.";
    public const string RolePointRulesUnavailable = "Role theo điểm chưa nằm ở đợt clan. Hãy dùng sau khi có điểm.";
    public const string RoleSelfNotAllowed = "Role này chưa được owner cho phép tự chọn.";
    public const string RoleAssignFailed = "Không thể cấp role. Role không thuộc clan, đã tắt hoặc bot không đủ quyền.";
    public const string RoleAssigned = "Đã cấp role.";
    public const string RoleRuleInvalid = "Rule role không hợp lệ hoặc thiếu điều kiện.";
    public const string UnknownClan = "Chưa xác định được clan.";
    public const string TemporaryFailure = "Lệnh gặp lỗi tạm thời. Hãy thử lại sau.";
    public const string MemberFallbackLabel = "thành viên";
    public const string DefaultWelcomeText = "Chào mừng bạn đến clan. Hãy xem kênh hướng dẫn và đọc quy định của clan.";

    public static string WelcomeSetupText(MonzeCommandOptions options)
        => $"Dùng {options.Command(MonzeCommandNames.Welcome, MonzeCommandActions.Setting)} để chỉnh mẫu chào. "
            + $"Bạn cũng có thể dùng {options.Command(MonzeCommandNames.Welcome, "on|off", "[nội dung]")}.";

    public static string ScheduleSaved(MeetingScheduleKind kind, DateTimeOffset next)
        => $"Đã lưu lịch {kind} vào {next:O} UTC.";

    public static string MeetingSuggested(string label)
        => $"Vào phòng voice {label} và bật Agent. Hết 20 phút không thấy Agent thì phiên hết hạn.";

    public static string EventOpened(string name, string joinCommand, string? note, int? capacity)
        => $"Đã mở đăng ký {name}. "
            + (capacity is int limit ? $"Sức chứa: {limit}. " : "Không giới hạn chỗ. ")
            + $"Thành viên gõ {joinCommand}. {note}";

    public static string PointsApplied(decimal balance)
        => $"Điểm hiện tại: {balance}.";

    public static string PointsAlreadyApplied(decimal balance)
        => $"Không cộng thêm. Số dư {balance}.";

    public static string LeaderboardRow(int rank, string label, decimal points)
        => $"{rank}. {label}: {points}";

    public static string SpinResult(int index)
        => $"Kết quả vòng quay: ô {index + 1}.";

    public static string RoleSelfAssignableChanged(bool enabled, string label)
        => enabled ? $"Đã cho phép tự chọn role {label}." : $"Đã tắt tự chọn role {label}.";

    public static string UnknownCommand(MonzeCommandOptions options)
        => $"Lệnh không rõ. Gõ {options.HelpCommand}.";

    public static string MonzeHelp(MonzeCommandOptions options, bool isAdmin = true)
        => isAdmin
            ? $"Dùng {options.HelpCommand} <chủ đề> để xem hướng dẫn và ví dụ."
            : $"Dùng {options.HelpCommand} <chủ đề> để xem hướng dẫn dành cho thành viên.";

    public static string CommandHelp(string topic, MonzeCommandOptions options, bool isAdmin = true)
    {
        string Command(string name, params string[] arguments) => options.Command(name, arguments);
        return MonzeCommandNames.Normalize(topic) switch
        {
            MonzeCommandNames.Setup => SetupHelp(options, isAdmin),
            MonzeCommandNames.Welcome => WelcomeHelp(options, isAdmin),
            MonzeCommandNames.Announce => isAdmin
                ? $"Gửi thông báo cho clan.\nDùng: {Command(MonzeCommandNames.Announce, "<nội dung>")}\nVí dụ: {Command(MonzeCommandNames.Announce, "Bảo trì lúc 22:00.")}"
                : AdminOnly,
            MonzeCommandNames.Outbox => isAdmin
                ? $"Xem hoặc gửi lại mục đang chờ.\nDùng: {Command(MonzeCommandNames.Outbox)} hoặc {Command(MonzeCommandNames.Outbox, MonzeCommandActions.Resend, "<mục>")}.\nVí dụ: {Command(MonzeCommandNames.Outbox, MonzeCommandActions.Resend, "42")}"
                : AdminOnly,
            MonzeCommandNames.Event => EventHelp(options, isAdmin),
            MonzeCommandNames.Faq => isAdmin
                ? $"Lưu FAQ cho clan.\nDùng: {Command(MonzeCommandNames.Faq, MonzeCommandActions.Add, "<câu hỏi>", "|", "<trả lời>")}\nVí dụ: {Command(MonzeCommandNames.Faq, MonzeCommandActions.Add, "Giờ làm việc?", "|", "09:00-18:00")}"
                : AdminOnly,
            MonzeCommandNames.Info => $"Tìm FAQ theo từ khóa.\nDùng: {Command(MonzeCommandNames.Info, "<từ khóa>")}\nVí dụ: {Command(MonzeCommandNames.Info, "onboarding")}",
            MonzeCommandNames.Points => $"Cộng điểm hoạt động một lần mỗi ngày.\nDùng: {Command(MonzeCommandNames.Points)}\nVí dụ: {Command(MonzeCommandNames.Points)}",
            MonzeCommandNames.Leaderboard => $"Xem bảng xếp hạng điểm hoạt động.\nDùng: {Command(MonzeCommandNames.Leaderboard)}\nVí dụ: {Command(MonzeCommandNames.Leaderboard)}",
            MonzeCommandNames.Spin => $"Quay vòng may mắn, mỗi thành viên một lượt mỗi phút.\nDùng: {Command(MonzeCommandNames.Spin)}\nVí dụ: {Command(MonzeCommandNames.Spin)}",
            MonzeCommandNames.Topic => TopicHelp(options, isAdmin),
            MonzeCommandNames.Summarize => AiHelp(Command(MonzeCommandNames.Summarize, "<nội dung>"), "Tóm tắt"),
            MonzeCommandNames.Translate => AiHelp(Command(MonzeCommandNames.Translate, "<nội dung>"), "Dịch"),
            MonzeCommandNames.Rewrite => AiHelp(Command(MonzeCommandNames.Rewrite, "<nội dung>"), "Viết lại"),
            MonzeCommandNames.Shorten => AiHelp(Command(MonzeCommandNames.Shorten, "<nội dung>"), "Rút gọn"),
            MonzeCommandNames.Role => RoleHelp(options, isAdmin),
            MonzeCommandNames.Meeting => MeetingHelp(options),
            MonzeCommandNames.Summary => $"Lấy summary đã lưu của phòng voice hiện tại.\nDùng: {options.DirectCommand(MonzeCommandNames.Summary)}\nVí dụ: {options.DirectCommand(MonzeCommandNames.Summary)}",
            _ => MonzeHelp(options, isAdmin)
        };
    }

    public static string WelcomeHelp(MonzeCommandOptions options, bool isAdmin = true)
        => isAdmin
            ? $"Bật, tắt hoặc chỉnh mẫu lời chào.\nDùng: {options.Command(MonzeCommandNames.Welcome, "on|off", "[nội dung]")}, {options.Command(MonzeCommandNames.Welcome, MonzeCommandActions.Setting)}. Sau khi xem trước, dùng {options.Command(MonzeCommandNames.Welcome, MonzeCommandActions.Apply, "<mã>")}.\nVí dụ: {options.Command(MonzeCommandNames.Welcome, "on", "Chào mừng bạn đến clan.")}"
            : $"Lệnh này chỉ dành cho owner hoặc delegate.\nDùng {options.Command(MonzeCommandNames.Help, MonzeCommandNames.Welcome)} để xem hướng dẫn khi bạn có quyền.";

    public static string WelcomeDraftReady(MonzeCommandOptions options, string token)
        => $"Mẫu đã kiểm tra. Dùng {options.Command(MonzeCommandNames.Welcome, MonzeCommandActions.Apply, token)} để lưu.";

    public static string EventHelp(MonzeCommandOptions options, bool isAdmin = true)
    {
        var exampleDate = EventExampleDate();
        var admin = isAdmin
            ? $"\nQuản trị: {options.Command(MonzeCommandNames.Event, MonzeCommandActions.Create, "<tên>", "|", "<YYYY-MM-DD>", "<HH:mm>", "[số chỗ]")} hoặc {options.Command(MonzeCommandNames.Event, MonzeCommandActions.Recap)}.\nVí dụ: {options.Command(MonzeCommandNames.Event, MonzeCommandActions.Create, "Town hall", "|", exampleDate, "18:00", "30")}"
            : string.Empty;
        return $"Thành viên dùng {options.Command(MonzeCommandNames.Event, MonzeCommandActions.Join)} để đăng ký.{admin}";
    }

    public static string EventExampleDate()
        => DateTimeOffset.Now.Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string MeetingHelp(MonzeCommandOptions options)
        => $"Dùng {options.DirectCommand(MonzeCommandNames.Meeting, "now")}, {options.DirectCommand(MonzeCommandNames.Meeting, "once", "<thời điểm>")}, {options.DirectCommand(MonzeCommandNames.Meeting, "daily", "<giờ>")}, hoặc {options.DirectCommand(MonzeCommandNames.Meeting, "weekly", "<thứ>", "<giờ>")}.\nVí dụ: {options.DirectCommand(MonzeCommandNames.Meeting, "daily", "09:00")}";

    public static string FaqHelp(MonzeCommandOptions options)
        => $"Dùng {options.Command(MonzeCommandNames.Faq, MonzeCommandActions.Add, "<câu hỏi>", "|", "<trả lời>")}.\nVí dụ: {options.Command(MonzeCommandNames.Faq, MonzeCommandActions.Add, "Giờ làm việc?", "|", "09:00-18:00")}";

    public static string TopicHelp(MonzeCommandOptions options, bool isAdmin = true)
        => isAdmin
            ? $"Xem hoặc quản lý chủ đề.\nDùng: {options.Command(MonzeCommandNames.Topic)}, {options.Command(MonzeCommandNames.Topic, MonzeCommandActions.Add, "<chủ đề>")} hoặc {options.Command(MonzeCommandNames.Topic, MonzeCommandActions.Remove, "<chủ đề>")}.\nVí dụ: {options.Command(MonzeCommandNames.Topic, MonzeCommandActions.Add, "Một thói quen tốt gần đây là gì?")}"
            : $"Xem chủ đề gợi ý.\nDùng: {options.Command(MonzeCommandNames.Topic)}";

    public static string RoleHelp(MonzeCommandOptions options, bool isAdmin = true)
        => isAdmin
            ? $"Tự chọn: {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Allow, "<role>")} / {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Deny, "<role>")}; thành viên dùng {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Self, "<role>")}.\nRule tự động: {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Rule, "join", "<role>")}, {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Rule, MonzeCommandActions.Tenure, "<role>", "|", "<ngày>")}, {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Rule, "points", "<role>", "|", "<điểm>")}, {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Rule, MonzeCommandActions.Existing, "<role>", "|", "<role yêu cầu>")}.\nXóa: {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Rule, MonzeCommandActions.Remove, "<kind>", "<role>")}.\nVí dụ: {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Allow, "Gamer")} rồi {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Self, "Gamer")}; {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Rule, MonzeCommandActions.Tenure, "Trusted", "|", "30")}" 
            : $"Tự chọn role đã được clan cho phép.\nDùng: {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Self, "<role>")}.\nVí dụ: {options.Command(MonzeCommandNames.Role, MonzeCommandActions.Self, "Gamer")}";

    public static string SetupHelp(MonzeCommandOptions options, bool isAdmin = true)
        => isAdmin
            ? $"Cấu hình welcome và người được ủy quyền.\nDùng: {options.Command(MonzeCommandNames.Setup, MonzeCommandNames.Welcome, "on|off", "[nội dung]")} hoặc {options.Command(MonzeCommandNames.Setup, MonzeCommandActions.Admin, "add|remove", "<@user>")}.\nVí dụ: {options.Command(MonzeCommandNames.Setup, MonzeCommandNames.Welcome, "on", "Chào mừng bạn đến clan.")}"
            : AdminOnly;

    public static string SetupAdminHelp(MonzeCommandOptions options)
        => $"Dùng: {options.Command(MonzeCommandNames.Setup, MonzeCommandActions.Admin, "add|remove", "<@user>")}.\nVí dụ: {options.Command(MonzeCommandNames.Setup, MonzeCommandActions.Admin, "add", "<@user>")}";

    public static string RateLimited(TimeSpan retryAfter)
        => $"Bạn thao tác quá nhanh. Hãy thử lại sau {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} giây.";

    private static string AiHelp(string syntax, string action)
        => $"{action} nội dung được gửi trong lệnh.\nDùng: {syntax}\nVí dụ: {syntax.Replace("<nội dung>", "nội dung cần xử lý", StringComparison.Ordinal)}";
}

