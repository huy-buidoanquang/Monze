# Cổng rà nguồn trước khi code

Đối chiếu mã trong workspace và tách rõ contract nguồn với bằng chứng deployment.
Monze hiện dùng gói 1.6.2; việc source đã đúng không chứng minh dev/prod đang chạy
cùng backend, web bundle hoặc binary bot.

| API / event | Bằng chứng nguồn hiện tại | Quyết định trong Monze | Còn cần live |
|---|---|---|---|
| `ClanDescsList` | `mezon-api/server/core_clan_desc.go` dùng giới hạn `DEFAULT_LIMIT_CATEGORY = 100`, không có cursor | Hợp nhất registry bền vững; đặt `discovery_incomplete` khi chạm giới hạn | Gọi bằng bot và xác nhận hành vi ở đúng deployment |
| `ListClanUsers` | `ORDER BY jointime DESC LIMIT MAX_USER_CHANNEL = 1000`; response không có bot identity | Không tuyên bố roster đầy đủ; join realtime bỏ qua bot bằng `is_bot`; periodic tenure chưa thể chứng minh loại bot | Pagination/snapshot đầy đủ và bot identity upstream |
| `MessageButtonClick` | `authenticatedMessageButtonClick` thay `UserId` bằng user từ request context; có regression test chống forged ID | Route thay đổi trạng thái yêu cầu `ServerAuthenticated` và kiểm tra binding user/message | Hai-user forged-ID replay trên backend đang deploy |
| `DropdownBoxSelected` | Backend lấy `UserId` từ request context; web source hiện dùng endpoint dropdown riêng | Dropdown/radio dùng cùng actor gate với button | Xác nhận bundle đang deploy phát event đúng end to end |
| SDK interaction router | Source 1.6.2 đánh dấu button và select là `ServerAuthenticated`; protected route kiểm tra trust | Monze kiểm tra lại binding private message và quyền DB | Replay bằng đúng package 1.6.2 |
| SDK quick menu | 1.6.2 có typed `QuickMenuReceivedData` và public `ListQuickMenuAccessAsync` | Chưa bật provisioning/AI quick menu | Canary menu, source message, actor, clan/channel và idempotency |
| SDK transport | Monze mặc định WebSocket; chỉ chọn TCP khi cấu hình `Mezon:Transport=Tcp` | Giữ WebSocket cho canary hiện tại | Framing/reconnect soak cho transport được deploy |
| Agent SSE | SDK phát `session_started`, `session_ended`, `session_done`; Monze dùng `AgentBaseUrl` cho SSE và transcript | Inbox/idempotency và meeting-cycle state nằm trong PostgreSQL | Agent/STT host thật, reconnect và nhiều chu kỳ trong một meeting |
| `UpdateRole` | API có `add_user_ids`; roster không cung cấp bot identity đầy đủ | Xác minh owner/admin, role và điều kiện ngay trước khi cấp | Quyền/hierarchy bot trong clan canary |
| Voice occupancy | SDK có `ListChannelVoiceUsers`; chưa có clan-wide snapshot | Recheck phòng trước claim; tránh coi claim DB là reservation Mezon | Race giữa nhiều scheduler và thay đổi occupancy |
| `VoiceEnded` | Không có contract event dùng được | Không chờ event này; dùng Agent lifecycle | Agent end-to-end hiện hành |

Không mở mutation từ interaction nếu actor hoặc private-message binding không xác
định được. Không dùng cache để quyết định owner/admin, role, occupancy, ngân sách AI
hoặc lease.
