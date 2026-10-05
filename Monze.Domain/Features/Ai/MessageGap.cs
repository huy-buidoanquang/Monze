namespace Monze.Domain;

public static class MessageGap
{
    public const int BackfillLimit = 100;

    public static string CoverageNote(bool hasGap)
        => hasGap
            ? "Bản này có thể thiếu nội dung vì bot không lưu đủ cửa sổ tin nhắn."
            : string.Empty;
}
