# Kiến trúc Monze

## Ranh giới project

- `Monze.Domain`: quy tắc thuần, parser và state machine; không phụ thuộc SDK,
  PostgreSQL, Redis hoặc HTTP.
- `Monze.Application`: use case, port repository/gateway và DTO bất biến.
- `Monze.Infrastructure`: adapter PostgreSQL, Redis và cache.
- `Monze`: Generic Host, adapter Mezon SDK, worker, HTTP client và message UI.
- `Monze.Tests`: unit và integration test; PostgreSQL test chỉ chạy khi
  `MONZE_RUN_DB_TESTS=1`.
- `Monze.Benchmarks`: microbenchmark hot path và capacity soak có giới hạn.

## Module runtime

```text
Features/
  Ai/          OpenAI-compatible adapter
  Avatar/      command adapter và target resolution
  Meeting/     command, schedule, Agent lifecycle, summary và meeting ingress
  Roles/       Mezon role gateway và automation worker
  Welcome/     interaction, renderer và welcome worker state
Hosting/       composition, routing, lifecycle, common ingress và outbox
Infrastructure/
  Agent/       Agent/STT HTTP client
  Http/        bounded response reader và payload limits
Ui/
  Meeting/     meeting form/button parser
  Welcome/     welcome form parser và section model
```

Application, Domain và Persistence dùng cùng tên module trong thư mục
`Features` hoặc `Persistence`. Namespace hiện tại được giữ để việc sắp xếp file
không thay đổi public contract.

Repository PostgreSQL được tách theo transaction boundary: quyền owner/admin,
welcome, role, meeting/summary, schedule, outbox, AI usage, history và inbox.
Application chỉ phụ thuộc các port tương ứng, không dùng một store tổng hợp.
Command và state-changing meeting interaction có inbox riêng; interaction key gồm
clan, channel, source message và action để các nút khác nhau trên cùng UI không khóa
nhầm nhau.

## Meeting và Agent

`meeting_session` biểu diễn một chu kỳ Agent và `room_id` là định danh duy nhất
của chu kỳ. Session đầu tiên của một lệnh meeting là context gốc. Các lần bật
Agent tiếp theo khi voice vẫn hoạt động tạo session con qua `root_session_id`.
Event `ended` hoặc `summary_done` đến trước `started` được ghi bền vững theo
`room_id`; thao tác bind sau đó tiêu thụ dấu này trong cùng advisory lock và đưa
session thẳng sang `summary_pending`. Event `started` lặp lại cùng room tái sử
dụng session hiện có thay vì tạo thêm chu kỳ.

- Mỗi room có tối đa một `meeting_summary`.
- Session con sao chép clan, text channel, voice channel, tiêu đề và invitation
  message từ session gốc.
- `source_message_id` là invitation/reply target bất biến.
- `notification_message_id` là message trạng thái hiện tại và không được outbox
  summary ghi đè.
- Voice empty/ended đóng context. Meeting mới cùng voice đóng context cũ.
- Realtime disconnect đóng context đã bắt đầu nhưng giữ invitation chưa được
  Agent bind, tránh gắn một Agent room mới vào cuộc họp cũ sau khi mất event.
- Agent callback chỉ enqueue vào bounded meeting ingress; PostgreSQL và HTTP chỉ
  chạy trong consumer.

## Nguồn dữ liệu

- PostgreSQL: authorization, settings, role rule/grant, meeting, schedule,
  summary, command/interaction/Agent inbox và outbox.
- Redis: read model cache và invalidation; không dùng để quyết định quyền hoặc
  trạng thái meeting.
- SQLite SDK history: message history phục vụ AI, không chứa nghiệp vụ Monze.
- Mezon SDK/API: owner, member, channel, role, voice occupancy và delivery ACK.

## Invariant vận hành

- Mọi mutation theo clan phải có predicate `clan_id` hoặc đi từ row đã khóa có
  `clan_id` xác định.
- Owner/admin được đọc từ nguồn bền vững; interaction phải qua private actor
  binding.
- Queue có capacity cố định. Message bị drop ở Monze được gom theo channel trong
  một queue phụ có giới hạn và đánh dấu `channel_policy.has_gap`; Agent, welcome và
  state transition dùng backpressure.
- Outbox complete/fail cần lease token hiện tại. Delivery không ACK chuyển sang
  uncertain thay vì blind retry.
- Migration append-only, checksum-safe và idempotent khi chạy lại.
- Log không chứa token, DSN, prompt, transcript hoặc response payload nhạy cảm.
