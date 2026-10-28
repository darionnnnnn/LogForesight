# LogForesight 第 58 輪規劃（第二版，四視角複檢後）

> 狀態：實作中（`feature/feedback-58`）
> 基準：dev@814c10b4，本輪開工全量 **6,487 通過／10 略過／0 失敗**（21 分鐘）
> 來源：使用者 2026-10-28 實測回饋六項（五張截圖）＋評估過程發現的三個缺陷＋第二版四視角複檢

## 作業總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A | 部分下拉選單選項空白 | 小 | — |
| B | 主機對應規則改版：IP 為主、名稱只補 IP 沒對到的主機、名稱變更提醒、對應往後沿用、升級重算 | 中大 | — |
| C | PRTG 評估完整性與失敗原因看得見 | 中 | — |
| D | 保守策略的回填＝全部已對應主機（勾選就是補全） | 中 | B |
| E | PRTG 只呈現主機清單內的主機；未對應清單改主機端；指派改由主機端搜尋裝置 | 大 | B |
| F | 總覽儀表板 KPI 區重排（ui-ux-pro-max），同元件套到另三頁 | 中 | — |
| G | PRTG 維護頁六個頁籤用途說明＋說明書 | 小中 | D、E |

順序 A → B → C → D → E → F → G。每批驗收後 commit 並 push 本輪分支。

**實作方式**：內建 Agent subagent（B、C、D、E 用 Sonnet 5.5，A、G 用 Haiku 5.5，effort 逐段評估）；同一 checkout 一次只派一個改檔代理；主代理逐段獨立重驗。
每段規格檔只放契約、限制、白名單、驗收；規劃裡「定案」每條都要出現在該段驗收。

**本輪新增的現行規則（寫進 PRTG-SPEC §1）**：PRTG 相關畫面與執行紀錄只呈現「主機清單內的主機」及其對應裝置。
例外三處，頁面明示「含不在主機清單內的裝置」：環境探測（全站診斷）、資源守門（PRTG／Sentinel 自身）、人工指派時的裝置搜尋（使用者主動輸入條件）。

**單一出口（SOLID／DRY 檢查點，終檢逐條 grep）**：
| 判定 | 唯一出口 | 不得另寫的地方 |
|---|---|---|
| 某日該用哪一份主機對應（往回 31 天優先、再往後） | `EfPrtgStore.FindLatestHostMapDate` | 管線、就緒度、快照、回填、校準都經 `GetLatestHostMapWithDate` |
| 回填／接續補值的主機範圍 | `PrtgValueFetchScope.BackfillHosts(strategy, …)` | 預覽、runner、估算端點 |
| 清單主機的 PRTG 對應狀態與未對應原因 | Web `PrtgHostCoverage`（新） | 主機頁篩選、維護頁未對應清單、排程頁 PRTG 卡計數 |
| 擷取階段失敗的記錄 | `PrtgFetchService.RecordStageFailure`（新小函式） | 14 處 `failures++` 全改呼叫它 |
| KPI 卡列 | `core/ui.js` `kpiStrip` | 四頁都用，不再有 `col-6 col-lg-3`＋`statCard` |

---

## 批次A：部分下拉選單選項空白

### 現況與核對結果
- `Views/Pages/Settings.cshtml:79`（AI 服務提供者）、`Views/Pages/Prtg.cshtml:218`（取數策略）：靜態 `<option>`，前端無程式改動它們。
- 實測計算樣式：select 文字 `rgb(15,23,42)`、底白；**option 背景透明、文字色繼承**。兩者都是一般尺寸 `.form-select` 撐滿寬卡片；其他下拉多為 `-sm` 或在窄欄／modal。
- Windows Chrome 已知同類狀況：option 無明確背景色時文字畫不出來、可點選、重繪後恢復；明確指定前景與背景後消失。**未在使用者 Chrome 重現，根因屬推斷。**

### 定案
- 全站 `select option`／`optgroup` 明確指定前景與背景（token），`select` 宣告 `color-scheme: light`（本站無深色模式）。風險極低；不選「把那兩個下拉改窄」（只是繞過）。
- 尖銳使用者視角：「沒重現就改」——承認；修法不改變任何正常畫面，實測交使用者，執行紀錄標「未重現」。

### 改動
1. `wwwroot/css/site.css`：option／optgroup 前景背景＋`color-scheme`；註解寫明原因。
2. `docs/DESIGN-SYSTEM.md` §4 補一行。

### 驗收（主代理執行）
- `FrontendConsistencyUiTests` 新增：讀 site.css，斷言存在 `option` 選擇器且同時宣告 `color` 與 `background-color`；突變（刪掉 background-color）必紅。
- 定向：`dotnet test --filter "FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout"` 全綠。
- 使用者實測（無法由主代理確認）：Chrome 兩個下拉看得到選項文字。

---

## 批次B：主機對應規則改版

### 現況與核對結果
- `PrtgHostMapper.cs:223-241`：同 IP ≥2 台 device 全標 `conflict`、不填主機 → 該清單主機完全沒 PRTG 資料。使用者「衝突 136」多屬此類，「監控 13 台、對應 10 台」亦然。
- 名稱退路對每台 IP 沒對到的 device 執行，衍生 `hostname-host-taken`、`hostname-same-host` 兩種衝突。
- 下游全以 device → 主機對照（`PrtgDailyPipeline.DeviceToHost`、`PrtgValueAssessment.Candidates`、快照目標、回填目標），**已支援一主機多裝置**。
- finding 簽章 `prtg:{規則}:{sensor objid}`（`PrtgFindingMapper.cs:85-111`）：同主機兩台裝置各有 Ping sensor 同時 down → **兩筆 finding、不撞鍵、也不合併**。
- 某日無「該日或之前」對應時不歸戶（`PrtgDailyPipeline.cs:399-405` 註解明寫不拿今天的對應套過去日）。對應日查詢唯一入口 `EfPrtgStore.FindLatestHostMapDate`（實作前再 grep 一次 `PrtgHostMaps` 直接查詢）。
- 升級重算可沿用 `PrtgHostMapRefresher`（Web，已有「重算今天」）。
- 既有退路：同 IP 已有 device 被人工指定 → 其餘 device 略過——這正好是「IP 底下只認其中一台」的 opt-out。

### 定案（使用者 2026-10-28）
1. **IP 為主**：IP 命中恰好一台清單主機 → 該 IP 底下**所有** device 都 `ok` 對到它。一 IP 多主機維持 `conflict`（HostId 最小者＋候選）。
   只想認其中一台：對它做人工對應，其餘自動略過（既有規則，寫進說明）。
2. **名稱只補 IP 完全沒對到的主機**：只拿「IP 沒對到清單主機或沒有 IP」的 device，比對「還沒有任何 IP／人工對應」的主機。
   一對一 → `ok`（Note `hostname`）；多台 device 命中同一台這類主機 → 全部 `ok`；命中多台主機 → `conflict`（`hostname-multi`）；
   命中的主機已有 IP／人工對應 → 不產生衝突、該 device 落 `unmatched`。刪除 `hostname-host-taken`、`hostname-same-host` 型別。
3. **名稱不一致提醒**：IP 對上的 device，能抽出名稱候選且無一等於主機 `ShortName` → Note 記「名稱不一致：<候選>」，不影響對應；抽不出候選不提醒。
4. **對應往後沿用（推翻既有設計）**：往回 31 天找不到時，取該日**之後**最早一份（上限今天）；輸出與逐日說明註明「沿用 YYYY-MM-DD 的對應」。
   使用者接受代價（IP 改配給別台時舊資料掛到現在的主機）。
5. **升級重算**：對應規則版本記在 blob；版本不符時**先以鏡像＋主檔算出今天的對應，成功後才在同一交易刪除其他日期的舊對應列並寫版本號**；
   鏡像空或計算失敗 → 不刪、不寫版本、警告並下次再試。寫一筆稽核與里程碑。人工對應、IP 排除不動。
6. 人工對應、IP 排除、同 IP 已有人工指定的略過：維持優先序。

### 四視角補強
- 使用者：「一台主機出現兩筆 Ping Down」——兩筆各帶 sensor 名稱，屬不同 sensor；不做合併（避免掩蓋其中一顆真的壞）。說明書寫明。
- 管理者：取數範圍會變大（同 IP 全部裝置的 sensor 都進鏡像與快照）；擷取參數「估算規模」會反映，快照上限 15,000 顆照舊擋。升級注意寫明。
- 上帝視角：曾是 `hostname-host-taken` 的 device 升級後變 `unmatched` → 離開範圍，其 sensor 鏡像與數值走既有「範圍外清除」流程（有縮小保護）。
  `PrtgScopeDevices` 的 HostId-null 衝突分支改版後只剩 `hostname-multi`（無 IP 命中），分支保留但註解更新（KISS，不為此重構）。

### 改動
1. `PrtgHostMapper`：規則 1–3；`PrtgHostMapResult` 增「名稱不一致」計數、移除不再產生的型別；主控台摘要改口徑（配合批次 E）。
2. `EfPrtgStore.FindLatestHostMapDate`：往回找不到再往後；`GetLatestHostMapWithDate` 回傳的 `MapDate` 即為沿用日（呼叫端比對 anchor 即知是否沿用，不另加欄位）。
3. `PrtgDailyPipeline`：無對應提示改為沿用說明；`PrtgValueAssessment`／PRTG-SPEC §9b 用語「該日或沿用的對應有效」。
4. 升級重算：Web 啟動或第一次 PRTG 路徑前執行（經 `PrtgHostMapRefresher`），版本常數在 `PrtgHostMapper`。
5. `SettingsController` conflictKind 與指派 modal 分岔：移除三個已不存在型別（實作前 grep 全部消費端：前端、匯出、測試、說明書）。
6. 文件：PRTG-SPEC §4（對應表、名稱退路、提醒）、§3a／§5（往後沿用）、§9b；WEB-SPEC §9.9e。

### 驗收（主代理執行）
- `PrtgHostMapperTests`（重寫受影響案例，預期多數現有 27 條要改）：
  1. 同 IP 3 台 device＋1 台主機 → 3 列 `ok` 同 HostId、conflict=0。
  2. 同 IP 2 台主機 → `conflict`、HostId 最小者。
  3. 主機 A 已由 IP 對上；另一台 device 名稱命中 A → 該 device `unmatched`，不產生 conflict。
  4. 主機 B 無 IP；兩台 device 名稱都命中 B → 兩列 `ok`。
  5. device 名稱命中兩台無 IP 主機 → `conflict`（`hostname-multi`）。
  6. IP 對上、裝置名候選 ≠ 主機名 → `ok`＋Note 含「名稱不一致」；候選抽不出 → Note null；候選等於主機名 → Note null。
  7. 同 IP 其中一台人工對應 → 其餘略過計數 +N（既有行為不退化）。
- `EfPrtgStoreTests`：只有 10-27 有對應，anchor 10-21 → MapDate=10-27；anchor 10-28 → 10-27；完全無對應 → null；10-20 與 10-27 都有、anchor 10-22 → 10-20（往回優先）。
- `PrtgDailyPipelineTests`：回望 7 天、只有最新日有對應 → 7 天 `MapAvailable` 皆 true、逐日說明含「沿用」；同主機兩台裝置各一顆 Ping down → 同主機日兩筆 finding、EventKey 不同、無例外。
- 升級重算測試（新）：版本舊＋鏡像有資料 → 今天對應重算、其他日期列刪除、版本寫入；再跑一次不再刪（冪等）；鏡像空 → 列不動、版本不寫、有警告。
- 突變：規則 1 改回 conflict → 案例 1 紅；往後沿用改回只往回 → EfPrtgStore 案例紅。
- 定向：`--filter "FullyQualifiedName~PrtgHostMapper|FullyQualifiedName~EfPrtgStore|FullyQualifiedName~PrtgDailyPipeline|FullyQualifiedName~PrtgScopeDevices"`。

---

## 批次C：PRTG 評估完整性與失敗原因

### 現況與核對結果
- **缺陷（已確認）**：無對應日（`PrtgDailyPipeline.cs:399-405`）與無規則日（`:379-382`）第一段 `DayEvaluated=true` 但 `FindingsByHost=null`；第二段遇 null 只發佈空結果 `continue`（`:612-616`），**不 `MarkEvaluated`**
  → `NeedsReevaluation` 恆 true → 每晚重挑、永遠收不掉（截圖「待補評估 6 天」）；該主機日 PRTG 腿一直「等待」。
- 部分失敗無原因：`ClassifyDay` 對 partial 回 null；共用階段失敗只計 `Failures`，原因只在主控台（`PrtgFetchService.cs` 14 處 `failures++`）。截圖每天「部分失敗、失敗 sensor 0」＝共用階段失敗套到每一天。
- 保守策略下頁首「sensor 0」、逐日三個觸發欄恆 0，像失敗。
- `BatchRun.PrtgDays` 已有逐日 `Note` 欄並顯示在「說明」欄 → **不需新增 schema 欄位**。

### 定案
1. 第二段：`plan.DayEvaluated` 為 true 就 `MarkEvaluated`，不論有沒有可歸戶 finding；只有擲例外／中斷不標。
   取捨：無規則日也算評估完成——之後啟用規則要用「立即執行」回望補評（說明書寫明）；不再每晚自動重評。
2. 共用階段失敗：`RecordStageFailure(階段, 訊息)` 集中記錄 → 整趟一筆**里程碑警告**（「PRTG <階段> 失敗：<訊息前 120 字>」）＋每個受影響日的 `Note`＝「整趟 <階段> 失敗，資料可能不完整」。
   逐日自己的失敗（觸發取數 sensor 失敗、磁碟證據）維持逐日說明。執行詳情 PRTG 頁首徽章 `title` 顯示第一條階段失敗訊息（無新 DTO 欄位）。
3. 保守策略：頁首 PRTG 顯示「保守策略（數值由快照供應）」取代「sensor 0」；逐日表三個觸發欄顯示「—」，表頭 `lf-help` 註明保守策略不適用。
4. 共用階段失敗的日子維持 `NeedsReevaluation=true`（隔晚重評合理）；驗收涵蓋「下一趟成功即收斂」。

### 四視角補強
- 使用者：「部分失敗但 finding 還是 2 筆，能信嗎？」——Note 寫明是哪個階段；狀態變更同步失敗時規則仍以已入庫資料評估，說明書寫「部分失敗＝該階段資料可能少，不是 finding 錯」。
- 管理者：里程碑警告會進執行詳情「重要紀錄」篩選，不必切「全部紀錄」。

### 改動
1. `PrtgDailyPipeline` 第二段 MarkEvaluated；`ClassifyDay` 收階段失敗說明。
2. `PrtgFetchService.RecordStageFailure`＋`PrtgFetchResult.StageFailures`（唯讀清單）；14 處改呼叫。
3. `runs.js` 頁首與逐日表；WEB-SPEC §9.10。

### 驗收（主代理執行）
- `PrtgDailyPipelineTests`：無對應日、無規則日 → `IsEvaluated` true、`NeedsReevaluation` false；評估擲例外 → false／true；
  替身讓狀態變更階段擲例外 → `StageFailures` 1 筆、每日 Note 含「狀態變更」、run 里程碑含警告；突變（拿掉 RecordStageFailure 呼叫）→ 紅。
- `PrtgReevaluationPlannerTests`：上一趟逐日都已評估 → 不挑回；上一趟共用階段失敗、本趟成功 → 本趟後不再入選。
- `RunsPageUiTests`／`PrtgAcceptanceBehaviorTests`：保守策略 DTO → 頁首無「sensor 0」、觸發欄「—」；激進策略維持數字。
- 定向：`--filter "FullyQualifiedName~PrtgDailyPipeline|FullyQualifiedName~PrtgReevaluation|FullyQualifiedName~RunsPage|FullyQualifiedName~BehaviorTests"`。

---

## 批次D：保守策略的回填＝全部已對應主機

### 現況與核對結果
- 勾選「一併補齊」只在保守策略出現（`ScheduleController.cs:376`、`runs.js:1875`），經 `PrtgBackfillService.RunTailAsync`；目標由 `PrtgFullBackfillPlan.ResolveDay` 以 `PrtgValueFetchScope.SelectHosts` 決定，預設 `triggered`＝當日高／中風險主機。
- 擷取參數頁寫「保守策略下此範圍不適用」——矛盾。維護頁全量回填說明同一誤會。
- `SelectHosts` 的 `all-mapped` 遇空白名單**靜默退回 triggered**（`PrtgValueFetchScope.cs:36-66`，防壓垮 PRTG）。
- 完成水位鍵含該日目標指紋（PRTG-SPEC §5）→ 範圍改變後舊水位自然失效。

### 定案（使用者：「勾選就是補全」）
- 新函式 `PrtgValueFetchScope.BackfillHosts(strategy, scopeSetting, triggered, mapped, extra, whitelistEmpty)`：保守 → 全部 `ok` 主機；激進 → 現行設定。預覽、runner、估算端點三處都經它。
- 保守＋空白名單：**明確拒絕**（預覽與啟動都回「sensor 白名單為空，請先在擷取參數設定」），不靜默退回。
- 立即執行確認框與維護頁預覽顯示「全部已對應主機 N 台、sensor M 顆、約 K 次請求、至少 T 分鐘」；超過 2,000 次請求時勾選框預設不勾（暫定，數值可依估算端點既有邏輯調整），讓使用者主動勾。

### 四視角補強
- 使用者：「勾了跑三小時」——確認框先給估算；回填可在維護頁狀態卡停止（既有）。
- 管理者：回填寫 `ok` 列覆蓋同小時 `sampled`（既有）；夜間取數遇回填最多等 30 分鐘（既有）。

### 改動
1. `PrtgValueFetchScope.BackfillHosts`；`PrtgFullBackfillPlan.ResolveDay`、預覽服務、`SettingsController.EstimatePrtgValues` 改用。
2. `runs.js` 確認框文字與預設勾選；`Prtg.cshtml` 全量回填說明（併 G 校稿）；擷取參數「保守策略下不適用」補一句「回填時補全部已對應主機」。
3. PRTG-SPEC §3a、§5；WEB-SPEC §9.10。

### 驗收（主代理執行）
- `PrtgFullBackfillPlanTests`：保守、某日 3 台 ok 主機只 1 台高風險 → 目標 3 台；激進＋triggered → 1 台；保守＋空白名單 → 擲明確例外（訊息含「白名單」）。
- `PrtgBackfillRunnerTests`：接續回填 30 天、對應只有最新日（搭配 B 沿用）→ 30 天都有目標、水位逐日寫入。
- 預覽與執行一致性：同一輸入預覽 sensor 數＝runner 目標數；突變（runner 改回 SelectHosts）→ 紅。
- `RunsPageUiTests`：確認框含估算文字；請求數超過門檻時勾選框 `checked=false`。
- 定向：`--filter "FullyQualifiedName~PrtgFullBackfill|FullyQualifiedName~PrtgBackfill|FullyQualifiedName~RunsPage|FullyQualifiedName~PrtgValueFetchScope"`。

---

## 批次E：PRTG 只呈現主機清單內的主機

### 現況與核對結果（子代理盤點，主代理抽查 `runs.js:1501-1504`、`PrtgScopeDevices.cs:60-78`、`SettingsController.cs:795-797` 屬實）
| 位置 | 呈現 | 來源 | 處置 |
|---|---|---|---|
| 排程頁 PRTG 卡「結構同步」`runs.js:1501-1504` | 衝突 136、查無主機 6,630 | 全站對應 | 改「對應 N 台、未對應 M 台主機」 |
| 里程碑 `PrtgDailyPipeline.cs:206`、同步輸出 `PrtgStructureSyncRunner.cs:149-150`、主控台 `PrtgHostMapper.cs:392-401`（逐筆 20 筆） | 全站計數／逐筆 | 全站 | 改主機口徑；逐筆移除 |
| 每日擷取摘要 `PrtgDailyPipeline.cs:235` | 裝置 N | 全站鏡像 | 改「監看裝置 N 台」 |
| 鏡像狀態「鏡像裝置數」`Prtg.cshtml:458` | 全站 device 數 | `PrtgDevices.Count()` | 改「監看裝置 N 台」 |
| 「主機對應摘要」`SettingsController.cs:795-797` | 衝突、未對應（PRTG 獨有） | 全站對應 | 改「已對應／未對應／衝突（主機口徑）／名稱不一致」 |
| 未對應清單 `prtg-admin.js:1543`（API `prtg-host-map?status=unmatched`，唯一呼叫端） | 全站裝置 | 全站 | 改主機端清單；端點 `status` 只留 conflict |
| 衝突清單 | 裝置、候選主機 | B 後只剩兩種、皆與清單有關 | 保留，說明改寫 |
| 監看範圍預覽「未歸戶」sensor（`PrtgMonitoringController`、`EfPrtgStore.cs:2396-2452`） | sensor 名 | 範圍內未對應 | fullScope 不列 |
| 使用效果低涵蓋小時 `PrtgEffectivenessService.cs:35`、校準匯出 host_map 全列 | 全部 values／全列 | 全站 | 限 `ok` |
| 範圍外資料裝置名、擷取紀錄 devices 類別 | 裝置名／筆數 | 範圍外／全站 | 保留、收合、標診斷 |
| 環境探測輸出、資源守門預覽 | 全站結構、PRTG／Sentinel 自身 | 全站 | 保留，頁首明示 |
儀表板、主機頁、主機詳情、問題查詢、交辦、郵件、報表、回填目標、就緒度卡：已是清單主機口徑，不改。

### 定案（使用者：未對應清單只顯示 NetIQ 已匯入但 PRTG 找不到對應的主機）
- 新 Web 服務 `PrtgHostCoverage.Compute(hosts, latestMap, excludes)` → 每台啟用、未合併主機的狀態：`mapped`（含裝置數、名稱不一致旗標）／`conflict`／`unmatched`＋原因
  （`no-ip`：主機沒有 IP；`ip-excluded`：IP 已排除；`not-in-prtg`：PRTG 查無此 IP）。主機頁篩選、維護頁清單、排程頁計數三處共用。
- 維護頁「未對應主機」清單：主機名、IP、原因、「指派 PRTG 裝置」按鈕（Maintain）；0 台時顯示「全部主機都已對應」。
- 指派改主機端發起：modal 固定主機，輸入 ≥2 字以名稱或 IP 搜尋裝置鏡像（新端點 `GET api/admin/settings/prtg-devices/search?q=`，Maintain，最多 20 筆、多於 20 筆回 `hasMore` 並提示縮小條件），選定寫人工對應（沿用現有 PUT）。
- 衝突清單維持；指派 modal 依型別分岔只剩兩型。
- 計數、里程碑、主控台、鏡像數字改主機口徑；逐筆非清單輸出移除。
- 環境探測、資源守門預覽、範圍外資料：保留＋明示＋收合；探測站台對照 [S1] 既有「範圍外顆數」就是管理者要知道「PRTG 還有多少不在清單」的地方（說明寫明）。
- 使用效果、校準匯出限 `ok` 對應裝置。

### 四視角補強
- 管理者：「要知道 PRTG 多出哪些裝置好補匯入 NetIQ」→ 環境探測輸出保留全站數字；不在日常畫面。
- 使用者：搜尋裝置「我有六千台、只回 20 筆」→ 名稱／IP 子字串＋`hasMore` 提示；IP 精確輸入必命中。
- 上帝視角：主機頁既有「未對應」篩選與維護頁清單改為同一份判定，不再各寫一套（原本主機頁自己算）。

### 改動
1. `PrtgHostCoverage`（Web Services）＋單元測試；`HostAdminService` 篩選改呼叫它。
2. `PrtgStructureSyncStatus`／DTO：以主機口徑欄位取代 `MapConflict／MapUnmatched`（舊 blob 讀取相容，不再顯示）。
3. 里程碑、同步輸出、`PrtgHostMapper` 摘要改口徑；逐筆輸出移除。
4. 維護頁：計數卡、對應摘要、未對應主機清單、裝置搜尋端點與 modal；`prtg-host-map` 端點 `status` 只接受 conflict。
5. 監看範圍預覽、使用效果、校準匯出限 ok；環境探測／資源守門／範圍外資料說明與收合。
6. 文件：PRTG-SPEC §1、§4a、§5a、§6、§11；WEB-SPEC §9.9e、§9.10；說明書（併 G）。

### 驗收（主代理執行）
- `PrtgHostCoverageTests`（新）：5 台主機（ok、無 IP、IP 排除、IP 查無、一 IP 多主機衝突）→ 狀態與原因逐台正確；停用／已合併主機不出現；名稱不一致旗標；突變（原因判定順序對調）→ 紅。
- `HostAdminServiceTests`：`prtgMap=unmatched` 結果＝Coverage 的 unmatched 集合（兩者一致性測試）。
- 裝置搜尋端點測試：`q` 少於 2 字 → 400；回 20 筆＋`hasMore`；IP 精確命中；非 Maintain → 403。
- `RunsPageUiTests`／`PrtgAdminPageUiTests`／`FrontendConsistencyUiTests`：畫面字串不含「查無主機」「PRTG 獨有」「鏡像裝置數」；`wwwroot/js` 無 `lastMapUnmatched`／`status=unmatched`；`PrtgHostMapper` 原始碼無逐筆輸出迴圈。
- `PrtgEffectivenessService` 測試：非 ok 裝置的 sampled 低涵蓋列不計。
- 定向：`--filter "FullyQualifiedName~PrtgHostCoverage|FullyQualifiedName~HostAdminService|FullyQualifiedName~PrtgAdminPage|FullyQualifiedName~RunsPage|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout|FullyQualifiedName~Calibration|FullyQualifiedName~Effectiveness"`（Effectiveness 篩選詞實作前確認命中）。

---

## 批次F：總覽儀表板 KPI 區重排（ui-ux-pro-max）

### 現況與核對結果
- `dashboard.js:333-348`：最多 10 張 `statCard` 在 `col-6 col-lg-3`，排 4＋4＋2；副標有無不一致（hint 只在 `title`），高度不一；寬螢幕每張約 420px。
- `statCard` 呼叫端 4 處：dashboard、handler-detail、reports、user-detail。
- DESIGN-SYSTEM §6b「KPI 一列不超過 4 張」是寬度過大的既有規則，本輪改寫。
- ui-ux-pro-max 檢索：設計系統既定 Data-Dense（KPI cards、minimal padding、space-efficient）；hover 不位移；說明不藏 tooltip。

### 定案
- `kpiStrip(groups)`（`core/ui.js`）取代四頁的 `col-6 col-lg-3`＋`statCard`；`statCard` 併入或刪除（grep 零呼叫端後）。
- 儀表板三組（整組無卡不顯示）：**風險概況**（高風險日、中風險日、監控主機數、涵蓋率缺口天數）／**待處理**（未處理問題、我的交辦單、待派工問題、未回報主機）／**權限與安全**（權限異動待確認、24 小時登入失敗）。
- 卡：數字左對齊 `tabular-nums`、標籤、副標固定一行（無副標保留佔位；超長省略並以 `title` 保留全文）；等高；原 hint 改可見副標。
- 卡寬 9–14rem（暫定）grid `auto-fill`；375px 每列兩張；可點卡整張可點、focus 可見、hover 只變陰影。
- 數字字級沿用 `lf-stat__value`（尖銳使用者：「改小反而看不清」）。

### 改動
1. `core/ui.js` `kpiStrip`；`site.css` 樣式（token 化）。
2. 四頁改用；`Dashboard.cshtml` 容器。
3. DESIGN-SYSTEM §6b、WEB-SPEC 儀表板節。

### 驗收（主代理執行）
- `FrontendConsistencyUiTests`：四頁檔案不含 `col-6 col-lg-3`；`kpiStrip` 僅定義於 `core/ui.js`；site.css 含 `.lf-kpi` grid 宣告與 `prefers-reduced-motion`。
- `JsBehavior`（`*.behavior.mjs`）：`kpiStrip` 空組不輸出；每張卡都有副標節點（無副標時為空佔位）；可點卡為 `<a>` 且 `href` 經 `appUrl`；替身補新匯入函式。
- 畫面驗證：主代理以內建瀏覽器開本機站台（**需使用者同意測試模式登入**），1920／1366／375 三寬度＋字級三檔截圖，檢查等高與無水平捲動；未獲同意則交使用者實測並在執行紀錄標「未驗證」。
- 定向：`--filter "FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout|FullyQualifiedName~DashboardService"`。

---

## 批次G：PRTG 維護頁頁籤用途說明＋說明書

### 現況與核對結果
- 鏡像狀態、歷史回填、使用效果說明只講技術限制；全量回填說明與行為不符（D 修）；說明書 `20-prtg.md`／`.ai.md`、`08-hosts.md` 需同步。

### 定案
每頁籤頂端固定三行：**這頁做什麼／什麼時候來／不會做什麼**（草稿）：
- 鏡像狀態：看 PRTG 資料是否完整複製過來、哪些主機還沒對應，在這裡手動同步或指派／新增主機後、主機頁出現「PRTG 未對應」、排程頁同步失敗時／不改主機清單、不補過去數值。
- 歷史回填：把過去幾天的 PRTG 逐小時數值補進來（全部已對應主機）／第一次啟用 PRTG、或保守策略下想補齊過去日時／不重判過去風險；要重判到排程作業頁「立即執行」回望。
- 使用效果：PRTG 抓到幾個問題、建了多少交辦、被日誌佐證多少、被標誤報多少／想決定規則門檻或是否改激進策略時／各數字分母不同，不能換算比例。
- 連線、擷取參數、環境探測套同格式；環境探測加「含不在主機清單內的裝置」。
- 說明書：同主機兩台裝置會各出一筆 finding（B）、部分失敗的意思（C）、啟用規則後要回望補評（C）、勾選補全的成本（D）、指派改從主機端（E）。

### 驗收（主代理執行）
- `PrtgAdminPageUiTests`：六個頁籤各有用途區且含三個固定標頭文字。
- `HelpContentServiceTests`、`HelpWritingGuideTests`、`HelpNavigationConsistencyTests` 綠；說明書新增句子通過句長守門。
- 定向：`--filter "FullyQualifiedName~PrtgAdminPage|FullyQualifiedName~Help|FullyQualifiedName~UiTests"`。

---

## 明確不做（本輪定案）
- 在使用者 Chrome 重現下拉空白（無更多資訊）。
- 同主機多裝置的 finding 合併（會掩蓋其中一顆真的壞）。
- 單一請求取多天 historicdata（單次已超過 100 秒）→ BACKLOG。
- 對應歷史的逐日 IP 異動追蹤（使用者定案 IP 很少異動）。
- `unmatched` 對應列不再寫入（校準／診斷仍用）。
- 為 `PrtgScopeDevices` 衝突分支做結構重構（改版後仍正確，只更新註解）。

## 複檢（第二版）
- 與既有設計衝突：B-4 推翻「不拿後來的對應套過去日」（程式註解、PRTG-SPEC §5、§9b 用語一併改）；F 推翻 DESIGN-SYSTEM §6b「一列不超過 4 張」；C-1 取消「無規則日自動重評」（改為使用者回望）。三條都寫明理由。
- 批次間：B→E（計數口徑）、B→D（沿用才補得到 30 天）、B 與 C 同檔不並行；D 的估算端點與 E 的計數分屬不同函式不衝突；G 最後統一校稿 D、E 文案。
- 四個坑：什麼算一個（未對應主機＝啟用、未合併、無 ok 對應；無 IP 也算一台，原因區分；0 台顯示全部已對應）；破壞性判準（升級只在重算成功後刪舊對應列，鏡像空不刪；反例：鏡像空時不能留空）；
  單向閘門（版本號只在成功後寫，失敗下次再試）；移除類（三個衝突型別、`status=unmatched` 端點、`MapConflict／MapUnmatched` 顯示、`statCard`——各自 grep 全部消費端後才刪）。
- 分母為零：D 的估算 sensor=0 → 顯示「沒有可補的 sensor」並停用勾選；E 的 Coverage 主機 0 台 → 「全部主機都已對應」。
- 升級路徑：舊 `prtg_sync_status` blob 相容讀；舊回填水位自然失效（升級注意）；升級重算有稽核與里程碑。
- SOLID／KISS／DRY：五個單一出口（頁首表）；不新增設定鍵（「有設定無行為」紅線不觸及）；不新增 schema 欄位（C 用既有 Note）；新類別只有 `PrtgHostCoverage` 一個，其餘是函式。
- 複檢完成，第二版新增：B-5 先算後刪、D 空白名單明確拒絕、E Coverage 單一判定、C 不加欄位、各批可機器驗證的驗收與突變。

## 升級注意（併 master 前帶給使用者）
1. 第一次啟動重算 PRTG 主機對應（有稽核）；過去日沿用新對應；用「立即執行」回望並勾「一併補齊」補數值。
2. 衝突數大幅下降（同 IP 多裝置自動對應）；取數範圍與快照 sensor 數會上升，到擷取參數「估算規模」確認。
3. 之前的人工對應、IP 排除不受影響。
4. 全量回填完成水位失效一次。
5. 說明書對應章節有更新。

## 體檢交接
（實作輪收官時填：全量測試總數、全綠與否、與基線差）

## 執行紀錄
| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| 開工基準 | 主代理 | 全量 6,487／10 略過 | — | — |
| A | 主代理（幾行 CSS） | 完成 | 前端定向 230 條綠；突變（刪 background-color）守門紅 | 未在使用者 Chrome 重現，實測交使用者 |
| B（含 C-1） | 主代理（已持有脈絡，自己做比寫規格便宜） | 完成 | 定向 236 條＋重寫 60 條綠；突變三條（同 IP 多 device、往後沿用、C-1 MarkEvaluated）皆紅 | ① 名稱不一致提醒原寫「抽出候選即比對」，但 `Extract` 會把不帶括號的整個裝置名當候選，幾乎每台都誤報→改成只看括號內與 host 欄 DNS 名稱。② 指定主機補值（`PrtgBackfillRunner`、`PrtgBackfillService` 預覽）原本直接讀當日對應、不經唯一出口→改走 `GetLatestHostMapWithDate`，三條舊測試依新定案改寫。③ 升級重算改記 NLog（啟動時沒有稽核服務），不寫稽核。④ 主控台逐筆列出非 ok 裝置原屬 E，同檔先在 B 移除。⑤ 前端 `multi-device`／`hostname-host-taken` 分支留給 E 隨指派 modal 重寫一起刪（目前是死碼、不影響行為）。⑥ C-1（MarkEvaluated）與 B 同檔，一起提交。 |
