# Monze — báo cáo chiến dịch kiểm thử tự động `20261008-2034-full-soak`

> **Verdict: NOT READY — failures** · profile `full-soak` · 2026-10-08 20:34 UTC

Đây là báo cáo duy nhất của lần chạy. Mọi số liệu được dựng tự động từ artifact trong `raw/` (không commit). Báo cáo không chứa secret, DSN hay ID thô.

## 1. Môi trường và commit

| Mục | Giá trị |
|---|---|
| Commit | `7611564` |
| Branch | `fix/browser-test-findings` |
| Bắt đầu / kết thúc (UTC) | 2026-10-08 20:34:22 / 2026-10-09 01:17:30 |
| Thời lượng | 4:43:08 |
| .NET SDK | 10.0.401 |
| CPU logic | 14 |
| Docker | 29.6.2 |
| Hệ điều hành | Microsoft Windows 10.0.26200 |
| Image | postgres:17-alpine@b0f9560a2de0, postgres:16@1a6ab3f5345e, redis:7-alpine@e7723ff73d96 |
| Namespace dữ liệu | container label monze.campaign=20261008-2034-full-soak, DB monze_t_*, cổng 55432/55433/56379 |
| Profile | full-soak |
| Seed | mặc định |
| mezube-redis-1 sau | không đổi |
| mezube-redis-1 trước | đang có (không đụng tới) |

## 2. Ma trận phạm vi

| Tier | Trạng thái | Thời lượng | Ghi chú |
|---|---|---|---|
| preflight | ✅ PASS | 2.5 s | Đã xoá 3 container campaign còn sót; branch fix/browser-test-findings |
| build | ✅ PASS | 3.4 s | Release, restore --locked-mode |
| inventory | ✅ PASS | 2.1 s | 15/15 pass, 0 fail, 0 skip |
| unit | ✅ PASS | 7.6 s | 267/267 pass, 0 fail, 0 skip |
| property | ✅ PASS | 13.6 s | 51/51 pass, 0 fail, 0 skip |
| integration | ✅ PASS | 24.3 s | 61/61 pass, 0 fail, 0 skip |
| e2e | ✅ PASS | 0:06:17 | 56/56 pass, 0 fail, 0 skip |
| micro | ✅ PASS | 0:03:34 | 24 benchmark, 9 gate 0 B/op, 0 mean vượt 1,2 × baseline |
| component | ✅ PASS | 0:03:50 | 8/9 PASS, 1 KNOWN_GAP (C-OUTBOX-CLAIM: CAND-17) |
| load | ❌ FAIL | 1:10:25 | 4/12 PASS, 2 KNOWN_GAP (L-V3L-S1: CAND-20, L-V3L-S10: CAND-20), FAIL: L-V1-S1=FAIL, L-V1-S10=FAIL, L-V1-S100=FAIL, L-V1-S1000=FAIL, L-V3L-S100=KNOWN_GAP_NOT_REPRODUCED, L-V3L-S1000=KNOWN_GAP_NOT_REPRODUCED |
| k6 | ✅ PASS | 0:02:02 | 1/1 PASS, 0 KNOWN_GAP |
| capacity | ✅ PASS | 0:03:10 | 7/10 PASS, 3 KNOWN_GAP (BP-2: WF-07, BP-5-V1: DEF-03, BP-8-DEF06: DEF-06) |
| chaos | ❌ FAIL | 1:12:45 | 33/49 PASS, 12 KNOWN_GAP (AG-01: DEF-08, AG-02: DEF-08, CLK-03: CAND-09, MZ-01: CAND-21, MZ-02: CAND-21, MZ-07: CAND-21, MZ-08: CAND-21, PG-04b: DEF-07, PR-01: DEF-08, RD-02: DEF-01, TR-01: WF-04, TR-04: WF-03), FAIL: CLK-02=KNOWN_GAP_NOT_REPRODUCED, PG-06=KNOWN_GAP_NOT_REPRODUCED, PG-07=FAIL, PR-06=FAIL |
| soak | ❌ FAIL | 2:00:03 | 0/1 PASS, 0 KNOWN_GAP, FAIL: SOAK-120M=FAIL |
| live | — NOT_RUN | — | Không thuộc profile full-soak |

## 3. Tổng hợp theo tier

| Nhóm | Test/case | Pass | Fail | Skip | Thời lượng |
|---|---|---|---|---|---|
| e2e (xUnit) | 56 | 56 | 0 | 0 | 0:06:15 |
| integration (xUnit) | 61 | 61 | 0 | 0 | 0:01:35 |
| inventory (xUnit) | 15 | 15 | 0 | 0 | 0.7 s |
| property (xUnit) | 51 | 51 | 0 | 0 | 0:01:15 |
| unit (xUnit) | 267 | 267 | 0 | 0 | 10.6 s |
| Case sinh tự động (ledger) | 1,438,845 | 1,438,845 | 0 | 0 | — |
| capacity (kịch bản) | 10 | 7 | 3 | 0 | — |
| chaos (kịch bản) | 49 | 33 | 14 | 0 | — |
| component (kịch bản) | 9 | 8 | 1 | 0 | — |
| k6 (kịch bản) | 1 | 1 | 0 | 0 | — |
| load (kịch bản) | 12 | 4 | 6 | 0 | — |
| micro (kịch bản) | 1 | 1 | 0 | 0 | — |
| soak (kịch bản) | 1 | 0 | 1 | 0 | — |
| traceability (kịch bản) | 1 | 1 | 0 | 0 | — |

**Tổng số case đã thực thi: 1,439,379** (test xUnit + case sinh tự động + kịch bản perf/chaos).

## 4. Kết quả chức năng

| Project | Tổng | Pass | Fail | Skip |
|---|---|---|---|---|
| e2e.Monze.Tests.E2E | 56 | 56 | 0 | 0 |
| integration.Monze.Tests.Integration | 61 | 61 | 0 | 0 |
| inventory.Monze.Tests | 15 | 15 | 0 | 0 |
| property.Monze.Tests.Property | 51 | 51 | 0 | 0 |
| unit.Monze.Tests | 267 | 267 | 0 | 0 |

## 5. Case sinh tự động (property/generative)

| Generator | Số check | Yêu cầu | Input khác nhau | Pass | Xfail | Fail | Pairwise |
|---|---|---|---|---|---|---|---|
| property/G01 | 150,000 | 150,000 | 99,508 | 135,375 | 14,625 | 0 | 138/138 (100.0%) |
| property/G01b | 20,000 | 20,000 | 2,041 | 13,377 | 6,623 | 0 | 12/12 (100.0%) |
| property/G02 | 300,000 | 300,000 | 299,991 | 299,774 | 226 | 0 | 95/95 (100.0%) |
| property/G03 | 80,000 | 80,000 | 78,616 | 45,044 | 34,858 | 0 | 70/70 (100.0%) |
| property/G04 | 60,000 | 60,000 | 38,155 | 56,906 | 3,094 | 0 | 273/273 (100.0%) |
| property/G05 | 20,000 | 20,000 | 16 | 20,000 | 0 | 0 | 32/32 (100.0%) |
| property/G06 | 100,000 | 100,000 | 65,187 | 56,688 | 43,312 | 0 | 31/31 (100.0%) |
| property/G07 | 50,000 | 50,000 | 29,152 | 50,000 | 0 | 0 | 8/8 (100.0%) |
| property/G07b | 30,000 | 30,000 | 15,875 | 30,000 | 0 | 0 | 15/15 (100.0%) |
| property/G08 | 720 | — | 720 | 720 | 0 | 0 | 240/240 (100.0%) |
| property/G09 | 20,000 | 20,000 | 5,499 | 20,000 | 0 | 0 | 12/12 (100.0%) |
| property/G09b | 20,000 | 20,000 | 11,960 | 20,000 | 0 | 0 | 4/4 (100.0%) |
| property/G10 | 50,000 | 50,000 | 40,096 | 39,877 | 10,123 | 0 | 3/3 (100.0%) |
| property/G10b | 50,000 | 50,000 | 38,152 | 50,000 | 0 | 0 | 18/18 (100.0%) |
| property/G11 | 30,000 | 30,000 | 7,076 | 30,000 | 0 | 0 | 15/15 (100.0%) |
| property/G12 | 10,000 | 10,000 | 2,863 | 10,000 | 0 | 0 | 16/16 (100.0%) |
| property/G13 | 20,000 | 20,000 | 16,299 | 20,000 | 0 | 0 | 48/48 (100.0%) |
| property/G14 | 4,000 | 4,000 | 4,000 | 1,354 | 2,646 | 0 | 6/6 (100.0%) |
| property/G14b | 300 | — | 39 | 300 | 0 | 0 | — |
| property/G15 | 30,000 | 30,000 | 1,536 | 30,000 | 0 | 0 | 12/12 (100.0%) |
| property/G15b | 200 | — | 200 | 200 | 0 | 0 | — |
| property/G15c-random | 1 | — | 1 | 1 | 0 | 0 | — |
| property/G15c-sequential | 1 | — | 1 | 1 | 0 | 0 | — |
| property/G15c-snowflake | 1 | — | 1 | 1 | 0 | 0 | — |
| property/G16 | 40,000 | 40,000 | 13,372 | 28,283 | 11,717 | 0 | 12/12 (100.0%) |
| property/G17 | 40,000 | 40,000 | 14,585 | 20,102 | 19,898 | 0 | 5/5 (100.0%) |
| property/G17b | 20,000 | 20,000 | 2,485 | 20,000 | 0 | 0 | 16/16 (100.0%) |
| property/G18 | 30,000 | 30,000 | 28,180 | 30,000 | 0 | 0 | 36/36 (100.0%) |
| property/G18b | 5,000 | — | 5,000 | 5,000 | 0 | 0 | — |
| property/G19 | 20,000 | 20,000 | 41 | 20,000 | 0 | 0 | — |
| property/G20 | 10,000 | 10,000 | 217 | 10,000 | 0 | 0 | 6/6 (100.0%) |
| property/G21 | 30,000 | 30,000 | 4,729 | 30,000 | 0 | 0 | 14/14 (100.0%) |
| property/G21b | 2,000 | 2,000 | 41 | 2,000 | 0 | 0 | — |
| property/G22 | 20,000 | 20,000 | 13,073 | 20,000 | 0 | 0 | — |
| property/G23 | 63 | — | 63 | 63 | 0 | 0 | — |
| property/G23b | 20,000 | 20,000 | 20,000 | 20,000 | 0 | 0 | — |
| property/G23c | 10,000 | 10,000 | 10,000 | 10,000 | 0 | 0 | 8/8 (100.0%) |
| property/G24 | 20,000 | 20,000 | 19,811 | 20,000 | 0 | 0 | 20/20 (100.0%) |
| property/G25 | 6,000 | 6,000 | 14 | 5,068 | 932 | 0 | — |
| property/G26 | 30,000 | 30,000 | 28,965 | 30,000 | 0 | 0 | 25/25 (100.0%) |
| property/G26b | 2,000 | — | 2,000 | 2,000 | 0 | 0 | — |
| property/G26c | 20,000 | 20,000 | 15,313 | 20,000 | 0 | 0 | 4/4 (100.0%) |
| property/G27 | 30,000 | 30,000 | 29,988 | 30,000 | 0 | 0 | — |
| property/G28 | 20,000 | 20,000 | 17,956 | 20,000 | 0 | 0 | — |
| property/G29 | 5,000 | 5,000 | 1 | 5,000 | 0 | 0 | 239/239 (100.0%) |
| property/G30 | 5,000 | 5,000 | 4,986 | 5,000 | 0 | 0 | 9/9 (100.0%) |
| property/G31 | 5,000 | 5,000 | 1,601 | 5,000 | 0 | 0 | — |
| integration/concurrency-agent-inbox | 200 | — | 200 | 200 | 0 | 0 | — |
| integration/concurrency-ai-budget | 100 | — | 100 | 100 | 0 | 0 | — |
| integration/concurrency-command-inbox | 200 | — | 200 | 200 | 0 | 0 | — |
| integration/concurrency-interaction-inbox | 200 | — | 200 | 200 | 0 | 0 | — |
| integration/concurrency-outbox | 1 | — | 1 | 1 | 0 | 0 | — |
| integration/concurrency-schedules | 1 | — | 1 | 1 | 0 | 0 | — |
| integration/concurrency-summary-lease | 200 | — | 200 | 200 | 0 | 0 | — |
| integration/concurrency-voice-suggest | 200 | — | 200 | 200 | 0 | 0 | — |
| integration/concurrency-welcome-cas | 200 | — | 200 | 200 | 0 | 0 | — |
| integration/concurrency-welcome-delivery | 200 | — | 200 | 200 | 0 | 0 | — |
| integration/migration-idempotency | 28 | — | 28 | 28 | 0 | 0 | — |
| integration/migration-split-points | 29 | — | 29 | 29 | 0 | 0 | — |
| integration/twin-model | 2,000 | — | 2,000 | 2,000 | 0 | 0 | 63/63 (100.0%) |

**Tổng: 1,438,845 check trên 60 generator.**

## 6. Coverage

| Assembly | Line | Branch | Dòng phủ/tổng | Nhánh phủ/tổng |
|---|---|---|---|---|
| Monze | 79.3% | 73.2% | 3,680/4,640 | 1,252/1,711 |
| Monze.Application | 93.5% | 90.3% | 1,361/1,455 | 662/733 |
| Monze.Domain | 95.8% | 75.0% | 300/313 | 204/272 |
| Monze.Infrastructure | 96.7% | 75.1% | 2,437/2,520 | 373/497 |
| **Tổng** | **87.1%** | **77.5%** | 7,778/8,928 | 2,491/3,213 |

Gộp từ mọi file cobertura của lần chạy theo (assembly, file, dòng); nhánh gộp là xấp xỉ. 164 file nguồn.

### 50 vùng chưa phủ lớn nhất

| Assembly | File | Dòng | Số dòng |
|---|---|---|---|
| Monze | Hosting/MonzeBot.Ingress.cs | 120–152 | 33 |
| Monze | Hosting/MonzeBot.cs | 129–149 | 21 |
| Monze.Infrastructure | Monze.Infrastructure/Caching/MonzeRedisRegistration.cs | 15–35 | 21 |
| Monze | Hosting/MonzeBot.InteractionResponses.cs | 61–79 | 19 |
| Monze | Features/Avatar/MonzeBot.Avatar.cs | 77–93 | 17 |
| Monze | Hosting/MonzeBot.InteractionInbox.cs | 107–123 | 17 |
| Monze | Features/Meeting/MonzeBot.Agent.cs | 186–201 | 16 |
| Monze | Features/Meeting/MonzeBot.ScheduledMeetings.cs | 92–107 | 16 |
| Monze | Hosting/MonzeBot.CommandInbox.cs | 55–70 | 16 |
| Monze | Hosting/MonzeBot.Commands.cs | 227–241 | 15 |
| Monze | Hosting/MonzeBot.Commands.cs | 328–342 | 15 |
| Monze | Hosting/MonzeBot.Commands.cs | 205–218 | 14 |
| Monze | Hosting/MonzeBot.InteractionInbox.cs | 19–31 | 13 |
| Monze | Hosting/MonzeBot.InteractionInbox.cs | 49–61 | 13 |
| Monze | Features/Avatar/MonzeBot.Avatar.cs | 121–132 | 12 |
| Monze | Features/Avatar/MonzeBot.Avatar.cs | 137–148 | 12 |
| Monze | Features/Meeting/MeetingMaintenanceWorker.cs | 117–128 | 12 |
| Monze | Hosting/MonzeBot.Ingress.cs | 165–176 | 12 |
| Monze | Hosting/MonzeBot.Ingress.cs | 276–287 | 12 |
| Monze | Hosting/MonzeBot.Connection.cs | 61–71 | 11 |
| Monze | Features/Meeting/MonzeBot.Meetings.cs | 85–94 | 10 |
| Monze | Features/Meeting/MonzeBot.Meetings.cs | 153–162 | 10 |
| Monze | Features/Meeting/MonzeBot.Meetings.cs | 166–175 | 10 |
| Monze | Features/Welcome/MonzeBot.WelcomeInteraction.cs | 57–66 | 10 |
| Monze | Hosting/MonzeBot.CommandInbox.cs | 106–115 | 10 |
| Monze | Hosting/MonzeBot.Commands.cs | 291–300 | 10 |
| Monze | Hosting/MonzeBot.Ingress.cs | 74–83 | 10 |
| Monze.Application | Monze.Application/Commands/MonzeRateLimitOptions.cs | 14–23 | 10 |
| Monze | Features/Meeting/MonzeBot.MeetingInteraction.cs | 64–72 | 9 |
| Monze | Features/Meeting/MonzeBot.MeetingScheduleInteraction.cs | 132–140 | 9 |
| Monze | Features/Meeting/MonzeBot.VoiceRooms.cs | 197–205 | 9 |
| Monze | Features/Roles/SdkRoleGateway.cs | 208–216 | 9 |
| Monze | Hosting/MonzeBot.Commands.cs | 246–254 | 9 |
| Monze | Hosting/MonzeBot.Commands.cs | 279–287 | 9 |
| Monze | Hosting/MonzeBot.Connection.cs | 34–42 | 9 |
| Monze | Hosting/MonzeBot.InteractionResponses.cs | 42–50 | 9 |
| Monze | Hosting/MonzeBot.Outbox.cs | 100–108 | 9 |
| Monze | Hosting/MonzeBot.Outbox.cs | 156–164 | 9 |
| Monze | Hosting/MonzeHostComposition.cs | 92–100 | 9 |
| Monze.Application | Monze.Application/Features/Welcome/MonzeApp.Welcome.cs | 180–188 | 9 |
| Monze | Features/Avatar/MonzeBot.Avatar.cs | 44–51 | 8 |
| Monze | Features/Meeting/MonzeBot.VoiceRooms.cs | 106–113 | 8 |
| Monze | Features/Meeting/MonzeBot.VoiceRooms.cs | 142–149 | 8 |
| Monze | Features/Roles/MonzeBot.Roles.cs | 15–22 | 8 |
| Monze | Features/Welcome/MonzeBot.WelcomeIngress.cs | 82–89 | 8 |
| Monze | Hosting/Logging/DailyFileLoggerProvider.cs | 102–109 | 8 |
| Monze | Hosting/MonzeBot.InteractionInbox.cs | 91–98 | 8 |
| Monze | Hosting/MonzeBot.Outbox.cs | 28–35 | 8 |
| Monze | Hosting/MonzeBot.Outbox.cs | 218–225 | 8 |
| Monze | Hosting/MonzeBot.cs | 258–265 | 8 |

## 7. Traceability

| Nhóm | Tổng | Đã map | Đã quan sát | Exclusion |
|---|---|---|---|---|
| btn | 42 | 42 | 0 | 0 |
| cfg | 41 | 41 | 0 | 0 |
| cmd | 15 | 15 | 0 | 0 |
| hosted | 3 | 3 | 0 | 0 |
| http | 4 | 4 | 0 | 0 |
| log | 110 | 110 | 0 | 0 |
| metric | 28 | 28 | 0 | 0 |
| migration | 28 | 28 | 0 | 0 |
| msg | 69 | 69 | 0 | 0 |
| port | 84 | 84 | 0 | 0 |
| sdk | 35 | 35 | 0 | 0 |
| table | 19 | 19 | 0 | 0 |

Inventory 478 mục: 478 đã khai báo, 0 chưa khai báo, 478 có test (100%). Requirement có test: 61/61.

### 37 TEST-ID của capability-inventory.json

| TEST-ID | Requirement | Số test |
|---|---|---|
| CMD-AI | REQ-AI-001 | 25 |
| CMD-AVATAR | REQ-AVA-001 | 5 |
| CMD-HELP | REQ-HELP-001 | 20 |
| CMD-MEETING | REQ-MTG-001 | 58 |
| CMD-ROLE | REQ-ROLE-001 | 40 |
| CMD-SETUP | REQ-SETUP-001 | 8 |
| CMD-SUMMARY | REQ-MTG-004 | 29 |
| CMD-WELCOME | REQ-WEL-001 | 16 |
| DB-MIGRATIONS | REQ-DB-001 | 37 |
| EVT-AGENT | REQ-MTG-003 | 25 |
| EVT-CHANNEL | REQ-MTG-005 | 4 |
| EVT-MESSAGE | REQ-ING-001 | 7 |
| EVT-VOICE | REQ-MTG-005 | 4 |
| EVT-WELCOME | REQ-WEL-003 | 11 |
| INT-HELP | REQ-HELP-001 | 20 |
| INT-MEETING | REQ-MTG-001 | 58 |
| INT-WELCOME | REQ-WEL-002 | 11 |
| PORT-AI-USAGE | REQ-AI-001 | 25 |
| PORT-AUTH | REQ-SETUP-001 | 8 |
| PORT-CLAN | REQ-CONN-001 | 8 |
| PORT-COMMAND-INBOX | REQ-INBOX-001 | 6 |
| PORT-HISTORY | REQ-ING-001 | 7 |
| PORT-INTERACTION-INBOX | REQ-INBOX-002 | 8 |
| PORT-MEETING | REQ-MTG-006 | 21 |
| PORT-OUTBOX | REQ-OUT-001 | 14 |
| PORT-PROFILE | REQ-AVA-001 | 5 |
| PORT-ROLE | REQ-ROLE-001 | 40 |
| PORT-SCHEDULED | REQ-MTG-002 | 42 |
| PORT-SCHEDULING | REQ-MTG-002 | 42 |
| PORT-WELCOME | REQ-WEL-001 | 16 |
| WRK-INGRESS | REQ-ING-001 | 7 |
| WRK-MEETING | REQ-MTG-003 | 25 |
| WRK-OUTBOX | REQ-OUT-001 | 14 |
| WRK-ROLE | REQ-ROLE-002 | 5 |
| WRK-SCHEDULE | REQ-MTG-002 | 42 |
| WRK-SUMMARY | REQ-MTG-004 | 29 |
| WRK-WELCOME | REQ-WEL-003 | 11 |

## 8. Hiệu năng

### 8.1 Micro benchmark

| Benchmark | Tham số | Mean | Allocated/op |
|---|---|---|---|
| CacheBenchmarks.L1Miss |  | 13.84 ns | 0 B |
| CacheBenchmarks.L1ReplaceNewerVersion |  | 152.21 ns | 512 B |
| CacheBenchmarks.CacheKeyHash |  | 9.58 ns | 0 B |
| IngressCallbackBenchmarks.MessageCallbackAccepted |  | 33.09 ns | 0 B |
| MonzeCacheHotPathBenchmarks.L1TypedKeyHit |  | 30.27 ns | 0 B |
| MonzeCapacityBenchmarks.ActiveClanIngressTryWrite | ClanCount=1&ActiveClanCount=100 | 30.11 ns | 0 B |
| MonzeCapacityBenchmarks.ActiveClanIngressTryWrite | ClanCount=10&ActiveClanCount=100 | 29.86 ns | 0 B |
| MonzeCapacityBenchmarks.ActiveClanIngressTryWrite | ClanCount=100&ActiveClanCount=100 | 30.51 ns | 0 B |
| MonzeCapacityBenchmarks.ActiveClanIngressTryWrite | ClanCount=1000&ActiveClanCount=100 | 31.89 ns | 0 B |
| MonzeHotPathBenchmarks.PartitionedIngressTryWrite |  | 28.15 ns | 0 B |
| MonzeHotPathBenchmarks.CommandArgumentsSingleItem |  | 0.66 ns | 0 B |
| MonzeHotPathBenchmarks.CommandRateLimitKnownKey |  | 17.77 ns | 0 B |
| MonzeMetricsBenchmarks.UpstreamRateLimitCallback |  | 2.16 ns | 0 B |
| MonzeMetricsBenchmarks.CommandInflightAndDuration |  | 2.50 ns | 0 B |
| MonzeMetricsBenchmarks.CommandModuleTag |  | 3.64 ns | 0 B |
| ParserBenchmarks.MeetingCommandSchedule |  | 132.69 ns | 144 B |
| ParserBenchmarks.MeetingScheduleForm |  | 1.83 µs | 552 B |
| ParserBenchmarks.ScheduleNextDaily |  | 108.04 ns | 0 B |
| ParserBenchmarks.AgentSummaryPayload |  | 1.80 µs | 2688 B |
| RateLimiterBenchmarks.KnownKeyOverLimit |  | 18.28 ns | 0 B |
| RateLimiterBenchmarks.NewKeyRejectedAtEntryBound |  | 9.72 µs | 0 B |
| RendererBenchmarks.WelcomeTemplate |  | 982.16 ns | 6344 B |
| RendererBenchmarks.HelpCommandsPage |  | 2.25 µs | 13816 B |
| RendererBenchmarks.ResultCard |  | 495.57 ns | 7168 B |

### 8.2 Component

| ID | Mô tả | Verdict | Chỉ số chính | Invariant | Defect |
|---|---|---|---|---|---|
| C-INBOX | Inbox race: 20 vòng × 2,000 claim cùng key | ✅ PASS | rounds=20; claims=80,000; claimP50Ms=127.999; claimP99Ms=333.823; claimMaxMs=395.775 | 2/2 |  |
| C-MIGRATE | Migrate từ DB rỗng và chạy lại, 5 lần mỗi server | ✅ PASS | pg17.fromZeroP50Ms=139.263; pg17.fromZeroP99Ms=145.042; pg17.fromZeroMaxMs=145.042; pg17.rerunP50Ms=11.711; pg17.rerunP99Ms=17.198; pg17.rerunMaxMs=17.198 | 4/4 |  |
| C-OUTBOX-CLAIM | Outbox claim: 50,000 dòng due trên 200,000 dòng đã gửi, 1/2/4/8 worker, rồi poll rảnh | ⚠️ KNOWN_GAP | rows=50,000; idleTuplesPerPoll=250,002; idleFreshStatsTuplesPerPoll=2; idlePollP50Ms=24.447; idlePollP99Ms=45.574; idlePollMaxMs=45.574 | 5/9 | CAND-17 |
| C-POOL | Pool 64 + 128 thao tác đồng thời + ~200 ms/round trip qua proxy, 10 vòng | ✅ PASS | operations=1,280; operationP50Ms=434.175; operationP99Ms=2171.536; operationMaxMs=2171.536; peakServerConnections=64; errors=0 | 2/2 |  |
| C-REDIS | Redis L2: 5,000 key, 500 lần lan truyền invalidation giữa 2 instance | ✅ PASS | keys=5,000; writeP50Ms=0.623; writeP99Ms=1.415; writeMaxMs=12.224; readP50Ms=0.267; readP99Ms=0.507 | 4/4 |  |
| C-SCHED | Scheduler: 10,000 lịch due, 8 worker | ✅ PASS | schedules=10,000; schedulesPerSecond=1950.416; claimP50Ms=3.679; claimP99Ms=20.738; claimMaxMs=20.738; commitP50Ms=3.823 | 5/5 |  |
| C-SKIPLOCKED | SKIP LOCKED fairness với 8 worker | ✅ PASS | workers=8; jainIndex=1; minRows=6,166; maxRows=6,400 | 1/1 |  |
| C-SQLITE | SqliteMessageStore 200/1.000/2.000 msg/s trong 30 s mỗi mức | ✅ PASS | r200.achievedPerSecond=200; r200.visibleP50Ms=7.487; r200.visibleP99Ms=15.54; r200.visibleMaxMs=15.54; r200.flushMs=0.895; r200.generatorLagP50Ms=0 | 12/12 |  |
| C-SUMMARY-LEASE | Summary lease: 2,000 meeting chờ tóm tắt, 8 worker, ~60 % lần thử lỗi | ✅ PASS | rooms=2,000; posted=1,973; summaryFailed=27; leaseP50Ms=1.495; leaseP99Ms=2.527; leaseMaxMs=16.202 | 6/6 |  |

### 8.3 Load toàn tiến trình

| ID | Mô tả | Verdict | Chỉ số chính | Invariant | Defect |
|---|---|---|---|---|---|
| L-V1-S1 | Load V1 (giới hạn transport mặc định của Monze), 1 clan đăng ký, 1 active | ❌ FAIL | commandP95Ms=3391.487; commandP99Ms=5636.095; outboxP99Ms=0; outboundWritesPerSecond=6.521; apiReadsPerSecond=1.804; commandsAnswered=709 | 2/11 | DEF-03 |
| L-V1-S10 | Load V1 (giới hạn transport mặc định của Monze), 10 clan đăng ký, 10 active | ❌ FAIL | commandP95Ms=4816.895; commandP99Ms=6586.367; outboxP99Ms=0; outboundWritesPerSecond=5.592; apiReadsPerSecond=2.646; commandsAnswered=672 | 2/11 | DEF-03 |
| L-V1-S100 | Load V1 (giới hạn transport mặc định của Monze), 100 clan đăng ký, 100 active | ❌ FAIL | commandP95Ms=9568.255; commandP99Ms=9961.471; outboxP99Ms=0; outboundWritesPerSecond=3.242; apiReadsPerSecond=5.092; commandsAnswered=475 | 2/11 | DEF-03 |
| L-V1-S1000 | Load V1 (giới hạn transport mặc định của Monze), 1,000 clan đăng ký, 100 active | ❌ FAIL | commandP95Ms=9175.039; commandP99Ms=9830.399; outboxP99Ms=0; outboundWritesPerSecond=3.296; apiReadsPerSecond=5.029; commandsAnswered=496 | 2/11 | DEF-03 |
| L-V3-S1 | Load V3 (không giới hạn transport (hook test)), 1 clan đăng ký, 1 active | ✅ PASS | commandP95Ms=3.423; commandP99Ms=6.463; outboxP99Ms=257.023; outboundWritesPerSecond=224.15; apiReadsPerSecond=12.283; commandsAnswered=4,800 | 11/11 |  |
| L-V3-S10 | Load V3 (không giới hạn transport (hook test)), 10 clan đăng ký, 10 active | ✅ PASS | commandP95Ms=3.375; commandP99Ms=6.079; outboxP99Ms=254.975; outboundWritesPerSecond=225.083; apiReadsPerSecond=15.838; commandsAnswered=4,800 | 11/11 |  |
| L-V3-S100 | Load V3 (không giới hạn transport (hook test)), 100 clan đăng ký, 100 active | ✅ PASS | commandP95Ms=3.471; commandP99Ms=6.847; outboxP99Ms=257.023; outboundWritesPerSecond=225.071; apiReadsPerSecond=23.183; commandsAnswered=4,800 | 11/11 |  |
| L-V3-S1000 | Load V3 (không giới hạn transport (hook test)), 1,000 clan đăng ký, 100 active | ✅ PASS | commandP95Ms=3.471; commandP99Ms=6.655; outboxP99Ms=257.023; outboundWritesPerSecond=225.017; apiReadsPerSecond=24.7; commandsAnswered=4,800 | 11/11 |  |
| L-V3L-S1 | Load V3L (V3 + ack log-normal p50 30 ms, p99 150 ms), 1 clan đăng ký, 1 active | ⚠️ KNOWN_GAP | commandP95Ms=182.271; commandP99Ms=276.479; outboxP99Ms=230686.719; outboundWritesPerSecond=84.546; apiReadsPerSecond=12.817; commandsAnswered=4,800 | 9/11 | CAND-20 |
| L-V3L-S10 | Load V3L (V3 + ack log-normal p50 30 ms, p99 150 ms), 10 clan đăng ký, 10 active | ⚠️ KNOWN_GAP | commandP95Ms=169.983; commandP99Ms=247.807; outboxP99Ms=82313.215; outboundWritesPerSecond=176.708; apiReadsPerSecond=15.879; commandsAnswered=4,800 | 9/11 | CAND-20 |
| L-V3L-S100 | Load V3L (V3 + ack log-normal p50 30 ms, p99 150 ms), 100 clan đăng ký, 100 active | 🔁 KNOWN_GAP_NOT_REPRODUCED | commandP95Ms=196.607; commandP99Ms=278.527; outboxP99Ms=1310.719; outboundWritesPerSecond=224.992; apiReadsPerSecond=22.983; commandsAnswered=4,800 | 11/11 | CAND-20 |
| L-V3L-S1000 | Load V3L (V3 + ack log-normal p50 30 ms, p99 150 ms), 1,000 clan đăng ký, 100 active | 🔁 KNOWN_GAP_NOT_REPRODUCED | commandP95Ms=191.487; commandP99Ms=266.239; outboxP99Ms=1482.751; outboundWritesPerSecond=224.771; apiReadsPerSecond=24.462; commandsAnswered=4,800 | 11/11 | CAND-20 |

### 8.4 Capacity và backpressure

| ID | Mô tả | Verdict | Chỉ số chính | Invariant | Defect |
|---|---|---|---|---|---|
| BP-1 | Burst 50,000 tin vào một lane ingress (capacity 8,192/16 lane) | ✅ PASS | pushed=50,000; pushPerSecond=96228.034; dropped=2,717; maxDepth=512; gapChannels=3; heapGrowthMiB=27.58 | 5/5 |  |
| BP-2 | Tràn hàng đợi gap: 150,000 tin vào 300 channel lưu lịch sử cùng lúc (MessageCapacity 1,024) | ⚠️ KNOWN_GAP | pushed=150,000; pushPerSecond=84845.818; messagesDropped=1,747; gapMarkersDropped=1,234; maxGapDepth=256; channelsWithLoss=268 | 4/5 | WF-07 |
| BP-3 | Burst 1,998 event Agent vào hàng đợi Agent capacity 128 | ✅ PASS | events=1,998; eventsLostUpstream=0; publishSeconds=0.024; drainSeconds=6.177; roomsPosted=666; maxDepth=128 | 6/6 |  |
| BP-4 | Bão 2,000 join qua hàng đợi welcome | ✅ PASS | joins=2,000; drainSeconds=5.264; welcomesSent=2,000; unwelcomed=0; welcomeDeliveries=2,000; maxPendingWriters=975 | 4/4 |  |
| BP-5 | Bão 2.000 lệnh/s trong 5 s (V3: không giới hạn transport (hook test)) | ✅ PASS | commandsSent=9,972; commandsPerSecond=1993.647; outputs=9,973; commandInboxRows=9,973; maxInflight=106; settleSeconds=0.081 | 4/4 |  |
| BP-5-V1 | Bão 2.000 lệnh/s trong 5 s (V1: giới hạn transport mặc định của Monze) | ⚠️ KNOWN_GAP | commandsSent=9,986; commandsPerSecond=1993.741; outputs=797; commandInboxRows=9,987; maxInflight=9,749; settleSeconds=60.152 | 3/4 | DEF-03 |
| BP-6 | Backlog 100,000 dòng outbox due khi khởi động | ✅ PASS | rows=100,000; drainSeconds=60.182; deliveredPerSecond=1661.628; undelivered=0; duplicates=0; heapGrowthMiB=60.511 | 3/3 |  |
| BP-7 | Rejoin sau mất socket với 1.000 clan đăng ký, 100 active | ✅ PASS | timeToReadyMs=253.705; rejoinSeconds=1.089; clansRejoined=1,000; registryInactive=0 | 3/3 |  |
| BP-8 | Biên discovery: 99 clan thấy được + 5 clan chỉ có trong registry | ✅ PASS | registryInactive=5 | 2/2 |  |
| BP-8-DEF06 | Cài mới (registry rỗng), bot là thành viên của 150 clan, discovery trả tối đa 100 | ⚠️ KNOWN_GAP | memberClans=150; joinedClans=100 | 1/2 | DEF-06 |

### 8.5 Soak

| ID | Mô tả | Verdict | Chỉ số chính | Invariant | Defect |
|---|---|---|---|---|---|
| SOAK-120M | Soak 120 phút: 1,000 clan đăng ký, V3L, 4 chu kỳ tải + nghỉ (reconnect) | ❌ FAIL | minutes=120.001; heapSlopeMiBPerHour=84.544; heapTrendP=0; privateSlopeMiBPerHour=113.525; earlyP99Ms=272.383; lateP99Ms=240.639 | 4/8 |  |

### 8.6 k6 cross-check

| ID | Mô tả | Verdict | Chỉ số chính | Invariant | Defect |
|---|---|---|---|---|---|
| K6-CROSSCHECK | k6 constant-arrival-rate 20 lệnh/s trong 120 s qua endpoint loopback, so với đo nội bộ | ✅ PASS | k6P95Ms=3.327; internalP95Ms=3.103; differenceMs=0.224; k6P99Ms=5.612; internalP99Ms=5.471; requests=2,401 | 3/3 |  |

## 9. Ma trận chaos

| ID | Mô tả | Verdict | Chỉ số chính | Invariant | Defect |
|---|---|---|---|---|---|
| AG-01 | Agent: server ngắt stream SSE 3 lần cách 7 s | ⚠️ KNOWN_GAP | rtoMs=97.987; lostDuringFault=0; answeredDuringFault=211; duringFaultP95Ms=8.447; lostAfterRecovery=0; outboxDuplicates=0 | 15/16 | DEF-08 |
| AG-02 | Agent: stream SSE half-open (kết nối còn, không byte nào tới) và không bao giờ được thay | ⚠️ KNOWN_GAP | rtoMs=97.466; lostDuringFault=0; answeredDuringFault=201; duringFaultP95Ms=7.071; lostAfterRecovery=0; outboxDuplicates=0 | 14/16 | DEF-08 |
| AG-03 | Agent: mọi event Agent được giao hai lần trong 20 s | ✅ PASS | rtoMs=84.341; lostDuringFault=0; answeredDuringFault=200; duringFaultP95Ms=8.447; lostAfterRecovery=0; outboxDuplicates=0 | 16/16 |  |
| AG-04 | Agent: room_ended tới trước room_started trong 20 s | ✅ PASS | rtoMs=99.47; lostDuringFault=0; answeredDuringFault=201; duringFaultP95Ms=6.879; lostAfterRecovery=0; outboxDuplicates=0 | 16/16 |  |
| AG-05 | Agent: event hỏng, JSON không phải object và event của clan khác trong 20 s | ✅ PASS | rtoMs=93.014; lostDuringFault=0; answeredDuringFault=200; duringFaultP95Ms=7.039; lostAfterRecovery=0; outboxDuplicates=0 | 17/17 |  |
| AG-06 | Agent: burst 1.998 event Agent (666 phòng × 3 event) | ✅ PASS | rtoMs=86.223; lostDuringFault=0; answeredDuringFault=201; duringFaultP95Ms=6.687; lostAfterRecovery=0; outboxDuplicates=0 | 17/17 |  |
| AI-01 | AI/Transcript: provider AI chậm 35 s (client timeout 30 s) | ✅ PASS | rtoMs=94.018; lostDuringFault=0; answeredDuringFault=401; duringFaultP95Ms=6.559; lostAfterRecovery=0; outboxDuplicates=0 | 18/18 |  |
| AI-02 | AI/Transcript: provider AI trả 429/500/503 xen kẽ | ✅ PASS | rtoMs=89.455; lostDuringFault=0; answeredDuringFault=201; duringFaultP95Ms=7.551; lostAfterRecovery=0; outboxDuplicates=0 | 18/18 |  |
| AI-03 | AI/Transcript: provider AI trả body 3 MiB (giới hạn 2 MiB) | ✅ PASS | rtoMs=92.157; lostDuringFault=0; answeredDuringFault=201; duringFaultP95Ms=7.583; lostAfterRecovery=0; outboxDuplicates=0 | 18/18 |  |
| AI-04 | AI/Transcript: provider AI nhỏ giọt body 150 ms/byte | ✅ PASS | rtoMs=97.776; lostDuringFault=0; answeredDuringFault=301; duringFaultP95Ms=6.495; lostAfterRecovery=0; outboxDuplicates=0 | 18/18 |  |
| AI-05 | AI/Transcript: provider AI trả JSON hỏng | ✅ PASS | rtoMs=95.942; lostDuringFault=0; answeredDuringFault=201; duringFaultP95Ms=6.591; lostAfterRecovery=0; outboxDuplicates=0 | 18/18 |  |
| AI-06 | AI/Transcript: AI bão hoà: provider chậm 10 s và 10 lệnh AI/s trong 30 s | ✅ PASS | rtoMs=99.319; lostDuringFault=0; answeredDuringFault=301; duringFaultP95Ms=6.559; lostAfterRecovery=0; outboxDuplicates=0 | 18/18 |  |
| CLK-01 | Clock: đồng hồ app nhảy +2 h | ✅ PASS | rtoMs=88.88; lostDuringFault=0; answeredDuringFault=100; duringFaultP95Ms=11.519; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| CLK-02 | Clock: đồng hồ app lùi −2 h | 🔁 KNOWN_GAP_NOT_REPRODUCED | rtoMs=92.932; lostDuringFault=0; answeredDuringFault=100; duringFaultP95Ms=11.583; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 | DEF-10 |
| CLK-03 | Clock: đồng hồ app nhảy tới trước ngày đổi giờ mùa hè (Europe/Berlin 28/03/2027) khi lịch đến hạn | ⚠️ KNOWN_GAP | rtoMs=69.811; lostDuringFault=0; answeredDuringFault=150; duringFaultP95Ms=10.943; lostAfterRecovery=0; outboxDuplicates=0 | 9/10 | CAND-09 |
| CLK-04 | Clock: app lệch database 90 s | ✅ PASS | rtoMs=2.681; lostDuringFault=0; answeredDuringFault=100; duringFaultP95Ms=11.455; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| EV-01 | Event: mọi tin nhắn được giao hai lần trong 20 s | ✅ PASS | rtoMs=73.605; lostDuringFault=0; answeredDuringFault=200; duringFaultP95Ms=7.231; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| EV-03 | Event: sự kiện voice đảo thứ tự trong 20 s | ✅ PASS | rtoMs=87.389; lostDuringFault=0; answeredDuringFault=200; duringFaultP95Ms=8.767; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| EV-04 | Event: join được giao hai lần trong 20 s | ✅ PASS | rtoMs=78.754; lostDuringFault=0; answeredDuringFault=200; duringFaultP95Ms=6.943; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| MZ-01 | Mezon: server đóng socket | ⚠️ KNOWN_GAP | rtoMs=7.788; lostDuringFault=10; answeredDuringFault=40; duringFaultP95Ms=12.031; lostAfterRecovery=0; outboxDuplicates=0 | 7/8 | CAND-21 |
| MZ-02 | Mezon: 20 lần đóng socket trong 60 s | ⚠️ KNOWN_GAP | rtoMs=2.728; lostDuringFault=200; answeredDuringFault=385; duringFaultP95Ms=6.687; lostAfterRecovery=0; outboxDuplicates=0 | 7/8 | CAND-21 |
| MZ-03 | Mezon: ack gửi tin chậm 2 s trong 30 s | ✅ PASS | rtoMs=1.898; lostDuringFault=0; answeredDuringFault=300; duringFaultP95Ms=2392.063; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| MZ-04 | Mezon: mất ack của 50 lần gửi | ✅ PASS | rtoMs=4.971; lostDuringFault=0; answeredDuringFault=200; duringFaultP95Ms=3325.951; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| MZ-05 | Mezon: ack lỗi 403/404/500 cho 60 lần gửi | ✅ PASS | rtoMs=1.731; lostDuringFault=0; answeredDuringFault=200; duringFaultP95Ms=6.847; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| MZ-07 | Mezon: đăng nhập/handshake bị từ chối 5 lần sau khi mất socket | ⚠️ KNOWN_GAP | rtoMs=11206.752; lostDuringFault=80; answeredDuringFault=0; duringFaultP95Ms=0; lostAfterRecovery=0; outboxDuplicates=0 | 7/8 | CAND-21 |
| MZ-08 | Mezon: discovery lỗi 3 lần khi kết nối lại | ⚠️ KNOWN_GAP | rtoMs=98.639; lostDuringFault=40; answeredDuringFault=100; duringFaultP95Ms=10.559; lostAfterRecovery=0; outboxDuplicates=0 | 7/8 | CAND-21 |
| PG-01 | PostgreSQL: treo mạng 20 s (blackhole) | ✅ PASS | rtoMs=53.372; lostDuringFault=27; answeredDuringFault=83; duringFaultP95Ms=12648.447; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| PG-02 | PostgreSQL: stop rồi start container | ✅ PASS | rtoMs=65.659; lostDuringFault=0; answeredDuringFault=144; duringFaultP95Ms=4112.383; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| PG-03 | PostgreSQL: SIGKILL rồi khởi động lại | ✅ PASS | rtoMs=50.832; lostDuringFault=0; answeredDuringFault=94; duringFaultP95Ms=4112.383; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| PG-04a | PostgreSQL: trễ 200 ms mỗi chiều trong 30 s | ✅ PASS | rtoMs=4.143; lostDuringFault=0; answeredDuringFault=300; duringFaultP95Ms=1941.503; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| PG-04b | PostgreSQL: trễ 2 s mỗi chiều trong 20 s | ⚠️ KNOWN_GAP | rtoMs=92.403; lostDuringFault=21; answeredDuringFault=109; duringFaultP95Ms=8045.992; lostAfterRecovery=0; outboxDuplicates=50 | 7/8 | DEF-07 |
| PG-05 | PostgreSQL: reset mọi kết nối 3 lần cách 5 s | ✅ PASS | rtoMs=4.56; lostDuringFault=0; answeredDuringFault=150; duringFaultP95Ms=12.095; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 |  |
| PG-06 | PostgreSQL: blackhole ngay sau ack của một lần gửi outbox, 20 s | 🔁 KNOWN_GAP_NOT_REPRODUCED | rtoMs=6.819; lostDuringFault=47; answeredDuringFault=62; duringFaultP95Ms=12713.983; lostAfterRecovery=0; outboxDuplicates=0 | 8/8 | DEF-07 |
| PG-07 | PostgreSQL: database không sẵn sàng lúc khởi động 20 s | ❌ FAIL |  | 0/1 |  |
| PG-08 | PostgreSQL: mất event Agent ended trong 20 s (upstream không giao room_ended) | ✅ PASS | rtoMs=98.04; lostDuringFault=0; answeredDuringFault=201; duringFaultP95Ms=7.295; lostAfterRecovery=0; outboxDuplicates=0 | 16/16 |  |
| PR-01 | Process: kill Monze giữa tải, khởi động lại sau 10 s trên cùng database | ⚠️ KNOWN_GAP | rtoMs=86.902; lostDuringFault=40; answeredDuringFault=0; duringFaultP95Ms=0; lostAfterRecovery=0; outboxDuplicates=0 | 16/17 | DEF-08 |
| PR-06 | Process: dừng Monze bình thường giữa tải khi outbox đang gửi (ack chậm 500 ms), khởi động lại sau 15 s trên cùng database | ❌ FAIL | rtoMs=4731.486; lostDuringFault=80; answeredDuringFault=20; duringFaultP95Ms=524.287; lostAfterRecovery=0; outboxDuplicates=30 | 13/17 | DEF-08, WF-05, WF-06 |
| PR-07 | Process: hai instance chồng nhau 30 s trên cùng database và platform, rồi một instance dừng | ✅ PASS | rtoMs=0.316; lostDuringFault=0; answeredDuringFault=300; duringFaultP95Ms=8.031; lostAfterRecovery=0; outboxDuplicates=0 | 19/19 |  |
| RD-01 | Redis: stop container 20 s | ✅ PASS | rtoMs=37.184; lostDuringFault=0; answeredDuringFault=205; duringFaultP95Ms=7.615; lostAfterRecovery=0; outboxDuplicates=0 | 13/13 |  |
| RD-02 | Redis: pause container 10 s (Redis chậm) | ⚠️ KNOWN_GAP | rtoMs=77.451; lostDuringFault=0; answeredDuringFault=102; duringFaultP95Ms=10.751; lostAfterRecovery=0; outboxDuplicates=0 | 12/13 | DEF-01 |
| RD-03 | Redis: trễ 200 ms mỗi chiều trong 20 s | ✅ PASS | rtoMs=3.363; lostDuringFault=0; answeredDuringFault=200; duringFaultP95Ms=6.271; lostAfterRecovery=0; outboxDuplicates=0 | 13/13 |  |
| RD-04 | Redis: reset kết nối (mất pub/sub) 3 lần | ✅ PASS | rtoMs=0.163; lostDuringFault=0; answeredDuringFault=150; duringFaultP95Ms=10.239; lostAfterRecovery=0; outboxDuplicates=0 | 13/13 |  |
| RD-05 | Redis: mất toàn bộ dữ liệu (FLUSHALL) | ✅ PASS | rtoMs=90.045; lostDuringFault=0; answeredDuringFault=51; duringFaultP95Ms=11.775; lostAfterRecovery=0; outboxDuplicates=0 | 13/13 |  |
| RD-06 | Redis: Redis xuất hiện sau khi bot đã chạy | ✅ PASS | rtoMs=0.973; lostDuringFault=0; answeredDuringFault=50; duringFaultP95Ms=11.391; lostAfterRecovery=0; outboxDuplicates=0 | 10/10 |  |
| TR-01 | AI/Transcript: transcript treo (client timeout 30 s) trong 40 s | ⚠️ KNOWN_GAP | rtoMs=90.756; lostDuringFault=0; answeredDuringFault=401; duringFaultP95Ms=6.303; lostAfterRecovery=0; outboxDuplicates=0 | 15/16 | WF-04 |
| TR-02 | AI/Transcript: access token transcript bị thu hồi mỗi 3 s (401 → refresh) | ✅ PASS | rtoMs=93.728; lostDuringFault=0; answeredDuringFault=200; duringFaultP95Ms=9.343; lostAfterRecovery=0; outboxDuplicates=0 | 16/16 |  |
| TR-03 | AI/Transcript: transcript trả 500 trong 30 s | ✅ PASS | rtoMs=80.602; lostDuringFault=0; answeredDuringFault=300; duringFaultP95Ms=6.527; lostAfterRecovery=0; outboxDuplicates=0 | 16/16 |  |
| TR-04 | AI/Transcript: transcript hợp lệ 600 KiB (giới hạn 512 KiB) trong 20 s | ⚠️ KNOWN_GAP | rtoMs=78.904; lostDuringFault=0; answeredDuringFault=200; duringFaultP95Ms=7.871; lostAfterRecovery=0; outboxDuplicates=0 | 16/17 | WF-03 |
| TR-05 | AI/Transcript: transcript rỗng: tám lần retry hết hạn (retry được warp) | ✅ PASS | rtoMs=94.538; lostDuringFault=0; answeredDuringFault=600; duringFaultP95Ms=6.335; lostAfterRecovery=0; outboxDuplicates=0 | 16/16 |  |

## 10. Defect

| Defect | Mức | Khu vực | Lần chạy này | Mô tả | Vị trí | Đề xuất sửa | Khối sửa |
|---|---|---|---|---|---|---|---|
| CAND-19 | P0 | security | ⚠️ tái hiện | Actor của button click do client gửi được tin là đã xác thực: thành viên giả user_id của owner để lưu welcome | Hosting/MonzeBot.InteractionResponses.cs:187 (fallback binding theo clan/channel/user); SDK 1.6.2 Interactions/InteractionRouter.cs:71,94; mezon-api server/api_interactive_message.go:10 | Không dùng fallback binding theo user cho thao tác thay đổi state; chỉ chấp nhận click khớp message_id đã bind với đúng người nhận ephemeral; handoff mezon-api ghi đè UserId bằng session user như DropdownBoxSelected và nâng SDK lên bản coi click là client-supplied (6f267e7). | 1 |
| CAND-21 | P1 | outbox | ⚠️ tái hiện | Socket đóng khi outbox đang gửi: dòng bị giữ uncertain vĩnh viễn kể cả khi tin chưa từng ra wire (mất tin ~1 giây lưu lượng mỗi lần mất kết nối) | Hosting/MonzeBot.Outbox.cs:228 IsUncertainDeliveryFailure | Phân biệt lỗi trước khi gửi (chưa kết nối, gửi thất bại cục bộ) với mất ack sau khi gửi; chỉ giữ uncertain cho trường hợp sau, trường hợp trước trả về pending để retry. | 2 |
| DEF-07 | P1 | outbox | 🔁 không còn tái hiện | Tin đã được ack nhưng database mất trước khi ghi hoàn tất bị gửi lại sau khi lease 60 s hết hạn | Hosting/MonzeBot.Outbox.cs (ack rồi CompleteOutboxAsync không nguyên tử) | Ghi nhận external id/ack bền vững trước khi trả lease (hoặc idempotency key phía Mezon); reclaim sau lease hết hạn phải kiểm tra tin đã có trên kênh. | 2 |
| SEC-01 | P1 | dependency | không chạy trong lần này | SQLitePCLRaw.lib.e_sqlite3 2.1.11 có lỗ hổng mức high (GHSA-2m69-gcr7-jv3q), kéo vào qua Mezon.Net.Sdk.Caching.Sqlite 1.6.2 | packages.lock.json (Monze, Monze.Infrastructure) | Pin SQLitePCLRaw.bundle_e_sqlite3 bản đã vá ở Monze hoặc phát hành SDK Caching.Sqlite mới; giữ lockfile đồng bộ. | 2 |
| DEF-03 | P1 | capacity | ⚠️ tái hiện | Giới hạn transport 500 request/phút (cửa sổ trượt, không ưu tiên) thấp hơn workload tài liệu ~26 lần: burst ~2 s rồi im ~58 s | Hosting/MonzeBot.cs:320-331; SDK 1.6.2 Queue/SlidingWindowRateLimiter.cs | Lấy giới hạn thật của Mezon (contract), cấu hình đúng, dàn đều (token bucket) và ưu tiên phản hồi lệnh/heartbeat trước outbox hàng loạt. | 3 |
| DEF-08 | P1 | agent | ⚠️ tái hiện | Agent SSE không phát hiện stream half-open và không gửi Last-Event-ID: mọi sự kiện Agent sau đó mất tới khi restart | SDK 1.6.2 Mezon.Net.Sdk/Agent/AgentSseManager.cs ReadOnceAsync (HttpClient timeout vô hạn, không idle timeout, không Last-Event-ID) | SDK: idle timeout theo keepalive, reconnect kèm Last-Event-ID; Monze: đối soát phiên live với Agent sau reconnect. | 4 |
| DEF-09 | P2 | mezon | không tái hiện được | Gửi mất ack chờ không giới hạn | SDK socket timeout | SDK timeout ack; dòng giữ uncertain, tin không bị gửi lại. | — |
| DEF-11 | P2 | agent | không tái hiện được | Transcript trả 500 không được retry | Infrastructure/Agent/HttpTranscriptClient.cs IsTransient (500 không retry trong cùng lần gọi) | Không cần sửa: 500 không retry trong cùng lần gọi nhưng worker bảo trì retry với backoff (5 s … 300 s) có lease; TR-03 tóm tắt đủ mọi cuộc họp sau khi transcript hồi phục. | — |
| WF-02 | P2 | outbox | ⚠️ tái hiện | Dòng outbox phụ thuộc (action items của summary) kẹt 'pending' vĩnh viễn khi dòng cha thành 'uncertain' | Monze.Infrastructure/Persistence/Outbox/PostgresOutboxRepository.Delivery.cs ClaimDueOutboxAsync (depends_on_id chỉ chấp nhận cha 'sent') | Khi cha uncertain/failed: gửi con (hoặc giữ con cùng trạng thái để admin xử lý) thay vì để pending không ai thấy. | 2 |
| WF-05 | P2 | outbox | FAIL | Dừng Monze bình thường khi đang gửi outbox: tin đã được ack nhưng bước hoàn tất chạy trên stopping token đã huỷ nên dòng vẫn giữ lease 'sending' và bị gửi lại sau khi lease 60 s hết hạn (PR-06: 14–20 tin trùng sau một lần restart) | Hosting/MonzeBot.Outbox.cs:167-173 và 200-226 (TryCompleteOutboxAsync dùng cancellationToken của worker; các catch bỏ qua khi token đã huỷ) | Hoàn tất (ghi external_message_id) bằng token riêng có timeout ngắn thay vì stopping token, hoặc chờ các lần gửi đang chạy hoàn tất trước khi dừng worker outbox. | 2 |
| CAND-17 | P2 | performance | ⚠️ tái hiện | Claim outbox duyệt khoá chính qua toàn bộ lịch sử đã gửi (bảng không có retention); poll rảnh sau burst đọc toàn bảng | Monze.Infrastructure/Persistence/Outbox/PostgresOutboxRepository.Delivery.cs:16-31 | Index phù hợp (partial index status pending/sending theo id) và/hoặc tách điều kiện để planner dùng outbox_due_ready; thêm retention cho dòng sent. | 3 |
| CAND-20 | P2 | capacity | 🔁 không còn tái hiện | Với ack trung vị 30 ms outbox chỉ đạt ~150/s < 200/s tài liệu, backlog tăng (p99 18 s) | Hosting/MonzeBot.Outbox.cs:40-62 (batch chờ phần tử chậm nhất trước khi poll lại) | Pipeline claim/gửi không chờ cả batch; đo lại concurrency hiệu dụng của SDK. | 3 |
| WF-07 | P2 | ingress | ⚠️ tái hiện | Hàng đợi gap đầy thì marker gap bị bỏ, không có đường dự phòng: channel mất tin nhưng has_gap vẫn false nên tóm tắt AI bỏ sót tin mà không báo (BP-2: 21/237 channel mất tin không được đánh dấu) | Hosting/MonzeBot.Ingress.cs:91-109 (TryQueueMessageGap chỉ tăng metric khi TryWrite thất bại); Hosting/MonzeBot.Ingress.cs:141-150 (hết retry cũng bỏ) | Giữ tập channel có gap chưa ghi (giới hạn theo số channel) hoặc cờ dự phòng đánh dấu gap cho cả clan khi marker bị bỏ; log cảnh báo khi bỏ marker. | 3 |
| CAND-18 | P2 | sdk | ⚠️ tái hiện | Write pump SQLite của SDK chết sau một batch lỗi: ghi sau đó xếp hàng vô hạn, FlushAsync/DisposeAsync treo khi tắt | SDK 1.6.2 Mezon.Net.Sdk.Caching.Sqlite/Internal/BatchWritePump.cs:64-120 | Handoff SDK: bắt lỗi theo batch, log, tiếp tục vòng lặp, hoàn tất flush target; Monze đặt timeout cho Flush/Dispose khi tắt. | 4 |
| DEF-06 | P2 | discovery | ⚠️ tái hiện | Discovery giới hạn 100 clan: cài mới với > 100 clan không bao giờ join phần còn lại | Hosting/MonzeBot.Connection.cs RefreshClansAsync; contract ListClanDescs | Handoff mezon-api/SDK: phân trang discovery; Monze đọc hết các trang. | 4 |
| CAND-15 | P2 | meeting | ⚠️ tái hiện | Đề xuất thua voice claim đóng context của meeting đang giữ phòng | Monze.Infrastructure/Persistence/Meeting/PostgresMeetingRepository.cs:85-120 | Chỉ đóng context sau khi giành được claim. | 5 |
| CAND-22 | P2 | roles | không chạy trong lần này | Rule role on-join áp cho mọi thành viên hiện có ở lần quét định kỳ (help nói chỉ người mới) | Monze.Application/Features/Roles/MonzeApp.Roles.cs:248 | Cần quyết định sản phẩm; nếu chỉ người mới thì quét định kỳ bỏ qua rule OnJoin. | 5 |
| CAND-23 | P2 | roles | không chạy trong lần này | Bot (kể cả Monze) nhận role tự động vì snapshot luôn IsBot = false | Features/Roles/SdkRoleGateway.cs:77 | Đọc cờ bot từ dữ liệu thành viên của Mezon và bỏ qua bot. | 5 |
| CAND-25 | P2 | connection | không chạy trong lần này | Clan bot được thêm vào sau khi khởi động không được đăng ký tới khi restart (owner bị coi như member) | Hosting/MonzeBot.cs:241; Features/Welcome/MonzeBot.WelcomeIngress.cs:65 | Khi bot được thêm vào clan (ClanUserAdded của chính bot) thì đăng ký clan và làm mới registry. | 5 |
| DEF-04 | P2 | welcome | ⚠️ tái hiện | Một worker welcome cho mọi clan: tra cứu chậm 4 s ở clan A làm welcome của clan B trễ 4 s | Features/Welcome/MonzeBot.WelcomeIngress.cs ConsumeWelcomeAsync; Hosting/MonzeBot.cs (SingleReader = true) | Phân vùng ingress welcome theo clan (như message ingress) hoặc chạy song song có giới hạn theo clan. | 5 |
| DEF-10 | P2 | rate-limit | 🔁 không còn tái hiện | Rate limiter lệnh theo đồng hồ tường: lùi giờ làm khoá người dùng, tiến giờ xoá giới hạn | Monze.Application/Commands/MonzeCommandRateLimiter.cs | Dùng TimeProvider.GetTimestamp (đơn điệu) cho cửa sổ rate limit. | 5 |
| WF-01 | P2 | welcome | ⚠️ tái hiện | Gửi welcome bị platform từ chối (error envelope) vẫn log 'đã gửi' và giữ claim: thành viên không bao giờ được chào | Features/Welcome/MonzeBot.WelcomeIngress.cs:138-142 (không kiểm ack); SDK 1.6.2 Generated/BaseMezonSocketClient.Realtime.g.cs SendChatMessageRtAsync (error envelope thành ack rỗng) | Coi ack không có message id là thất bại và nhả claim; SDK ném MezonApiException khi envelope phản hồi là Error. | 5 |
| CAND-01 | P2 | agent | ⚠️ tái hiện | Root JSON không phải object làm AgentEventPayload/Identity ném lỗi | Monze.Application/Features/Meeting/AgentEventPayload.cs; AgentEventIdentity.cs | Kiểm tra ValueKind trước khi đọc property. | 6 |
| CAND-05 | P2 | ai | ⚠️ tái hiện | Ngân sách AI không được hoàn khi provider lỗi (429/5xx/timeout/body rỗng, hỏng, quá lớn đều vẫn trừ token) | Monze.Application/Features/Ai/MonzeApp.Ai.cs CompleteAiAsync (ConsumeAiAsync trước khi gọi provider, không có đường hoàn) | Hoàn token (hoặc chỉ ghi nhận sau khi provider trả nội dung) khi CompleteAsync trả null. | 6 |
| DEF-01 | P2 | cache | ⚠️ tái hiện | RedisTimeoutException không phải RedisException: Redis chậm thoát khỏi fallback và cooldown | Monze.Infrastructure/Caching/MonzeRedis.cs (catch RedisException) | Bắt cả RedisTimeoutException/TimeoutException để fallback và vào cooldown. | 6 |
| DEF-12 | P2 | meeting | ⚠️ tái hiện | Mất room_ended thì phiên ở 'live' mãi; chu kỳ Agent sau trong cùng phòng tạo phiên live thứ hai (vi phạm meeting.live-per-voice) | Features/Meeting/MonzeBot.Agent.cs; Monze.Infrastructure/Persistence/Meeting/PostgresMeetingRepository.Context.cs CloseMeetingContextAsync/ExpireSuggestedAsync | Khi phòng voice trống hoặc phiên live quá hạn, chuyển phiên sang summary_pending và hỏi transcript; không mở phiên live mới khi phòng còn phiên live. | 6 |
| WF-04 | P2 | agent | ⚠️ tái hiện | Lấy transcript chạy ngay trong worker Agent duy nhất: một request transcript treo (timeout 30 s) giữ mọi event Agent và voice-empty; TR-01 hàng đợi Agent lên 150, room_started được áp dụng p99 ~9 s | Features/Meeting/MonzeBot.Agent.cs:66 (FetchSummaryAsync trong ProcessAgentAsync); Features/Meeting/MonzeBot.MeetingIngress.cs ConsumeAgentEventsAsync (một reader) | Khi room_summary_done chỉ đánh dấu summary_pending rồi để worker tóm tắt (đã có lease) lấy transcript, hoặc lấy transcript ngoài hàng đợi Agent với timeout ngắn. | 6 |
| CAND-09 | P2 | schedule | ⚠️ tái hiện | Lịch daily/weekly bỏ cuộc khi cửa sổ ngắn không có giờ địa phương hợp lệ (DST, ngày bị bỏ) | Monze.Domain/Features/Meeting/MeetingScheduleCalculator.cs; Features/Meeting/MonzeBot.ScheduledMeetings.cs:46 (hoàn tất lịch trước khi chạy lần đến hạn: lần đó cũng mất) | Tìm lần chạy kế tiếp ngoài cửa sổ ngắn. | 7 |
| CAND-04 | P3 | meeting | không tái hiện được | Tìm phòng voice giới hạn limit 100 | Features/Meeting | Chưa có kịch bản > 100 phòng voice. | — |
| DEF-02 | P3 | benchmark | ✅ đã sửa | Benchmark hot path đo nhầm lane | Monze.Benchmarks/MonzeHotPathBenchmarks.cs | Đã sửa trong commit benchmark. | — |
| CAND-11 | P3 | agent | ⚠️ tái hiện | AgentSummaryParser ném lỗi với phần tử khác kiểu mong đợi | Monze.Application/Features/Meeting/AgentSummaryParser.cs | Bỏ qua phần tử sai kiểu thay vì ném. | 6 |
| CAND-14 | P3 | ai | ⚠️ tái hiện | OpenAiCompatibleProvider ném lỗi với root/choice không phải object | Features/Ai/OpenAiCompatibleProvider.cs | Trả lỗi provider có kiểm soát. | 6 |
| WF-03 | P3 | agent | ⚠️ tái hiện | Transcript hợp lệ lớn hơn 512 KiB bị coi như chưa có tóm tắt ở mọi lần retry (không log cảnh báo): cuộc họp rất dài không bao giờ được tóm tắt, cuối cùng summary_failed | Infrastructure/Http/HttpPayloadLimits.cs:5 (TranscriptResponseBytes); Infrastructure/Agent/HttpTranscriptClient.cs:64-68 | Đọc transcript theo luồng chỉ lấy summary_data/participants/speech_durations (bỏ full_text khi quá lớn) hoặc nâng giới hạn có cấu hình; log cảnh báo khi vượt giới hạn. | 6 |
| WF-06 | P3 | ai | FAIL | Dừng Monze khi lệnh AI đang chạy: lệnh bị huỷ, OperationCanceledException bị nuốt nên thẻ 'Đang xử lý yêu cầu…' không bao giờ được cập nhật; lệnh bị đánh dấu uncertain nên không chạy lại sau khi khởi động | Hosting/MonzeBot.Commands.cs:148 (catch OperationCanceledException rỗng); Hosting/MonzeBot.CommandInbox.cs:82 | Khi lệnh AI bị huỷ lúc dừng, cập nhật thẻ loading bằng thông báo tạm lỗi (token riêng, timeout ngắn) hoặc cho lệnh đang chạy hoàn tất trong thời gian dừng. | 6 |
| CAND-02 | P3 | schedule | ⚠️ tái hiện | Lịch có tên không nhận giờ dạng H:mm | Monze.Domain/Features/Meeting/MeetingCommandParser.cs | Chấp nhận H:mm như lịch không tên. | 7 |
| CAND-03 | P3 | commands | ⚠️ tái hiện | `cancel +5` được chấp nhận (long.TryParse theo culture) | Monze.Domain/Features/Meeting/MeetingCommandParser.cs | NumberStyles.None + InvariantCulture. | 7 |
| CAND-08 | P3 | schedule | ⚠️ tái hiện | Hai tần suất được chấp nhận, tần suất sau thắng | Ui/Meeting/MeetingScheduleFormParser.cs | Từ chối input có nhiều tần suất. | 7 |
| CAND-10 | P3 | schedule | ⚠️ tái hiện | LocalSchedule.Describe dùng culture của host (tr-TR, ar-SA Hijri) | Monze.Domain/Features/Meeting/LocalSchedule.cs | Định dạng bằng InvariantCulture/vi-VN cố định. | 7 |
| CAND-12 | P3 | schedule | ⚠️ tái hiện | Ngày ISO có offset bị đổi sang múi giờ server trước khi lấy ngày | Ui/Meeting/MeetingScheduleFormParser.cs | Lấy phần ngày trước khi chuyển múi giờ. | 7 |
| CAND-13 | P3 | welcome | ⚠️ tái hiện | Một dấu `{` lạc làm token welcome kế tiếp không được thay | Features/Welcome/WelcomeMessageRenderer.cs | Quét token theo cặp ngoặc thay vì dừng ở `{` đầu tiên. | 7 |
| CAND-06 | P3 | performance | quan sát | Limiter quét O(MaxEntries) dưới lock khi bảng đầy | Monze.Application/Commands/MonzeCommandRateLimiter.cs | Evict theo hàng đợi thời gian. | 8 |
| CAND-07 | P3 | ai | ⚠️ tái hiện | AiBudget tràn int (class chưa được dùng ở production) | Monze.Domain/Features/Ai/AiBudget.cs | Dùng long hoặc xoá code chết. | 8 |
| CAND-16 | P3 | migrations | quan sát | Migration 001, 002, 004, 005, 008, 014, 015, 027 không idempotent nếu chạy lại ngoài migrator | Monze.Infrastructure/Persistence/Migrations | Không sửa migration đã áp dụng; migration mới phải idempotent. | 8 |
| CAND-24 | P3 | roles | không chạy trong lần này | RoleAssignFailed không bao giờ được gửi; UpdateRole lỗi được thử lại mỗi lần quét, không backoff | Features/Roles/SdkRoleGateway.cs:189 | Backoff và dừng với PermissionDenied; báo admin một lần. | 8 |
| CAND-26 | P3 | commands | không chạy trong lần này | Lệnh gửi trong DM (clan 0) bị nuốt: không trả lời, không log | SDK CommandService.cs:90,179; Hosting/MonzeBot.Commands.cs:48 | Log ở mức Debug và/hoặc trả lời hướng dẫn dùng trong clan (cần SDK hỗ trợ context DM). | 8 |
| CAND-27 | P3 | schedule | không chạy trong lần này | Lịch trùng khung giờ không bị kiểm tra; lịch once không có phòng thử lại mỗi 5 phút không báo | Features/Meeting/MonzeBot.ScheduledMeetings.cs:37 | Cảnh báo khi tạo lịch trùng; báo requester khi lịch bị trễ. | 8 |
| CAND-28 | P3 | observability | quan sát | Dừng bình thường ghi warning kèm TaskCanceledException; Monze không subscribe client.Log nên mất cảnh báo SDK; NpgsqlDataSource không được dispose cùng host | Hosting/MonzeBot.Connection.cs:197; Hosting/MonzeBot.cs:220; Hosting/MonzeHostComposition.cs:116 | Hạ log khi huỷ có chủ đích; nối client.Log vào ILogger; đăng ký data source bằng factory để DI dispose. | 8 |
| DEF-05 | P3 | memory | ⚠️ tái hiện | IMemoryCache SizeLimit 64 MiB đếm số entry (Size = 1), không phải byte | Hosting/MonzeHostComposition.cs (MemoryCacheOptions) | Ước lượng Size theo byte hoặc đổi SizeLimit thành số entry có chủ đích. | 8 |
| CAND-29 | P3 | startup | không chạy trong lần này | Khởi động kiểm schema một lần: PostgreSQL chưa sẵn sàng (khởi động cùng lúc, mạng chậm) làm host dừng ngay, chỉ hồi phục nếu có supervisor khởi động lại | Hosting/StartupSchemaValidator.cs StartAsync (ValidateAsync một lần, lỗi thì throw) | Thử lại kết nối/kiểm schema với backoff có giới hạn (vd. tới 5 phút) trước khi dừng; lỗi schema thật (thiếu migration, checksum sai) vẫn dừng ngay. | 9 |

1 P0, 5 P1, 22 P2, 21 P3 trong registry. Khối sửa tăng dần theo mức ưu tiên.

### Bằng chứng của lần chạy

| Defect | Nguồn | Kết quả | Số lần |
|---|---|---|---|
| CAND-01 | known-defect test | xfail | 2 |
| CAND-02 | known-defect test | xfail | 1 |
| CAND-03 | known-defect test | xfail | 1 |
| CAND-05 | known-defect test | xfail | 1 |
| CAND-07 | known-defect test | xfail | 1 |
| CAND-08 | known-defect test | xfail | 1 |
| CAND-09 | known-defect test | xfail | 1 |
| CAND-10 | known-defect test | xfail | 1 |
| CAND-11 | known-defect test | xfail | 1 |
| CAND-12 | known-defect test | xfail | 1 |
| CAND-13 | known-defect test | xfail | 1 |
| CAND-14 | known-defect test | xfail | 1 |
| CAND-15 | known-defect test | xfail | 1 |
| CAND-18 | known-defect test | xfail | 1 |
| CAND-19 | known-defect test | xfail | 2 |
| CAND-21 | known-defect test | xfail | 1 |
| DEF-01 | known-defect test | xfail | 1 |
| DEF-04 | known-defect test | xfail | 1 |
| DEF-05 | known-defect test | xfail | 1 |
| DEF-06 | known-defect test | xfail | 1 |
| DEF-08 | known-defect test | xfail | 1 |
| DEF-10 | known-defect test | xfail | 1 |
| DEF-12 | known-defect test | xfail | 1 |
| WF-01 | known-defect test | xfail | 1 |
| WF-02 | known-defect test | xfail | 1 |
| CAND-17 | component/C-OUTBOX-CLAIM | KNOWN_GAP | 1 |
| DEF-03 | load/L-V1-S1 | FAIL | 1 |
| DEF-03 | load/L-V1-S10 | FAIL | 1 |
| DEF-03 | load/L-V1-S100 | FAIL | 1 |
| DEF-03 | load/L-V1-S1000 | FAIL | 1 |
| CAND-20 | load/L-V3L-S1 | KNOWN_GAP | 1 |
| CAND-20 | load/L-V3L-S10 | KNOWN_GAP | 1 |
| CAND-20 | load/L-V3L-S100 | KNOWN_GAP_NOT_REPRODUCED | 1 |
| CAND-20 | load/L-V3L-S1000 | KNOWN_GAP_NOT_REPRODUCED | 1 |
| WF-07 | capacity/BP-2 | KNOWN_GAP | 1 |
| DEF-03 | capacity/BP-5-V1 | KNOWN_GAP | 1 |
| DEF-06 | capacity/BP-8-DEF06 | KNOWN_GAP | 1 |
| DEF-08 | chaos/AG-01 | KNOWN_GAP | 1 |
| DEF-08 | chaos/AG-02 | KNOWN_GAP | 1 |
| DEF-10 | chaos/CLK-02 | KNOWN_GAP_NOT_REPRODUCED | 1 |
| CAND-09 | chaos/CLK-03 | KNOWN_GAP | 1 |
| CAND-21 | chaos/MZ-01 | KNOWN_GAP | 1 |
| CAND-21 | chaos/MZ-02 | KNOWN_GAP | 1 |
| CAND-21 | chaos/MZ-07 | KNOWN_GAP | 1 |
| CAND-21 | chaos/MZ-08 | KNOWN_GAP | 1 |
| DEF-07 | chaos/PG-04b | KNOWN_GAP | 1 |
| DEF-07 | chaos/PG-06 | KNOWN_GAP_NOT_REPRODUCED | 1 |
| DEF-08 | chaos/PR-01 | KNOWN_GAP | 1 |
| DEF-08 | chaos/PR-06 | FAIL | 1 |
| WF-05 | chaos/PR-06 | FAIL | 1 |
| WF-06 | chaos/PR-06 | FAIL | 1 |
| DEF-01 | chaos/RD-02 | KNOWN_GAP | 1 |
| WF-04 | chaos/TR-01 | KNOWN_GAP | 1 |
| WF-03 | chaos/TR-04 | KNOWN_GAP | 1 |

`xfail` = lỗi đã biết vẫn tái hiện (test giữ hành vi đúng làm kỳ vọng); `xpass` = lỗi không còn tái hiện, cần gỡ marker cùng bản sửa.

## 11. Blocked / không chạy

- **live** — NOT_RUN: Không thuộc profile full-soak

## 12. Dọn dẹp và an toàn dữ liệu

| Kiểm tra | Kết quả |
|---|---|
| Container campaign còn lại | 0 |
| mezube-redis-1 không đổi | True |
| Guard kết nối | TestPostgres từ chối cổng 5432 và host không phải loopback; TestRedis từ chối cổng 6379 |
| Thư mục tạm (gồm file secret) | đã xoá sau khi dựng báo cáo |
| Lệnh dọn dữ liệu live | không chạy (campaign không bao giờ gọi cleanup-live-test.ps1) |

Quét redaction trên `raw/`: không có phát hiện.

## 13. Verdict G0–G4

| Gate | Nội dung | Verdict | Bằng chứng |
|---|---|---|---|
| G0 | Inventory và hygiene | **READY** | build=PASS, inventory=PASS |
| G1 | Logic xác định (unit, property, E2E offline) | **READY** | unit=PASS, property=PASS, e2e=PASS |
| G2 | PostgreSQL từ zero, migration, concurrency | **READY** | integration=PASS |
| G3 | Fault injection và chaos | **FAILED** | chaos=FAIL, capacity=PASS |
| G4 | Quy mô, soak 2 giờ và bằng chứng live | **FAILED** | micro=PASS, component=PASS, load=FAIL, soak=FAIL, live=NOT_RUN |

**Kết luận: NOT READY — failures.** G4 chỉ READY khi load V3 đạt, có soak đủ 2 giờ và có bằng chứng live.

## 14. Phụ lục

### Lệnh đã chạy

- `build`: `dotnet build Monze.slnx -c Release --no-restore --nologo` (exit 0)
- `inventory`: `dotnet test Monze.Tests/Monze.Tests.csproj -c Release --no-build --nologo --logger trx;LogFileName=inventory.Monze.Tests.trx --results-directory F:\projects\mezon\Monze\docs\test-artifacts\20261008-2034-full-soak-campaign\raw\testresults\inventory --blame-hang-timeout 10m --blame-hang-dump-type none --filter FullyQualifiedName~Monze.Tests.Inventory` (exit 0)
- `unit`: `dotnet test Monze.Tests/Monze.Tests.csproj -c Release --no-build --nologo --logger trx;LogFileName=unit.Monze.Tests.trx --results-directory F:\projects\mezon\Monze\docs\test-artifacts\20261008-2034-full-soak-campaign\raw\testresults\unit --blame-hang-timeout 10m --blame-hang-dump-type none --filter FullyQualifiedName!~Monze.Tests.Inventory --collect "Code Coverage;Format=cobertura" --settings F:\projects\mezon\Monze\tests\campaign.runsettings` (exit 0)
- `property`: `dotnet test tests/Monze.Tests.Property/Monze.Tests.Property.csproj -c Release --no-build --nologo --logger trx;LogFileName=property.Monze.Tests.Property.trx --results-directory F:\projects\mezon\Monze\docs\test-artifacts\20261008-2034-full-soak-campaign\raw\testresults\property --blame-hang-timeout 10m --blame-hang-dump-type none --collect "Code Coverage;Format=cobertura" --settings F:\projects\mezon\Monze\tests\campaign.runsettings` (exit 0)
- `integration`: `dotnet test tests/Monze.Tests.Integration/Monze.Tests.Integration.csproj -c Release --no-build --nologo --logger trx;LogFileName=integration.Monze.Tests.Integration.trx --results-directory F:\projects\mezon\Monze\docs\test-artifacts\20261008-2034-full-soak-campaign\raw\testresults\integration --blame-hang-timeout 10m --blame-hang-dump-type none --collect "Code Coverage;Format=cobertura" --settings F:\projects\mezon\Monze\tests\campaign.runsettings` (exit 0)
- `e2e`: `dotnet test tests/Monze.Tests.E2E/Monze.Tests.E2E.csproj -c Release --no-build --nologo --logger trx;LogFileName=e2e.Monze.Tests.E2E.trx --results-directory F:\projects\mezon\Monze\docs\test-artifacts\20261008-2034-full-soak-campaign\raw\testresults\e2e --blame-hang-timeout 10m --blame-hang-dump-type none --collect "Code Coverage;Format=cobertura" --settings F:\projects\mezon\Monze\tests\campaign.runsettings` (exit 0)
- `micro`: `dotnet F:\projects\mezon\Monze\Monze.Benchmarks\bin\Release\net10.0\Monze.Benchmarks.dll --filter * --artifacts "C:\Users\Huy Bui\AppData\Local\Temp\monze-campaign-20261008-2034-full-soak\bdn" --exporters json --campaign-artifact F:\projects\mezon\Monze\docs\test-artifacts\20261008-2034-full-soak-campaign\raw\micro\micro.json --baseline F:\projects\mezon\Monze\tests\perf-baseline.json` (exit 0)
- `component`: `dotnet F:\projects\mezon\Monze\tests\Monze.Campaign\bin\Release\net10.0\Monze.Campaign.dll component --artifacts F:\projects\mezon\Monze\docs\test-artifacts\20261008-2034-full-soak-campaign\raw --full` (exit 1)
- `load`: `dotnet F:\projects\mezon\Monze\tests\Monze.Campaign\bin\Release\net10.0\Monze.Campaign.dll load --artifacts F:\projects\mezon\Monze\docs\test-artifacts\20261008-2034-full-soak-campaign\raw --full` (exit 1)
- `k6`: `dotnet F:\projects\mezon\Monze\tests\Monze.Campaign\bin\Release\net10.0\Monze.Campaign.dll k6 --artifacts F:\projects\mezon\Monze\docs\test-artifacts\20261008-2034-full-soak-campaign\raw --full` (exit 0)
- `capacity`: `dotnet F:\projects\mezon\Monze\tests\Monze.Campaign\bin\Release\net10.0\Monze.Campaign.dll capacity --artifacts F:\projects\mezon\Monze\docs\test-artifacts\20261008-2034-full-soak-campaign\raw --full` (exit 0)
- `chaos`: `dotnet F:\projects\mezon\Monze\tests\Monze.Campaign\bin\Release\net10.0\Monze.Campaign.dll chaos --artifacts F:\projects\mezon\Monze\docs\test-artifacts\20261008-2034-full-soak-campaign\raw --full` (exit 1)
- `soak`: `dotnet F:\projects\mezon\Monze\tests\Monze.Campaign\bin\Release\net10.0\Monze.Campaign.dll soak --artifacts F:\projects\mezon\Monze\docs\test-artifacts\20261008-2034-full-soak-campaign\raw --minutes 120` (exit 1)

### Chỉ mục artifact

197 file trong `raw/`.

| File | Kích thước | SHA-256 (16 ký tự đầu) |
|---|---|---|
| bdn/CacheBenchmarks-report-full-compressed.json | 30,878 B | `f04aef218d9e2009` |
| bdn/IngressCallbackBenchmarks-report-full-compressed.json | 10,954 B | `acf9c09a0b8d2654` |
| bdn/MonzeCacheHotPathBenchmarks-report-full-compressed.json | 11,323 B | `29c29e557d5d409c` |
| bdn/MonzeCapacityBenchmarks-report-full-compressed.json | 41,810 B | `7a75905978f66bf3` |
| bdn/MonzeHotPathBenchmarks-report-full-compressed.json | 32,288 B | `61bf66383b468c64` |
| bdn/MonzeMetricsBenchmarks-report-full-compressed.json | 32,348 B | `f5fe76727c5013a2` |
| bdn/ParserBenchmarks-report-full-compressed.json | 39,003 B | `43f97907ee55bd25` |
| bdn/RateLimiterBenchmarks-report-full-compressed.json | 20,162 B | `14a7a3410092a6e2` |
| bdn/RendererBenchmarks-report-full-compressed.json | 26,897 B | `973e47e000b78215` |
| capacity/bp-1.json | 1,322 B | `99cb7b5001f567e6` |
| capacity/bp-2.json | 1,667 B | `e2747891aaf88792` |
| capacity/bp-3.json | 1,558 B | `42923744137fbcf3` |
| capacity/bp-4.json | 1,377 B | `aba66d9f873de9cf` |
| capacity/bp-5-v1.json | 1,202 B | `50a46793fa193a41` |
| capacity/bp-5.json | 1,146 B | `4167e96e2cdb108e` |
| capacity/bp-6.json | 1,091 B | `e6486a47ae84ab8f` |
| capacity/bp-7.json | 1,036 B | `78e314d3ed8ea52b` |
| capacity/bp-8-def06.json | 840 B | `612619ab8e446f7e` |
| capacity/bp-8.json | 776 B | `7c53f85576f5e53d` |
| chaos/ag-01.json | 5,622 B | `fddb7a27c0ff7371` |
| chaos/ag-02.json | 5,707 B | `b9c51a754cf09b94` |
| chaos/ag-03.json | 5,613 B | `cab291934ccf27d2` |
| chaos/ag-04.json | 5,601 B | `61e026258faab1e2` |
| chaos/ag-05.json | 5,898 B | `7383fcb08a7b7148` |
| chaos/ag-06.json | 5,860 B | `fa918ccc10b699d2` |
| chaos/ai-01.json | 6,170 B | `9ce2d557e3423170` |
| chaos/ai-02.json | 6,162 B | `4fd301d6f2ca229e` |
| chaos/ai-03.json | 6,180 B | `b80c360f92979ac5` |
| chaos/ai-04.json | 6,168 B | `40558d4b13186a80` |
| chaos/ai-05.json | 6,155 B | `3d8496871695c41c` |
| chaos/ai-06.json | 6,207 B | `663ea11906938662` |
| chaos/clk-01.json | 2,408 B | `ed6a86872a9572b3` |
| chaos/clk-02.json | 2,446 B | `af4dd3e2999c180c` |
| chaos/clk-03.json | 3,275 B | `8644b91693ec2d19` |
| chaos/clk-04.json | 2,389 B | `2abd9077118f0d16` |
| chaos/ev-01.json | 2,433 B | `698862185f3ca4f4` |
| chaos/ev-03.json | 2,425 B | `bb99a00793f5acd0` |
| chaos/ev-04.json | 2,414 B | `6dda2c9c93baf022` |
| chaos/mz-01.json | 2,770 B | `256cb1eb543f2c93` |
| chaos/mz-02.json | 2,798 B | `ded0de6652b506a4` |
| chaos/mz-03.json | 2,406 B | `e35f27466ad230aa` |
| chaos/mz-04.json | 2,436 B | `43df969448d61d0a` |
| chaos/mz-05.json | 2,409 B | `92c3d472a321bf35` |
| chaos/mz-07.json | 2,845 B | `9b402117ff3bd7f4` |
| chaos/mz-08.json | 2,806 B | `eb62baddfbf3528d` |
| chaos/pg-01.json | 2,783 B | `593f85abef73745f` |
| chaos/pg-02.json | 2,429 B | `b5d7b6911c3a36a6` |
| chaos/pg-03.json | 2,451 B | `66f39b357754a674` |
| chaos/pg-04a.json | 2,415 B | `44999b0efcc4df36` |
| chaos/pg-04b.json | 2,822 B | `a59660ef49348e4f` |
| chaos/pg-05.json | 2,451 B | `e9bc60580e9dcd1b` |
| chaos/pg-06.json | 2,861 B | `5b9663911296c8cd` |
| chaos/pg-07.json | 505 B | `860515326b34f479` |
| chaos/pg-08.json | 5,625 B | `c81e6c38b5a2cdda` |
| chaos/pr-01.json | 6,309 B | `15277a4a78ad5cbf` |
| chaos/pr-06.json | 6,460 B | `0eea2dc016a634a1` |
| chaos/pr-07.json | 6,520 B | `6144dd40956fefdd` |
| chaos/rd-01.json | 3,985 B | `f3f1bc8804891d14` |
| chaos/rd-02.json | 4,027 B | `754552877f76ceed` |
| chaos/rd-03.json | 4,007 B | `0167a786d0021120` |
| chaos/rd-04.json | 4,010 B | `7060ecd32ae82067` |
| chaos/rd-05.json | 4,007 B | `7789358659ba3c89` |
| chaos/rd-06.json | 3,204 B | `a575010af1a8c129` |
| chaos/tr-01.json | 5,626 B | `c7c0cf40888a8c89` |
| chaos/tr-02.json | 5,629 B | `35198d66d44e7d22` |
| chaos/tr-03.json | 5,587 B | `eab2706c81bb32c8` |
| chaos/tr-04.json | 5,927 B | `2f05d80ed9b1597c` |
| chaos/tr-05.json | 5,659 B | `f6d4cf75652c5eca` |
| cleanup.json | 410 B | `594f2a3223273751` |
| component/c-inbox.json | 927 B | `599de4f6319c5f5f` |
| component/c-migrate.json | 1,345 B | `cc7a2c0660c4a350` |
| component/c-outbox-claim.json | 3,479 B | `d8f1d2a7aee715ba` |
| component/c-pool.json | 882 B | `3aa1badd0434f35d` |
| component/c-redis.json | 1,394 B | `ef0c1126307bf1b9` |
| component/c-sched.json | 1,403 B | `b2ace0a98e95c2ba` |
| component/c-skiplocked.json | 579 B | `07c8d290587e15fc` |
| component/c-sqlite.json | 3,358 B | `e3cd7fd9adc47cac` |
| component/c-summary-lease.json | 1,754 B | `6dcd7d9445e31813` |
| coverage/e2e.Monze.Tests.E2E.0.cobertura.xml | 1,952,523 B | `704e465d83735577` |
| coverage/e2e.Monze.Tests.E2E.1.cobertura.xml | 1,952,523 B | `704e465d83735577` |
| coverage/integration.Monze.Tests.Integration.0.cobertura.xml | 1,941,475 B | `55190971eee66aed` |
| coverage/integration.Monze.Tests.Integration.1.cobertura.xml | 1,941,475 B | `55190971eee66aed` |
| coverage/property.Monze.Tests.Property.0.cobertura.xml | 1,945,455 B | `b10839ed25f014f0` |
| coverage/property.Monze.Tests.Property.1.cobertura.xml | 1,945,455 B | `b10839ed25f014f0` |
| coverage/unit.Monze.Tests.0.cobertura.xml | 1,947,371 B | `e71715144699d2df` |
| coverage/unit.Monze.Tests.1.cobertura.xml | 1,947,371 B | `e71715144699d2df` |
| k6/k6-crosscheck.json | 1,155 B | `c59c6d99143ebbd0` |
| ledgers/integration.concurrency-agent-inbox.jsonl | 50,978 B | `cb85389367018f45` |
| ledgers/integration.concurrency-ai-budget.jsonl | 25,376 B | `c2f0e2c44aab1773` |
| ledgers/integration.concurrency-command-inbox.jsonl | 51,380 B | `1f2e9510cecd6298` |
| ledgers/integration.concurrency-interaction-inbox.jsonl | 52,184 B | `34cb7f058baba082` |
| ledgers/integration.concurrency-outbox.jsonl | 562 B | `42da4e287629a495` |
| ledgers/integration.concurrency-schedules.jsonl | 566 B | `d5ea1723263858ab` |
| ledgers/integration.concurrency-summary-lease.jsonl | 51,380 B | `b594c08fa72e3210` |
| ledgers/integration.concurrency-voice-suggest.jsonl | 51,380 B | `46d8de1becfdeaeb` |
| ledgers/integration.concurrency-welcome-cas.jsonl | 50,978 B | `02ea693612fb8800` |
| ledgers/integration.concurrency-welcome-delivery.jsonl | 51,983 B | `2ffbfb455e3b1fd2` |
| ledgers/integration.migration-idempotency.jsonl | 7,927 B | `96cd34d483bf1033` |
| ledgers/integration.migration-split-points.jsonl | 8,336 B | `fb9d7206bb7a5c9d` |
| ledgers/integration.twin-model.jsonl | 596,857 B | `3320f9fb853d6912` |
| ledgers/known-defects.jsonl | 4,896 B | `2991c84b9c143f03` |
| ledgers/property.G01.jsonl | 2,733,427 B | `13064b8d9f51e07d` |
| ledgers/property.G01b.jsonl | 2,631,201 B | `acf83e44e7cc47a1` |
| ledgers/property.G02.jsonl | 3,089,078 B | `7d1fd9af29d073f5` |
| ledgers/property.G03.jsonl | 3,022,843 B | `0fe78ad44c9d9c7f` |
| ledgers/property.G04.jsonl | 2,961,371 B | `0980b81d3f6cd6c6` |
| ledgers/property.G05.jsonl | 2,678,055 B | `f369f94cf4344fa4` |
| ledgers/property.G06.jsonl | 3,178,934 B | `c0479260310fb936` |
| ledgers/property.G07.jsonl | 2,398,674 B | `3bcea943d1e44175` |
| ledgers/property.G07b.jsonl | 2,537,251 B | `c52271bc9c482e3b` |
| ledgers/property.G08.jsonl | 224,265 B | `bd9d7e2cf6d99c7d` |
| ledgers/property.G09.jsonl | 2,465,515 B | `9b10a91511e5b597` |
| ledgers/property.G09b.jsonl | 2,494,304 B | `9ce4d30a9f947a89` |
| ledgers/property.G10.jsonl | 2,545,381 B | `fdb82a09c2b8fa13` |
| ledgers/property.G10b.jsonl | 2,676,046 B | `1aae8b89c20de81d` |
| ledgers/property.G11.jsonl | 2,393,612 B | `1de6466c478aabcc` |
| ledgers/property.G12.jsonl | 2,720,598 B | `0f0fb60d874153be` |
| ledgers/property.G13.jsonl | 2,524,816 B | `778af49813dbbba1` |
| ledgers/property.G14.jsonl | 1,266,569 B | `f72d91f8720980af` |
| ledgers/property.G14b.jsonl | 69,069 B | `4ca8a4fd2018a4fa` |
| ledgers/property.G15.jsonl | 2,422,380 B | `4044705b2f15b43c` |
| ledgers/property.G15b.jsonl | 47,556 B | `320aa18269fa70d2` |
| ledgers/property.G15c-random.jsonl | 525 B | `a557ca4aa6a2265d` |
| ledgers/property.G15c-sequential.jsonl | 537 B | `e7ac5da6a4691224` |
| ledgers/property.G15c-snowflake.jsonl | 534 B | `4fb746dceab1c4b8` |
| ledgers/property.G16.jsonl | 3,165,849 B | `7dcc7d45ec4dec98` |
| ledgers/property.G17.jsonl | 3,068,697 B | `f2821edc63ad8d3b` |
| ledgers/property.G17b.jsonl | 2,687,683 B | `803dd8589d2550f4` |
| ledgers/property.G18.jsonl | 2,533,809 B | `61a2021848ab2e1f` |
| ledgers/property.G18b.jsonl | 1,066,559 B | `4af0bf70cc38eeda` |
| ledgers/property.G19.jsonl | 2,323,644 B | `6adf888f91f62206` |
| ledgers/property.G20.jsonl | 2,460,885 B | `e700e71d6bbb51a2` |
| ledgers/property.G21.jsonl | 2,491,957 B | `7be4ea2ff6f31423` |
| ledgers/property.G21b.jsonl | 457,608 B | `27599b70bd4a7b9b` |
| ledgers/property.G22.jsonl | 2,282,311 B | `69288c2346a6758d` |
| ledgers/property.G23.jsonl | 14,495 B | `8d3ff6f4f95469a4` |
| ledgers/property.G23b.jsonl | 2,243,520 B | `b47df160ef524818` |
| ledgers/property.G23c.jsonl | 2,405,537 B | `983529049496c5f6` |
| ledgers/property.G24.jsonl | 2,587,982 B | `f1d6d55430815dfc` |
| ledgers/property.G25.jsonl | 1,493,715 B | `7892dd00c1d6c0bb` |
| ledgers/property.G26.jsonl | 2,448,554 B | `fde1503cbd4bd602` |
| ledgers/property.G26b.jsonl | 429,759 B | `bee26e0a2a3ca4dc` |
| ledgers/property.G26c.jsonl | 2,405,695 B | `eef60b9715420bfa` |
| ledgers/property.G27.jsonl | 2,276,248 B | `65a7005b9ae48589` |
| ledgers/property.G28.jsonl | 2,236,465 B | `c64fc4a75e0cb171` |
| ledgers/property.G29.jsonl | 1,323,024 B | `213ee4635466ff04` |
| ledgers/property.G30.jsonl | 1,257,602 B | `e639c33992e434a4` |
| ledgers/property.G31.jsonl | 1,119,697 B | `03cd7a632e35137d` |
| load/l-v1-s1.json | 4,422 B | `460923f8c75679ea` |
| load/l-v1-s10.json | 4,431 B | `e9fa9a66583d62c0` |
| load/l-v1-s100.json | 4,456 B | `12f9c5d56e363af5` |
| load/l-v1-s1000.json | 4,460 B | `caad99ee573ca0a8` |
| load/l-v3-s1.json | 4,316 B | `7d1a482fc53db91b` |
| load/l-v3-s10.json | 4,314 B | `b097baf1f6903c0e` |
| load/l-v3-s100.json | 4,316 B | `4dc34271c152a502` |
| load/l-v3-s1000.json | 4,312 B | `58087c82eee33e66` |
| load/l-v3l-s1.json | 4,374 B | `04cf15d3420d4bb1` |
| load/l-v3l-s10.json | 4,377 B | `49a50ea944c1219a` |
| load/l-v3l-s100.json | 4,380 B | `057334882a78801e` |
| load/l-v3l-s1000.json | 4,382 B | `bf99d18236ec0d36` |
| logs/build.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/build.log | 1,450 B | `035b66c7ea4c8ecd` |
| logs/build.restore.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/build.restore.log | 82 B | `2ad9967f2280d019` |
| logs/capacity.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/capacity.log | 537 B | `45ec6fd46fae08f5` |
| logs/chaos.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/chaos.log | 2,422 B | `ba5f246f60b9e967` |
| logs/component.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/component.log | 548 B | `0e620aa1a04cee34` |
| logs/e2e.Monze.Tests.E2E.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/e2e.Monze.Tests.E2E.log | 748 B | `c5b7e8c9a16424ee` |
| logs/integration.Monze.Tests.Integration.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/integration.Monze.Tests.Integration.log | 800 B | `d81434d35ca0cf2f` |
| logs/inventory.Monze.Tests.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/inventory.Monze.Tests.log | 531 B | `fa55926d7c2183f0` |
| logs/k6.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/k6.log | 36 B | `1652898ad0cb5659` |
| logs/load.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/load.log | 694 B | `da2206f1b883e9aa` |
| logs/micro.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/micro.log | 159,337 B | `3779d0e2abd47673` |
| logs/property.Monze.Tests.Property.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/property.Monze.Tests.Property.log | 779 B | `6471546d60682c43` |
| logs/soak.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/soak.log | 205 B | `fa431608cf79913c` |
| logs/unit.Monze.Tests.err.log | 0 B | `e3b0c44298fc1c14` |
| logs/unit.Monze.Tests.log | 724 B | `4d03975de367bd16` |
| manifest.json | 8,211 B | `76e8aa42aadb59a6` |
| micro/micro.json | 4,745 B | `2d162babeb6aa8e3` |
| soak/soak-120m.json | 3,042 B | `e4ec9dff55b7cb2d` |
| traceability/trace-map.json | 21,863 B | `da4eff0159c0482a` |
| trx/e2e.Monze.Tests.E2E.trx | 90,272 B | `4ee3094014daf9bc` |
| trx/integration.Monze.Tests.Integration.trx | 87,733 B | `b11656779771eb4d` |
| trx/inventory.Monze.Tests.trx | 22,356 B | `23fbb395ba472d87` |
| trx/property.Monze.Tests.Property.trx | 73,367 B | `12aa4dd66d4a0f15` |
| trx/unit.Monze.Tests.trx | 354,138 B | `3fac440e92b0b441` |
