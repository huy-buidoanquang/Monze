namespace Monze.Application.Commands;

public static class MonzeHelpCatalog
{
    public static IReadOnlyList<MonzeHelpEntry> Commands(
        MonzeCommandOptions options,
        bool isAdmin,
        bool canManageWelcome,
        bool isOwner = false)
    {
        var entries = new List<MonzeHelpEntry>(6)
        {
            Entry(options.DirectCommand(MonzeCommandNames.Meeting), "Xem lịch và gợi ý phòng voice trống."),
            Entry(options.DirectCommand(MonzeCommandNames.Summary), "Admin tra cứu summary theo mã."),
            Entry(options.DirectCommand(MonzeCommandNames.Welcome), canManageWelcome ? "Quản lý lời chào thành viên mới." : "Xem lời chào thành viên mới."),
            Entry(options.DirectCommand(MonzeCommandNames.Role), isAdmin ? "Bật tắt và cấu hình cấp role tự động." : "Chức năng role do admin quản lý."),
            Entry(options.DirectCommand(MonzeCommandNames.Ai), "Tóm tắt, dịch, biên soạn và rút gọn nội dung.")
        };

        if (isOwner)
        {
            entries.Add(Entry(options.DirectCommand(MonzeCommandNames.Setup), "Quản lý admin được ủy quyền."));
        }

        return entries;
    }

    public static IReadOnlyList<MonzeHelpEntry> ForModule(
        string topic,
        MonzeCommandOptions options,
        bool isAdmin,
        bool canManageWelcome,
        bool isOwner = false)
    {
        var normalized = MonzeCommandNames.Normalize(topic);
        return normalized switch
        {
            MonzeCommandNames.Welcome => Welcome(options, canManageWelcome),
            MonzeCommandNames.Meeting => Meeting(options),
            MonzeCommandNames.Summary => Summary(options, isAdmin),
            MonzeCommandNames.Setup => Setup(options, isOwner),
            MonzeCommandNames.Role => Role(options, isAdmin),
            MonzeCommandNames.Ai => Ai(options),
            _ => [Entry(options.HasRoot ? options.Command(MonzeCommandNames.Help) : options.DirectCommand(MonzeCommandNames.Meeting), "Chọn một module để xem hướng dẫn.")]
        };
    }

    private static IReadOnlyList<MonzeHelpEntry> Welcome(MonzeCommandOptions options, bool canManageWelcome)
        => canManageWelcome
            ?
            [
                Entry(options.DirectCommand(MonzeCommandNames.Welcome, "on|off"), "Bật hoặc tắt lời chào thành viên mới."),
                Entry(options.DirectCommand(MonzeCommandNames.Welcome, MonzeCommandActions.Message, "<nội dung>"), "Lưu welcome text; hỗ trợ {user}, {role:Tên}, {channel:tên-kênh}."),
                Entry(options.DirectCommand(MonzeCommandNames.Welcome, MonzeCommandActions.Message, MonzeCommandActions.Remove), "Xóa welcome text."),
                Entry(options.DirectCommand(MonzeCommandNames.Welcome, MonzeCommandActions.Setup), "Mở form chỉnh welcome embed."),
                Entry(options.DirectCommand(MonzeCommandNames.Welcome, MonzeCommandActions.Preview), "Xem cả welcome text và embed đã lưu."),
                Entry(options.DirectCommand(MonzeCommandNames.Welcome, MonzeCommandActions.Setup, MonzeCommandActions.Remove), "Xóa welcome embed."),
                Entry(options.DirectCommand(MonzeCommandNames.Welcome, MonzeCommandActions.Message, "Chào mừng {user} đến {channel:general}!"), "Mention thành viên mới, role hoặc kênh theo placeholder.")
            ]
            : [new MonzeHelpEntry(string.Empty, MonzeMessages.WelcomeAdminOnly)];

    private static IReadOnlyList<MonzeHelpEntry> Meeting(MonzeCommandOptions options)
        =>
        [
            Entry(options.DirectCommand(MonzeCommandNames.Meeting), "Xem các lịch họp của kênh."),
            Entry(options.DirectCommand(MonzeCommandNames.Meeting, "now"), "Gợi ý phòng voice đang trống."),
            Entry(options.DirectCommand(MonzeCommandNames.Meeting, "cancel", "<ID_lịch_họp>"), "Hủy lịch họp đang chờ."),
            Entry(options.DirectCommand(MonzeCommandNames.Meeting, "<tên_cuộc_họp>", "dd/MM/yyyy", "HH:mm", "once"), "Lên lịch một lần."),
            Entry(options.DirectCommand(MonzeCommandNames.Meeting, "<tên_cuộc_họp>", "dd/MM/yyyy", "HH:mm", "daily"), "Lặp lại hằng ngày."),
            Entry(options.DirectCommand(MonzeCommandNames.Meeting, "<tên_cuộc_họp>", "dd/MM/yyyy", "HH:mm", "weekly"), "Lặp lại hằng tuần.")
        ];

    private static IReadOnlyList<MonzeHelpEntry> Summary(MonzeCommandOptions options, bool isAdmin)
        => isAdmin
            ? [Entry(options.DirectCommand(MonzeCommandNames.Summary, "<id>"), "Lấy summary đã lưu theo mã cuộc họp.")]
            : [new MonzeHelpEntry(string.Empty, MonzeMessages.SummaryAdminOnly)];

    private static IReadOnlyList<MonzeHelpEntry> Setup(MonzeCommandOptions options, bool isOwner)
        => isOwner
            ?
            [
                Entry(options.DirectCommand(MonzeCommandNames.Setup, MonzeCommandActions.Admin, MonzeCommandActions.Add, "<@user>"), "Thêm admin được ủy quyền."),
                Entry(options.DirectCommand(MonzeCommandNames.Setup, MonzeCommandActions.Admin, MonzeCommandActions.Remove, "<@user>"), "Xóa admin được ủy quyền.")
            ]
            : [new MonzeHelpEntry(string.Empty, MonzeMessages.OwnerOnly)];

    private static IReadOnlyList<MonzeHelpEntry> Role(MonzeCommandOptions options, bool isAdmin)
        => isAdmin
            ?
            [
                Entry(options.DirectCommand(MonzeCommandNames.Role, "on|off"), "Bật hoặc tắt cấp role tự động."),
                Entry(options.DirectCommand(MonzeCommandNames.Role, "join", "<tên_role>"), "Cấp role khi thành viên mới tham gia."),
                Entry(options.DirectCommand(MonzeCommandNames.Role, "tenure", "<tên_role>"), "Cấp role sau thời gian tham gia mặc định 30 ngày."),
                Entry(options.DirectCommand(MonzeCommandNames.Role, "join|tenure", "remove", "<tên_role>"), "Xóa rule cấp role tương ứng.")
            ]
            : [new MonzeHelpEntry(string.Empty, MonzeMessages.AdminOnly)];

    private static IReadOnlyList<MonzeHelpEntry> Ai(MonzeCommandOptions options)
        =>
        [
            Entry(options.DirectCommand(MonzeCommandNames.Ai, MonzeCommandNames.AiSummary, "<nội dung>"), "Tóm tắt nội dung hoặc đoạn chat trong tối đa một giờ."),
            Entry(options.DirectCommand(MonzeCommandNames.Ai, MonzeCommandNames.Translate, "<nội dung>"), "Dịch nội dung."),
            Entry(options.DirectCommand(MonzeCommandNames.Ai, MonzeCommandNames.Composer, "<nội dung>"), "Biên soạn hoặc viết lại nội dung."),
            Entry(options.DirectCommand(MonzeCommandNames.Ai, MonzeCommandNames.Simplify, "<nội dung>"), "Rút gọn nội dung.")
        ];

    private static MonzeHelpEntry Entry(string command, string description)
        => new(command, description);
}
