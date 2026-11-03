# LogForesight 第 63-2 輪規劃：第一份診斷匯出的回饋（AI 格式異常、憑證解不開、執行時間、儀表板與設定頁）

> 狀態：規劃中，等第 63 輪體檢併 dev 後，自 dev 開 `feature/feedback-63-2`
> 基準：feature/feedback-63@698db8d7（7,934 通過／11 略過；併 dev 後以 dev 的 sha 為準）
> 來源：使用者 2026-11-03 五項重點，加上第一份診斷匯出檔（14 天、站台 1.0.53.2、13 台主機、18 趟）的分析
> 實作方式：主代理寫規格與驗收；改檔代理依複雜度選 Sonnet 5.5 或 Haiku 5.5；同一 checkout 一次一個改檔代理；主代理獨立重驗
> 設計面：套用 `ui-ux-pro-max`（使用者 2026-11-03 確認）；檢索結果只有表單 label 綁定（#43）、表格不撐破版面（#71）、動作回饋（#61）三條相關，其餘離題未採用；版面判斷以 `docs/DESIGN-SYSTEM.md` 與 WEB-SPEC §8.6a 為準

## 0. 總結與定案

使用者七項待決（2026-11-03）全部授權「再次評估後依建議處理」，第 5 項另問 PRTG 是否太忙、要不要加等待重試。再評估後的定案：

| 項目 | 定案 | 再評估的依據 |
|---|---|---|
| 1 輪次切法 | 先收第 63 輪（目前模型體檢、併 dev），63-2 自 dev 開新分支 | 第 63 輪全量已綠；留在同一分支會讓匯出功能一直進不了 dev |
| 2 UI 設計 | 套用 `ui-ux-pro-max` | 使用者確認 |
| 3 AI 格式異常 | 兩段：補證據＋「空陣列視為空回覆、第二次重試不帶 JSON 文法」；降級主機日可補跑，上限 3 次，需加一個欄位 | 25 個案例回覆全是 `[\n]`、內容檢查失敗 0 次、對照組特徵相同，詳見 §1 |
| 4 AI 趟次狀態 | 部分主機日降級的趟次顯示「有警告」；降級計入 `aiFailures` | 目前 6 趟有 5 趟顯示「失敗」，但沒有一趟真的中斷 |
| 5 憑證解不開與 PRTG 重試 | 11-02 的「未啟用」確定是憑證解不開，不是 PRTG 忙碌（證據見 §1）；PRTG 結局沿用 `disabled` 並附原因；**另外**對結構同步與狀態變更查詢加有上限的等待重試，逐顆 sensor 取數不加 | 匯出檔 10-28 有 2 顆 sensor 逾時（已有隔日重試）；`PrtgClient` 刻意不重試是為了避免 401 累計鎖定帳號，傳輸類失敗重試有意義 |
| 6 固定警告 | 略過憑證驗證改每趟一行 Info；Security 記錄檔讀不到維持 Warn | 前者是刻意設定，後者是真的偵測盲區 |
| 7 去識別化 | 兩個字以下的中文字典詞不遮；群組與角色名至少 3 字才進字典；登錄機碼名稱進保留清單；AD 搜尋基準的第一個 DC 進網域字典 | 匯出檔出現「已group-002診斷」這類常見詞被遮、`HKLM\Software` 被當成網域\帳號、NetBIOS 網域名漏遮 |

## 1. 第一份匯出檔的分析

### 1.1 AI 回覆格式異常

| 事實 | 數字 |
|---|---|
| 案例數；回覆全部是 `[\n]`（3 個字元） | 25／25 |
| 解析失敗／內容檢查失敗 | 148／0 |
| 每案嘗試次數 | 全部 3 次 |
| 案例與對照組（同主機前後一天）輸入特徵中位數 | 問題數 2 vs 3、字數 555 vs 645，其餘相同 |
| AI 呼叫總數／最終失敗 | 225／49（約 22%） |
| `detailedDiagnosticsFirstSeen` | null（匯出時第 63 輪的診斷尚未遇到失敗） |

**判斷**：不是事件內容造成。程式對本機端點送 `response_format: json_object`，伺服器以文法強制輸出合法 JSON，而文法允許陣列開頭；模型（gemma-4-31B，會先思考）想先輸出思考文字而被文法擋下時，最短的合法輸出就是 `[]`。這解釋了為什麼永遠是同一個 3 字元回覆、為什麼重試提示也救不回來（重試仍帶同一個文法）。

這仍是假設。缺的證據：`finish_reason`、清洗前的原始回覆、伺服器是否把思考放在 `reasoning_content` 這類欄位。第 63 輪的診斷只記清洗後內容，批次B 補齊。

**不等證據也該做的保護**：空陣列、空物件一律視為空回覆；第二次重試不帶 `response_format`，讓模型先思考再給 JSON，由既有的括號配對抽出物件。

### 1.2 憑證解不開的連鎖反應

匯出檔 `undecryptableSecrets`：PRTG 密碼、PRTG passhash、Sentinel 密碼。造成：

- 11-03 取數 17 趟：`S002` CryptoHelper 於 prtg 階段 2 次、netiq 階段 1 次「密文無法解密…此欄視為未設定」，接著 `S003` 兩台 Sentinel 回 401「帳號或密碼錯誤」、12 台主機全部失敗。`Sentinel.CanDiscover` 只檢查密文欄位非空（`Sentinel.cs:65`），解不開變成**用空密碼登入**，訊息誤導，而且每晚重複有鎖帳號風險。
- 11-02 取數 14 趟：`PrtgClientFactory.HasUsableCredentials` 會解密（`PrtgClientFactory.cs:108-110`），解不開＝未設定，`PrtgDailyPipeline.cs:111-118` 寫成 `disabled`，執行總表顯示「未啟用」，同頁 PRTG 卡卻寫「已啟用」。**這就是使用者問的第 5 項，不是 PRTG 忙碌**。
- 站台需要重新輸入這三組憑證（金鑰檔與資料庫不相符）；這是環境問題，本輪只負責讓它「看得見、不誤導、不誤傷」。

### 1.3 每趟固定出現的警告

每趟取數固定 4 則 Warn：Sentinel 略過憑證驗證 ×2（`SentinelClient.cs:193`）、PRTG 略過憑證驗證 ×1（`PrtgClient.cs:138`）、Security 事件記錄檔讀取失敗 ×1（登錄存取被拒）。結果是這個環境永遠不會有「成功」的取數趟，執行總表每天固定 1 個「有警告」（本機主機）。

### 1.4 AI 趟次的狀態與計數

AI 跟隨取數 6 趟有 5 趟「失敗」、`exitCode 0`。原因是 `ChatJsonAsync` 最終失敗寫 `Log.Error`，`ErrorCount>0` 判成 failed；同時 `aiFailures=0`，因為降級的主機日在 `AiAnalysisHostedService.cs:539` 算進 `Done`。畫面把「部分主機日摘要降級」說成「這趟失敗」，又把失敗率報成 0。

### 1.5 降級結果的呈現

降級把「AI 回覆格式異常，以下為原始內容」寫成標題，儀表板「最新狀況」整欄都是這句（使用者截圖），規則層的結論被蓋掉；且 `AiAnalyzed=true`（`LogAnalysisService.cs:692-696`），「只補跑失敗或未執行」永遠不會再挑到它。

### 1.6 去識別化的誤遮與漏遮

- 「Windows 已group-002診斷」「上次關機的group-002狀態」：某個群組名是常見中文詞，整份檔案都被換掉。
- `domain-002\user-004e\Microsoft\EventSystem`：登錄機碼路徑被當成 `網域\帳號`。
- 「無法在網域 BRK 上」：NetBIOS 網域名不在任何設定欄位，漏遮。
- 憑證指紋 `… d3 ba group-001 28`：兩個字元的群組名撞到十六進位。

### 1.7 其他觀察（本輪不處理，記 BACKLOG）

- 10-20～10-26 的 PRTG 每晚重新評估都是「沒有任何主機對應」：主機對應從 10-27 才存在，依 PRTG-SPEC §4 只往後沿用，這些日子永遠不會成功；回望窗口會自然把它們排除，只是浪費。
- `S009` System 記錄保留不足、`S011` 2 顆 sensor 逾時：都已有正確的提示與隔日重試。

## 2. 其他需求的核對結果

| 需求 | 核對結果（檔案:行號） | 本輪做法 |
|---|---|---|
| 1 儀表板第一排寬度不一 | 三組各 4、4、2 張卡（`dashboard.js` `group:` 計數）；單排模式 `.lf-kpi-strip--row .lf-kpi-group` 的 `flex` 用**總卡數**（`site.css:1171-1174`），`revealFirstRow` 只顯示放得下的第一排，於是 1268px 時每組只剩 1 張、寬度 2:2:1 | 揭露機制量到每組顯示張數後，依**顯示中的張數**分寬（批次D） |
| 3 「無 PRTG 對應」跳轉 | 徽章是純文字（`hosts.js:454`）；目標清單在 PRTG 維護頁「鏡像狀態」頁籤（`Prtg.cshtml:544-550`），沒有錨點；`bindTabs` 只支援 `#頁籤` 深連結（`ui.js:203-214`）；兩頁都要 `Maintain`（`PagesController.cs:107,132`），沒有權限落差 | 徽章改連結，`?focus=unmapped#mirror`，到頁後捲動並短暫標示（批次D） |
| 4 執行時間只有 44 秒 | `RunDayTimingBuilder.cs:18-33` 的總耗時只算取數趟，AI 跟隨取數（87～152 秒）是另一趟；分路明細只在展開列（`runs.js:299`）；**升級前的紀錄沒有 `Phases`**（匯出檔 `phases.runs` 全是 1，只有 11-03 的兩趟有），展開後四路都是「—」 | 總耗時改為取數＋AI 的聯集；欄位直接顯示兩段；舊紀錄的 AI 趟用整趟起訖補上（批次C） |
| 5 設定頁說明文字 | 81 條：常駐說明 33、`?` 提示 31、警示框 5、副標 12；AI 與郵件頁籤各 16 條最多。只有 `ReportRetentionWebTests.cs:143` 鎖定一句說明文字（「會佔用資料庫空間」） | 依 §8.6a 逐條處理，清單見批次E |

## 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A | 憑證前置檢查、PRTG 等待重試、固定警告降級（§1.2、§1.3、定案 5、6） | 中 | 無 |
| B | AI 格式異常：證據、空陣列判定與無文法重試、降級可補跑與標題、趟次狀態與計數、匯出案例判定改欄位（§1.1、§1.4、§1.5、定案 3、4） | 大 | 無 |
| F | 去識別化修正（§1.6、定案 7） | 小 | 無 |
| C | 執行總表時間（需求 4） | 中 | 無 |
| D | 儀表板第一排寬度、主機頁 PRTG 對應連結（需求 1、3） | 小 | 無 |
| E | 設定頁說明文字（需求 5） | 中 | 無 |
| G | 文件、說明書、BACKLOG、全量測試 | 小 | A～F |

建議順序：A → B → F → C → D → E → G。每批驗收通過後 commit 並 push。

## 批次A：憑證前置檢查、PRTG 等待重試、固定警告

### 現況與核對結果

- `Sentinel.CanDiscover`（`Sentinel.cs:65`）：只看 `Username` 與 `PasswordEnc` 非空；NetIQ 管線（`NetiqPipelineService.cs:236-241`）據此決定要不要查詢。
- `PrtgClientFactory.HasUsableCredentials`（`:92-110`）：會嘗試解密，解不開＝未設定；`PrtgDailyPipeline.cs:111-118` 因此寫 `disabled` 並印「PRTG 未啟用或尚未設定認證資訊，略過」。
- `PrtgClient` 類別註解「不抽介面、不加重試、不加節流、不加分頁」（`:31`）；401 黏住以免累計鎖定（`:202-205`、`:439`）；傳輸類失敗不黏，註解明寫「那種重試是有意義的」。逐顆 sensor 取數失敗已有隔日重試（匯出檔 `S011`）。
- `HealthService.cs:119-128`：`CryptoHelper.DecryptFailureSeen` 已會讓健康狀態變 degraded，但旗標只在**有人嘗試解密後**才會設，夜間第一次失敗前沒有任何提示。
- 略過憑證驗證的 Warn：`SentinelClient.cs:193`（每台 Sentinel 每趟一次）、`PrtgClient.cs:138`（每趟一次）。

### 定案

- 解不開的憑證一律「不送請求、說清楚原因」：空密碼登入既誤導又可能鎖帳號。
- PRTG 結局**不新增第六種值**：五值的消費端有十幾處（徽章、匯出、說明書、guide），新增值的成本高於收益；沿用 `disabled` 並以 `Note` 交代原因，畫面顯示原因。
- PRTG 等待重試只加在**單發大查詢**（結構同步的 devices／sensors、狀態變更 messages、historicdata 單發）：次數 2、間隔 15 秒→60 秒（暫定），只對逾時、連線失敗、HTTP 5xx／429 重試，401／403／4xx 不重試，接受取消與停止鈕。逐顆 sensor 取數不加（三千顆 × 重試會讓整晚耗在等待，而且已有隔日重試）。
- 略過憑證驗證改 Info：設定頁已有該開關與警語，每趟再 Warn 一次沒有新資訊。Security 記錄檔讀取失敗維持 Warn。

### 改動

- **A-1 憑證前置檢查**：每趟取數開始時（本機／NetIQ／PRTG 三路之前）檢查所有已設定的密文能否解密：AI 金鑰、SMTP、PRTG 三種、每台 Sentinel。解不開的寫一行 Error（logger 固定、訊息列名稱不列值）、`Output` 一行「✗ 下列憑證無法解密，本趟略過：…；請到對應設定頁重新輸入」。這一行讓 `S002` 之後不再出現，並進匯出的訊號組。
- **A-2 Sentinel**：`CanDiscover` 的判定改為「帳號非空且密文**解得開且非空**」（唯一判定點，`NetiqProbeRunner.cs:66` 的同型檢查一併改用）；解不開時的略過訊息寫「密碼無法解密」，不寫「帳密未設定」。不送任何請求。
- **A-3 PRTG**：憑證解不開時 `prtgOutcome=disabled`，`PrtgDayStat.Note` 與主結局說明寫「憑證無法解密，請到 PRTG 維護頁重新輸入」；排程頁 PRTG 卡的模組狀態在 `已啟用` 但憑證解不開時顯示警示文字與連結（走既有 `prtg-disabled-hint` 的形狀）。`PrtgOutcomeDisabled` 的語意在 WEB-SPEC 改為「未啟用或憑證不可用」。
- **A-4 PRTG 等待重試**：`PrtgClient` 的單發查詢加上述重試；每次重試寫 Warn「PRTG 回應逾時／HTTP 503，N 秒後第 k 次重試」，讓「PRTG 太忙」看得見；總等待時間計入探測與同步的既有逾時預算，不得讓停止鈕失效。類別註解同步改寫。
- **A-5 固定警告降級**：兩處略過憑證驗證改 `Log.Info`；訊息不變。

### 容易漏的地方

- `CanDiscover` 的呼叫端不只管線：NetIQ 維護頁的探測、診斷、同步結構也用它，先 grep 全部呼叫端再改。
- PRTG 重試不可包住 `FailCredentials` 路徑：401 要立刻黏住，不能被重試放大。
- 前置檢查只讀設定，不得因檢查本身失敗中斷分析（§11-4）。

### 測試 / 驗收

- `NetiqPipelineServiceLookbackTests`／新增案例：密文解不開 → 不呼叫 Sentinel 用戶端（替身計數為 0）、輸出含「無法解密」、`AddFailed` 計數與既有「帳密未設定」一致。
- `PrtgClientFactoryTests`：解不開 → `HasUsableCredentials` false 不變；新增「原因」查詢方法的案例。
- `PrtgDailyPipelineTests`：憑證解不開 → `disabled` 且 `Note` 含「憑證無法解密」；PRTG 關閉時 `Note` 不含它。
- `PrtgClientTests`：逾時→重試 2 次後成功／失敗；401 不重試且黏住；取消令牌在等待期間生效；逐顆取數路徑不重試（呼叫次數斷言）。
- `SentinelClientTests`、`PrtgClientTests`：略過憑證驗證為 Info（以 NLog 記憶體 target 斷言等級）。
- 突變：A-2 判定拿掉解密檢查、A-4 重試包住 401、A-1 列出憑證值，測試都要紅。
- 定向篩選（實作後確認命中數 > 0）：`FullyQualifiedName~NetiqPipelineService|FullyQualifiedName~PrtgClient|FullyQualifiedName~PrtgDailyPipeline|FullyQualifiedName~SentinelClient|FullyQualifiedName~BatchRunRecorder|FullyQualifiedName~PrtgRunsCard`。

## 批次B：AI 格式異常

### 現況與核對結果

- 請求：`AIService.cs:252` 在 `jsonMode` 時送 `response_format: {type: json_object}`；`ChatJsonAsync`（`:362-437`）三次嘗試都帶同一個 `jsonMode: true`。
- 解析：`AiJson.TryParse` 候選為原文、括號配對的物件、補括號修復；`[\n]` 三者皆不是物件 → null。
- 回應欄位：`OpenAIMessage` 只讀 `content`（`:748-755`）；思考類欄位（`reasoning_content`／`reasoning`）沒有讀。
- 降級：`LogAnalysisService.cs:687-696` 有回覆原文就降級為「原文保留」，`AiAnalyzed=true`，標題為 `AiFormatAnomalyHeadline`。
- 補跑選擇：`EfAnalysisRecordStore.cs:1845` `WhereAiPending`＝`AiPending && !DetailPruned`；降級後 `AiPending=false`。
- AI 排程計數：`AiAnalysisHostedService.cs:539-565` 只有擲例外或 `AiAnalyzed=false` 算 `Failed`；降級算 `Done`。`AiOutcome`（`DailyAnalysisRecord.cs:220`）沒有「降級」旗標。
- 趟次狀態：`BatchRunStatus.Compute` `ErrorCount>0`→failed、`AiFailures>0`→warning；`ChatJsonAsync` 最終失敗是 `Log.Error`（`:434`）。
- 第 63 輪匯出的案例判定：`QueryByHeadline(..., AiFormatAnomalyHeadline)`。改標題後這條判定會失效，要改。
- schema 升級：`SchemaUpgrader.AddColumnIfMissing` 是既有機制（`SchemaUpgrader.cs:62-68` 有 `lf_daily_records` 的先例）。

### 定案

- **證據與保護分開**：證據（B-1）決定之後怎麼修提示詞與 `response_format`；保護（B-2）不等證據，因為它在任何原因下都成立。
- **第二次重試不帶文法**：允許模型先思考；抽取由 `AiJson` 負責。第一次仍帶文法，保留它對一般情況的好處。
- **降級主機日可補跑，上限 3 次**：新增欄位 `ai_degraded_attempts`（int，預設 0），降級時 +1 並維持 `AiPending=true`；達 3 次才 `AiPending=false`。欄位同時是匯出的案例判定（`> 0`），比標題字串可靠。
- **降級標題用規則層結論**：命中規則時取規則的白話說明；否則取最嚴重問題的來源與次數；後綴「（AI 摘要待補）」。原始回覆仍留在摘要欄（它是匯出案例的證據）。
- **降級算失敗**：`AiOutcome` 加 `AiDegraded`；排程計入 `Failed`，`ChatJsonAsync` 最終失敗改 `Log.Warn`；趟次因此是「有警告」。互動式 AI 卡片同樣受影響，只是等級，不影響行為。

### 改動

- **B-1 證據**：`AiResponse` 多帶清洗前原文長度與頭尾（1,500／500 字）、`reasoning_content`／`reasoning` 欄位是否存在與長度、`finish_reason`（已有）；診斷 JSON 加這些欄位；`AiJsonDiagnostics.TryRead` 相容舊格式（缺欄為 null）。
- **B-2 空陣列判定與無文法重試**：清洗後的內容去空白後是 `[]`、`{}` 或只有括號與換行 → 視為空回覆（類別 `empty`，診斷另標 `emptyShape: array|object`）；`ChatJsonAsync` 第二次起 `jsonMode:false`，重試提示加一句「可以先思考，最後輸出一個 JSON 物件」。成功路徑的 `RawContent` 仍是清洗後內容。
- **B-3 schema**：`SchemaUpgrader` 加 `lf_daily_records.ai_degraded_attempts`；`DailyRecordRow`、`DailyAnalysisRecord` 對應欄位；`DB-SPEC` 同步。SQL Server／SQLite 型別照既有先例。
- **B-4 降級語意**：`LogAnalysisService` 降級分支：標題依定案產生、`AiAnalyzed=false`、`AiPending=true`、`ai_degraded_attempts+1`，達上限時 `AiPending=false` 並在標題後綴改為「（AI 摘要已放棄：連續 3 次格式異常）」；`AiOutcome.AiDegraded=true`。`RetryAiAsync` 的低風險跳過與其他分支不變。**什麼算一次**：同一主機日每趟 AI 排程最多 +1，同趟內的 3 次 `ChatJsonAsync` 重試不算 3 次。
- **B-5 計數與等級**：`AiAnalysisHostedService` 對 `AiDegraded` 計 `Failed`、不計 `Done`；`ChatJsonAsync` 最終失敗 `Log.Error`→`Log.Warn`；`aiFailureRate` 因此有值。
- **B-6 匯出案例判定**：`IAnalysisRecordQuery.QueryByHeadline` 改為 `QueryAiDegraded(from, to, take)`（`ai_degraded_attempts > 0`，SQL 下推）；第 63 輪的 `AiFormatAnomalyHeadline` 常數保留給歷史資料：**歷史案例**（欄位為 0 但標題等於舊常數）也要算進案例，查詢用 `attempts > 0 OR headline = 舊常數`，guide 說明兩種來源。
- **B-7 畫面**：記錄詳情與儀表板不改版，標題自然換成規則層結論；記錄詳情在 `AiPending` 且 `attempts>0` 時的狀態文字寫「AI 摘要待補（第 N 次格式異常）」。

### 容易漏的地方

- `AiAnalyzed=false` 的既有消費端：報告產生、週檢、郵件、儀表板的「AI 待補」顯示，先 grep `AiAnalyzed` 全部讀取點，確認降級主機日在這些地方的呈現合理（它有規則結論，不是「暫缺」）。
- 第 63 輪 `LogAnalysisServiceRetryAiTests` 斷言舊標題與 `AiAnalyzed=true`，這批**刻意**推翻，改測試時在執行紀錄寫明。
- 補跑選擇 `SelectNextBatch` 的近日層／積壓層排序不變，只是多了這類待補。

### 測試 / 驗收

- `AIServiceJsonDiagnosticsTests`：`[\n]`→類別 `empty`＋`emptyShape=array`；第二次請求不含 `response_format`（攔截請求本體斷言）；第一次仍含；`reasoning_content` 長度記錄；舊格式診斷可讀。
- `LogAnalysisServiceRetryAiTests`：降級 → 標題為規則結論＋後綴、`AiAnalyzed=false`、`AiPending=true`、attempts=1；第 3 次 → `AiPending=false`、後綴改為放棄；同趟 3 次重試只 +1；成功後 attempts 不歸零（保留歷史）但 `AiPending=false`。
- `AiAnalysisSchedulerTests`：降級計 `Failed`、趟次狀態 warning、`AiFailures` 正確。
- `SchemaUpgraderTests`／`SchemaUpgraderModelParityTests`：新欄位兩後端一致；既有列預設 0。
- `AnalysisRecordHeadlineQueryTests`（改名為 degraded 查詢測試）：`attempts>0` 與舊標題都算案例；`ToQueryString` 兩 provider 翻譯是欄位比較。
- `RunExportServiceTests`：案例來源兩種都進 `aiFormatAnomalies`，guide 有說明。
- 突變：B-2 第二次仍帶文法、B-4 上限拿掉、B-5 降級仍算 Done、B-6 只查欄位不查舊標題，測試都要紅。
- 定向篩選：`FullyQualifiedName~AIService|FullyQualifiedName~LogAnalysisService|FullyQualifiedName~AiAnalysisScheduler|FullyQualifiedName~SchemaUpgrader|FullyQualifiedName~AnalysisRecord|FullyQualifiedName~RunExport|FullyQualifiedName~BranchOutcomeAndCache`。

## 批次F：去識別化修正

### 改動（`RunExportDeidentifier`／`Factory`）

- **F-1** 中文（CJK）字典詞長度 ≤ 2 不進字典；ASCII 的群組、角色、顯示名稱長度 < 3 不進字典；主機名與帳號維持 ≥ 2。
- **F-2** `KeepDomains` 加登錄機碼：`HKLM`、`HKCU`、`HKCR`、`HKU`、`HKCC`、`HKEY_LOCAL_MACHINE`、`HKEY_CURRENT_USER`、`HKEY_CLASSES_ROOT`、`HKEY_USERS`、`SOFTWARE`、`SYSTEM`、`Wow6432Node`。
- **F-3** `DeidentifierSettings.From`：`AdSearchBase` 的每個 `DC=` 標籤進網域字典（`DC=brk,DC=local` → `brk`、`brk.local`）；`AdServers` 的主機部分已在。
- **F-4** `meta.deidentification.notice` 補一句「群組名稱若是常見詞且長度在兩字以內不會被遮」。

### 測試 / 驗收

- `RunExportDeidentifierTests`：群組「成功」不遮、群組「成功廠區」會遮；`HKLM\Software\Microsoft` 原樣；`DC=brk,DC=local` 讓「網域 BRK 上」被遮；兩字元 ASCII 群組名不遮、主機名 `DB` 仍遮。
- 定向篩選：`FullyQualifiedName~RunExportDeidentifier|FullyQualifiedName~RunExportService`。

## 批次C：執行總表的執行時間

### 定案（`ui-ux-pro-max`：表格不撐破版面 #71，兩行呈現沿用既有 `withPhaseDuration` 的形狀）

- 「執行時間」欄第一行：整天的起訖與**聯集總耗時**（取數趟＋AI 趟，排除異常中斷）；第二行小字：「取數 44 秒 · AI 1 分 27 秒」。
- 展開列的四路明細保留，並加「總計」一列。
- 舊紀錄：AI 趟沒有 `Phases` 時，AI 那一路用整趟起訖；取數趟沒有 `Phases` 時，本機／NetIQ／PRTG 顯示「無階段資料（升級前紀錄）」而不是「—」；當天完全沒有趟次才是「—」。

### 改動

- **C-1** `RunDayTimingBuilder`：`TotalSeconds`、`FirstStartedAt`、`LastFinishedAt` 涵蓋 AI 趟；新增 `FetchSeconds`、`AiSeconds`（各自聯集）；`Branches` 的 AI 路在沒有 `Phases` 時退回整趟起訖；新增 `MissingPhaseFetchRuns` 計數供前端顯示「無階段資料」。
- **C-2** `run-timeline.js`：`dayTimingText` 兩行；`renderDayTiming` 加總計與「無階段資料」文字。
- **C-3** WEB-SPEC §9.10（`:3019-3020`）改寫；說明書 `10-scheduler.md` 的「執行總表」一列同步。

### 測試 / 驗收

- `RunMonitorServiceTests`／`RunDayTimingBuilder` 案例：取數 44 秒＋AI 87 秒不重疊 → 總計 131；重疊只算一次；AI 趟無 `Phases` → AI 路為整趟；取數趟無 `Phases` → `MissingPhaseFetchRuns=1`；異常中斷不計。
- `RunsTimelineBehaviorTests` 新案例（登錄 `InlineData`）：兩行文字、無階段資料文字、無趟次為「—」。
- 定向篩選：`FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~RunsTimelineBehaviorTests|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout`。

## 批次D：儀表板第一排、主機頁 PRTG 對應連結

### 改動

- **D-1 第一排等寬**：`revealFirstRow` 量到每組顯示張數後，在單排模式為每組設 `lf-kpi-group--v{n}` 類別（n＝顯示中的張數）；CSS 的 `.lf-kpi-strip--row .lf-kpi-group` 以 `--lf-kpi-visible` 優先、沒有時退回 `--lf-kpi-count`。展開後回到總卡數。字級切換事件與 ResizeObserver 重算時同步更新。
- **D-2 徽章連結**：`hosts.js` 的 `no-map` 徽章改為 `<a class="lf-badge …">`，`href=appUrl('/admin/prtg?focus=unmapped#mirror')`，帶 `title`「前往 PRTG 維護頁的未對應主機清單」；觸控區 36px。
- **D-3 定位**：`prtg-admin.js` 讀 `focus` 查詢參數，等未對應清單渲染完成後 `scrollIntoView` 到該區塊（區塊加 `id="prtg-unmapped-section"`），加 1.5 秒的 `lf-focus-flash` 樣式（用 `--lf-focus-ring`，不寫 inline style），並以 `replaceState` 移除參數避免重整再跳。清單載入失敗時不捲動。

### 測試 / 驗收

- `DashboardRevealBehaviorTests` 新案例：4/4/2 張、每組只放得下 1 張時三組寬度類別都是 `--v1`；展開後移除。
- `FrontendMiscUiTests`／hosts 行為測試：徽章是連結、href 正確、不含 `style=`。
- `PrtgAdminPageUiTests`：區塊有 id；行為測試：帶 `focus=unmapped` 時呼叫捲動、參數被移除、失敗不捲動。
- 瀏覽器實測：1268px 與 375px 第一排等寬；點徽章落在正確區塊。
- 定向篩選：`FullyQualifiedName~DashboardReveal|FullyQualifiedName~FrontendMisc|FullyQualifiedName~PrtgAdminPage|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout`。

## 批次E：設定頁說明文字

### 定案

原則是 §8.6a 加使用者引用的「讓版面自己說明」：**版面與 label 能講的不再寫；要講的放 `?`；只有後果句常駐**。不重排版面、不改欄位 id（`SetupUxF2Tests` 守住 `進階設定` summary）。`ReportRetentionWebTests.cs:143` 鎖定的「會佔用資料庫空間」保留原句。

### 改動（行號為 `Settings.cshtml` 現況，實作前重新核對）

**刪除或合併（同一件事講兩三次）**
- AI：模型名稱的 form-text「選填，預設 local-model」（98，`settings.js:233`）刪，placeholder 已是預設值；金鑰 form-text（`settings.js:414-416`）只留「已設定金鑰」狀態句，「留空＝沿用」由 placeholder 承擔；估費單價 236／242 同句兩次，留一次放在兩欄共用的 `?`。
- AD：伺服器 popover 386 刪「一行一台」（placeholder 389 已有）；基準 DN popover 399 刪（label 已寫選填）；篩選器 popover 407 刪預設值（placeholder 410）。
- 分析參數：頻道 popover 307 刪「留空＝預設六頻道」（placeholder 311）；監控資料夾 form-text 301 與 popover 296 合併成一條。
- 郵件：SMTP 密碼 form-text（`settings.js:547-549`）同金鑰處理；收件人 popover 736 刪「一行一位」。
- 層級：「高風險日為必要顯示項目」popover 48 刪（勾選框已鎖定）；常用語副標 62-65 的「一行一條」刪，上限收進 `?`。
- 外觀：副標題字距說明 905 刪；圖示 form-text 922 刪「內建圖示」重複句。
- 資源守門：674 前半「與本頁其他設定共用下方的儲存」刪。

**收進 `?`（口徑、調校指引、範圍定義）**
- 層級 32-37 日風險等級說明；資料保留 473／481／489／497 的範圍定義前半（硬性限制「不可小於／大於」留常駐）；AI 141「27B 級模型」刪除、不收；派工 870／874／878 三條口徑合成一個 `?`。

**修正與行為不符的文字**
- 郵件 689「三路觸發」實際列四項 → 改為「以下通知」並列實際路數（先 grep `MailNotificationService` 確認路數）；736「三路通知共用」同步。
- 郵件 827 寫死「12:00 寄、隔天 08:00 寄」→ 改為以目前設定值描述的動態句，或只留「以逗號分隔，最多四組」。
- 分析參數 269「下一次排程生效」與 345「立刻生效」→ 269 限縮為它實際管的欄位。
- AI 133「appsettings 出廠值」→ 改為「出廠值」。

**保留常駐**：AI 82 資料外送、AI 88「留空會停用」（現在藏在 `?`，依 §8.6a 要常駐，改成 form-text）、92／166／192、AD 375-380 後果句（縮短）、資料保留 462-464 不可逆、508-510 硬性限制、郵件 689／776／818／866、守門 607-610／654／665 前半、外觀 900／909。

### 測試 / 驗收

- 既有守門：`LocalizationLintTests`、`AccessibilityUiTests`（label 綁定 #43）、`CspPolicyGuardTests`、`InlineStyleUiTests`、`PageHeaderUiTests`、`SetupUxF2Tests`、`ReportRetentionWebTests`、`HealthTabUiTests`、`SetupGuideLayoutUiTests`。
- 新增守門一條：`Settings.cshtml` 同一個 `form-group` 內 placeholder 與 form-text 不得含相同的「留空＝」句（靜態檢查）。
- 說明書 `12-settings.md`／`.ai.md` 引用到被改句子的地方同步（grep 關鍵詞）。
- 瀏覽器實測九個頁籤各一張截圖，375px 一張。
- 定向篩選：`FullyQualifiedName~UiTests|FullyQualifiedName~LocalizationLint|FullyQualifiedName~ReportRetentionWebTests|FullyQualifiedName~SetupUx|FullyQualifiedName~HelpContentServiceTests`。

## 批次G：文件與收官

- WEB-SPEC：§9.10 執行時間與 PRTG 卡提示、§11 憑證前置檢查與警告等級、AI 降級語意與 `ai_degraded_attempts`、匯出案例判定、`disabled` 語意。
- DB-SPEC：`lf_daily_records.ai_degraded_attempts`。
- PRTG-SPEC：單發查詢的等待重試與 `disabled` 附原因。
- 說明書：`10-scheduler`（執行時間、PRTG 憑證提示、AI 待補）、`08-hosts`（徽章可點）、`12-settings`、`02-dashboard`（若有提到第一排）。
- BACKLOG：§1.7 兩條；第 63 輪的三條維持。
- 全量 `dotnet test`，結果寫進本文「體檢交接」。

## 明確不做（本輪定案）

- **改提示詞、`response_format` 改 `json_schema`、調 `AiMaxTokens`**：等批次B 的證據與一夜資料後再決定；`json_schema` 支援度依伺服器而異，未驗證。
- **新增 PRTG 結局值**：理由見批次A 定案。
- **逐顆 sensor 取數的重試**：已有隔日重試；逐顆重試會讓整晚耗在等待。
- **PRTG 停止重新評估「主機對應存在之前」的日子**：回望窗口自然排除，記 BACKLOG。
- **設定頁重排版面**：只做文字取捨。
- **代號對照表、serverAdmin 可見輸出**：第 63 輪 BACKLOG 維持。

## 規劃完成後複檢

- **與既有行為的衝突**：
  - 第 63 輪 F-4「案例＝標題精確等於常數」被 B-6 推翻，改為欄位判定並相容歷史標題；第 63 輪 PLAN 歸檔前在執行紀錄註明。
  - 降級 `AiAnalyzed` 由 true 改 false：`AiAnalyzed=false` 的消費端在批次B「容易漏的地方」要求全數盤點；報告產生不看 `AiAnalyzed`（`LogAnalysisService.cs:756` 註解），不受影響。
  - `PrtgOutcomeDisabled` 語意擴大為「未啟用或憑證不可用」，所有顯示「未啟用」的地方要能帶原因；執行總表徽章文字維持，滑鼠提示與下方小字顯示原因（既有 `prtgNote` 機制）。
  - 略過憑證驗證改 Info 後，執行詳情「重要紀錄」不再顯示它；設定頁的開關警語是唯一提示，已存在。
- **批次之間**：A-1 的 Error 行會進匯出訊號組（批次F 的遮罩不影響，訊息不含值）；B-6 改查詢後批次G 的 guide 文字要跟著改；C 與 D 都改 `runs.js`／`site.css`，順序 C→D 不並行。
- **四個坑**：
  - 什麼算一個：B-4「每趟 +1」、A-4「只對單發查詢」、D-1「顯示中的張數」、F-1「CJK ≤2」都已寫明。
  - 不可逆／破壞性：B-3 新增欄位有預設值，沒有破壞；B-4 達上限後 `AiPending=false` 是單向閘門，繞過路徑是既有的「強制重新分析」（會重標待補並重置？**實作前確認**強制重新分析是否重置 attempts；定案：重置為 0）。
  - 單向閘門：PRTG 401 黏住是既有閘門，A-4 不得改變。
  - 移除類：`QueryByHeadline` 被取代，呼叫端只有 `RunExportService` 與替身兩處，白名單涵蓋。
- **升級與既有資料**：歷史降級案例沒有 attempts 值，B-6 以舊標題相容；它們 `AiAnalyzed=true、AiPending=false`，不會自動進補跑——若要補，使用者用「只補跑失敗或未執行」不會選到，需「強制重新分析」。guide 與說明書寫明。
- 複檢結果：補進一條待實作前確認（強制重新分析重置 attempts），其餘無新增事項。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
