# Kế hoạch kiểm thử toàn diện Monze

## 1. Mục tiêu kiểm thử

Mục tiêu của kế hoạch này là chứng minh tính đúng đắn của mọi capability đang được route hoặc chạy nền trong Monze trước khi release.

Release chỉ được đánh dấu **READY** khi đồng thời thỏa mãn tất cả điều kiện sau:

1. 100% capability có trong inventory đã được gán `TEST-ID`, owner, expected result và lớp bằng chứng phù hợp.
2. Mọi mutation nhạy cảm có test quyền, cross-clan và actor giả.
3. Mọi state durable có test duplicate, retry, lease, timeout và restart khi phù hợp.
4. PostgreSQL, Redis, Mezon SDK, Agent SSE và AI provider đều có failure test.
5. Không có lỗi P0/P1 đang mở.
6. Không có claim scale/capacity nếu chưa có workload, metrics và soak report tương ứng.

Build xanh, HTTP 200 hoặc unit test pass riêng lẻ không đủ để đóng release gate.

## 2. Phạm vi đã scan

Phạm vi gồm source hiện tại của `F:/projects/mezon/Monze`:

- 178 file C# trong solution.
- 23 migration SQL.
- Domain, Application, Infrastructure, runtime `MonzeBot`, UI builders, workers, tests và benchmarks.
- Mezon SDK, PostgreSQL, Redis, SQLite history facade, Agent SSE, transcript client và AI provider.

### 2.1 Public surface

- Help/setup.
- Welcome: on/off, text, embed setup/preview/save/remove.
- Role: automation on/off, on-join, tenure.
- AI: summary, translate, composer, simplify.
- Avatar: self, username/mention, reply target; aliases `avatar`, `ava`, `avt`.
- Meeting và summary: list, now, cancel, once/daily/weekly, Agent binding và summary retrieval.
- Rooted/rootless command routing và prefix configuration.
- Private help, welcome và meeting interactions.

### 2.2 Background/runtime surface

- Message ingress, bounded partition queue và ordering.
- Clan discovery, discovery cap, reconnect và rejoin.
- Channel/voice lifecycle và occupancy refresh.
- Welcome delivery.
- Role automation/rescan.
- Scheduled meeting worker.
- Agent start/end/summary event processing.
- Summary retry và maintenance worker.
- PostgreSQL outbox delivery.
- Command inbox idempotency.
- L1/L2 cache, Redis invalidation và fallback PostgreSQL.
- Startup schema validation, migration và runtime shutdown.

## 3. Scope lock bắt buộc

Trước khi viết test chi tiết, phải xuất inventory từ:

- `CommandService` registrations.
- `MonzeCommandNames`, `MonzeHelpCatalog` và command actions.
- Tất cả button IDs và interaction handlers.
- Event subscriptions trong `MonzeBot`.
- Hosted workers.
- Application repository interfaces.
- PostgreSQL tables/migrations và cache keys.

### Contract drift cần quyết định trước release

`RoleRuleKind` và `IAuthorizationRepository` hiện còn các khái niệm self-select/existing-role, nhưng command router, help và `MonzeApp.Roles` hiện chưa cung cấp đầy đủ public flow tương ứng.

Phải chọn một trong hai hướng:

1. Implement đầy đủ command, authorization, gateway, persistence và end-to-end tests; hoặc
2. Tuyên bố retired rõ ràng, loại khỏi public contract/schema kỳ vọng và thêm regression test chứng minh không còn route.

Không dùng test domain riêng lẻ để tuyên bố capability public đã hoạt động.

## 4. Quality gates

| Gate | Bằng chứng bắt buộc | Điều kiện đạt |
|---|---|---|
| G0 — Scope lock | Route, event, worker, interface và schema inventory | Mỗi capability có owner, contract và `TEST-ID`; không có route undocumented hoặc mutation mồ côi |
| G1 — Deterministic correctness | Domain property tests và Application tests với fake repository/gateway/provider/clock | Happy path, invalid input, permission denial, cancellation, duplicate và retry đúng contract |
| G2 — Durable correctness | PostgreSQL disposable database từ zero và upgrade snapshot; SQL/concurrency assertions | Transaction, clan predicate, unique key, lease, CAS/version và checksum sống đúng qua replay/concurrency |
| G3 — Dependency resilience | Redis, Mezon SDK, Agent SSE, AI provider fault injection; live smoke trên test clan | Outage có fallback/bounded failure; không grant authority, duplicate delivery hoặc mất durable state |
| G4 — Scale/operations | Profile 1/10/100/1.000 clans, 100 active clans, sustained load và soak 2 giờ | Có p50/p95/p99, allocation, GC, heap/RSS, queue, DB, Redis, outbox, reconnect và upstream-rate-limit evidence |

## 5. Capability matrix

| Nhóm | Bề mặt | Lớp bằng chứng | Invariant phải chứng minh |
|---|---|---|---|
| Command router/help | Rooted/rootless, prefix, help, direct modules, aliases, case và unknown command | Application + SDK command replay + UI JSON snapshot | Command xuất hiện trong help phải routable; unauthorized route không được hiển thị; invalid command không mutation |
| Setup/authorization | Owner thêm/xóa delegated admin; owner/admin; server-authenticated actor; clan-scoped reads/writes | Application matrix + PostgreSQL security + cross-clan replay | Actor/clan/channel/role ID do client cung cấp không được mở rộng quyền |
| Welcome | On/off, text, embed, preview/save/remove, normalization, placeholder và channel fallback | Renderer/property + draft state machine + PostgreSQL/cache + live join | Một delivery mỗi clan/user; bỏ qua bot; transient failure release claim; cache stale không che setting mới |
| Role automation | On/off, on-join, tenure, role resolution, rescan, bot/already-assigned và grant audit | Application gateway fake + PostgreSQL + Mezon role sandbox | Chỉ apply role thuộc clan; off là inert; concurrent scan không duplicate; grant failure retryable |
| AI | Summary/translate/composer/simplify, reply history, input limit, concurrency, budget, gap note | Fake provider + atomic budget + message-history replay | Không gọi provider khi validation/concurrency/budget reject; budget atomic; history gap được thông báo |
| Avatar/profile | Self, mention, username, reply target, profile upsert và label priority | SDK directory fake + profile repository + cross-clan tests | Username/reply từ clan khác không resolve; missing avatar là response bình thường |
| Meeting/schedule | List/now/cancel, once/daily/weekly, timezone, occupancy, voice claim, schedule worker | Domain/property + repository concurrency + SDK event replay + live smoke | Chỉ suggest voice empty/unclaimed; cancel đúng requester/channel/clan; recurring lease replay idempotent |
| Agent/summary | Start/end/summary, room binding, duplicate inbox, pending retry, stored summary, admin lookup | SSE replay + PostgreSQL lease + transcript fault + end-to-end meeting | Một logical meeting tối đa một summary; không cross-channel reply target; retry không tạo bản thứ hai |
| Ingress/lifecycle | Bounded queue, ordering, backpressure, channel/voice events, discovery, reconnect, shutdown | Queue stress/property + SDK recorder + reconnect injection + metrics/log assertions | Callback bounded; per-key ordering; capacity cố định; reconnect không mất clan hoặc nhân subscription |
| Outbox/cache/migration | Claim/lease/reclaim/uncertain, L1/L2 versioning, tombstone, Pub/Sub, migrations 001–023 | Concurrent SQL + Redis fault + migration matrix + fake transport | Chỉ lease hiện tại complete; uncertain không blind resend; Redis không phải authority; migration checksum-safe |

## 6. Scenario bắt buộc

### 6.1 Command và quyền

- Rooted, rootless, prefix rỗng, case-insensitive, avatar aliases.
- Help theo member/admin/owner.
- Owner, delegated admin, member, outsider, inactive clan.
- Button actor giả, actor không authenticated, mention nhiều hoặc invalid.
- Replay cùng `(clan_id, channel_id, message_id)`.
- Rate limit theo clan/user/bucket/canonical command.
- Với mỗi mutation: assert cả reply, database row và external call.

### 6.2 Welcome và interaction

- Text/embed đầy đủ.
- URL chỉ HTTPS.
- Color và length normalization.
- Preview, save, expiry, replay, cancel.
- Placeholder `{user}`, `{role:name}`, `{channel:name}`.
- Lookup directory lỗi nhưng text welcome vẫn gửi được.
- Fallback channel đúng thứ tự.
- Bot join bị bỏ qua.
- Join trùng chỉ một delivery.
- Transient send failure release claim.
- Cache sequence `v1 -> tombstone -> v2` đọc đúng.

### 6.3 Role và profile

- Automation off/on.
- On-join và tenure.
- Member đã có role, member là bot, role không thuộc clan.
- Mezon role API failure.
- Concurrent rescan.
- Profile upsert không chéo clan.
- Label priority: `clan_nick -> display_name -> username`.
- Avatar self/mention/username/reply.
- Nếu giữ self-select/existing-role thì phải có full command, authorization, gateway và negative test.

### 6.4 AI và history

- Provider chưa cấu hình.
- Input rỗng/quá dài.
- Output rỗng.
- HTTP 5xx, timeout, cancellation.
- Concurrency gate đầy.
- Summary trực tiếp và summary từ reply.
- Reply trong/ngoài cửa sổ một giờ.
- History gap note.
- Message order và cross-clan history.
- Atomic token budget theo clan/user.
- Không consume budget khi validation hoặc concurrency gate từ chối.

### 6.5 Meeting và schedule

- Voice snapshot stale/fail.
- Voice occupied.
- Durable voice claim đang tồn tại.
- Channel event fallback.
- Label fallback.
- Hai lệnh `meeting now` đồng thời.
- Once/daily/weekly.
- Ngày/giờ quá khứ, invalid timezone/local time và DST contract.
- Cancel đúng requester/channel.
- Schedule lease crash.
- Voice conflict rollback.
- Recurring `next_run_at`.
- Một outbox announcement cho mỗi occurrence.

### 6.6 Agent, outbox và recovery

- Agent duplicate, out-of-order, malformed payload và unmatched room.
- Transcript empty/timeout/error.
- Outbox current lease, wrong token, expired reclaim.
- Retry backoff.
- External message ID precedence.
- Uncertain delivery và admin hold.
- Restart giữa từng state transition.
- Sau restart không duplicate summary/invitation.

## 7. Failure injection matrix

| Fault | Expected behavior | Bằng chứng |
|---|---|---|
| PostgreSQL unavailable/timeout/serialization/deadlock | Temporary failure bounded; không partial authorization, claim, schedule hoặc acknowledgement | Fault-injected data source và post-test row invariants |
| Redis down/stale/tombstone/lost Pub/Sub | Fallback PostgreSQL; version mới thắng; cache không authorize hoặc suppress welcome mới | Two-process cache test với `v1 -> tombstone -> v2` |
| Mezon reconnect khi discovery/channel/voice/send | Retry/backoff bounded; capped discovery giữ incomplete; send thành retryable/uncertain | SDK transport recorder và reconnect script |
| Agent duplicate/out-of-order/unknown payload | Payload invalid được bỏ qua an toàn; duplicate bị suppress; summary retry bounded | Deterministic event sequence và DB assertions |
| Hai worker cùng xử lý command/welcome/schedule/outbox | Một durable claim thắng; stale lease token không complete; replay là no-op hoặc existing result | Barrier-controlled concurrency với hai connections/process |
| AI provider empty/5xx/timeout/cancel | Không leak secret/prompt; release budget/concurrency đúng; response ổn định | Fake HTTP server với response/cancellation matrix |

## 8. Dữ liệu và isolation

### Seed cố định

- Bốn clan: owner, delegated admin, member và outsider.
- Mỗi clan có text channel, public/fallback channel, voice channel và role riêng.
- Một clan có discovery incomplete.
- Một clan có schedule và outbox pending.
- Một clan có Redis stale cache.
- ID test namespace riêng; không dùng secret/token thật trong fixture, log, screenshot hoặc artifact.

### Clock và barrier

- Inject clock cho daily/weekly/expiry/lease/backoff.
- Không dùng `DateTime.UtcNow` trực tiếp trong test logic.
- Barrier-controlled workers cho duplicate claim, stale completion, voice conflict và concurrent budget.
- Mỗi test ghi precondition, event sequence, expected DB rows, external calls và cleanup result.

### Isolation contract

- PostgreSQL disposable database hoặc schema riêng.
- Migration chạy từ zero và upgrade từ snapshot gần nhất.
- Redis prefix riêng; chỉ flush prefix của test.
- Live smoke chỉ dùng clan/channel test được chỉ định.
- Sau test chạy cleanup và assert không còn test rows.

## 9. Performance, capacity và soak

Không đánh tráo microbenchmark với production proof.

### Workload profile

- 1, 10, 100 và 1.000 registered clans.
- Tối thiểu 100 active clans.
- Message ingress: 200/s.
- Commands: 20/s.
- Meeting candidates: 100.
- Outbox: 200/s.
- Agent events theo cùng tỷ lệ workload.
- Soak tối thiểu hai giờ, có restart giữa soak và snapshot trước/sau.

### Metrics bắt buộc

- p50/p95/p99 latency.
- Allocation rate, Gen0/Gen1, managed heap, RSS.
- Ingress depth, drops và backpressure.
- DB QPS/latency.
- Redis hit/miss/error.
- Outbox lag/in-flight/uncertain.
- Reconnects.
- Upstream rate limits.

Microbenchmark chỉ được dùng để đóng callback/hot-path boundary; không dùng để claim toàn bộ SDK, DB, Redis hoặc production capacity.

## 10. Lộ trình thực thi

### P0 — Freeze scope

Xuất route/event/interface inventory, xử lý role contract drift và gán `TEST-ID`.

### P1 — Close unit/application gaps

Bổ sung Application fakes, domain property tests, renderer/UI contract tests và negative authorization tests.

### P2 — Close integration gaps

Bổ sung migration matrix, PostgreSQL concurrency, two-process cache, outbox và Agent replay.

### P3 — Live canary

Một test clan; mỗi module phải kiểm tra visible response, runtime log và durable DB/Redis state.

### P4 — Scale gate

Chạy workload 1 → 1.000 clans, soak hai giờ, restart/fault injection và signed report.

## 11. Artifact phải lưu

- `capability-inventory.json`.
- Test matrix có `TEST-ID`.
- Release build log.
- Unit/integration report.
- Migration checksum report.
- Redis fault report.
- Live smoke transcript đã redacted.
- DB assertion output.
- Metrics export.
- Load/soak report.
- Defect disposition.

Mỗi report phải ghi commit SHA, config profile, test data namespace, thời gian UTC và phần chưa chạy.

## 12. Release decision

Trạng thái là **READY** chỉ khi:

- Capability inventory không còn dòng chưa map.
- Role contract drift đã được quyết định và kiểm chứng.
- Deterministic test suite pass.
- PostgreSQL, Redis, SDK, Agent và provider failure paths pass.
- Live smoke xác nhận visible response và durable state.
- Không có P0/P1.
- Capacity claim chỉ dùng workload có metrics đầy đủ.

Nếu thiếu bất kỳ bằng chứng nào, trạng thái bắt buộc là:

> **NOT READY — evidence missing**

## 13. Baseline hiện tại

- Release test runner hiện báo `99/99`.
- Đây là baseline của test suite hiện có, không phải bằng chứng đầy đủ cho runtime, Redis, Mezon, Agent, PostgreSQL concurrency hoặc production capacity.
- Các thay đổi chưa commit trong working tree được giữ nguyên; kế hoạch này không tự ý sửa code.

