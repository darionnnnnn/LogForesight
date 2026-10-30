# 第 59 輪規劃：全專案體檢

> 狀態：**第 59 輪（批次 A～G）實作完成、待換模型體檢**（2026-10-30），分支 `feature/feedback-59`；全量 **6,849 通過／10 略過**（含收尾修正兩條與〈實作完成核對〉補齊六項）；第 60、61 輪為草案，尚未開工（見〈作業總覽〉與〈執行紀錄〉）
> 基準：dev@03dc3873（本機全量 **6,550 通過／10 略過**，32 分 27 秒；CLAUDE.md 寫 6,549，差 1 筆，收尾時對齊）
> 來源：使用者要求以 subagent 對全專案程式碼與文件做五面向總體檢，規劃一至多輪優化
> 體檢方式：11 個唯讀 subagent 分面向體檢（原始報告在 session scratchpad `findings-*.md`）→ 主代理親核高嚴重度 → 18 項待決逐項回到程式碼複核 → 三個視角審查員（使用者／管理者、程式與 SOLID、上帝視角跨批影響）對照程式碼挑缺漏 → 回寫本文。
> 讀取紀律：排除 `docs/archive/`、`.claude/worktrees/`、bin／obj。
> 實作方式：內建 Agent（Sonnet 5.5）一次一個改檔代理、一批一個規格檔；C、E 規格主代理親寫並對驗收測試做突變；主代理獨立重驗。
> 驗收原則：每條改動的驗收都是主代理能自己執行並判定的三種之一——(T) `dotnet test --filter` 或 node 跑出紅綠、(G) grep／反射斷言、(D) 文件逐句比對。寫規格時每個篩選詞先實跑確認命中 >0。

## 作業總覽（委派）

- **委派模型**：Claude Code 內建 Agent，`model: sonnet`（Sonnet 5.5），整輪不換；一次只派一個改檔代理（同一 checkout）。小型機械掃描／對帳可用 Haiku 5.5 唯讀。
- **規格檔**：`.gemini-tasks/r59/<階段>.md`（`.git/info/exclude` 已排除，用完即刪）。每階段一檔，只含目標、契約、白名單、驗收、停止條件；不讓執行端看整份 PLAN。
- **規格寫作時機**：批次 A 規格隨定案一起寫；其後各批在前一批驗收通過後才寫，因為 C／D／F 的規格要標出前批已改的區段（避免重寫或覆蓋）。
- **每段驗收**：主代理親跑該段定向篩選（含新測試）、grep 斷言、對驗收測試做規格指定的突變（備份還原＋`touch`，禁用 `git checkout` 還原未提交檔）；通過才 commit 並 push 到 `feature/feedback-59`。
- **每批結束**：跑該批合併定向篩選；整輪結束跑全量（基準 6,550／10）。

### 階段拆分（第 59 輪）

| 批次 | 階段（一階段＝一個可獨立驗證的機制，實作與測試同段） |
|---|---|
| A | A1 `RequireViewAll` 與三控制器（A-1）／A2 display 輕量讀取＋help ask 授權（A-2、A-3）／A3 帳號正規化與登入長度（A-4）／A4 returnUrl（A-5）／A5 兩快取版本戳＋interpret 鍵（A-6、A-7）／A6 Sentinel source 白名單＋權限異動 API 能力（A-8、A-9）／A7 文件（WEB-SPEC §7.1、§9.5、§9.9c、§9.9f；PRTG-SPEC §11；CLAUDE.md 快取紅線） |
| B | B1 `ColumnClamp` 與 store 安全網、刪兩份 Truncate／B2 入口截斷（簽章與 SourceKeyOf）＋UpsertFirstSeen catch＋issue_key 拒寫／B3 rule_id 放寬／B4 workflow_ 清除／B5 兩個 Prune 分批＋LastRunDate／B6 IsSqlite＋QueryPage ThenBy／B7 backfiller 版本條件＋BatchedPrune 零進展＋IsTransient／B8 文件與 BACKLOG |
| C | C1 前置掃描退場（主代理親寫規格＋突變）／C2 移除 evidence-aligned＋退役徽章／C3 滅跡＋降級旗標＋時間偏移／C4 Linux 關聯／C5 Normalize 與降級路徑／C6 SlowTrend 殘留降噪／C7 報告規則反查＋Suppressed＋EventKey／C8 prompt 依 OS／C9 文件 |
| D | D1 重標入 try／D2 排程判準常數與擴充／D3 Tick 重排＋OCE catch／D4 迴圈計數＋共用 AIService＋ChatJson 中止／D5 版本回退／D7 Sentinel 取消＋Unreadable／D8 Stuck 持有判定／D9 文件／D6 gate 必填（最後） |
| E | E1 IssueOwnerStore.Update／E2 報表靜音口徑／E3 刪 BuildCase 與守門／E4 AttachNewDay／E5 夜間 GetOpenMany／E6 DispatchContext 靜音／E7 帳本滿載出聲／E8 嚴重度裸轉型／E9 立即派工 Origin／E10a 用詞（程式）／E10b 用詞（說明書＋manifest）／E11 日層級指派閘門／E12 文件 |
| F | F1 人工對應缺鏡像／F2 pager treesize／F3 對應往後沿用／F4 快照真子集／F5 範圍柵欄／F6 快照空範圍＋區域副本／F7 結構同步 0 裝置與例外狀態／F8a NetIQ 主機 AddHost／SetActive／F8b 查詢子句 IPv4／Os 警告／排除閘門／F8c 精靈輪詢＋重新認證登出／F9 文件 |
| G | G1 async-guards／storage 新模組／G2 records＋runs 序號守門／G3 其餘五頁序號守門／G4 pollWhile 取代 setInterval(async)／G5 失敗態與 toast 與儀表板／G6 safeStorage 套用／G7 chat-panel 世代／G8 api.download／G9 prtg-admin scopeRevision／G10 record-detail 報告／G11 小項／G12 文件 |

### 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| （開輪） | 主代理 | 開 `feature/feedback-59`、PLAN 定案 | 批次 A 定向篩選前基準 601／0 | — |
| A1 | Sonnet | `CurrentUserAuthorization.RequireViewAll/HasViewAll`；Settings／Effectiveness／Calibration 改用；9 個測試依賴方補使用者 | 主代理重跑 367 綠；突變 HasViewAll 恆真 → 主機對應、校準、效能端點受限案皆紅 | 代理宣稱新增 `Endpoint_無ViewAll回ForbidResult` 但檔內不存在（突變存活抓到）→ 主代理補 Theory 兩案；Edit 把該檔轉 LF → 轉回 CRLF。附帶消除 BACKLOG「主機表為空也 403」 |
| A2 | Sonnet | display 改讀 `ISystemSettingsStore`；`help/ask` 掛 Maintain、`ask-available` 無權回 false | 主代理重跑 231 綠；突變 display 改回 `_settings.Get()` → 3 紅；代理已對 Maintain 判斷做突變 | 代理給兩個原無 BOM 的控制器加了 BOM（回報稱維持原樣）→ 主代理移除 |
| A3 | Sonnet | `AccountNames.Canonical` 單一出口；LDAP、節流鍵、FindByAccount（先正規化後原字串）；登入欄位 256 上限 | 主代理重跑 116 綠；代理突變 FindByAccount 改回原字串 → 網域前綴案紅 | AuthController 節流接線無測試（控制器難組裝，規格允許），主代理讀 diff 確認四處皆改 |
| A4 | Sonnet | `isSafeReturnUrl` 搬到 `core/safe-return-url.js`，含 tab／LF／CR 直接拒絕 | 主代理重跑 216 綠；突變拿掉控制字元判斷 → 3 紅（tab／LF／CR） | 無 |
| A5 | Sonnet | `IssueRankingCache`／`ActionableSnapshotCache` 鍵加資料版本戳（呼叫端先取 `Version` 再查）；interpret 鍵加輸入雜湊 | 主代理重跑 210 綠；突變 Version 恆 0 → 版本推進案紅；代理另對 interpret 雜湊與另一快取做突變 | 白名單外必要改動 `IssueRankingBuilder`、`IssueTodoQuery`（KeyOf 呼叫端），合理 |
| A6 | Sonnet | Linux 現場取數 program 白名單（不符回空 Filter，呼叫端視為查不到）；權限異動四個 GET 加 ConfirmPermission | 主代理重跑 526 綠；突變拿掉白名單 → 4 紅；代理突變拿掉 ids 的 Permission → 反射守門紅 | 以空 Filter 表示不查詢而非 null（避免改既有測試型別），合理 |
| A7 | 主代理 | WEB-SPEC §6.2／§7.1／§9.5／§9.9c／§9.9f、PRTG-SPEC §8／§11、CLAUDE.md 快取紅線 | 逐句比對；CRLF 保留 | heredoc 吞反斜線造成退格字元，已以位元組修正 |
| B1 | Sonnet | `ColumnClamp`（上限讀 LfDbContext Model）；top_issues／first_seen／risky_events／permission_changes／issue_cases／manual_map 寫入截斷；刪三份私有 Truncate | 主代理重跑 536 綠；突變拿掉 RuleId 截斷 → 1 紅 | `EfPrtgStore.Truncate` 原只被 IpExclude 用，UpsertManualMap 原本完全沒截斷，兩處都補；`Target`／`ProcessName` 無上限不截 |
| B2 | Sonnet | 首見日與聚合查詢的來源鍵改走 `SourceKeyOf`（舊相容路徑保留）；首見日 catch 只吞唯一鍵；issue_key 超長拒寫並警告 | 主代理重跑 321 綠；突變首見日鍵改回全長 → 1 紅；代理另突變聚合鍵與 issue_key 守門 | SQLite 不強制長度，測試改斷言「存檔鍵＝SourceKeyOf 且 255 字」以確保改動前會紅；SqlException 無法建構只測 SQLite 19 |
| B3 | Sonnet | `rule_id` 上限改引用 `RuleSchemaLimits.IdMaxLength`；SQL Server 建表 100、每次升級以 INFORMATION_SCHEMA 判斷長度不足才 ALTER | 主代理重跑 111 綠；突變 ALTER 改 SELECT → DDL 測試紅 | SQL Server 實機 ALTER 未連線驗證（專案限制）；呼叫點以讀碼確認（SchemaUpgrader.cs:361） |
| B4 | Sonnet | 復原服務孤兒頁對保留期外 workflow blob 精確刪除（同租約、同邊界；不刪未來日、保留期內孤兒、cursor） | 主代理重跑 36 綠；突變邊界 `<` 改 `<=` → 邊界日案紅；代理突變停用刪除 → 2 紅 | 無 |
| B5 | Sonnet | 風險事件與權限異動 Prune 改 `BatchedPrune`（只撈主鍵、單次上限）；`RetentionPruner` 任一步失敗不寫 LastRunDate | 主代理重跑 243 綠；突變拿掉 Take → 分批測試紅；代理突變 `if (!anyFailed)` → 失敗不記日期案紅 | 無 |
| B6 | Sonnet＋主代理 | `StorageSettings.IsSqlite／IsSqlServer`；QueryPage SQL 分支末尾加 RecordId 次要鍵 | 主代理重跑 209 綠；突變 Storage 改回字面 → 3 紅；突變拿掉 date 降序次要鍵 → 主代理補的 `QueryPageOrderSqlTests` 紅 | 代理的翻頁測試在 SQLite 抓不到缺次要鍵（同分依 rowid 輸出）→ 主代理補 SQL 文字守門（攔截器檢查 ORDER BY 最後一鍵） |
| B7 | Sonnet＋主代理 | 回填只對 extract_version<2 以 JSON 覆寫 AiPending／AiAnalyzed；BatchedPrune 零進展中止；IsTransient 只對暫時性錯誤重試 | 主代理逐一單獨突變三處 → 各 1 紅；定向 71 綠 | 代理三突變同時施加致測試卡死（無限迴圈）→ 主代理把 prune 測試改成超過 3 次呼叫即擲例外，突變變成乾淨紅燈 |
| B8 | 主代理 | DB-SPEC（六個保留期、ColumnClamp 規則、workflow_ 清除、回填版本語意）；BACKLOG（刪「全新安裝 PRTG 維護頁 403」已由 A1 解決、改寫雙份索引項、新增升級期機制退場表） | 逐句比對；CRLF 保留 | `lf_risky_events` 欄位級定義缺漏屬 61-L（B-11） |
| C1 | Sonnet | 統計段 `needsAi = useAi && !lowRisk`；`BuildStatisticalRecordAsync` 回 `(Record, NeedsAi)`；刪 AnalyzeDayAsync／CompleteAiAsync／ApplyOutcome／ScreenTailAsync／AiWorkItem 等；RetryAiAsync 低風險回 Skipped；排程以成功路徑寫回並計 Skipped | 主代理重跑 271 綠（0 警告）；突變拿掉 `|| outcome.Skipped` → 紅；代理另突變統計段與補跑判斷各紅 | 五個測試檔改寫最多約 30%；`BuildPrompt` 的 screening 參數一併移除；Skipped 沿用舊紀錄的掃描欄位值避免被覆寫清空 |
| C2 | Sonnet | 移除三條 `prtg-*-evidence-aligned` 與 PrtgCorroboration 對齊半邊、PrtgFinding 無生產者屬性；抑制清單 DTO 加 `Retired`、規則頁「已退役」徽章 | 主代理重跑 301 綠、前端行為 63 綠；突變 Retired 判定 → 紅 | `SourceObservations` 仍有 NetIQ／本機生產者（LogAggregator.cs:213-226），保留；`PrtgPresence*` 五屬性已無生產者但可能存在歷史 JSON，保留。疑似新死碼（`CorrelationAnalyzer.StorageSignalCandidates`／`UnexpectedShutdownCandidates`、`SourceEvidenceRelation` 三成員）→ 60-H 盤點 |
| C3 | Sonnet | 簽章加 `[JsonIgnore] BelowThreshold`（ApplyRule 設定）；關聯層 Find 跳過未達門檻（不跳過已抑制，守 RULES-SPEC 語意邊界）；【滅跡】錨點改 {1102,4719}、其他安全事件需 Severity>Low 且非 tamper／4907；時間偏移限 29/36/47/50 | 主代理重跑 464 綠；突變拿掉 Severity>Low → 1 紅；代理突變拿掉 BelowThreshold 跳過 → 2 紅 | PRTG 佐證走獨立 FindIn 不受影響；四檔被轉 LF，主代理轉回 CRLF。**PLAN 原寫「排除已抑制」違反語意邊界，規格已改** |
| C4 | Sonnet | Linux【SSH 破解得手】失敗面改 `TryParseAuthFailure`（三類行、rhost 空不算解析失敗）；帳號與 IP 加時序（共用 `FilterByTiming`） | 主代理重跑 94 綠；突變解析回到只認 Failed password → 混合格式案紅；代理突變拿掉時序 → 時序案紅 | 無 |
| C5 | Sonnet | `RiskLevels.Normalize` 只接受 ≤4 字且首字為等級；AI 格式異常降級路徑風險＝規則風險、不標 ai_raise | 主代理重跑 151 綠；突變長度門檻 → 2 紅；代理突變降級路徑 → 紅 | 舊 Theory 中「風險等級：高，理由…」「這次判定為低」改為未知（新契約）；`風險：高` 也判未知，屬規格意圖 |
| C6 | Haiku | 慢速趨勢對疑似殘留憑證重試簽章進 suppressedAlerts 並加註；註記字串抽常數兩處共用 | 主代理重跑 136 綠；突變殘留判斷 → 紅 | 檔案被轉 LF，主代理轉回 CRLF |
| C7 | Sonnet | 報告處置參考以簽章 RuleId 在注入規則清單反查（三個組裝點傳 `ResolveRules`）；重點問題排除已抑制；原始 log 配對改用 `GroupKeyFor` | 主代理重跑 188 綠；突變拿掉已抑制排除 → 紅；代理另突變 RuleId 反查（2 紅）與配對鍵（1 紅） | 只有已抑制簽章時重點問題為空（不再退回取前三），屬契約自然結果 |
| C8 | Haiku＋Sonnet | `PromptGuidelines.HostKindText`；主分析與深入分析 prompt 依主機作業系統（`LogAnalysisService`／`RiskReportService` 建構時帶入 hostOs；NetIQ、AI 排程、復原服務傳入；本機與週期體檢維持 Windows） | 主代理重跑 228 綠；突變深入分析 prompt 改傳 null → 紅；代理突變主分析 → 紅 | 第一段 Haiku 依停止條件停下（逐方法傳參超過 6 點）→ 主代理改「建構時帶入」設計再派 Sonnet。AI 排程每分片重讀主機表取 Os，併入 D4「同輪共用」 |
| C9 | 主代理 | DETECTION／RULES／LINUX-RULES／BACKLOG 同步；FindLinuxRule 補 Enabled；兩處過時註解 | 規則相關 520 綠；逐句比對 | 無 |
| D1 | Haiku | `BatchResetAiPending` 擲例外時 `EndRun(false, …)` 後 rethrow | 代理：改動前紅、改動後綠、突變紅；主代理重跑 45 綠 | 以 DispatchProxy 包真 store 只讓重標擲例外（既有替身不在白名單） |
| D2 | Sonnet | `BatchRunTriggers` 常數＋`IsScheduledFetch()` 單一出口；PRTG 重評中斷判定排除 AI 排程；窗口略過判定改 `!=` | 代理：突變拿掉 AI 排除 5 紅；主代理重跑規格篩選 254 綠 | `StartsWith("schedule")` 全專案無其他前綴值，改 `!=` 等價 |
| D3 | Sonnet | Tick 改為先判斷窗口與觸發、finally 再寄信；四個背景服務停機取消不記 Error；TriggerRunAsync 取消改記 Info | 代理：兩個突變（先寄信、拿掉取消 catch）各 1 紅；主代理重跑 304 綠 | 規格外同型處理：PRTG 啟動補抓與權限遷移 :46 的取消豁免；新增 `StartupDelay` 供測試縮短 |
| D4a | Sonnet | 迴圈只在開頭實查待補、全滅才再查；`AiCalls = Done − Skipped + Failed`；一輪共用 `IAiService` 與 HostId→Os 對照 | 代理：三個突變各紅；主代理重跑 166 綠 | 主代理驗收抓到：輪詢改讀 30 秒快取後，取數期間的「0 筆」會擋住接續開跑 → 跟隨取數期間與接續觸發先使快取失效，加守門測試（拿掉修正即紅）；驗收 2 改 4 筆（3 筆時新舊公式同值） |
| D4b | Haiku | `ChatJsonAsync` 傳輸層失敗立即回傳、不進 JSON 重試；`IssueOwnedHostIdsCache` 只在版本前進時清空 | 代理：改動前 3 紅、兩個突變各紅；主代理重跑 170 綠 | 空回應由 Polly 層重試，JSON 層不再加倍；舊版本晚到結果存在舊版本鍵下，不污染新版本 |
| D5 | Sonnet | Sentinel 重試包裝不再把呼叫端取消包成「重試已耗盡」；結果加 `Unreadable`（回報有資料卻取回 0 筆），管線遇到時不二分、不設高流量、記失敗 | 代理：三個突變各紅；主代理重跑 236 綠 | 取消測試需 `RetryCount=0`（有 Polly 時取消本來就以 OCE 上傳）；白名單外補一行串流替身帶 `Unreadable`；NetiqDirectoryClient 經合併權杖自動對稱，不另改 |
| D6 | Sonnet | `BatchRunStatus.Compute` 加 `heldByThisProcess`；兩個 RunState 提供 `HoldsRun`；執行監控與新鮮度傳入 | 代理：等價突變 2 紅；主代理重跑 234 綠 | AI 以 RunId 精確比對；取數的 BatchRun 在協調器內建立，改用「非 AI、未結束、開始不早於本趟」後備判定（單一閘門保證唯一）；精確登記需動協調器與進度介面，收益小不做 |
| D9 | 主代理 | WEB-SPEC §9.10：節流段補「每輪共用一個 AIService」與 `AiProbeService` 探活例外、待補快取的實際用法、敘事行改現況、觸發來源補 `fetch-followup`、異常中斷補持有判定；程式註解：`BatchRun.Trigger` 補 `fetch-followup`，SchedulerRunState／SchedulerHostedService／AiAnalysisHostedService 的錯位與重複 summary 歸位 | 建置 0 錯誤；只動註解與文件，測試略過（過度） | 「未接閘道為 null」句留待 D7 改必填時一起改 |
| D7 | Sonnet | 五個 AI 消費端的閘道參數改必填（null 擲例外）；`AiRequestGateTests` 刪寫死的 5、新增正式 DI 解析與非 nullable 反射守門；WEB-SPEC 節流段同步 | 代理：DI 改傳 Null、參數改回 nullable 兩個突變各紅；主代理重跑 148 綠、Core＋Web `IAiRequestGate? ` 0 筆 | 必填參數須排在選填前，建構式參數順序改變；WebAiService 新增 `_aiGate` 欄位供反射讀取 |
| E1 | Sonnet | `IIssueOwnerStore.Update(source, eventId, apply)` 在互斥區塊內只改指定欄位；負責人、結論、靜音五個入口改用它 | 代理：三個突變各紅；主代理重跑 511 綠 4 略過（既有） | 驗證仍在 `Update` 之前以 `Get` 判斷，極端競態（毫秒級）下解除靜音可能靜默不動作、清除結論可能建空列，影響輕微接受 |
| E2 | Haiku | 報表記憶體路徑的問題總數（本期與上期）排除靜音中的問題，與 scope=all 的 SQL 聚合同口徑 | 代理：改動前紅、突變紅；主代理重跑 183 綠 | 涵蓋缺口天數兩路徑定義相同（`HasCoverageGap`＝SQL 條件），差別只在顯示範圍篩選本身，不改 |
| E3 | Sonnet | 刪 `BuildCase`／`ReassignCase`／`CreateOpenCase`／`CaseBuildResult`；測試前置改走 `WorkOrderCoordinator.Create`（`WorkOrderCaseSeeder`），需「無單案件」的案例改 `LegacyCaseSeeder` 直寫資料列；新增 `IssueCaseCreationGuardTests` | 代理：守門突變紅；主代理重跑 714 綠 4 略過（既有）、grep 三個方法 0 筆 | 「重現的問題」測試的前置改為舊案結案後才加 newDay（正式流程不強制寫入已結案觸發日），斷言未放寬；主代理補修 WorkOrderCoordinator 失效 cref、DayHandlingCommandService 註解、CLAUDE.md 紅線、WEB-SPEC 改派項；兩個新檔換行補成 CRLF |
| E4 | Sonnet | `AttachNewDay` 存檔前重讀最新案件，只套 `LastLinkedDate`（只升不降）與 `PrtgEvidence`，已結案跳過；夜間寫成員鎖內重查主機進行中案件，已存在不建案、計 `open_case_exists` 並帶進摘要 | 代理：兩個突變各紅；主代理重跑 459 綠 4 略過（既有） | 改用 `GetOpenForHost`（夜間寫成員是單一主機）；主代理驗收抓到：略過後本趟新建的單會變零成員進行中空單 → `FlushRun` 以結案推導收掉並移出摘要，加端對端測試（拿掉即紅） |
| E5a | Sonnet | `DispatchContext` 靜音判定委派 `IssueExclusion`（刪自建區間字典）；立即派工分組鍵加 Origin，建單來源與備註依決策（`NightlyNoteOf`） | 代理：先釘現行語意再改、兩個突變各紅；主代理重跑規格篩選 484 綠 | 白名單外：`NightlyNoteOf` 改 internal、派工器測試改用 `Exclusion`；立即派工的策略單備註由「系統依派工策略立即派工」改為與夜間相同 |
| E5b | Haiku | 九處嚴重度裸轉型改 `LegacySeverityRank.ToSeverity`；郵件貢獻帳本上限改建構式參數、滿載記 Warn | 代理：改動前紅、兩個突變各紅；主代理重跑 581 綠 | 主代理驗收抓到兩處：輕量查詢在 store 端正規化 Critical 後，讀取端看不到 Critical 而漏標「重大」→ 同處補 `ForcesElevate`（加契約測試，拿掉即紅）；預約回 null 也可能是鍵已被佔用（正常去重）→ 依 store 拒絕順序區分，只有真的滿載才記 Warn |
| E6 | Sonnet | 處理人可見字串「案件」改「交辦」（9 個前端檔、2 個後端服務，含規格未列的同型字樣）；新增 `HandlerFacingWordingUiTests` 掃字串字面量 | 代理：守門改動前紅、突變紅；主代理逐行比對 33 行只改字串、重跑 969 綠 4 略過（既有） | 稽核動作中文名、管理者專屬頁、註解不動 |
| E7 | Sonnet | 日層級指派依序套用抑制、已知雜訊、不再打擾、靜音四道閘門，略過原因帶進預覽 DTO、寫入回應與處理面板文字 | 代理：後端兩條改動前紅、三個突變各紅；主代理重跑 881 綠 4 略過（既有） | 雜訊用 `INoiseMarkStore.Get`（同鍵比對）、不再打擾用 `DismissedKeys`（改 internal）、靜音用 `IIssueExclusionSource`；三個相依為選用參數 → 主代理補 `DayHandlingGateWiringTests` 以正式 DI 解析守住都有注入（突變紅） |
| E8 | Sonnet | 說明書第 5 章改名「處理狀態與交辦單」、小節改「成員與跨日問題」、「案件授與」改「交辦授與」，引用章名同步；`.ai.md` 加「畫面上稱為交辦；舊稱案件」；manifest keywords 保留「案件」並加「交辦授與」「成員」 | 主代理重跑 `Help` 71 綠；使用者版 `.md` 的「案件」0 處 | 主代理修正一句不精確改寫：問題結案後再出現是「新的成員，掛進進行中的單或開新單」，不一定開新單；第 16 章補「目前沒有進行中的交辦」提示（對齊 E6 畫面） |
| E12 | 主代理 | WEB-SPEC：§9.1 還原被拆開的項目符號、排序段改現況；用詞表補處理人側「交辦」與守門；日層級指派補四道閘門；待派移除 `disabled`、補試跑說明與立即派工來源；夜間掛接補 `related_warning_active`、`open_case_exists` 與空單收尾、摘要句；單一列判定改為記憶體一份；API 表補 due-date；§9.6 scope 作用範圍與問題總數排除靜音；資料存取表補郵件帳本與負責人原子更新。BACKLOG：IssueKey 條改現況、並行競態條改為殘留項 | (D) 逐句比對；無測試讀文件內容 | IssueCaseCoordinator 類別摘要已在 E3 完成 |
| F1 | Sonnet | 人工對應裝置不在鏡像不入範圍（`ManualMissingInMirror`＋警告），維護頁標「PRTG 已無此裝置」；分頁器第一頁形狀不對或讀到筆數少於 treesize 擲未收斂例外（messages 只出聲） | 代理：6 條改動前紅、treesize 突變 3 紅；主代理重跑 271 綠 | 鏡像是全站全量同步，無雞生蛋；`PrtgFetchService` 既有 catch 已走「記失敗不清除」不需改；鏡像全空時人工對應暫不入範圍，下次同步成功即恢復 |
| F2 | Sonnet | 新增 `PrtgHostMapLookup.Select`（回看窗內最晚，否則往後沿用到今天的最早一份）與共用 `LookbackDays`；數值評估改用它，就緒對應讀取窗上界延到今天 | 代理：舊判定與下界兩個突變各紅；主代理重跑 178 綠 | 回看窗由 32 天統一為 31 天（與 SQL `FindLatestHostMapDate` 同範圍）；SQL 端不便共用，兩處註解互引；讀取窗延長只有一個呼叫端且未來日期不會被選 |
| F3a | Sonnet | 快照回應為請求集合的非空真子集時收下（缺顆警告可達），含請求外 objid 或空回應拒收；`_sensorTypes`／`_targetObjids` 讀取改區域副本並加原始碼守門；目標為空不記成功、略過原因帶「取數範圍為空」 | 代理：6 條改動前紅、三個突變各紅；主代理重跑 457 綠（含健康頁） | 4 條既有測試改為新語意；競態無法穩定重現改用原始碼守門；空範圍時健康頁顯示略過原因、快照新鮮度不假更新；代理殘留背景工作已由主代理停止 |
| F3b | Sonnet | 範圍柵欄 `ReadCore` 加 `includeRules`：`Read()` 字串格式不變（golden 測試），`ReadStructural`／`ReadRawSemantic` 不含規則，結構同步改用 `ReadStructural`；結構同步 0 台判失敗並提示；一般例外也保存失敗狀態；結構同步、回填、探測的 `PrtgOperationScope` 移入 try | 代理：四個突變各紅；主代理重跑 476 綠 | 兩個柵欄字串只在操作期間於記憶體比對、未持久化，升級不受影響；建構擲例外無公開觸發方式，以結構保證與一般例外測試涵蓋；主代理還原一個被誤加的 BOM |
| F4a | Haiku | NetIQ 新增主機對既有主機沿用未提供的 Sentinel／描述（同 BulkAddHosts 判定）；主機與 Sentinel 啟停改以既有物件複本更新 | 代理：兩條改動前紅、五個突變各紅；主代理重跑 362 綠 | 保留既有語意：停用保留孤兒標記、人工重新啟用才清除（加守門測試）；Sentinel 啟停原本已逐欄帶入，改複本後行為不變 |
| F4b | Sonnet | `BuildIpClause` 只放行合法 IPv4（全不合法擲例外）；pipeline 對非 IPv4 位址與無法辨識的 Os 逐台警告、不查詢、標未完整；`NormalizeKnownIps` 移除總數上限、保留段內閘門 | 代理：新測試改動前紅、四個突變各紅；主代理重跑 384 綠 | 被濾掉的主機不進進度分母（不卡在 100% 前）；刻意不計入「已完成跳過」以免誤導；主代理補現場取數單台查詢位址非 IPv4 回空查詢而非擲例外（加 Theory，拿掉即紅）；`IsValidIpv4` 收緊為每段 1～3 位純數字 |
| F5 | Sonnet | 掃描精靈輪詢：只有後端回「工作不存在或已逾期」（實為 400＋`validation_failed`，相容 404）才停並關精靈，其他錯誤連續 3 次才停並提示、成功歸零；重新認證前 best-effort 登出舊 token（5 秒、例外只記 Debug） | 代理：Sentinel 兩案與行為四案改動前紅、兩個突變各紅；主代理重跑 432 綠 | 連續失敗收尾保留 jobId，重開精靈走既有接續路徑；主代理把登出請求抽成 `DeleteTokenAsync`，結束登出與重新認證前登出共用 |
| F9 | Sonnet | PRTG-SPEC（down 語意、磁碟趨勢、§3 分頁器、§3b 真子集與空範圍與請求預算、§3c 人工對應缺鏡像、§4 名稱退路與往後沿用、§5a 結構同步 0 台、範圍柵欄、§6 資料流、§7 監看政策、§8 operations、§10 空陣列、§11 授權）；NETIQ 文件標頭、§2～§7 與新增 §8 SourceEvidence／欄位形狀探測；WEB-SPEC §9.9a；BACKLOG NetIQ #3／#4／#8／#9／:302；註解 A3-08／09／18、A2b-16、A2a-18 | 代理逐項讀碼核對、建置 0 錯誤、相關測試 860 綠；主代理抽查敘事字眼與請求預算數字 | A2b-02 經核對不成立（已有 ViewAll 檢查）；§3c 第 4 項依程式改寫；順手修 SentinelQueryBuilder 與三個測試檔頭的章節引用 |
| G1 | Haiku | 新增 `core/async-guards.js`（`createSeqGuard`、`pollWhile`：fn 跑完才排下一輪、例外只警告一次續排、回 false 或 stop 即停）與 `core/storage.js`（`safeStorage` 全部 try/catch）；行為測試 7＋3 案 | 代理 M1～M3 紅；主代理補跑 M5～M8 各紅、還原後 10 綠 | 主代理抓到測試骨架先 `ReadToEnd` 再等待，輪詢停不下時 node 永不結束而卡死 → 改非同步讀輸出、15 秒逾時結束程序樹；代理兩支突變腳本併發改檔、中斷時檔案停在突變狀態，主代理以備份比對後還原 |
| G2 | Sonnet | 查詢頁 `search` 序號＋視角守門與失敗態；排程作業頁執行總表序號守門與失敗態、排程狀態輪詢排程放 finally；排程作業頁 2 處與 PRTG 維護頁 3 處輪詢改 `pollWhile`（`clearInterval` 全對應 `stop()`）；新增 `PollingUiTests` 掃 `setInterval(async`／具名 async 函式 | 代理：新測試改動前紅、六個突變各紅；主代理重跑 293 綠 | 原本皆非「先呼叫一次再 setInterval」，首輪節奏不變；排程狀態依狀態切換間隔，維持 setTimeout 只修 finally；主代理把 netiq.js 探測輪詢也改 `pollWhile` 並移除守門的暫存例外 |
| G3 | Sonnet×2 | 權限異動、稽核、主機、儀表板、報表加序號守門；篩選、換頁、儲存後重載經 guardLoad（權限異動非網段錯誤外擲、不回填空清單；主機、使用者、群組、負責人、匯入儲存後重載失敗不再被吞）；儀表板 serverAdmin 隱藏與 guardLoad 容器補重點問題；兩處 toast `error`→`danger`；localStorage 改 `safeStorage`（已有 try/catch 者列白名單）；新增 `FrontendGuardUiTests`（toast 型別、localStorage 白名單）與 13 案清單頁行為測試 | 代理：十個突變各紅；主代理重跑 565 綠 | 兩段依檔案拆分、前半段單獨提交會帶全站守門紅燈，合併為一個提交；ui.js 收合記憶也改 safeStorage（守門要求，行為等價） |
| G4a | Sonnet | 對話面板加世代計數器（換問題或清除後舊回覆與失敗皆丟棄）、pending 期間停用下拉與清除、finally 依是否已選問題決定輸入框；`api.download`（有界讀取、逾時、401 導登入、Content-Disposition 檔名）與 `saveBlobAsFile`，校準與 PRTG 維護兩處下載改用，失敗 toast 不整頁跳轉；新增 `FrontendMiscUiTests`（頁面不得直接 fetch／location.assign） | 代理：十個突變各紅（兩個存活突變補強後轉紅）；主代理重跑 402 綠 | 代理覆寫了主代理的 scratchpad 突變腳本（主代理改用新腳本）；既有 13 個行為測試檔「先 ReadToEnd 再 WaitForExit」在實作壞掉時會卡死，列入第 61 輪 M 改共用 node 執行器 |
| G4b | Sonnet | PRTG 維護頁 `requireScopeRevision()` 守六個寫入點、移除 `/runs` fallback；詳情頁兩份報告各自容錯、批次儲存後重載經 guardLoad；主機頁 `activeFilterEntries()` 單一條件清單（含 prtgMap）；預計完成日上限常數 `MAX_DUE_DATE_DAYS`＝90 集中並與後端一致（另加手打超限擋下）；負責人匯入確認文字；說明書首次載入不捲動；報表起訖日檢查與範圍 chip 載入成功才更新 | 代理：十三個突變各紅（一個存活突變補強後轉紅）；主代理重跑 430 綠 | 報表維持無參數 `load()`（既有測試鎖定簽名），以 `requestedScope` 傳遞點選範圍 |
| G12 | Sonnet | WEB-SPEC §8.1 core 模組總表（24 檔，含 async-guards、storage）、inline script 例外（LF_BASE／LF_LABELS／登入頁備援）、api 唯一出口含 download 與 saveBlobAsFile；§8.1a toast link 由呼叫端套前綴；§8.6-1 篩選記憶以網址為準；§8.6 序號守門與 pollWhile 唯一寫法與三條守門；§9.3／§9.4b／§9.6／§9.9c 與 PRTG 維護頁補批次 G 行為；BACKLOG 補 pollWhile 降頻與退避 | 代理逐項讀碼核對；主代理抽查模組數與注入點 | 篩選記憶依程式寫實際範圍（只有權限異動與儀表板天數記本機）；交辦 modal 無 90 天前端檢查，文件只寫有的兩處 |
| 全量 | 主代理 | 第 59 輪全量測試：6,837 通過、2 失敗、10 略過。① `ChatJsonAsync` 空回覆被歸為傳輸失敗後錯誤訊息少了「（空回覆）」（D4b 回歸）→ 仍立即回傳、訊息沿用契約失敗組法；② `EfWebdataStoreTests` 以不帶內因的 DbUpdateException 測重試，B7 收窄後本就不該重試 → 改帶「database is locked」內因 | 修正後相關篩選 55 綠；整輪 6,839 通過／10 略過（失敗僅兩條、修正局部，不重跑全量） | 兩條都不在當時的定向篩選內：定向篩選要含被改函式的所有測試類 |
| 核對補修 | 主代理＋Sonnet | 逐條核對 80 項：補 B-1 簽章入口截斷、刪 C-1 遺留 `GetTailIssues`、補 E-7 帳本健康細節（Sonnet）、補 E-8 裸轉型守門測試、README 升級注意事項四條、說明書低風險日不送 AI、DB-SPEC 已知限制 | 入口截斷相關 731 綠、綜合篩選 851 綠；入口截斷改動前紅、E-7 四突變紅、E-8 守門突變紅 | B-3 健康細節、B-6 主機排序大小寫經評估不做（理由見〈實作完成核對〉） |
| A 規格 | 主代理 | `.gemini-tasks/r59/_common.md`＋A1～A7 | PLAN A-1～A-9 逐條對照規格與驗收皆有對應 | 寫規格時核對推翻兩處：A-1 不動 CaptureBinding／PrtgMonitoringController；A-6 儀表板快取鍵項撤銷（死條件非缺口）。A-5 改為含控制字元直接拒絕 |

## 實作完成核對（2026-10-30，主代理逐條對照程式碼）

第 59 輪批次 A～G 共 80 條規劃項目逐條以程式碼、測試或文件證據核對。結論：全部已實作，其中下列 6 條核對時發現缺漏，已當場補齊；2 條子項經評估維持不做（理由見下表）。

| 批次 | 項目 | 狀態 |
|---|---|---|
| A 授權與安全 | A-1～A-9 | ✅ 全部完成；A-4 的升級注意事項本次補進 README |
| B 儲存層 | B-1～B-9 | ✅ 全部完成；B-1 的「簽章入口截斷」本次補齊；B-3、B-6 各一子項不做（見下） |
| C 偵測邏輯 | C-1～C-10 | ✅ 全部完成；C-1 遺留的 `GetTailIssues` 與其測試本次刪除；C-1、C-5 的升級注意事項本次補進 README，低風險日不送 AI 補進說明書 |
| D 排程與 AI | D-1～D-9 | ✅ 全部完成 |
| E 交辦與派工 | E-1～E-12 | ✅ 全部完成；E-7 的健康細節、E-8 的裸轉型守門測試本次補齊 |
| F PRTG 與 NetIQ | F-1～F-9 | ✅ 全部完成；F-7 的升級注意事項本次補進 README |
| G 前端 | G-1～G-12 | ✅ 全部完成 |

**本次補齊**：
- **B-1**：`LogIssueSignature.Source`／`LogName` 在 setter 截到 255（與 `SourceKeyOf` 同常數），所有建構與反序列化路徑都經過，問題鍵與存檔讀回的鍵一致；`SignatureEntryClampTests` 改動前 3 紅、改動後綠；DB-SPEC 補已知限制。
- **C-1**：前置掃描退場後 `AnalysisPromptBuilder.GetTailIssues` 只剩一條測試在用，連同測試刪除；類別摘要與註解改為現況。
- **E-7**：`MailNotificationService.GetContributionLedgerStatus()`（佔用數、上限、最近滿載時間與收件人，與滿載警告共用同一份佔用算法），健康細節 `MailContributionLedger`，設定頁健康區塊在佔用 ≥ 九成或曾滿載時顯示提示；四個突變各紅。
- **E-8**：新增 `SeverityCastGuardTests` 掃 Core／Web 不得以 `(IssueSeverity)…Rank` 裸轉型；加回一處裸轉型即紅。
- **升級注意事項**：A-4（帳號網域前綴）、C-1（低風險日不送 AI）、C-5（Linux 時序可能讓重跑降級）、F-7（結構同步 0 台判失敗）四條寫進 README；這四條原列在本輪驗收欄、又被第 61 輪 L 草案重列，兩邊都沒做，以本輪為準（第 61 輪 L 不再重複）。

**經評估不做的子項**：

| 項目 | 子項 | 理由 |
|---|---|---|
| B-3 | 清除數寫入復原服務的健康細節 | 復原服務沒有任何健康細節出口（游標資料無讀取端），要顯示等於新增一個健康項目；清除數以 Info log 記錄 |
| B-6 | 主機排序改不分大小寫 | 寫規格時刻意保留原排序以免改變使用者看到的順序；分頁大表的 ORDER BY 套 UPPER 會失去索引排序。唯一次要鍵 `RecordId` 已補，翻頁不重不漏 |

**有理由的規格偏差（已記於執行紀錄）**：C-2 保留仍有 NetIQ／本機生產者的 `SourceObservations`；C-9 週期體檢只在 Windows 本機執行，維持「Windows Server」；E-3 守門白名單只放 `WorkOrderCoordinator.cs`（資料層沒有直接 `new IssueCase`）。

## 0. 總結

| 面向 | 高 | 中 | 低 | 一句話 |
|---|---|---|---|---|
| A1 偵測核心 | 2 | 8 | 11 | 前置掃描正式路徑不存在；關聯層不看門檻／嚴重度 |
| A2a PRTG 管線 | 0 | 8 | 13 | 紅線落實；R54 殘留讓「精確對齊」不可達；往後沿用漏一處 |
| A2b PRTG 維運 | 1 | 8 | 9 | 全站可見檢查拿含停用主機比對→管理者 403；死碼多 |
| A3 NetIQ | 0 | 8 | 12 | 取消被包成失敗；文件章節漂移 |
| B 儲存層 | 3 | 10 | 11 | workflow_ blob 無保留期；寫入前截斷只有一張表做到 |
| C1 Web 安全 | 2 | 10 | 16 | 同 A2b-01；display 設定端點每頁全表聚合 |
| C2 排程／AI | 0 | 6 | 15 | AI 閘門可能卡死到重啟；排程判準三份 |
| C3 交辦處理 | 0 | 10 | 13 | 負責人設定 lost update；報表靜音口徑分岔 |
| D1 前端核心 | 0 | 6 | 12 | 三大頁無序號守門與失敗路徑 |
| D2 前端頁面 | 1 | 12 | 25 | 權限異動頁失敗畫成空狀態；returnUrl 可繞 |
| E 文件 | 0 | 5 | 12 | BACKLOG 6 項過時、WEB-SPEC §4/§5/§10/§12 落後 |

紅線面：innerHTML 插值 0、寫死 `/` 路徑 0（僅 1 處死 fallback）、`isRunning` 當互斥 0、`new AIService(` 全帶 gate、排程觸發單一查詢點**被兩處繞過**、建案一律經 `WorkOrderCoordinator` 成立但禁用入口仍 public、HasMaxLength 截斷**只有 EfWorkOrderStore 與 PRTG 部分做到**。

## 1. 核對表（主代理親核）

| 項目 | 判定 | 證據 |
|---|---|---|
| A2b-01／C1-01 全站可見檢查用 `GetAll()`（含停用）對 `GetVisibleHostIds()`（只含 Active） | ✅ | SettingsController.cs:1641-1648；VisibilityService.cs:197；21 處呼叫；PrtgHostMapEndpointTests.cs:69-79 假件把停用主機算可見 |
| A1-01 正式 AI 路徑只走 `RetryAiAsync`，`screening: null`、無 `skipForLowRisk` | ✅ | LogAnalysisService.cs:687-705；:627/:641 只在 CompleteAiAsync；統計段 :368-369 仍以 tail≥20 標 AiPending |
| **低風險日存檔時 `SampleMessages` 清空、`KeyDetails=null`** | ✅ | RecordStorageShaper.cs:15-19,86-89 → 補跑路徑對低風險日做前置掃描拿不到訊息文字（審查 3 抓到，推翻上一版待決 1 的「合一」建議） |
| 現行 skip 分支落盤 `aiAnalyzed=false`；hosted service 只對 `outcome.AiAnalyzed==true` 走成功分支 | ✅ | LogAnalysisService.cs:632-636；AiAnalysisHostedService.cs:471,517 |
| A1-02 【滅跡】＝錨點＋任一 Security 類簽章 | ✅ | CorrelationAnalyzer.cs:187-196 |
| A1-06 降級路徑 `RiskLevels.Normalize(RawContent)` 用 `Contains` | ✅ | LogAnalysisService.cs:828；RiskLevels.cs:54-64 |
| C2-01 `BatchResetAiPending()` 在 `TryBeginRun` 之後、try 之外 | ✅ | AiAnalysisHostedService.cs:234-243 |
| C3-01 `IssueOwnerStore.Upsert` 整列覆寫、SetMute 先讀後寫 | ✅ | IIssueOwnerStore.cs:47-57；IssueOwnerAdminService.cs:283-300 |
| C3-04 `BuildCase`／`ReassignCase` public 且 production 零呼叫 | ✅ | grep Core／Web 只命中定義；`new IssueCase` 只剩 IssueCaseCoordinator.cs:825、WorkOrderCoordinator.cs:220,583 |
| A3-03 已登錄 >500 整組放棄排除 | ✅ | NetiqDirectoryClient.cs:757-772 |
| B-01 `workflow_{host}_{day}` blob 無任何清除；`workflow_recovery_cursor` 同前綴 | ✅ | HostDayWorkflowState.cs:112,334；RetentionPruner 只清 `prtg_import_diagnostic_` |
| B-02 `rule_id` HasMaxLength(64) vs `IdMaxLength=100`；`rule_id` 在 CREATE TABLE 內非 AddColumn 路徑 | ✅ | LfDbContext.cs:448；RuleSchemaLimits.cs:10；SchemaUpgrader.cs:1210,1227 |
| B-05 `IsValidType` 不分大小寫 vs `Type == "Sqlite"` 字面 | ✅ | AppSettings.cs:232；StorageBackend.cs:75 |
| A2a-03 `PrtgFinding.EvidenceWindows` 等無生產者、`ResourceScope=Unknown` 寫死 | ✅ | PrtgFindingMapper.cs:97 → E-01「精確對齊已做」被推翻 |
| 規則頁抑制目標顯示建立時存的 `targetLabel`，不反查 PatternId | ✅ | rules.js:1323；RuleAdminService.cs:825-828；RemoveSuppression :931 不驗 PatternId |
| C1-02 display 端點走 `Get()`→`ReadReportUsage()` 全表聚合 | ✅ | DisplaySettingsController.cs:30；SystemSettingsService.cs:1308 |
| C1-03 `help/ask` 無 Maintain；前端 `ask-available` 失敗時已隱藏卡片 | ✅ | HelpController.cs:44-46；help-manual.js:146-155 |
| C1-06 LDAP 剝 `DOMAIN\` 但 `FindByAccount(account)` 用原字串 | ✅ | LdapCredentialVerifier.cs:23；IdentityService.cs:105 |
| D2-01／D2-03／D2-04 | ✅ | permission-changes.js:285-288；login.js:76-81；help-manual.js:223、settings.js:1060、ui.js:421 |
| F「`prtg-admin.js:610` 的 `dataset.runsUrl` 無設定者」 | ❌ | Prtg.cshtml:83 有 `data-runs-url`；只是 fallback 死碼 |
| 待決 6：版本戳在排程與 AI 結束時推進；靜音已在 `IssueExclusion.CacheToken` 鍵內 | ✅ | SchedulerHostedService.cs:446、AiAnalysisHostedService.cs:305；IssueRankingCache.cs:42 → 真正缺口是「背景寫入執行中」不推進，TTL 必須留 |
| 待決 7：範圍計算與回填不讀規則；`Read()` 被郵件冪等鍵（MailNotificationService.cs:1884）與快照佇列雜湊（PrtgSnapshotHostedService.cs:369,957,980,1022）當持久鍵 | ✅ | PrtgScopeDevices.cs 無 rule 引用；`Read()` 輸出格式不可改 |
| 待決 11：BatchRun 無 heartbeat；RunMonitorService 未注入 RunState | ✅ | BatchRunStore.cs；RunMonitorService.cs:31-33 |
| 待決 3：Linux 事件有 `TimeGenerated`；`FilterByTiming` 可共用 | ✅ | EventLogEntryData.cs:13；LogAnalysisService.cs:984-996 |
| A-1：`ICurrentUser.Has(ViewAll)` 已存在且 PrtgTransferController.cs:219 已用；`IsCaseGrantOnly` 對 ViewAll 使用者恆 false；serverAdmin 無 ViewAll | ✅ | ICurrentUser.cs:28；VisibilityService.cs:294-296；RoleCapabilityMap.cs:70-74 |
| C2-03a／C3-21 儀表板快取鍵缺 case-grant 維度 | ❌ | 儀表板可見主機來自 `GetVisibleHostIds()`，`IsCaseGrantOnly` 對它們恆 false → 死條件不是授權缺口 |
| 批次 A 定向篩選前基準 | 事實 | 601 通過／0 失敗（feature/feedback-59 開分支時） |
| 說明書「案件」49 處；處理人可讀 .md 在 05-handling（章名、§案件與跨日問題、:105）、03-issues:55、04-record-detail:38、12-settings:140；manifest.json:21-22 keywords | ✅ | grep HelpContent |
| 環境：`.claude/worktrees/agent-ae5416dc66ce5f090` 162 MB 殘留複本；Codex worktree 88 筆 HEAD 全部可達，約 50 個有 100～340 個未提交來源檔修改 | 事實 | `git worktree list`＋逐一 `git status --porcelain` |

## 2. 待決定案（18 項，審查後最終建議）

| # | 待決 | 最終建議 | 理由（含審查後的變化） |
|---|---|---|---|
| 1 | A1-01 前置掃描正式路徑不存在 | **(b′) 退場並止血**：統計段不再因「低風險且 tail≥20」標 AiPending；`RetryAiAsync` 遇低風險（`RiskLevel==低` 且 `RiskBasis` 非 `ai_raise`）回 `Skipped`（AiPending→false、AiAnalyzed 維持 false、不呼叫 AI），hosted service 把 Skipped 計入完成但不計 AiCalls；刪 `AnalyzeDayAsync`／`CompleteAiAsync`／`ApplyOutcome`／`ScreenTailAsync`／`ScreeningOutcome`／`AiWorkItem.Logs`（保留 `AiWorkItem` 型別，NetiqPipelineService.cs:669 與 AnalysisOrchestrator.cs:1000 用它判定）；DETECTION-SPEC 改寫 | 上一版建議「合一」被審查 3 推翻：低風險日存檔時 `SampleMessages` 已清空，補跑掃描等於盲掃；要恢復得讓 shaper 對 tail 保留樣本＋新增 Skipped 語意＋改 7 處 `AiAnalyzed` 消費端＋升級當晚一次性掃描風暴。退場後比現況省（低風險日不再跑完整主分析），既有存量 AiPending 低風險列由 Skipped 自然排掉，無升級風暴。損失是「低風險日未分類事件不再有 AI 看過」，這些事件仍在紀錄 TopIssues 可見 |
| 2 | A2a-03 三條 `prtg-*-evidence-aligned` 不可達 | **(b) 整串移除**＋既有抑制列顯示「已退役」徽章（抑制清單 DTO 加 `retired`，rules.js 目標欄顯示，07-suppression 補一句）；不自動刪 | 無持久化 aligned 參照，`RemoveSuppression` 現在就能刪；徽章是必要的（規則頁顯示的是建立時 label，否則退役列看起來仍在生效）。說明書 07-suppression.ai.md:45、01-overview.ai.md:88 的「19 種」一併改成守門數字 |
| 3 | A1-05 Linux 破解得手無時序 | **(a) 補時序**（共用 `FilterByTiming`）；升級注意事項與 DETECTION-SPEC 寫明「重跑可能讓先前判 High 的 Linux 日降級，是判定更準不是資料遺失」 | 反例成立（管理員當日先以 root 登入、之後開始被掃）；既有已落盤紀錄不變，只有重跑才變 |
| 4 | A2b-03 快照契約 | **(a)** 含請求外 objid → 拒；真子集 → 收並計缺顆接現有警告 | 原契約防的是「PRTG 忽略 filter」，真子集不觸及 |
| 5 | A2b-02 校準匯出授權 | **(a)** 用批次 A 的 `RequireViewAll` helper | 校準是全站診斷，結論要可重現 |
| 6 | C1-07 兩個快取只靠 TTL | **(a) 鍵加 `DataVersionStamp.Current`，TTL 保留**；註解改為「版本戳在背景寫入執行中不推進，TTL 是上限不是可移除項」 | 反例修正：靜音已在鍵內、標記走非 GET 會推進；真正缺口是背景寫入 |
| 7 | A2b-08 規則變動中止回填／同步 | **(a)** 結構同步 Checkpoint 改 `ReadStructural`；`ReadStructural`／`ReadRawSemantic` 排除 rules；**`Read()` 輸出格式不動**（郵件冪等鍵與快照佇列雜湊依賴）；每日管線維持 `Read()` | 範圍與回填不讀規則；改 `Read()` 會讓升級後郵件冪等鍵全失效 |
| 8 | 處理人畫面「案件」 | 替代詞**「交辦」**，逐句對照見批次 E；稽核動作名（建立案件／案件同步／排程掛接案件／改派案件處理人）**不改**（歷史列一致）；說明書處理人可讀 .md 同步、.ai.md 與 manifest keywords 保留「案件」並加「交辦」 | §8.6a 已把「成員」定義為交辦單下的案件；稽核是證據層 |
| 9 | C3-20 日層級指派不過濾閘門 | **(a)** 套派工閘門＋目前靜音；預覽 DTO 加 `SkippedByReason`、畫面顯示「N 個問題因抑制／雜訊／靜音／不再打擾略過，如需指派請先解除」、寫入結果 toast 同句 | 審查 1：只改行為會讓管理者看到「建 0 件」不知原因 |
| 10 | A2a-08／09 down 與磁碟趨勢 | **(a) 以程式為準改 SPEC** | flapping 承接短暫中斷；測試已釘 |
| 11 | C2-05 Stuck 六小時 | **(a′)** 注入兩個 RunState，本行程持有者恆 Running；不加欄位 | 多實例排程遞延中 |
| 12 | C1-04 `session_expired` | **(b) 刪** | 價值低於節流複雜度 |
| 13 | A2a-02 逐小時對應往後沿用 | **(a)** 與 `FindLatestHostMapDate` 共用純函式 | §9b 白紙黑字 |
| 14 | §8.6-1 篩選記 localStorage | **(a) 改文件** | 下鑽帶參數是主流程 |
| 15 | BACKLOG 主題化 | 做（61-L） | — |
| 16 | 環境整理 | 刪殘留複本；worktree dirty≤3 直接移除、其餘先存 patch；`work/` 等三目錄請你定 | commit 全可達，風險只在未提交改動 |
| 17 | 輪次 | 三輪；59 順序 **A → B → C → D（D-6 gate 必填放 D 最後）→ E → F → G** | 審查 3：D-6 改建構子簽章，C 的 23 處測試若在 D 後改要重寫兩次，故 C 先 |
| 18 | 實作方式 | Sonnet subagent 一次一批；C、E 規格主代理親寫 | — |

## 3. 第 59 輪批次：正確性、安全、紅線

每批格式：現況（引用核對表）→ 改動表（ID｜改動｜驗收，驗收標 T／G／D）→ 文件 → 定向篩選。

### 批次 A：授權與安全

現況：核對表 A2b-01／C1-01、C1-02、C1-03、C1-06、D2-03；另 login DTO 無長度上限（AuthDtos.cs:5-12）、儀表板快取鍵缺 case-grant 維度（DashboardService.cs:88-89,152）、`interpret` 快取鍵用 hostName 無 hostId（AiInsightService.cs:172）、`SentinelEventFetchService.cs:165` 未跳脫、權限異動讀取 API（list／report／ids／categories）無能力標註、PRTG 授權「ViewAll」寫法散在 SettingsController／PrtgTransferController.cs:219／PrtgMonitoringController.cs:35 三處。

| ID | 改動 | 驗收 |
|---|---|---|
| A-1 | 新增 `RequireViewAll()`（Web/Auth 擴充，內部只做 `ICurrentUser.Has(Capability.ViewAll)`，非 ViewAll 擲 Forbidden；**不**重算可見集合）；`SettingsController.RequireFullPrtgTransferAccess`／`HasFullPrtgTransferAccess`、`PrtgEffectivenessController.cs:31`、`CalibrationController`（待決 5）改用；刪這三處對 `_hosts.GetAll()`／`IVisibilityService` 的授權用途。**不動** `PrtgTransferController.CaptureBinding`（它同時算範圍指紋，現況正確）與 `PrtgMonitoringController`（刻意支援受限使用者的監看政策路徑）。serverAdmin 能力集只有 Maintain／ViewAudit（RoleCapabilityMap.cs:70-74），新 helper 對它仍 403，與現況一致 | (T) `PrtgHostMapEndpointTests` 新增：HostStore 含一台 Active=false、一台 MergedInto，ViewAll 管理者 → `prtg-manual-map` GET 200；非 ViewAll Maintain → 403；serverAdmin → 403。`PrtgEffectivenessServiceTests` 新增端點授權兩案；`CalibrationController` 授權兩案。(G) SettingsController／PrtgEffectivenessController 不再含 `GetVisibleHostIds()`＋`GetAll()` 的授權比對。突變：helper 內 Has 改恆 true，非 ViewAll 403 測試必須紅 |
| A-2 | `DisplaySettingsController` 改注入 `ISystemSettingsStore` 直讀 `MailEnabled`／`MailNotifyWorkOrders`；`ReadReportUsage` 只留 `SystemSettingsService.Get()` 設定頁路徑 | (T) 新測試：以替身 `IReportUsageQuery` 組 `DisplaySettingsController`，呼叫 Get 後替身呼叫次數＝0；既有 `SystemSettingsServiceTests` 全綠。(G) `DisplaySettingsController.cs` 不含 `_settings.Get()` |
| A-3 | `HelpController.Ask` 掛 `[Permission(Capability.Maintain)]`；`AskAvailable` 對無 Maintain 回 `available:false`（不 403，前端 help-manual.js:149-155 已隱藏，不動前端） | (T) 新測試 `HelpControllerAuthorizationTests`：非 Maintain POST ask → 403；ask-available → 200 且 available=false；Maintain → 走 `IHelpQaService`。(G) `HelpController.cs` 的 Ask 上方有 `[Permission(Capability.Maintain)]` |
| A-4 | `LoginRequest.Account`／`Password` 加 `[StringLength(256)]`；新增單一 `AccountNames.Canonical(string)`（Trim、去掉第一個 `\` 之前的網域前綴），`LdapCredentialVerifier` 改用它（DRY）；`AuthController` 的 `LoginThrottle` 鍵用 Canonical；`IdentityService.Login` 的 `FindByAccount` 先查 Canonical、查不到再查原字串（相容既有含 `\` 的本地帳號）；serverAdmin 比對與稽核記錄維持原字串 | (T) 新測試：`CORP\bob` 與 `bob` 經 LDAP 假 provider 後 `FindByAccount` 收到 `bob`、LoginThrottle 同一桶；本地帳號 `a\b` 不剝；300 字帳號 → validation_failed 且 LoginThrottle 字典不新增鍵。既有 `LoginThrottleTests`、`DynamicAuthenticationProviderTests` 綠。(D) 升級注意事項加一句「升級前查 `lf_users` 的 account 含 `\` 者」 |
| A-5 | `isSafeReturnUrl` 含 `\t`、`\n`、`\r` 任一字元**直接拒絕**（比剝除後放行更保守）；其餘維持拒絕 `//`、`\`、`://`；函式搬到 `core/safe-return-url.js` 以便測試 | (T) 新 `safe-return-url.behavior.mjs`＋`SafeReturnUrlBehaviorTests`：`/\t/evil.com`、`/\n/evil`、`/\r/evil`、`//evil.com`、`/\evil.com`、`http://evil.com`、`evil.com`、null、空字串皆 false；`/`、`/records?x=1`、`/hosts/12#t` true；逐案檢查 PASS；突變拿掉控制字元判斷前三案必須 FAIL |
| A-6 | `interpret` 快取鍵改 `interpret|{hostName}|{date}|{issueKey}|{HashInput(prompt)}`，與同檔 focus／summary 兩把鍵一致（輸入不變則命中率不變，紀錄重新分析後才失效）。**撤銷儀表板快取鍵項**：核對發現儀表板的 `visibleHostIds` 來自 `GetVisibleHostIds()`（VisibilityService.cs:269-276），`IsCaseGrantOnly` 對可見主機恆回 false（:296），`fullIds` 篩選是死條件、快取鍵沒有缺維度；死條件的移除列入 60-H | (T) `AiInsightServiceTests`：同 host／date／issueKey 但 Count 不同 → 第二次不命中；完全相同 → 命中 |
| A-7 | `IssueRankingCache`／`ActionableSnapshotCache` 建構式注入 `DataVersionStamp`，鍵加 `Current`；TTL 保留；註解改寫；CLAUDE.md 快取紅線補「兩快取已含版本戳，TTL 為背景寫入期間的上限」 | (T) `IssueRankingCacheTests`／`ActionableSnapshotCacheTests`：版本推進後不命中、未推進且 TTL 內命中。(G) 所有 `new IssueRankingCache(` 呼叫點一次改完（grep 編譯即驗）。(D) 兩類別 `<summary>` 不再含「沒有單一版本錨點」 |
| A-8 | `SentinelEventFetchService.cs:165` `source` 只放行 `[A-Za-z0-9_.-]`，其餘回 null 並 Log.Debug | (T) 新測試：`source="sshd (x)"` → 不送查詢回 null；`source="sshd"` → 查詢含 `sp:sshd*` |
| A-9 | `GET /api/permission-changes`、`/report`、`/ids`、`/categories` 掛 `[Permission(Capability.ConfirmPermission)]`；確認儀表板 `CountPending` 不走這四個端點 | (T) 新測試：ViewAll 無 ConfirmPermission 呼叫四端點 → 403；有 → 200。(G) `grep -rn "api/permission-changes" wwwroot/js` 只在 permission-changes.js |

文件：WEB-SPEC §7.1（RequireViewAll 單一出口）、§9.5（API 能力）、§9.9c（ask-available 語意）、§9.9f；PRTG-SPEC §11 授權句；CLAUDE.md 快取紅線；18-audit 無需改。
定向：`--filter "FullyQualifiedName~PrtgHostMapEndpointTests|FullyQualifiedName~VisibilityServiceTests|FullyQualifiedName~CaseGrantVisibilityTests|FullyQualifiedName~AuthCookieTests|FullyQualifiedName~ServerAdminAuthenticatorTests|FullyQualifiedName~DynamicAuthenticationProviderTests|FullyQualifiedName~SecurityHardeningTests|FullyQualifiedName~LoginThrottleTests|FullyQualifiedName~DashboardServiceTests|FullyQualifiedName~AiInsightServiceTests|FullyQualifiedName~IssueRankingCache|FullyQualifiedName~ActionableSnapshotCacheTests|FullyQualifiedName~PrtgEffectivenessServiceTests|FullyQualifiedName~HostDetailPrtgAuthorizationTests|FullyQualifiedName~PrtgTransferControllerTests|FullyQualifiedName~SystemSettingsServiceTests|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout"`

### 批次 B：儲存層與雙後端

現況：核對表 B-01、B-02、B-05；`EfRiskyEventStore.Prune`（:151-165）整批 ToList；`PermissionChangeStore.Prune`（:551-558）單句；`QueryPage`（EfAnalysisRecordStore.cs:1538-1548）無唯一次要鍵；`DailyRecordBackfiller.cs:114` 以 JSON 覆寫 AiPending（版本 2 正是校正欄位漂移）；`BatchedPrune.cs:48-56` 無零進展中止；`EfJsonBlobStore.IsTransient` 全 DbUpdateException 重試；既有兩份 `Truncate`（EfWorkOrderStore.cs:46、EfPrtgStore.cs:2840）；復原服務 `ProcessOrphanPage`（HostDayWorkflowRecoveryHostedService.cs:216-250）每日已掃全部 workflow blob 並對保留期外的重置狀態但不刪。

| ID | 改動 | 驗收 |
|---|---|---|
| B-1 | 外部字串在**入口**截斷：`LogIssueSignature.Source`／`LogName` 於 `LogAggregator` 與 `SentinelEventMapper`、`PrtgFindingMapper` 建簽章時 clamp 255（key 衍生前），`WorkOrderIssueKey.SourceKeyOf` 內部同一 clamp；新增 `internal static ColumnClamp`（靜態 lazy 以 SQLite 記憶體 options 的 `LfDbContext.Model` 建「實體.屬性→MaxLength」表）供 store 端安全網：`MapToTopIssueRow`、`UpsertFirstSeen`、`EfRiskyEventStore`、`PermissionChangeStore`（InitiatorAccount／TargetAccount／ObjectType／HostName）、`EfIssueCaseStore`（issue_label）、`EfPrtgStore.UpsertManualMap`（Note／CreatedBy）；刪兩份既有 `Truncate` 改委派；`issue_key`（512）超長→拒寫並 Log.Warn（不截斷到段中）；`UpsertFirstSeen` 的 catch 只吞唯一鍵（SQLite 19、SQL Server 2601／2627）。已知限制（寫進 DB-SPEC）：SQLite 上升級前已存在的 >255 字 source 列不回補，之後同來源的新列會被視為另一個問題；實務上來源名超過 255 字極罕見 | (T) 新 `ColumnClampTests`：反射列舉 `LfDbContext` 所有有 MaxLength 的 string 屬性，斷言每個都在 Clamp 表（對帳）；逐 store 測試：`EfAnalysisRecordStore.Append` 餵 Source 300 字 → `lf_top_issues.source_name` 與 `source_key` 皆 255 且兩晚 `UpsertFirstSeen` 只一列、first_seen 取較早日；`EfRiskyEventStore.ReplaceDay` RuleId 100 字成功；`PermissionChangeStore.AppendChanges` 四欄超長落地＝MaxLength；`UpsertManualMap` Note 600 字→512；issue_key 600 字→不寫且 Warn。(G) `grep -rn "private static string? Truncate" Core` ＝0 |
| B-2 | `rule_id` 改 `HasMaxLength(100)`；SchemaUpgrader 加「每次啟動判斷 SQL Server `INFORMATION_SCHEMA` 長度 <100 才 `ALTER COLUMN rule_id nvarchar(100)`」；SQLite 不動作；兩種 CREATE TABLE 常數同步 | (T) `SchemaUpgraderTests`：SqlServer 升級 SQL 字串含 `ALTER COLUMN rule_id nvarchar(100)` 且受長度判斷包住；SQLite 舊表升級後 INSERT 100 字 rule_id 成功；EF model vs 兩份 DDL 長度對照測試（既有）綠 |
| B-3 | `workflow_*` 清除放進 `HostDayWorkflowRecoveryHostedService.ProcessOrphanPage`（同租約、同 `retentionFrom` 邊界）：對保留期外且 `ParentDeleted…` 處理完的 key 刪 blob；排除 `workflow_recovery_cursor`；每趟上限沿用 `BatchedPrune` 單次上限；清除數寫入該服務的 health detail | (T) `HostDayWorkflowTests`：三筆 blob（邊界日、邊界前一日、cursor）→ 只刪邊界前一日、cursor 與邊界日留下；超過單次上限留待下次；復原對「blob 已不存在」仍回 true（既有）。(D) DB-SPEC 新增「主機日工作流狀態 blob」一節（key、內容、保留期、首次清除分多日） |
| B-4 | `EfRiskyEventStore.Prune`、`PermissionChangeStore.Prune` 改 `BatchedPrune.Run<long>`；`RetentionPruner` 任一步驟失敗不寫 `LastRunDate`（只改這一行，不重排流程） | (T) `RiskyEventStoreContractTests`、`EfPermissionChangeStoreTests`：超過單次上限留待下次；`RetentionPrunerTests`：一步擲例外 → `LastRunDate` 不變 |
| B-5 | `StorageSettings.IsSqlite`／`IsSqlServer`（OrdinalIgnoreCase）；`StorageBackend`、`Program.cs` 改用 | (T) 新測試 `Type="sqlite"`／`"SQLITE"` 建 StorageBackend 走 SQLite；`IsValidType` 三種大小寫 true |
| B-6 | `QueryPage` 四個排序分支末尾 `.ThenBy(RecordId)`（同方向）；host 排序改 `HostNameKey`（或 ToUpper）不分大小寫 | (T) `RecordQueryTests`：同風險同日 300 列亂序插入、每頁 50 翻 6 頁聯集＝300 且無重複；突變拿掉 ThenBy 必須紅 |
| B-7 | `DailyRecordBackfiller`：`AiPending`／`AiAnalyzed` 只在 `extract_version < 2` 以 JSON 為準，≥2 保留欄位 | (T) 新測試：version=1 欄位=1／JSON=0 → 校正為 0；version=3 欄位=1／JSON=0 → 仍 1。(D) DB-SPEC ai_pending 回填語意一句 |
| B-8 | `BatchedPrune` 加零進展中止；`EfJsonBlobStore.IsTransient` 只對並發衝突、SQLite busy／locked、SQL Server deadlock（1205）重試 | (T) `PruneBudgetTests`：deleteByKeys 恆回 0 → 迴圈一次即止；`BlobStoreRoundTripTests`：唯一鍵 DbUpdateException 不重試直接拋；`HostDayWorkflowTests` 綠 |
| B-9 | BACKLOG 補「升級期一次性機制與退場條件」表（Handling／PermissionChange／ReportFile 遷移、TopIssue／WorkOrder／DailyRecord 回填、`RemoveRetiredPrtgBlobs`、manual map identity 檢查）；BACKLOG:304 雙份索引改寫 | (D) 表存在且每列有「退場條件」欄 |

移出本輪：TopIssueBackfill 啟動快判（B-07 體檢項）→ 60-K，且快判必須逐閘門（top／first_seen／risky）分開，不得合併旗標。
文件：DB-SPEC（保留策略六期、workflow_ 節、rule_id、ai_pending 回填、雙 DB 規則第三列）、BACKLOG。
定向：`--filter "FullyQualifiedName~AnalysisRecordStoreContractTests|FullyQualifiedName~RiskyEventStoreContractTests|FullyQualifiedName~EfPermissionChangeStoreTests|FullyQualifiedName~PruneBudgetTests|FullyQualifiedName~SchemaUpgraderTests|FullyQualifiedName~DataConsistencyJ2Tests|FullyQualifiedName~SourceKeyBackfillTests|FullyQualifiedName~EfIssueAggregateQueryReadyParityTests|FullyQualifiedName~HostDayWorkflowTests|FullyQualifiedName~EfPrtgStoreTests|FullyQualifiedName~RetentionPrunerTests|FullyQualifiedName~BlobStoreRoundTripTests|FullyQualifiedName~RecordQueryTests|FullyQualifiedName~ColumnClamp|FullyQualifiedName~WorkOrder|FullyQualifiedName~IssueAggregateQueryTests"`

### 批次 C：偵測邏輯（規格主代理親寫）

現況：核對表 A1-01（含樣本清空事實）、A1-02、A1-06；A1-03（`ApplyRule` 未達門檻只降級、`Find` 只比來源與 EventId；降級無持久化標記，`PrtgCorroboration.Refresh` 重算已存檔簽章時無法重現）、A1-04（Linux 關聯只用 `FailedPasswordRegex`）、A1-05、A1-07（`SlowTrendAnalyzer` 無殘留憑證降噪）、A1-08（報告靜態 `FindRule`，Linux 恆空、重啟後是種子）、A1-09／10、A1-17（prompt 寫死 Windows Server）、A2a-03 依賴方（`PrtgHistoricalRiskReview`、`PrtgCorroboration`、`CorrelationPatternIds.All`、`RuleAdminService.IsValid`、rules.js targetLabel）。

| ID | 改動 | 驗收 |
|---|---|---|
| C-1 | 前置掃描退場（待決 1-b′）：統計段 `needsAi = useAi && !lowRisk`；`RetryAiAsync` 對 `RiskLevel==低 && RiskBasis != "ai_raise"` 回 `AiOutcome.Skipped`（不呼叫 AI；落盤 `AiPending=false`、`AiAnalyzed=false`、headline 不含「AI 待補」）；`AiAnalysisHostedService` 對 Skipped：計 `Skipped`、不計 Failed、不計 AiCalls、不觸發「全滅」防線；刪 `AnalyzeDayAsync`／`CompleteAiAsync`／`ApplyOutcome`／`ScreenTailAsync`／`ScreeningOutcome`／`AiWorkItem.Logs`／`FinalizeModeReplayReportAsync`；`DailyAnalysisRecord.ScreenedTailCount`／`ScreeningNotes` 欄位保留讀取相容（舊資料）但不再寫入；23 處 `AnalyzeDayAsync` 測試改為「統計段＋RetryAiAsync」 | (T) `LogAnalysisServiceRetryAiTests`：低風險紀錄 → AI 替身零呼叫、Skipped、AiPending=false；`RiskBasis=ai_raise` 的低風險紀錄 → 仍走主分析；統計段低風險 tail 30 種 → AiPending=false；`AiAnalysisSchedulerTests`：一批 5 筆含 3 筆 Skipped → Done 5、Failed 0、AiCalls 2、不中止。(G) `grep -rn "AnalyzeDayAsync\|CompleteAiAsync\|ScreenTailAsync" Core Web Tests` ＝0；`NetiqPipelineService.cs:669` 的 `workItem == null` 判定測試（`NetiqPipelineBaselineTests`）與 `AnalysisOrchestrator` 的 `hadAiWorkItem` 測試綠。(D) DETECTION-SPEC 小模型策略 #1／#10、補跑語意改寫；說明書 04-record-detail.md:16、10-scheduler.ai.md:34,38「待補判定」補「低風險日不進 AI」；format.js `isAiRetryPending` 不受影響（headline 無「AI 待補」） |
| C-2 | 移除三條 `prtg-*-evidence-aligned`：`CorrelationPatternIds`、`PrtgCorroboration`（對齊半邊、SuppressionInheritance、MaximumEvidencePairComparisons）、`PrtgHistoricalRiskReview.EvidenceAlignmentPatterns`、`PrtgFinding` 六個無生產者屬性、`ToSignature` observations 映射、`LogIssueSignature.SourceObservations`／`SourceObservationsTruncated`（含 `lf_top_issues` 兩欄的寫入端；欄位保留不刪 schema）、對應測試；抑制清單 DTO 加 `retired`（`!CorrelationPatternIds.IsValid`），rules.js 目標欄顯示「已退役（模式已移除）」徽章 | (T) `PrtgCorroborationTests` 刪手造對齊案例後全綠；`CorrelationPatternIdsTests`（新）：`All.Length==23` 且無 `evidence-aligned`；`RuleAdminServiceTests`：含 `prtg-storage-evidence-aligned` 的既有抑制列可載入、`retired=true`、可刪除、其他抑制不受影響；新增 `RulesPageRetiredBadgeUiTests`：rules.js 含退役徽章文案。(G) `grep -rn "EvidenceAligned\|SourceObservations" Core Web` 只剩 LogIssueSignature 的相容欄位註解。(D) DETECTION-SPEC「合計 23（Windows 18、Linux 2、PRTG 3）」；RULES-SPEC 抑制表；PRTG-SPEC §9；07-suppression.ai.md:45、01-overview.ai.md:88 改 23；BACKLOG:430 刪 |
| C-3 | 【滅跡】：「其他安全事件」排除 `Severity==Low`、收集用（`KnownIssue` 規則標記為 Collect／RDP 收集規則）、被降級、被抑制者；4907 不單獨作錨點 | (T) `CorrelationAnalyzerTests` 反例：4907＋只有 RDP 收集簽章不命中；4719＋Low 簽章不命中；1102＋High Security 命中（正例保留）。突變：拿掉 Low 排除必須紅 |
| C-4 | `CorrelationAnalyzer.Analyze` 的 `Find`／`Has` 只認未被 `CountThreshold` 降級的簽章：`KnownIssueCatalog.ApplyRule` 降級時在記憶體簽章設 `Demoted=true`（**不持久化**、`[JsonIgnore]`，KISS）；`PrtgCorroboration`（弱佐證、非風險訊號）維持現行不看降級，規格明寫這個不對稱與理由；【時間偏移→驗證失敗】限 29/36/47/50 | (T) 4703 x1＋7045 不命中、x10 命中；1026 x1＋7034 不命中；Time-Service 事件 Id 1 配 4625 不命中、Id 36 命中；`PrtgCorroborationTests` 既有案例全綠（行為不變）。(G) `Demoted` 標 `[JsonIgnore]` 且不出現在 `RecordStorageShaper` 投影 |
| C-5 | Linux 關聯：失敗面改 `LinuxAuthParser.TryParseAuthFailure`（Invalid user／pam 含，`rhost=` 空不算解析失敗）；加時序（帳號與 IP 各自最晚成功 ≥ 最早失敗，共用 `FilterByTiming`）；`LinuxCorrelationAnalyzerTests.cs:30-42` 改帶時間 | (T) 混合三類失敗行＋成功 → 不產生「不確定」；成功 09:00、失敗 10:00 起 → 不命中；失敗 09:00 起、成功 11:00 → 命中。(D) LINUX-RULES §關聯層補時序；DETECTION-SPEC Linux 列；升級注意事項一句 |
| C-6 | AI 降級路徑（原文保留）：**不再**對原文呼叫 Normalize，直接 `riskLevel = ruleRisk`、`riskBasisOverride = null`，headline「AI 回覆格式異常，以下為原始內容」；成功路徑的 `Normalize(result.Value.RiskLevel)` 改為「去空白後長度 ≤4 且以 高／中／低 開頭」才對應（容許 prompt 要求「低、中、高擇一」下的短變體如「高風險」「中等」），其餘「未知」（`MoreSevere(未知, ruleRisk)` 本就回 ruleRisk，不會降級） | (T) `RiskLevelsTests`：「高」「高風險」「中等」「 低 」→對應；「其中有問題」「高度懷疑但中斷」「」「高風險，中等」→未知；`LogAnalysisServiceRetryAiTests`：壞 JSON 原文含「高」→ 風險＝ruleRisk、無 ai_raise；JSON risk_level=「高風險」→ 可拉高。(D) DETECTION-SPEC 穩定性表一句 |
| C-7 | `SlowTrendAnalyzer` 對殘留憑證簽章比照 Rising 進 `suppressedAlerts` 並加註原因 | (T) `SlowTrendAnalyzerTests`：殘留簽章慢速惡化 → alerts 空、suppressedAlerts 1；一般簽章不變。(D) DETECTION-SPEC 殘留後果補慢速趨勢 |
| C-8 | `RiskReportService.BuildStaticOutcome` 以簽章 `RuleId` 反查，規則清單由呼叫端注入（`ResolveRules(store)`）；`SelectFocusIssues` 排除 `Suppressed`；`SelectRawLogs` 用 `LogAggregator.GroupKeyFor` | (T) `RiskReportServiceTests`：Linux 規則簽章有處置參考；自訂規則（不在種子）有處置參考；Suppressed High 不進重點問題；同 program 不同規則的原始 log 不互撈。(D) BACKLOG:244 改寫 |
| C-9 | prompt 依 `hostOs` 取「Windows Server」／「Linux 主機」（AnalysisPromptBuilder、WeeklyCheckupService、RiskReportService DeepDive） | (T) `AnalysisPromptBuilderPrtgTests` 或新測試：hostOs=linux 的 prompt 不含「Windows Server」 |
| C-10 | 文件：DETECTION-SPEC（PatternId 23、逾時不重試、rep_pen＋AppSettings.cs:112 註解、體檢觸發改 SystemSettings、前置掃描段刪除、補跑語意、關聯層表【滅跡】定義與 4703 門檻、殘留後果）；RULES-SPEC（抑制表 18/2/3、Host 比對對象、`rules.json`／`--import-rules`／「未來 DB 映射」歷史段移 archive）；LINUX-RULES（28 條不綁版本、時序）；RuleValidator「17 條種子」註解；`KnownIssueCatalog.FindLinuxRule` 補 Enabled | (D) 逐句比對；(G) `grep -n "前置掃描" docs/DETECTION-SPEC.md` ＝0 |

定向：`--filter "FullyQualifiedName~LogAnalysisService|FullyQualifiedName~CorrelationAnalyzer|FullyQualifiedName~LinuxCorrelationAnalyzerTests|FullyQualifiedName~RiskLevelsTests|FullyQualifiedName~SlowTrendAnalyzerTests|FullyQualifiedName~RiskReportServiceTests|FullyQualifiedName~AnalysisPromptBuilder|FullyQualifiedName~AiAnalysisSchedulerTests|FullyQualifiedName~HostDayWorkflowTests|FullyQualifiedName~TrendAnalyzer|FullyQualifiedName~PrtgCorroborationTests|FullyQualifiedName~PrtgHistoricalRiskReview|FullyQualifiedName~RuleAdminServiceTests|FullyQualifiedName~NetiqPipelineBaselineTests|FullyQualifiedName~UiTests|FullyQualifiedName~CorrelationPatternIds"`

### 批次 D：排程、背景工作、AI 整合（D-6 放最後）

現況：核對表 C2-01；C2-02／14（`IsScheduledFetchRun` 只依賴 `BatchRun`，重複判準 3 處；run state 類比對的是沒有 JobType 的 Trigger 字串；`SchedulerHostedService.cs:180` 用 `StartsWith`）；C2-13（寄信 `_mail` 為具體類別 `MailNotificationService`，`_lastMailTask.WaitAsync(5 分鐘)` 在輪詢路徑）；F 四處 BackgroundService 輪詢 catch 吞 OCE；C2-04／07／08／17／21；C2-16／A1-20（gate 可 null）；A3-01／02；C2-05。

| ID | 改動 | 驗收 |
|---|---|---|
| D-1 | `TriggerRunAsync`：`BatchResetAiPending()` 移入 Task.Run 的 try（或包 try/catch 失敗 `EndRun(false, ex.Message)` 後 throw） | (T) `AiAnalysisSchedulerTests`：重標替身擲例外 → 之後 `TryBeginRun` 可再取得且 `IsRunning=false`。突變移回 try 外必須紅 |
| D-2 | Core 新增 `BatchRunTriggers.Schedule` 常數與 `BatchRun.IsScheduledFetch()`（＝Trigger==Schedule && JobType!=ai）；BatchRun 列的判定（`SchedulerHostedService.IsScheduledFetchRun`、`LastScheduledPrtgDay`、`ScheduleFreshnessService.cs:33-34`、`PrtgReevaluationPlanner.cs:35,58`）改用擴充；run state 類（`SchedulerRunState.cs:237`、`AiAnalysisRunState.cs:151`、`AiAnalysisHostedService.cs:155,173,220`、`AnalysisOrchestrator.cs:1327`、`SchedulerHostedService.cs:171,180`）改用常數；`StartsWith("schedule")` 先 grep 確認無 `schedule*` 變體後改 `==` | (T) `SchedulerTriggerQueryTests`、`ScheduleFreshnessTests`、`PrtgReevaluation` 測試綠；新測試：JobType=ai 的 Trigger=schedule 列在三處判定皆不算排程取數。(G) regex `\.Trigger\s*==\s*"schedule"` 於 Core＋Web 非測試檔 ＝0；`"schedule"` 字面只剩常數定義與稽核 targetKind／預設參數（白名單列出行號） |
| D-3 | `TickAsync`：窗口 End 停止與觸發判斷移到寄信之前；寄信 Task 以 `Func<DateTime, CancellationToken, Task>` 注入（預設包 `_mail.CheckAndSendDailyWeeklyAsync`），輪詢只 `IsCompleted` 檢查不 await 超過 1 秒；四處輪詢 catch（PrtgSnapshotHostedService.cs:287、SchedulerHostedService.cs:112,434、PermissionChangeMigrationHostedService.cs:60）加 `when (!(ex is OperationCanceledException && stoppingToken.IsCancellationRequested))` | (T) 新測試：寄信 Func 回永不完成的 Task、窗口 End 已到 → 一個 tick 內 `TryCancel` 被呼叫；stoppingToken 取消時 Error log 替身收到 0 筆 |
| D-4 | 處理迴圈以上一批 `remaining` 遞推、輪詢用 `_runState.GetPendingAiCount`；`AiCalls` 只算實際呼叫 AI 的日子（Done − Skipped），`AiFailures` 不變；同一輪共用一個 `AIService`（`ResolveAiService` 一輪一次）；`ChatJsonAsync` 傳輸層失敗直接 break | (T) `AiAnalysisSchedulerTests`：50 筆一批 → `CountPendingAi` 呼叫 ≤2 次；AiCalls＝實際呼叫數；`ResolveAiService` 一輪一次；`AIServiceRetryCountZeroTests` 或新測試：ChatAsync 逾時 → 只呼叫一次 |
| D-5 | `IssueOwnedHostIdsCache` 只在 `version > _seenVersion` 清空 | (T) `IssueOwnedHostIdsCacheTests`：v2 後 v1 請求不清空 |
| D-6 | `aiGate` 改必填：`AIService`、`AnalysisOrchestrator`、`AiAnalysisHostedService`、`HostDayWorkflowRecoveryHostedService`、`WebAiService`；`AiRequestGateTests` 改 DI 解析斷言三服務內部 gate 非 `NullAiRequestGate`，刪 `Assert.Equal(5, calls.Count)` | (T) 新測試用正式 `ServiceCollectionExtensions` 組 DI 解析三服務，反射取 gate 欄位型別≠Null；(G) `grep -rn "IAiRequestGate? " Core Web` ＝0 |
| D-7 | Sentinel Polly filter（Auth.cs:37,107）加 `&& !ct.IsCancellationRequested`；pipeline「Found>0 且 Retrieved=0 且未達 max-results」→ Unreadable：不二分、不設 IsHighVolume、該批 receipt=failed 並出聲 | (T) `SentinelClientTests`：handler 在 SendAsync 內觸發 ct 取消 → 擲 OCE 非 SentinelClientException；`NetiqPipelineBaselineTests`：Found=10 Retrieved=0 → 查詢次數 1、IsHighVolume 不變、AddFailed 1 |
| D-8 | Stuck（待決 11）：`RunMonitorService`、`ScheduleFreshnessService` 注入 `SchedulerRunState`／`AiAnalysisRunState`；RunId 為本行程持有且 `IsRunning` → Running；其餘套 6 小時 | (T) `RunMonitorServiceTests`：BatchRun StartedAt=7 小時前、RunState 持有該 RunId → Running；不持有 → Stuck |
| D-9 | 文件與註解：WEB-SPEC §9.10 敘事行、AI 呼叫節流補探活例外（C2-15 只補文件）、2724 待補件數快取；`BatchRun.Trigger` 註解補 `fetch-followup`；XML 註解錯位四處；`RunTriggerText`／`RunMonitorService` 兩份 switch 本輪不動（60-I 合併） | (D) 逐句比對 |

定向：`--filter "FullyQualifiedName~AiAnalysisSchedulerTests|FullyQualifiedName~SchedulerTriggerQueryTests|FullyQualifiedName~ScheduleFreshnessTests|FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~AiRequestGateTests|FullyQualifiedName~AIService|FullyQualifiedName~BatchRun|FullyQualifiedName~BackgroundWorkGateTests|FullyQualifiedName~IssueOwnedHostIdsCacheTests|FullyQualifiedName~SentinelClientTests|FullyQualifiedName~SentinelRestDirectoryClientTests|FullyQualifiedName~NetiqPipeline|FullyQualifiedName~InterruptedRunTests|FullyQualifiedName~PrtgReevaluation|FullyQualifiedName~HostDayWorkflow"`

### 批次 E：交辦與問題處理（規格主代理親寫）

現況：核對表 C3-01、C3-04；C3-02、C3-05、C3-06、C3-07、C3-14、C3-15（7 處裸轉型）、C3-16、C3-03／D2-12（顯示點清單見 E-10）、C3-20（預覽 DTO `HandlingAssignPreviewDto` 無略過欄位，handling-panel.js:252-259）。

| ID | 改動 | 驗收 |
|---|---|---|
| E-1 | `IIssueOwnerStore` 新增單一 `IssueProfile Update(string source, int eventId, Action<IssueProfile> apply)`（不存在則 new 後套用，於 `Mutate` 原子區塊內）；`IssueOwnerAdminService` 的 SetMute／SetConclusion／ClearConclusion／ClearMute／Upsert(owners) 改用；`Upsert` 保留給匯入／新增 | (T) `IssueOwnerStoreRoundTripTests`：對真 store 以假 `Get` 回過期副本模擬舊流程不再可能（改用 Update 後）：先 `Update(加靜音)` 再 `Update(設結論)` → 兩者並存；`IssueOwnerAdminServiceTests`、`IssueMuteTests` 綠。(G) `IssueOwnerAdminService.cs` 不含 `_issueOwners.Upsert(` 於五個入口 |
| E-2 | `ReportService` scope≠all 記憶體路徑 `TotalIssues` 剔除 `exclusion.IsMuted`；`CoverageGapDays` 口徑以 SQL 路徑為準 | (T) `ReportServiceTests`：同資料有一筆靜音，scope=all 與 unresolved 的 TotalIssues 相等 |
| E-3 | 刪 `IssueCaseCoordinator.BuildCase`／`ReassignCase`／`CreateOpenCase`；`NightlyDispatchTests`、`IssueCaseCoordinatorTests`、`SourceKeyComparerTests` 前置改走 `WorkOrderCoordinator.Create`；類別摘要改現況；守門測試「`new IssueCase` 只在 WorkOrderCoordinator 與 IssueCaseCoordinator 的 SyncStatus」 | (G) `grep -rn "BuildCase(\|ReassignCase(" Core Web Tests` ＝0；(T) 新守門測試掃 `new IssueCase` 檔案白名單 |
| E-4 | `AttachNewDay` 存檔前重 Get，只套 `LastLinkedDate`／`PrtgEvidence`（同 `ApplyPendingCaseDays`） | (T) `CaseDaySyncTests`：假 `IIssueCaseStore` 在 Get 與 SaveMany 之間 callback 把案件結案 → 存檔後 `ClosedAt` 非 null |
| E-5 | `WriteNightlyMembersLocked` 鎖內 `GetOpenMany` 比對，已有進行中案件者計入略過；寫入順序案件→逐日列→log，規格寫明重入冪等 | (T) `NightlyDispatchTests`：Web 已建同（主機,問題）案件 → 略過計數 1、無例外 |
| E-6 | `DispatchContext` 改用 `IssueExclusion.From(allProfiles, now.Date).IsMuted`，刪自建字典 | (T) `NightlyDispatchTests` 靜音案例綠；(G) `DispatchContext.cs` 不含 `MuteIntervals` |
| E-7 | 貢獻帳本：`TryReserveMailContributionKeys` 回 null 時 `Log.Warn`＋health detail（收件人、佔用數）；上限改可注入常數（預設不變）；分帳與釋放策略**不在本輪**（記 BACKLOG） | (T) `MailNotificationServiceTests`：上限注入 3、第 4 筆預約 → Warn 替身收到 1 筆、health detail 含收件人 |
| E-8 | 7 處裸 `(IssueSeverity)` 轉型改 `LegacySeverityRank.ToSeverity`；守門 grep 測試 | (T) `OccurrenceStatusResolverTests`：SeverityRank=3 舊列計入未處理；(G) regex `\(IssueSeverity\)\s*\w+\.?\w*Rank` 於 Core＋Web ＝0 |
| E-9 | `RunAutoDispatch` 分組鍵加 `Decision.Origin`，建單帶對應 Origin 與備註 | (T) `WorkOrderBoardServiceTests`：owner_rule 決策 → 單 Origin=owner_rule |
| E-10 | 用詞（待決 8）逐句：handling-panel.js:254「建立案件並交辦給」→「建立交辦給」、:278「已為 N 個問題建立案件」→「已為 N 個問題建立交辦」、:281「的案件涵蓋」→「的交辦涵蓋」；record-detail.js:586,617「由 X 的案件處理中」→「已在 X 的交辦單處理中」；host-detail.js:462,464「案件處理人／案件狀態」→「交辦處理人／交辦狀態」、:499「案件同步」→「交辦逐日同步」、:67/153/346「案件授與」→「交辦授與」；layout.js:717「案件逐日同步」→「交辦逐日同步」；ui.js:1466「將以案件授與補上」→「將以交辦授與補上」；work-orders.js:457「尚無案件負載資料」→「尚無交辦負載資料」；issue-owners.js:45「自動建立案件」→「自動建立交辦」；handler-detail.js:1039；records.js:629,1276,1524,2155（Assign 面板，管理者可見仍改以求一致）；DomainException：IssueHandlingCommandService.cs:128,208,268,570、DayHandlingCommandService.cs:202-203；稽核動作名不改；`FrontendConsistencyUiTests:419-424` 斷言同步；新增守門「pages 與 core 的使用者可見字串不得含『案件』」（白名單：user-detail.js、prtg-admin.js、runs.js、audit 動作名）；說明書 05-handling.md 章名「處理狀態、交辦單與案件」→「處理狀態與交辦單」（03-issues:55、04-record-detail:38、12-settings:140 三處引用同步、manifest title 同步）、§「案件與跨日問題」改「成員與跨日問題」、:105「案件授與」；.ai.md 與 manifest keywords 保留「案件」並加「交辦」；§8.6a 補「交辦授與」定義 | (T) 新 `HandlerFacingWordingUiTests`：掃 wwwroot/js 非白名單檔的字串字面量不含「案件」；`HelpNavigationConsistencyTests`、`HelpContentServiceTests` 綠；`FrontendConsistencyUiTests` 綠。(G) `grep -c "案件" HelpContent/05-handling.md` 只剩「成員與跨日問題」段內解釋句（≤3）；manifest keywords 同時含「案件」「交辦」 |
| E-11 | 日層級指派（待決 9）：`PlanCasesForDay` 套派工閘門（Suppressed、已知雜訊、嚴重度、不再打擾）＋目前靜音；`DayCasePlan`／`HandlingAssignPreviewDto` 加 `SkippedByReason`（字典）；handling-panel.js 預覽與寫入 toast 顯示「N 個問題因抑制／雜訊／靜音／不再打擾略過，如需指派請先解除」 | (T) `DayHandlingDerivationTests` 或新 `DayHandlingCommandServiceTests`：一台主機 3 問題（1 抑制、1 靜音、1 正常）→ CasesToCreate 1、SkippedByReason {suppressed:1, muted:1}，寫入只建 1；`HandlingStatusCatalogUiTests` 新增案例：handling-panel.js 含略過文案。(D) WEB-SPEC §9.3／§9.4b 補一句 |
| E-12 | 文件：§9.1 項目符號還原＋敘事、§9.6 scope 作用範圍、§9.4b 待派原因移除 disabled＋試跑說明＋related_warning_active＋due-date API＋90 天、L2979 MailNotifyStateStore 欄位、§9.4b 單一列判定、BACKLOG:341 改寫、:557 補交錯、IssueCaseCoordinator 類別摘要 | (D) 逐句比對 |

定向：`--filter "FullyQualifiedName~IssueMuteTests|FullyQualifiedName~IssueOwner|FullyQualifiedName~ReportServiceTests|FullyQualifiedName~NightlyDispatchTests|FullyQualifiedName~IssueCaseCoordinatorTests|FullyQualifiedName~WorkOrder|FullyQualifiedName~CaseDaySyncTests|FullyQualifiedName~MailNotificationServiceTests|FullyQualifiedName~DayHandling|FullyQualifiedName~HandlingServiceTests|FullyQualifiedName~OccurrenceStatus|FullyQualifiedName~DashboardServiceTests|FullyQualifiedName~HelpContentServiceTests|FullyQualifiedName~HelpNavigationConsistencyTests|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout"`

### 批次 F：PRTG 與 NetIQ 主機

現況：A2a-07、A2a-06、A2a-02、A2b-03、A2b-08（`Read()` 呼叫端：StructureSync Checkpoint、Snapshot 佇列雜湊 :369/957/980/1022、Backfill 前後比對 :453/467、Probe 非 dataFlow、Mail 冪等鍵 :1884、每日管線 :125；`ReadRawSemantic`：Snapshot :406、Backfill :613/810、Probe dataFlow :273）、A2b-09／10／11／12、A3-03／04／05／06／07／14／19、A2a-08／09／11。

| ID | 改動 | 驗收 |
|---|---|---|
| F-1 | `PrtgScopeDevices.Compute`：鏡像沒有的人工對應 device 不入範圍、計入 `ManualMissingInMirror` 並出聲；維護頁人工對應列顯示「PRTG 已無此裝置」與既有移除按鈕 | (T) `PrtgScopeDevicesTests`：人工對應 device 不在鏡像 → 不入範圍、計數 1、警告 1 行；`PrtgAdminPageUiTests`：prtg-admin.js 含該文案 |
| F-2 | `PrtgTablePager`：首頁缺 `content` → 擲 `PrtgPagingNotConvergedException`；結束時 `treeSize>0 && readRows<treeSize && !stoppedEarly` → devices／sensors 擲（階段失敗、不清除）、messages 只 Log.Warn | (T) `PrtgTablePagerTests`：首頁 `{}` 擲；短頁 treesize 更大 devices 擲、messages 不擲有警告；`PrtgFetchServiceTests`：擲後裝置不被 `DeleteDevicesNotSyncedSince` 清 |
| F-3 | 往後沿用（待決 13）：抽 `PrtgHostMapLookup.Resolve(maps, day, lookback)` 純函式（找不到 ≤day 回看窗內 → 取 >day 最早一份 ≤today），`MappedToSensorHost` 與 `FindLatestHostMapDate` 共用；`GetReadinessMaps` 窗延到 today | (T) `PrtgValueAssessmentTests`：只有今日一份 ok 對應、回填 30 日數值 → 全部可用、原因非 MappingInvalid；`EfPrtgStoreTests` 既有沿用案例綠 |
| F-4 | 快照契約（待決 4）：含請求外 objid → 拒；真子集 → 收並計缺顆接 :704-710 警告；§3b 與測試同步 | (T) `PrtgSnapshotHostedServiceTests`：真子集 → 寫入且警告含「缺 N 顆」；含請求外 → 拒不寫 |
| F-5 | 範圍柵欄（待決 7）：`ReadStructural`／`ReadRawSemantic` 排除 rules；`PrtgStructureSyncService` Checkpoint 改 `ReadStructural`；`Read()` 字串格式與其餘呼叫端不動 | (T) `PrtgStructureSyncServiceTests`：同步中規則 blob 版本 +1 → Checkpoint 不取消；設定變更 → 取消；`PrtgDailyPipelineTests` 規則變動仍中止。(G) `Read()` 實作 diff 為空 |
| F-6 | `PrtgSnapshotHostedService`：`_sensorTypes`／`_targetObjids` 區域副本；目標為空 → 寫 `RecordDiagnostic("範圍為空")`、不 `RecordSuccess` 也不計失敗、健康頁顯示原因 | (T) 空範圍 → `LastSuccessAt` 不變、`ConsecutiveFailures` 不變、diagnostics 含「範圍為空」 |
| F-7 | `PrtgStructureSyncRunner`：`Devices==0` → `Success=false`＋訊息「PRTG 回傳 0 台裝置（檢查帳號權限與取數範圍）」；`PrtgStructureSyncService` 一般例外也 `Persist(失敗)`；三個 `using var operation = new PrtgOperationScope` 移進 try | (T) `PrtgStructureSyncServiceTests`：0 裝置 → 失敗＋訊息；一般例外 → 狀態 Persist 為失敗；scope 建構擲例外 → `EndRun` 仍被呼叫 |
| F-8 | NetIQ 主機：`AddHost` 比照 `BulkAddHosts` 保留未提供欄位；`SetActive` 帶 `OrphanedFromSentinel`（`SentinelAdminService.SetActive` 同查）；`BuildIpClause` 只放行 IPv4、其餘進 `Warnings`；非 windows／linux Os → `AddWarning`；`NormalizeKnownIps` 移除全域 >500 閘門；精靈輪詢只對「工作不存在／逾期」停、暫時性錯誤重試 3 次；重新認證前 best-effort DELETE 舊 token | (T) `NetiqHostServiceTests`：AddHost 省略 Sentinel 不清空；SetActive 保留孤兒標記；`SentinelQueryBuilder` 測試：IPv6 進 Warnings 不進子句；`NetiqPipelineBaselineTests`：Os=unknown 的主機出警告；`SentinelRestDirectoryClientTests.cs:242` 改為 600 台仍排除；`NetiqScanPickerUiTests`：wizard 含重試邏輯字串；`SentinelClientTests`：重新認證前有 DELETE |
| F-9 | 文件：PRTG-SPEC :376 down、:449 磁碟、§3b 真子集、§3c 人工對應缺鏡像、§4 名稱退路、§9b 沿用維持、§11 授權、§3b 請求預算（A2a-05）、§7 監看政策（A2a-10）、§8 operations、§6 資料流、§10 三空陣列；NETIQ 12 條；WEB-SPEC §9.9a 補三項；BACKLOG NetIQ #4／#8／#9／:302；註解 A3-18 五處、A2b-16、A2a-18 | (D) 逐句比對 |

定向：`--filter "FullyQualifiedName~PrtgScopeDevicesTests|FullyQualifiedName~PrtgTablePagerTests|FullyQualifiedName~PrtgFetchServiceTests|FullyQualifiedName~PrtgValueAssessmentTests|FullyQualifiedName~PrtgSnapshotHostedServiceTests|FullyQualifiedName~PrtgStructureSync|FullyQualifiedName~PrtgBackfill|FullyQualifiedName~PrtgProbe|FullyQualifiedName~PrtgDailyPipelineTests|FullyQualifiedName~EfPrtgStoreTests|FullyQualifiedName~NetiqOrphanSweeperTests|FullyQualifiedName~NetiqDiscoveryServiceTests|FullyQualifiedName~NetiqHost|FullyQualifiedName~SentinelQueryBuilder|FullyQualifiedName~SentinelRestDirectoryClientTests|FullyQualifiedName~SentinelClientTests|FullyQualifiedName~NetiqPipeline|FullyQualifiedName~NetiqScanPickerUiTests|FullyQualifiedName~PrtgAdminPageUiTests|FullyQualifiedName~MailNotificationServiceTests"`

### 批次 G：前端健壯性

現況：D1-01／04／D2-10（無序號守門）、D1-02、D1-03（5 處 `setInterval(async)`）、D1-05、D2-01、D2-02、D2-04、D2-05／09、D2-06（`localStorage` 無 try：dashboard.js:17,830、reports.js:65,434,445、handler-detail.js:1106,1121、ui.js:235,241）、D2-08、D1-06／D2-07、D1-08、D1-17、D2-14／15／22／29／30／31、D1-07；`guardLoad(containers, fn, {backLink})` 已存在（ui.js:1149）。

| ID | 改動 | 驗收 |
|---|---|---|
| G-1 | 新檔 `core/async-guards.js`：`createSeqGuard()`（`{ next(), isCurrent(seq) }`）、`pollWhile(fn, intervalMs)`（await 完成才排下一輪、try/finally；**不做** hidden 降頻與退避，記 BACKLOG）；新檔 `core/storage.js`：`safeStorage.get/set/remove`（try/catch 回 null）；不新增 `reloadWith`，各頁直接用既有 `guardLoad`；`toast` 不加執行期降級 | (T) 新 `async-guards.behavior.mjs`：舊序號晚到 `isCurrent` false；`pollWhile` 上一輪未回不發下一輪、fn 擲例外下一輪仍排；`storage.behavior.mjs`：getItem 擲例外回 null；`JsModuleImportTests` 認得新 core 檔 |
| G-2 | records.js `search()`、runs.js `loadSummary()`、hosts／audit／permission-changes／dashboard／reports 查詢改序號守門＋`guardLoad`；records 把請求當下的視角鎖進序號 | (T) 新 `records-search.behavior.mjs`（切 `search` 函式＋可控時序的 api 替身）：舊回應晚到不渲染、視角切換後舊形狀不進 render；`RunsPageUiTests` 與新增 `ListPagesSeqGuardUiTests`（hosts／audit／permission-changes／dashboard／reports）斷言含 `createSeqGuard` 匯入 |
| G-3 | runs.js `refreshScheduleStatus` try/finally 排下一輪；5 處 `setInterval(async` 改 `pollWhile` | (G) `grep -rn "setInterval(async" wwwroot/js/pages` ＝0；(T) UiTests 守門同一條 |
| G-4 | permission-changes 非網段錯誤 `throw`；users modal 儲存後重載失敗顯示 `renderError` | (T) `permission-changes-load.behavior.mjs`：api 擲一般錯誤 → 容器為 renderError 非空狀態 |
| G-5 | `toast(...,'error')` 兩處改 `danger`；儀表板 serverAdmin 隱藏清單補 `dashboard-issues`；`guardLoad` 容器補 issues／kpi | (T) UiTests：`toast(` 第二參數白名單守門；`FrontendConsistencyUiTests` 新增案例：dashboard.js 的 serverAdmin 隱藏清單含 `dashboard-issues` |
| G-6 | 不安全的 `localStorage` 呼叫改 `safeStorage`（ui.js loadPageSize／savePageSize、dashboard、reports、handler-detail） | (G) UiTests 守門：pages 與 ui.js 不得直呼 `localStorage.`（白名單 core/storage.js、已 try/catch 的 note-editor.js、permission-changes.js、settings.js、layout.js 標註行） |
| G-7 | chat-panel：對話世代計數，回覆抵達世代已變即丟棄；pending 期間停用下拉與清除；finally 依 `currentIssueKey` 決定 disabled | (T) `chat-panel-generation.behavior.mjs`：送出後換問題、回覆抵達 → messages 不含舊回覆 |
| G-8 | `api.js` 暴露 `download(url, {timeoutMs, maxBytes})` 回 `{blob, filename}`（沿用 readJsonDownload 的有界讀取與 401 處理）；prtg-calibration.js:286 與 prtg-admin.js:2686 改用 | (T) `api-guard.behavior.mjs` 加 download 逾時／401／超量三案；(G) `grep -rn "fetch(" wwwroot/js/pages` ＝0、`location.assign(` ＝0 |
| G-9 | prtg-admin `requireScopeRevision()` 集中六個寫入點；`|| '/runs'` fallback 移除 | (T) `prtg-scope-revision.behavior.mjs`：scopeRevision 為 null 時按移除 → 不發請求、toast「範圍尚未載入」；(G) prtg-admin.js 不含 `'/runs'` |
| G-10 | record-detail 兩份報告各自 `.catch(() => null)`；`onBatchSaved → load()` 包 guardLoad | (T) behavior：一份報告 404 → 列表仍渲染、`setupNextUnhandled` 被呼叫 |
| G-11 | 小項：hosts `hasActiveFilter()` 含 prtgMap；issue-status-reply 到期日 max=90（常數由 `core/handling-statuses.js` 或 DTO 帶）；imports 確認文字補「N 台主機的既有負責人會被整組取代」；help-manual 首次不捲動；host-detail 展開改 `onRowExpand` 且收合後不高亮；reports from>to 擋、scope 成功後才寫 | (T) 各頁 UiTests 字串守門；`host-detail` behavior：收合後回應不呼叫 highlightTimeline |
| G-12 | 文件：WEB-SPEC §8.1 core 模組總表（含兩個新檔）、inline script 例外（LF_BASE／LF_LABELS）、fetch 唯一出口含 download；§8.1a toast link 誰套；§8.6-1 篩選記憶（待決 14）；§8.6 補「序號守門與 pollWhile 為清單頁與輪詢的唯一寫法」；BACKLOG 補 hidden 降頻／退避 | (D) 逐句比對 |

定向：`--filter "FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout|FullyQualifiedName~HelpManual|FullyQualifiedName~RunsPage|FullyQualifiedName~PrtgAdminPage"`；Node 必須存在（略過數＝10）。

## 4. 第 60 輪：冗餘、死碼、結構（草案）

| 批次 | 內容 | 驗收要點 |
|---|---|---|
| H 刪死碼 | 第 59 輪收尾體檢新增：`IIssueOwnerStore.Upsert`（正式碼已無呼叫端）、`SchedulerHostedService.IsScheduledFetchRun` 轉接、`PrtgScopeResult.Warnings`／`ManualMissingInMirror` 無消費端（接到範圍試算頁或刪除）、`NullAiRequestGate` 僅測試使用（註明或移到測試）；A2b-04 PrtgBackfillRunner 非觸發式分支；A2b-05 死包裝＋`GetStatus` 改 `Ok()`；A2b-06 PrtgDataTransfer Export／Import、`[NonAction]`；A2a-14 EfPrtgStore 舊 readiness 查詢群等 8 項；A2b-13；B-17；D1-10；C3-10；A3-16；A3-17；A2a-19 | 每項刪前 `grep -w` 正式碼＝0；測試同刪；全量綠 |
| I 收斂重複 | C2-06 七支一次性 hosted service 共用骨架＋退場旗標；C3-17 `ResolveVisibility`；C1-16；C2-10 兩份 trigger switch 合一（含 fetch-followup）；A2a-12；A2a-13；A2a-21；D1-09／D2-16／18／19／20／21 前端上提；C1-25；C3-22 | 每組只剩一份（grep）；基底測試三路徑 |
| J 拆分與分層 | C1-09 SettingsController 依路由前綴拆、可選相依必填；C1-08；C1-23；C1-14 RecordsController 查詢模型（先表驅動端對端測試）；D1-11；C1-10 稽核快照＋ExpectedRevision 必填 | 路由快照（method, route, permission）前後相同；表驅動測試先綠 |
| K 效能與韌性 | C2-11／12／20；A2b-17；A2a-16／17；B-10；B-19；**B-07 TopIssueBackfill 啟動快判（逐閘門）**；C1-27 `[ReadOnlyPost]`＋安全標頭；C1-21 AI 每使用者節流＋C1-24 錯誤碼；C1-18；B-22；C1-13；E-7 帳本分帳與釋放策略 | 各快取失效測試；節流 429；錯誤碼守門 |

## 5. 第 61 輪：文件對齊與守門測試（草案）

| 批次 | 內容 |
|---|---|
| L 現況文件 | 59／60 未涵蓋的全面對齊：WEB-SPEC §1／§4.1／§5／§6／§7／§8／§9 各頁／§10（含 10.9→10.6）／§11／§12、敘事 69 處；DB-SPEC B-11 全部欄位級與索引、刪 EF migration／檔案輪替、升級器行為、A2a-20；PRTG-SPEC 其餘；DETECTION／RULES／LINUX／NETIQ／DESIGN-SYSTEM 敘事；README 機制章搬遷與沿革句與文件地圖；CLAUDE.md :42／:101、基線；BACKLOG 主題式分節、6 項過時、### 歸位、程式註解失效引用 11＋7 處等 |
| M 守門測試 | 每個端點都有授權（反射＋白名單）；SQL Server DDL vs design-time model；DB-SPEC 對 `ctx.Model`；文件 §xref 存在性；PatternId 數量對文件與說明書；HelpWritingGuide 字詞與實作名詞；`.ai.md`／`.md` 標題同步；HelpNavigationConsistency 升級；NodeTheory 略過數；無斷言 22 條；CsrfHeaderMiddleware／ForwardedHeaders／MigrationGate handling 分支；mini-dom innerHTML setter；AuditActions 中文名與 `access_denied` 常數；BrandProvider；前端行為測試改用共用 node 執行器（非同步讀輸出＋逾時結束程序樹），取代既有 13 檔「先 `ReadToEnd` 再 `WaitForExit`」——實作壞掉時 node 不結束會讓測試卡死而非失敗（第 59 輪 G1、G4a 突變時實際發生） |

## 6. 環境整理（需你逐項同意）

1. 刪 `.claude\worktrees\agent-ae5416dc66ce5f090`（162 MB）。
2. Codex worktree 88 筆：dirty≤3 的 10 個直接 `git worktree remove --force`；其餘每個 `git diff > patches/<name>.patch`＋`git status --porcelain > patches/<name>.status` 後 remove；最後 `git worktree prune`。執行前把清單給你過目。
3. `work/`（262 MB）、`presentations/`、`artifacts/`：請告知用途。

## 明確不做（本輪）

- B-18 索引瘦身（無統計）；prtg-admin.js／ui.js／RecordListQueryService 不拆檔；C1-19 只補文件；DateTime 直呼與字面數字不重構；A1-18 記 BACKLOG；C2-15 只補文件；C2-09 改註解；B-23 維持；A2a-15 依探測結論再定；pollWhile 的 hidden 降頻／退避（BACKLOG）；帳本分帳與釋放（60-K）；TopIssueBackfill 快判（60-K）；RunTriggerText／RunMonitorService 兩份 switch（60-I）。

## 最終方向確認（主代理逐條重讀後的補核）

| 疑點 | 查證 | 結果 |
|---|---|---|
| C-2 移除 `SourceObservations` 欄位後，舊紀錄 JSON 含該欄位會不會反序列化失敗 | `LfJsonOptions.cs` 未設 `UnmappedMemberHandling.Disallow`，預設忽略未知屬性 | 安全；DB 欄位保留、只停寫 |
| C-6 嚴格化 `Normalize` 會不會誤殺成功回覆 | prompt 要求 risk_level「低、中、高擇一」（AnalysisPromptBuilder.cs:320）；成功路徑 :819 也走 Normalize；`MoreSevere(未知, ruleRisk)` 回 ruleRisk | 原「精確等於」會讓「高風險」這類短變體失去拉高能力 → 改為「≤4 字且以等級字開頭」；降級路徑直接用 ruleRisk 不再 Normalize 原文（已改寫 C-6） |
| C-4 降級旗標要不要持久化 | `PrtgCorroboration` 只產非風險的弱佐證；持久化要動 JSON 與 shaper | 不持久化、只在統計段生效，規格寫明不對稱（已改寫 C-4） |
| F-5 結構同步 Checkpoint 用哪個 Read | PrtgStructureSyncService.cs:375 `.Read` | 確認需改 `ReadStructural`，其餘 `Read()` 呼叫端不動 |
| C-1 退場後 NetIQ 與本機路徑的 `AiWorkItem` 判定 | NetiqPipelineService.cs:669 `workItem == null`；AnalysisOrchestrator.cs:1000 `record.AiPending` | 型別與判定保留，只刪 `Logs` 與掃描 |
| A-1 serverAdmin 行為 | 現況 visible 為空恆 403；新 helper 看能力集 | 測試釘住與現況相同結果（已補進 A-1） |
| B-1 入口截斷對既有 >255 來源列的影響 | SQLite 舊列可能存全長 | 寫進 DB-SPEC 已知限制（已補進 B-1） |
| D-2 run state 的 Trigger 比對不能用 `IsScheduledFetch`（無 JobType） | SchedulerRunState／AiAnalysisRunState 只有 Trigger 字串 | PLAN 已區分「BatchRun 列用擴充、run state 用常數」 |
| G-8 以 blob 下載取代 `location.assign` | 匯出檔名靠 Content-Disposition | `api.download` 回 `{blob, filename}` 並由前端 `a.download` 觸發，檔名從標頭取 |
| 待決 1 退場後使用者能看到什麼 | 低風險日 TopIssues 仍完整顯示；只是不再有 AI 看未分類事件 | 說明書與 DETECTION-SPEC 寫明；若日後需要，重新設計時要先讓 shaper 保留 tail 樣本 |

## 規劃完成後複檢（已依三視角審查做過一輪）

- 與既有設計衝突：待決 1 由「合一」改「退場」（RecordStorageShaper 清空樣本）；待決 6 註解反例改寫；待決 7 保護 `Read()` 格式。
- 批次間同檔：C↔D 對調（D-6 簽章改動放最後）；A↔F helper 只在 A；B↔E clamp 在 key 衍生前一處；E↔G 同五檔，G 以 E 完成後檔案為基準且兩守門 G 後重跑；D↔F 同 `SentinelClient.Auth.cs`／`NetiqPipelineService.cs`／`PrtgSnapshotHostedService.cs`，F 規格標註 D 已改區段。
- 四個坑：什麼算一個（E-11 略過計數依原因分桶、F-1 計數定義）；分母為零（E-2 無靜音時兩路徑相等）；破壞性判準反例（B-1 issue_key 超長拒寫不截、B-3 cursor 排除與邊界日保留、F-2 messages 不當失敗）；單向閘門繞過（C-1 Skipped 可由強制重跑或風險上調重新 AiPending——`EfAnalysisRecordStore.cs:~742` 風險上調時 `!AiAnalyzed` 才重標，Skipped 的 AiAnalyzed=false 符合）；移除類依賴方（C-1 AiWorkItem 型別保留、C-2 抑制列顯示退役、E-3 測試前置改走 Create）。
- 升級時刻：A-4 帳號含 `\` 盤點、B-2 條件式 ALTER、B-3 分多日清、C-1 存量低風險 AiPending 由 Skipped 排掉、C-5 重跑降級說明、F-7 0 裝置文案、A-9 `categories` 一併掛權限、A-3 `ask-available` 回 false 不 403。
- 拆段寫規格前再做一次（本節更新）。
