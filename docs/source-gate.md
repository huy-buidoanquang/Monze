# Cổng rà nguồn trước khi code

Đối chiếu mã trong workspace này và kiểm chứng các đường chính trên clan development được ủy quyền. Những contract upstream chưa được sửa vẫn giữ trạng thái handoff. Lệnh có quyền trong bản này đi bằng chat đã xác thực (`sender` của tin nhắn), không bằng button.

| API / event | File | Đã thấy trong mã | Còn cần live |
|---|---|---|---|
| `ClanDescsList` giới hạn `DEFAULT_LIMIT_CATEGORY` = 100, không cursor | `mezon-api/server/core_clan_desc.go`, `mezon-api/constant/constant.go` | Có `LIMIT` cố định | Đúng khi gọi bằng bot |
| `ListClanUsers` `ORDER BY jointime DESC LIMIT MAX_USER_CHANNEL` = 1000 | `core_clan_desc.go`, `constant.go` | Có | Đúng khi gọi bằng bot |
| `MessageButtonClick` chuyển tiếp payload client, không gắn user từ session | `mezon-api/server/api_interactive_message.go` | `user_id` không lấy từ context; live test không dùng button để đổi quyền | Không dùng button cho thao tác quyền |
| UI select trong bundle đang chạy gọi `clickButtonMessage` và đi qua `MessageButtonClick` | `mezon/libs/components/.../MessageSelect.tsx` | Local source đã đổi sang `clickDropdownBoxSelected`; dev site vẫn đang phục vụ bundle cũ theo Chrome console | Publish bundle mới rồi kiểm tra dropdown event end-to-end |
| `DropdownBoxSelected` gán `UserId` từ `ctxUserIDKey` | `mezon-api/server/api_interactive_message.go` | Contract server có actor xác thực, nhưng UI web hiện tại không dùng endpoint này cho select message | Chỉ bật mutation khi frontend và upstream phát event này end-to-end |
| SDK interaction provenance và protected route | `Mezon.Net.Sdk/Interactions/InteractionRouter.cs`, `InteractionRouteRegistration.cs` | Button là `ClientSupplied`, dropdown là `ServerAuthenticated`; route protected từ chối button trước handler | Dùng `RequireServerAuthenticatedActor()` khi upstream/frontend đã đi đúng dropdown contract |
| `Mezon.Net.Sdk` local source for pending 1.5.1, `TransportType.Tcp` | package/source + `TransportType.cs` | TCP là lựa chọn tường minh | Envelope `0x00` chỉ thấy trong audit, chưa thấy live |
| Agent SSE `room_started` / ended / summary | `AgentSseManager.cs`, `MezonClient.Events.cs` | Event `event_type` trong JSON | URL agent và STT cần cấu hình thật |
| `UpdateRole` `add_user_ids` | `UpdateRoleParams` | Có | Quyền bot cấp role; dev live test trả `PermissionDenied` khi bot chưa có quyền/hierarchy phù hợp |
| `ListChannelVoiceUsers` | SDK | Có; đã xác minh live trong `*meeting now` | Agent session và summary hoàn chỉnh vẫn cần live |
| `VoiceEnded` | plan + API đã rà trước | API không phát event này | Không chờ event này |

Quyết định vì bundle dev hiện tại chưa phát actor xác thực end-to-end: welcome chỉ hiển thị embed và nút help; bật, tắt hoặc đổi nội dung phải dùng command chat đã xác thực. Local web source đã có đường `DropdownBoxSelected` đúng contract, nhưng chưa được publish và live-verified. `*monze event join`, `*monze role self` và các lệnh quản trị dùng người gửi của lệnh chat. Sau khi publish, chỉ bật control mutation nếu Chrome và PostgreSQL cùng chứng minh event server-authenticated.
