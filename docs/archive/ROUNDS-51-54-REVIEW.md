# 第 51～54 輪全面審查（2026-10-15）

> 用途:回答「這四輪哪些對準目標、哪些偏離、怎麼導正」。這是過程記錄,現況以 `docs/PRTG-SPEC.md` 為準;導正作業的執行契約在 `docs/FEEDBACK-54-PLAN.md`(重寫版)。
> 範圍:commit `0c1e0c3d`(50 輪收尾)→ `feature/prtg-feedback-54` HEAD;51 輪 commit 日期 9/23–9/24 在 9/20 之後,列入。52、53 輪由 Codex(gpt-6-luna)為主代理開發。
> 方法:逐輪讀 PLAN(53 輪只讀契約段,§18 流水帳跳過)、現況規格、三組程式事實核對(對應/管線/規則、53–54 輪新增機制、驗收基建與測試)。本機為 macOS,全部為靜態審查,未建置或執行測試。

## 1. 審查基準(2026-10-15 與使用者定案)

**北極星**:以 NetIQ 主機清單為準,用 IP(找不到才用 hostname)對應 PRTG 裝置,取得 CPU／記憶體／磁碟／ping 數值,透過**過往趨勢提前發現未來可能的問題**;每筆風險有憑有據、不誤報;PRTG 風險可單獨成立也可搭配 NetIQ;設計規模 3,000 主機／15,000 sensor／180 日;SQLite 與 SQL Server 都支援。

**非目標**:即時告警(本服務資料最新也是前一日)、處理當下事故、鏡像整台 PRTG、NetIQ 清單以外的主機。

**篩選問句**(每項規劃與程式都過一次):對準目標?是規格還是證據流水帳?有更簡單的做法?操作者要懂內部機制才能用?驗收能客觀判定、分母清楚?

## 2. 各輪變更量

| 輪 | 區間 | Core/Web .cs | JS/cshtml | 測試 | scripts | docs | 新 .cs |
|---|---|---|---|---|---|---|---|
| 51 | 0c1e0c3d..d39c887a(9/23–24) | +3,604 | +1,228 | +5,185 | 0 | +317 | 18 |
| 52 | d39c887a..beda825b(9/29–10/01) | +3,730 | +665 | +2,659 | 0 | +1,164 | 24 |
| **53** | beda825b..a69fedd7(10/03–10/14) | **+48,893** | +3,297 | **+60,073** | **+10,470** | **+15,234** | **96** |
| 54 | a69fedd7..HEAD(10/13–10/15) | +6,711 | +632 | +7,553 | +8,124 | +779 | 12 |

現況:檔名含 Prtg 的 .cs 共 58,894 行,佔 Core＋Web 總行數 39%;`prtg-admin.js` 5,003 行;PRTG 相關 API 端點 62 個(全站 278);`SystemSettings` 的 Prtg 鍵 25 個;PRTG 測試 213 檔 84,070 行。

## 3. 逐輪裁定

### 51 輪(磁碟趨勢、快照診斷、局部回填、驗證工作台)

| 項目 | 裁定 | 理由 |
|---|---|---|
| A 快照每小時 coverage 診斷 | **保留** | 回答「資料有沒有進來」,對準目標 1 |
| B 逐顆 28 日 readiness(X／28 天、缺小時 bitmask) | **保留** | 趨勢規則的資料門;UI 直接告訴管理者還差幾天 |
| B 語意驗證工作台(抽查 PRTG 頻道/單位、人工確認 `Unit=null`、證據失效) | **簡化** | 要求管理者逐顆對照 PRTG 畫面確認單位,是操作負擔;改為「依 sensor type／channel unit 自動判定,判不出就不評估」 |
| C 指定主機／日期回填 | **保留** | 補資料缺口的唯一入口 |
| D `disk_free_trend` 規則(預設停用、730 日試算、分批 10 萬點) | **保留並擴充為共用引擎** | 唯一已實作的趨勢型規則;記憶體／CPU／ping 趨勢沿用同一引擎 |
| E 使用效果面板(finding→案件→交辦→靜音計數) | **保留** | 最低成本的「有沒有用」觀測 |
| F 五種就緒狀態徽章、至少一顆語意確認才能啟用規則 | **簡化** | 啟用條件改為「至少一顆 sensor 達 28 日可用資料且單位可判」,不需人工確認 |

### 52 輪(全鏈體檢、NetIQ 主體修訂)

| 項目 | 裁定 | 理由 |
|---|---|---|
| A 啟用開關與數值範圇分離、首次啟用動線摘要 | **保留** | 直接改善操作 |
| B 白名單語意統一、快照批次交易冪等、snapshot journal(v2 checksum、writer lease) | **保留** | 資料不丟不重,對準企業級可靠 |
| B2 可信前導狀態 / `PrtgCoveredStateTimeline` / `PrtgSensorTimelineCollector` | **保留但降級** | 狀態型規則(down／warning)需要日初前導狀態才不漏;但 timeline 的「解析版本／fingerprint／授權」疊加(53–54 輪)拆掉,只留逐 sensor 時間軸 |
| C1 PRTG-only 獨立問題 → 9/30 撤回 → 改為「只補充 NetIQ 主機日」 | **再次推翻**:10/15 定案 PRTG 可單獨成立風險 | 三層父紀錄閘(`CanSupplementWithPrtg`、`AttachPrtgFindings` 查無即 false、記憶體回讀)須解除;載體用 `LogSource=Prtg` 的 `DailyAnalysisRecord`,涵蓋統計以 `LogSource` 區分 |
| D1 同日配對降為弱佐證、不升風險 | **保留** | 防誤報 |
| D2／Q3 ACK 與 14 日長期 Down 不降風險 | **保留** | 正確 |
| Q1 歷史有效風險投影、待重評 | **簡化** | 保留原始／有效風險分開的欄位,刪「待重評」分頁與 `PrtgRiskReviewHostedService` 背景修訂——歷史弱佐證的一次性降級用一次 migration 完成 |
| Q2／N1／S4 事故通知版本、per-recipient urgent outbox、SMTP 接受狀態 | **PRTG 部分移除** | PRTG 不即時通知;NetIQ 既有高風險即時信不動 |
| A03 來源變更的「延續／新來源／無法確認」三選一與影響預覽 | **簡化** | 改 URL 就重算對應、數值歷史照舊;不需要「來源世代」概念 |
| A04 作用範圍預覽(有效對應∩監看∩規則∩試點) | **保留** | 「規則啟用了但實際套用零顆」是真問題 |
| A05 作業採用版本／期望版本雙欄 | **移除** | 管理者看不懂、也用不到;停用後安全點停止(N2)保留即可 |
| S7／P01–P07／G1–G8／F1–F4／U01–U09／R01–R08／T01–T06／V01–V10 驗收矩陣 | **全部廢止** | 這是膨脹起點;改用重寫版 PLAN 的目標導向驗收 |
| `PrtgAcceptanceController` 四基準比對、事故查證表單、驗收證據包 | **移除** | 「比原生 PRTG 早多少分鐮」的量測工具不是產品功能;「有用／誤報」標記改走既有問題處理狀態 |

### 53 輪(「完全體」)

| 項目 | 裁定 | 理由 |
|---|---|---|
| trusted sampling profile／explicit channel binding／raw proof／qualification job(720h、每顆一次 attempt)／channel discovery／profile refresh(23h)／authority context fingerprint／authority repair | **移除** | 為了「證明每顆 sensor 的數值可信」蓋的整套機制;`PrtgResourcePeriodConsumer` 四道閘缺一就等待,管理者要逐顆綁定核驗。替代:對應有效＋品質可用(ok 或 coverage≥75)＋單位可判 |
| capacity admission／pilot／五份新鮮成功快照／25% 餘裕／request-shape fingerprint 含 build MVID | **移除** | 快照服務的「准入」在本機測不出、現場也不需要;保留 table 2/秒、historic 5/60、在途 4 的固定配額與退避 |
| 資源兩小時壓力(CPU／記憶體≥90%、磁碟≤10%)與 hint→formal 試算授權 | **移除規則,移除授權** | 兩小時壓力是即時狀態不是趨勢(非目標);「同版試算授權 24h」是操作負擔 |
| formal episode／pressure mode store／mode replay hosted service | **移除** | 隸屬上一項 |
| formal mail claim／fence／reservation、`PrtgUrgentFacts` | **移除** | PRTG 不即時通知 |
| `PrtgSilentPresenceFormalConsumer`(完整原生 inventory＋timeline 才判 silent) | **簡化** | silent 回到 51 輪前的定義:availability Up 而其他 sensor 全 Unknown,以鏡像狀態判;不需 resource qualification |
| NetIQ 回執兩步提交、父列 keyset 每頁 16 | **保留** | 正確性 |
| build identity(PowerShell stamp、manifest、全量回填 verified 閘、artifact identity／descriptor fence) | **移除閘,簡化版本字串** | 每次建置依賴 PowerShell;唯一 runtime 消費是擋全量回填。保留 `AssemblyInformationalVersion` 由 MSBuild 原生 `SourceRevisionId` 供應 |
| 診斷匯出 V2 串流、匯入分片 4MiB、256MiB 帳本、transfer sessions／chunks 兩張表 | **簡化** | 匯出保留(有界串流是對的);匯入分片續傳、帳本、7 天保留清理移除——診斷匯入不是日常功能 |
| `PrtgCompatibilityProbe`(2,746 行安全 JSON、b1–b100 別名、`Verify-PrtgProbeEvidence.ps1`) | **移除** | 51 輪前的「完整環境探測」文字輸出已夠用 |
| 期間 finding 融合至 NetIQ 主機日、風險取較高不相加 | **保留契約** | 正確,但 consumer 換成簡化版 |
| 郵件 Date Format 六格式嚴格解析、message-time fingerprint、單調 RawAuthorityRevision | **簡化** | 保留「依帳號 Date Format 解析訊息時間、解析失敗計數出聲」;fingerprint／authority revision 移除 |
| 範圍清除(`PrtgScopePurge`)父子所有權、preview 5 分鐘、每批撤權重驗、`lf_prtg_scope_purge_jobs` | **簡化** | 保留 CheckScope／CheckShrink 保護與人工預覽確認;作業所有權、撤權重驗、耐久進度表移除 |
| 27 項 E01–E10 TODO、AC01–AC22、U53-01–10、Q1–Q6、222 固定案例、53 條缺口、coverage-inventory、user-matrix、R54／C54 | **全部廢止** | 驗收制度本身是負擔;`scripts/round53-user-matrix/`、`scripts/prtg-workload/`、`docs/FEEDBACK-53-ACCEPTANCE.json` 刪除 |
| Q1「3,000 台容量」→ 54 輪 §4.12 改程式審查＋小型驗證 | **保留 §4.12 方向** | 寫進 README 的「開發測試與驗收準則」有效 |

### 54 輪(剩餘實作與驗收)

| 項目 | 裁定 |
|---|---|
| 訊息時間授權接線(B1／B2)、磁碟核驗接線(C1) | 隨 53 輪對應機制一併簡化 |
| 有界讀取(D1 十段) | `lf_blobs` JSON store 砍掉後大半自然消失;留下的 blob 讀取保留長度上限即可 |
| 回退控制工具(`scripts/round54/rollback`)、容量預檢 | 移除 |
| 維護權限子矩陣(M54-*)、serverAdmin 無業務明細 | 保留權限分離的行為;移除矩陣驗收腳本 |
| §4.12 測試準則 | 保留 |

## 4. 程式審查發現(跨輪)

1. **數值→finding 的閘過多**:`PrtgResourcePeriodConsumer.cs:233–260` 四道閘(policy.Ready→strategy.Ready→profile→resolver.Ready)+ identity Active + 範圍。任何一道未就緒就「等待」,畫面顯示「證據不足」,管理者無從下手。
2. **對應缺 hostname 退路**:`PrtgHostMapper.MapForDate` 只比 IP;NetIQ 主機有 `HostName`,PRTG device 的 `host` 欄可能是 DNS 名稱、另有 device 名稱,兩者都沒拿來比。
3. **趨勢規則只有磁碟一條**:八條規則中四條狀態型(當日事件)、三條兩小時壓力(即時)、一條趨勢(`disk_free_trend`)。記憶體攀升、CPU 長期惡化、ping 延遾惡化沒有。
4. **父紀錄閘三層**:`DailyAnalysisRecord.CanSupplementWithPrtg`、`EfAnalysisRecordStore.AttachPrtgFindings` 查無即 false、`HostDayPostProcessor` 回讀;沒有 NetIQ 紀錄時 finding 只留在登錄簿等補追加(`PrtgSupplementReplay`)。`lf_top_issues.record_id` 非空外鍵到 `lf_daily_records`,所以 PRTG 獨立風險需要一筆 `DailyAnalysisRecord` 承載。
5. **JSON 塞 lf_blobs**:mode／binding／profile／qualification／episode／claim／identity／disk evidence／historic admission／channel discovery 全走 `EfJsonBlobStore`,再用 `ReadBoundedWithVersion` 與 262,144／33,554,432 字元上限防爆。關聯資料用 blob 存,查詢只能整份載入,是容量設計的反向。
6. **建置依賴腳本**:`Directory.Build.targets` 在 CoreCompile 前執行 `Write-BuildIdentityStamp.ps1`,發佈後 `dotnet run` manifest 專案;Mac／Linux 需 pwsh。`LogForesight.Tests.csproj:26` 把 `scripts/prtg-workload/QualificationCoverage.cs` 編進測試。
7. **PRTG 即時郵件**:`MailNotificationService` 的 `PrtgUrgentFacts`、formal claim／fence、`TryReserveRenderedMailContributions`;`PrtgOperationsController` 的 retry 會觸發 `NotifyAfterRunAsync`。
8. **測試假通過溫床**:25 處以上讀 `prtg-admin.js`／`Prtg.cshtml` 原始碜做 `Assert.Contains`;`PrtgFormalRuleCaseManifestTests` 測 fixture JSON 結構;`PrtgRollbackReplyGraphFixtureTests`、`PrtgQualificationCoverageTests` 測腳本。
9. **設定過多**:25 個 Prtg* 設定鍵,其中資源守門 8 個、trusted sampling／pilot 相關多個是本輪要移除機制的消費端,移除後要同步刪鍵(紅線:有設定無行為)。
10. **DI 不一致**:`PrtgDailyPipeline` 直接 `new PrtgFetchService`(`:112`),`PrtgHostMapper` 無 DI 註冊——測試替身與正式路徑分岔的來源,順手修。
11. **保留且健康的部分**:`PrtgTablePager`(三道停止條件、頁數上限、messages 以 objid＋時間為鍵)、快照 journal、`PrtgScopeDevices.Compute` 四項聯集、`PrtgFindingMapper`／`PrtgCrossDay`／`PrtgCorroboration`、資源守門(`PrtgResourceGuard`)、人工對應與 IP 排除。

## 5. 根因

52 輪開始驗收清單只加不減(U／R／T／P／G／S／V／Q／N→AC／E／U53／R54／C54),每次審查都新增「必須證明」的項目;53 輪為了在無法連現場的條件下「證明可信」,把信任問題轉成機制(profile／proof／authority／qualification),又把開發期驗收基建做進產品與 repo。每一輪都缺「這項是否對準目標、是否可刪」的閘。重寫版 PLAN 的每個批次因此都要回答「刪掉後誰依賴、怎麼處理」,且驗收只認目標導向的行為。
