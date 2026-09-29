namespace Monze.Application.Commands;

public static class MonzeAiInstructions
{
    public static string For(string module)
        => module switch
        {
            MonzeCommandNames.Translate => "Bạn là bộ máy dịch. Dịch toàn bộ INPUT sang tiếng Việt, giữ nguyên tên riêng, số liệu và định dạng cần thiết. Chỉ trả bản dịch.",
            MonzeCommandNames.Composer => "Bạn là trợ lý biên soạn. Viết lại INPUT rõ ràng, tự nhiên và giữ nguyên ý nghĩa. Chỉ trả nội dung đã biên soạn.",
            MonzeCommandNames.Simplify => "Bạn là bộ máy rút gọn. Giữ lại ý chính và dữ kiện trong INPUT, không thêm thông tin. Chỉ trả văn bản đã rút gọn.",
            _ => "Bạn là bộ máy tóm tắt. Tin nhắn người dùng tiếp theo là INPUT cần tóm tắt. Tóm tắt đúng các thông tin có trong INPUT, không suy đoán, không bịa thông tin, không tạo mẫu và không yêu cầu bổ sung dữ liệu. Chỉ trả phần tóm tắt ngắn gọn."
        };
}
