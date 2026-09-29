using Monze.Domain;

namespace Monze.Application.Commands;

public static class MonzeMessages
{
    public const string TitleMonze = "Monze";
    public const string TitleMeeting = "Meeting";
    public const string TitleSummary = "Summary";
    public const string TitleRole = "Role";
    public const string TitleHelp = "Hướng dẫn";
    public const string TitleWelcome = "Welcome";
    public const string TitleClanSetup = "Cấu hình clan";
    public const string TitleWelcomeSetup = "Cấu hình welcome";
    public const string TitleRateLimited = "Giới hạn thao tác";
    public const string AdminOnly = "Chỉ owner hoặc người được ủy quyền.";
    public const string WelcomeAdminOnly = "Chỉ owner hoặc admin của clan được quản lý welcome.";
    public const string OwnerOnly = "Chỉ owner của clan được thay đổi người được ủy quyền.";
    public const string NoVoiceRoom = "Không còn phòng voice trống.";
    public const string AiNotConfigured = "Chưa cấu hình AI provider.";
    public const string AiProviderEmpty = "Provider không trả nội dung.";
    public const string AiBudgetExceeded = "Hết ngân sách AI trong ngày hoặc chưa bật provider.";
    public const string AiInputEmpty = "Hãy nhập nội dung cần xử lý.";
    public const string AiInputTooLong = "Nội dung AI vượt quá giới hạn cho phép của clan.";
    public const string AiHistoryWindow = "Tin nhắn được chọn đã quá một giờ. Hãy reply một tin nhắn gần đây hơn.";
    public const string AiBusy = "AI đang xử lý các yêu cầu khác. Hãy thử lại sau.";
    public const string OwnerAlreadyAdmin = "Owner đã có quyền quản trị sẵn.";
    public const string DelegateAdded = "Đã thêm người được ủy quyền.";
    public const string DelegateRemoved = "Đã xóa người được ủy quyền.";
    public const string DelegateAlreadyExists = "Người này đã được ủy quyền trước đó.";
    public const string DelegateNotFound = "Người này không có trong danh sách ủy quyền.";
    public const string VoiceClaimConflict = "Phòng voice vừa được giữ bởi meeting khác. Hãy thử lại sau.";
    public const string InvalidTime = "Thời điểm không hợp lệ.";
    public const string MeetingScheduleNotFound = "Không tìm thấy lịch họp đang chờ trong kênh này.";
    public const string MeetingScheduleCancelled = "Đã hủy lịch họp.";
    public const string MeetingScheduleEmpty = "Chưa có lịch họp đang chờ trong kênh này.";
    public const string NoSummary = "Chưa có kết quả summary cho phòng này.";
    public const string SummaryAdminOnly = "Chỉ owner hoặc admin mới được tra cứu summary.";
    public const string WelcomeEnabled = "Đã bật welcome.";
    public const string WelcomeDisabled = "Đã tắt welcome.";
    public const string WelcomeEmbedSaved = "Đã lưu mẫu welcome.";
    public const string WelcomeEmbedRemoved = "Đã xoá mẫu embed welcome.";
    public const string WelcomeMessageSaved = "Đã lưu nội dung welcome.";
    public const string WelcomeMessageRemoved = "Đã xóa nội dung welcome.";
    public const string WelcomeMessageRequired = "Hãy nhập nội dung welcome.";
    public const string WelcomeSetupCancelled = "Đã huỷ cấu hình welcome. Chưa lưu thay đổi.";
    public const string WelcomeDraftInvalid = "Mã mẫu đã hết hạn, đã dùng hoặc không thuộc kênh này. Hãy mở lại welcome setup.";
    public const string WelcomePreview = "Bản xem trước welcome";
    public const string RoleGatewayNotReady = "Role gateway chưa sẵn sàng.";
    public const string RoleNotFound = "Không tìm thấy role đang hoạt động trong clan.";
    public const string RoleSelfNotAllowed = "Role này chưa được owner cho phép tự chọn.";
    public const string RoleAssignFailed = "Không thể cấp role. Role không thuộc clan, đã tắt hoặc bot không đủ quyền.";
    public const string RoleAssigned = "Đã cấp role.";
    public const string RoleRuleInvalid = "Rule role không hợp lệ hoặc thiếu điều kiện.";
    public const string RoleRuleNotFound = "Không tìm thấy rule role.";
    public const string RoleAutomationEnabled = "Đã bật cấp role tự động.";
    public const string RoleAutomationDisabled = "Đã tắt cấp role tự động.";
    public const string UnknownClan = "Chưa xác định được clan.";
    public const string TemporaryFailure = "Lệnh gặp lỗi tạm thời. Hãy thử lại sau.";
    public const string MemberFallbackLabel = "thành viên";
    public const string DefaultWelcomeText = "Chào mừng bạn đến clan. Hãy xem kênh hướng dẫn và đọc quy định của clan.";

    public static string WelcomeSetupText()
        => "Chọn nhóm để sửa. Preview để xem trước, Submit để lưu.";

    public static string ScheduleSaved(
        string name,
        long id,
        MeetingScheduleKind kind,
        DateTimeOffset next)
        => $"Đã lưu lịch \"{name}\" (#{id}), kiểu {kind.ToString().ToLowerInvariant()}, lần tới {next:dd/MM/yyyy HH:mm} UTC.";

    public static string MeetingSuggested(string label)
        => $"Đã gửi lời mời vào phòng {label}. Hãy bật Agent để bắt đầu cuộc hội thoại.";

    public static string RoleSelfAssignableChanged(bool enabled, string label)
        => enabled ? $"Đã cho phép tự chọn role {label}." : $"Đã tắt tự chọn role {label}.";

    public static string UnknownCommand(MonzeCommandOptions options)
        => $"Lệnh không rõ. Gõ {options.HelpCommand}.";

    public static string MonzeHelp(MonzeCommandOptions options, bool isAdmin = true)
        => isAdmin
            ? $"Dùng {options.HelpCommand} <chủ đề> để xem hướng dẫn và ví dụ."
            : $"Dùng {options.HelpCommand} <chủ đề> để xem hướng dẫn dành cho thành viên.";

    public static string WelcomeDraftReady()
        => "Mẫu đã kiểm tra. Nhấn Submit để lưu.";

    public static string RateLimited(TimeSpan retryAfter)
        => $"Bạn thao tác quá nhanh. Hãy thử lại sau {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} giây.";
}

