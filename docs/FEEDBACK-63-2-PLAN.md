# LogForesight 第 63-2 輪規劃：第一份診斷匯出的回饋（AI 格式異常、憑證解不開、執行時間、儀表板與設定頁）

> 狀態：實作完成，待體檢（`feature/feedback-63-2`）
> 基準：dev@0bf12e40（7,956 通過／11 略過，Windows）
> 來源：使用者 2026-11-03 五項重點，加上第一份診斷匯出檔（14 天、站台 1.0.53.2、13 台主機、18 趟）的分析
> 實作方式：主代理寫規格與驗收；改檔代理依複雜度選 Sonnet 5.5 或 Haiku 5.5；同一 checkout 一次一個改檔代理；主代理獨立重驗
> 設計面：套用 `ui-ux-pro-max`（使用者確認）；檢索結果只有表單 label 綁定（#43）、表格不撐破版面（#71）、動作回饋（#61）三條相關；版面判斷以 `docs/DESIGN-SYSTEM.md` 與 WEB-SPEC §8.6a 為準
> 審查：第一版經「值班使用者／系統管理者／驗收者」與「程式面／架構」兩份唯讀審查（Sonnet），抓到 8 條會讓批次做不出結果的缺口與約 30 條補強；本版已全部吸收

## 0. 總結與定案

| 使用者定案（2026-11-03） | 本版落實方式 |
|---|---|
| 1 先收第 63 輪，63-2 自 dev 開新分支 | 不變 |
| 2 套用 `ui-ux-pro-max` | 不變 |
| 3 AI 格式異常：補證據＋空陣列視為空回覆、第二次重試不帶 JSON 文法；降級可補跑上限 3 次 | 不變，但實作路徑整個改寫：**降級不翻 `AiAnalyzed`**（它有 17 個檔案在讀、排程只在 true 時寫回），改以 `AiPending=true` 加新欄位 `ai_degraded_attempts` 表達；詳見批次B-b |
| 4 AI 趟次部分降級顯示「有警告」、降級計入 `aiFailures` | 不變；另定「降級算有進度、不觸發本批全滅防線」 |
| 5 憑證解不開沿用 `disabled` 附原因；單發大查詢加等待重試 | 不變；原因改放趟次層欄位（禁用路徑沒有逐日統計可放）；重試放在 `PrtgTablePager` 單點、可重試判斷用型別化欄位不比對訊息 |
| 6 略過憑證驗證改 Info；Security 記錄檔讀不到維持 Warn | 不變；**明講**：做完後本機主機仍每天一個「有警告」，那是真的偵測盲區，要消失得讓服務帳號有讀取權 |
| 7 去識別化：短詞與登錄機碼、DC 標籤 | 不變；長度規則只套用群組與角色，顯示名稱維持（兩字中文姓名要遮）；登錄機碼的真因是 regex 回溯，不只字典 |

### 0.1 分析用名詞（兩種「AI 失敗率」）

匯出檔與畫面會出現兩種分母，guide 與說明書都要寫明：
- **呼叫層**：`AIService` 的 `ChatJsonAsync` 最終失敗次數／呼叫次數（匯出 `signalGroups`、診斷行）。
- **主機日層**：AI 排程的 `aiFailures／aiCalls`（趟次欄位、`runStats`），一個主機日算一次，本輪起降級算一次失敗。

## 1. 第一份匯出檔的分析

### 1.1 AI 回覆格式異常

| 事實 | 數字 |
|---|---|
| 案例數；回覆全部是 `[\n]`（3 個字元） | 25／25 |
| 解析失敗／內容檢查失敗 | 148／0 |
| 每案嘗試次數 | 全部 3 次 |
| 案例與對照組（同主機前後一天）輸入特徵中位數 | 問題數 2 vs 3、字數 555 vs 645，其餘相同 |
| AI 呼叫總數／最終失敗（呼叫層） | 225／49（約 22%） |
| `detailedDiagnosticsFirstSeen` | null（匯出時第 63 輪的診斷尚未遇到失敗） |

**判斷**：不是事件內容造成。程式對本機端點送 `response_format: json_object`，伺服器以文法強制輸出合法 JSON，而文法允許陣列開頭；模型（gemma-4-31B，會先思考）想先輸出思考文字而被文法擋下時，最短的合法輸出就是 `[]`。這解釋了為什麼永遠是同一個 3 字元回覆、為什麼重試提示也救不回來（重試仍帶同一個文法）。

這仍是假設。缺的證據：`finish_reason`、清洗前的原始回覆、伺服器是否把思考放在 `reasoning_content` 這類欄位。第 63 輪的診斷只記清洗後內容，批次B-a 補齊。

### 1.2 憑證解不開的連鎖反應

匯出檔 `undecryptableSecrets`：PRTG 密碼、PRTG passhash、Sentinel 密碼。造成：
- 11-03 取數：`S002` CryptoHelper「密文無法解密…此欄視為未設定」後，`S003` 兩台 Sentinel 回 401、12 台主機全部失敗。`Sentinel.CanDiscover`（`Sentinel.cs:65`）只檢查密文欄位非空，`SentinelConnectionFactory`（`:16-17`）把解不開的密碼變成空字串，於是**用空密碼登入**：訊息誤導，每晚重複有鎖帳號風險。
- 11-02 取數：`PrtgClientFactory.HasUsableCredentials`（`:92-110`）解不開＝未設定，`PrtgDailyPipeline.cs:111-118` 早退寫 `disabled`，執行總表「未啟用」、同頁 PRTG 卡「已啟用」（卡片靠前端 `hasPrtgConnection` 看 `prtgHas*` 布林，`runs.js:1786` 註解自承是第二份判定）。**這就是使用者問的第 5 項，不是 PRTG 忙碌**。
- 站台需要重新輸入這三組憑證（金鑰檔與資料庫不相符）；本輪只負責讓它「提早看得見、不誤導、不誤傷」。

### 1.3 每趟固定出現的警告

每趟取數固定 4 則 Warn：Sentinel 略過憑證驗證 ×2（`SentinelClient.cs:193`）、PRTG 略過憑證驗證 ×1（`PrtgClient.cs:138`）、Security 事件記錄檔讀取失敗 ×1（登錄存取被拒）。前三則是刻意設定，改 Info；第四則是真的偵測盲區，維持 Warn——**所以做完後本機主機仍每天「有警告」**，說明書要寫「怎麼消掉」（服務帳號加入 Event Log Readers）。

### 1.4 AI 趟次的狀態與計數

AI 跟隨取數 6 趟有 5 趟「失敗」、`exitCode 0`：`ChatJsonAsync` 最終失敗寫 `Log.Error`（`AIService.cs:499`），`ErrorCount>0` 判成 failed；同時 `aiFailures=0`，因為降級的主機日算進 `Done`（`AiAnalysisHostedService.cs:539`）。

### 1.5 降級結果的呈現

降級把「AI 回覆格式異常，以下為原始內容」寫成標題，儀表板「最新狀況」整欄都是這句；`AiAnalyzed=true、AiPending=false`，「只補跑失敗或未執行」永遠不會再挑到它。前端以 `headline.includes('AI 待補')` 判斷待補徽章（`format.js:156-158`、`records.js:635`、`record-detail.js:286`）——任何改標題的做法都要同時換掉這個字串判定。

### 1.6 去識別化的誤遮與漏遮

- 「Windows 已group-002診斷」：某個群組名是常見中文詞。
- `domain-002\user-004e\Microsoft\EventSystem`：`DomainUserRx` 的 `(?![\\/])` 回溯，`HKLM\Software\Microsoft` 被切成 `HKLM`＋`Softwar`（審查以 .NET Regex 實測），殘字 `e` 就是證據；只把 hive 加進保留清單修不好。
- 「無法在網域 BRK 上」：NetBIOS 網域名不在任何設定欄位。
- 兩字元群組名撞到十六進位指紋。

### 1.7 其他觀察（本輪不處理，記 BACKLOG）

- 10-20～10-26 的 PRTG 每晚重新評估都是「沒有任何主機對應」：對應從 10-27 才存在，依 PRTG-SPEC §4 只往後沿用，這些日子永遠不會成功；回望窗口會自然排除，只是浪費。
- `S009` System 記錄保留不足、`S011` 2 顆 sensor 逾時：都已有正確提示與隔日重試。

## 2. 其他需求的核對結果

| 需求 | 核對結果（檔案:行號） | 本輪做法 |
|---|---|---|
| 1 儀表板第一排寬度不一 | 三組各 4、4、2 張卡；單排模式 `.lf-kpi-strip--row .lf-kpi-group` 的 `flex` 用**總卡數**（`site.css:1171-1174`），`revealFirstRow` 只顯示放得下的第一排，於是每組只剩 1 張時寬度 2:2:1。審查指出「依顯示張數改權重」會形成量測→權重→寬度→量測的回授迴圈 | 收合狀態三組**等寬**（只看收合／展開，不看量測結果，沒有迴圈）；展開後回到依卡數分寬（批次D） |
| 3 「無 PRTG 對應」跳轉 | 徽章是純文字（`hosts.js:454`）；PRTG 維護頁「鏡像狀態」頁籤**已有**區塊 `id="prtg-unmatched-section"`、徽章按鈕 `prtg-mirror-map-unmatched` 與 `jumpToUnmatched()`（`prtg-admin.js:2949-3010`）；清單 API `prtg-unmapped-hosts` 另貼 `[RequireViewAll]`（`PrtgHostMappingAdminController.cs:53-55`），清單分頁 20 筆 | 沿用既有跳轉機制；連結帶主機、清單把該台釘在第一頁最上面；只有 `ViewAll` 的使用者才把徽章做成連結（批次D） |
| 4 執行時間只有 44 秒 | 總耗時只算取數趟（`RunDayTimingBuilder.cs:18-33`），AI 跟隨取數是另一趟；分路明細只在展開列；**升級前的紀錄沒有 `Phases`**，展開後四路都是「—」。既有測試 `RunDayTimingBuilderTests` 的 `AI趟只進AI那一路_不算進取數總耗時與起訖` 把舊語意當定案，本輪刻意推翻 | 總耗時＝取數＋**跟隨取數**的 AI 趟聯集；非跟隨的 AI 趟（白天手動、積壓補跑）另列，不混進同一個起訖（批次C） |
| 5 設定頁說明文字 | 81 條靜態＋約 10 條 `settings.js` 動態寫入的說明；只有 `ReportRetentionWebTests.cs:143` 鎖定一句；刪 cshtml 元素而不刪 JS 賦值會讓 `settings.js:233` 的 `modelHint.textContent=` 對 null 拋例外、整頁腳本中斷 | 依 §8.6a 逐條處理，cshtml 與 JS 一起改，三條守門保護後果句（批次E） |

## 3. 第二份匯出（2026-11-04）的核對

使用者在 A～G 文件段完成後交回第二份匯出，問「log 中的問題是否都處理完、48 小時未成功更新怎麼辦、PRTG 取值失敗要加強等待重試」。逐組對照程式（事實以 `feature/feedback-63-2@6d74925a` 為準）：

| 訊號 | 內容 | 本輪 A～G 之後 | 處置 |
|---|---|---|---|
| 48 小時警示 | 最近一次排程成功 11-02 01:01；11-03、11-04 兩趟 Sentinel／PRTG 憑證解不開，12 個 NetIQ 主機日全失敗 | 判定正確（`ScheduleFreshnessService`：只看排程取數趟且狀態為 success／warning）。A-1／A-2 讓它不再送空密碼，但主機日仍計失敗，警示**會留著**，直到憑證恢復後下一趟排程成功 | 不能靠程式消除；批次H-3 讓警示說出原因與下一步，並讓完整範圍的手動執行算數 |
| S002 解密錯誤（金鑰不符） | 本機金鑰檔 `keys\lf-crypto.key` 在 `DataRoot`（預設＝執行檔目錄）下，重新部署清目錄會在下次啟動默默產生新金鑰 | A-1b 啟動探測只提早看見；重新輸入密碼後 `crypto_key_fingerprint` 沒有任何更新途徑，`KeyMismatch` 永遠為真（README 寫「重新輸入即可」做不到） | 批次H-2 |
| S011 PRTG 數值擷取逾時 | `FetchValuesAsync`（`PrtgFetchService.cs:1004`）單顆 `historicdata` 失敗只計數，不重試；畫面寫「明日排程會自動再試」 | A-4 的等待重試只包 `PrtgTablePager`（表格分頁），逐顆數值沒有 | 批次H-1 |
| S001／S004／S005 AI 回 `[\n]` | 148 次解析失敗 | B-a／B-b 已做；無文法重試能否拿到答案要等實際跑一晚 | 體檢交接寫明 |
| S003／S009 Sentinel 401 | 憑證解不開 | A-2 不再送請求、計失敗主機日 | 等憑證恢復 |
| S006／S008 略過憑證驗證 | — | A-5 改 Info | 完成 |
| S007 Security 記錄檔 | 堆疊是登錄存取被拒（傳統 `EventLog` API 會查登錄） | 維持 Warn；說明書已改為「群組＋登錄機碼」 | 使用者端權限 |
| S010、PRTG `no_output` 重評 | 環境限制／對應存在前的日子 | — | BACKLOG |

**既有的節流**（H-1 不重做）：`PrtgRequestBudget.Shared` 全程序共用配額——`historicdata` 滾動 60 秒最多 5 次、`table` 每秒 2 次、在途上限 4；`PrtgResourceGuard` 依 PRTG 自身資源 sensor 暫停；快照服務連續失敗每三次把間隔加倍。所以「打爆 PRTG」在送出速率上已有上限，缺的是**失敗後的暫停、補抓與全趟停損**。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| F | 去識別化修正（§1.6、定案 7） | 小 | 無 |
| A | 憑證狀態單一判定、前置檢查與啟動探測、PRTG 等待重試、固定警告降級（§1.2、§1.3、定案 5、6） | 中 | 無 |
| B-a | AI 失敗證據補齊、空陣列判定與無文法重試、最終失敗改 Warn（§1.1、定案 3 前半、4 的等級） | 中 | 無 |
| B-b | 降級主機日的狀態、欄位、補跑上限、標題、計數、匯出案例、前端判定（§1.4、§1.5、定案 3 後半、4 的計數） | 大 | B-a |
| C | 執行總表時間（需求 4） | 中 | 無 |
| D | 儀表板第一排、主機頁 PRTG 對應連結（需求 1、3） | 小 | 無 |
| E | 設定頁說明文字（需求 5） | 中 | 無 |
| G | 文件、說明書、BACKLOG、升級注意、全量測試 | 小 | A～F |
| H | 第二份匯出（11-04）追加：PRTG 數值取數失敗的暫停與補抓、金鑰復原、新鮮度警示可行動（§3） | 中 | A、G 文件段 |

順序：F → A → B-a → B-b → C → D → E → G（文件段）→ H → G（收官段：全量測試、執行紀錄、體檢交接）。F 先做：它最小、完全獨立，先讓匯出端穩定，其餘批次的匯出回歸測試才不會被遮罩差異干擾。B 拆兩段：B-a 只動 `AIService`／診斷，可以先上線收一夜證據；B-b 動狀態機與 schema。共改檔案：A-3 與 C 都改 `runs.js`（PRTG 卡 vs 時間欄，不同函式）；A-1、B-b、F、G 都改 `RunExportService` 的 guide 文字，依序做即可。

### 驗收的共同規則

- 每條改動都要在該批「測試／驗收」出現；驗收寫成主代理在 Windows 上可自己跑的形式：`dotnet test --filter`、`node` 行為測試（新案例登錄 `InlineData`）、或瀏覽器實測（寫明看什麼、數值多少才算過）。
- 篩選詞在寫規格前實跑一次確認命中 > 0；新類別寫完再跑一次。本輪**新增**的測試類別：`SentinelConnectionFactoryTests`、`PrtgTablePagerTests` 的重試案（類別已存在）、`PrtgHostMappingAdminControllerTests`、`SettingsPageWordingUiTests`、`HostDayPostProcessor` 的降級案（放既有 `HostDayWorkflow*` 類或新增）；既有類別已涵蓋就併入並同步篩選詞。
- 每批至少一個突變（文件列出）；突變還原用備份 `cp`＋`cmp`＋`touch`，不用 `git checkout`。
- 改到前端的批次，定向篩選一律加 `UiTests|JsModuleImport|BehaviorTests|ApiTimeout`。

## 批次F：去識別化修正

### 現況與核對結果

- 字典入口是單點 `AddText`（`RunExportDeidentifier.cs:378-383`，硬規則 `t.Length < 2` 不進）；使用者顯示名稱也走它（`:241`）。
- `DomainUserRx`（`:90-91`）對 `HKLM\Software\Microsoft\EventSystem` 匹配 d=`HKLM`、u=`Softwar`（回溯）。
- `DeidentifierSettings.From`（`Factory.cs:90-98`）把 `AdSearchBase` 整串當網域。
- 第 63 輪體檢已把邊界改為「非英數即邊界」、IP 前綴放寬、國碼網域、代號字彙密鑰、額外取數主機與最後修改帳號；本批不重做。

### 定案

- **長度規則依種類**：主機名、帳號、使用者顯示名稱維持 ≥ 2（兩字中文姓名要遮，這份檔案是給外部的）；群組名、角色描述、站台描述：ASCII ≥ 3、CJK ≥ 3。優先序：種類規則先於通用規則；兩字中文**主機名**仍遮。
- 登錄機碼：修 regex，不靠保留清單。

### 改動

- **F-1** `AddText(text, entry, minLength)`：由登錄端依種類給最小長度；CJK 與 ASCII 各算自己的字數。
- **F-2** `DomainUserRx` 的帳號段改為不可回溯（atomic group）且後面不得接 `\`／`/`；登錄 hive（`HKLM`、`HKCU`、`HKCR`、`HKU`、`HKCC`、`HKEY_*`）進 `KeepDomains` 作第二道。
- **F-3** `AdSearchBase` 的每個 `DC=` 標籤與其串接（`brk`、`brk.local`）都進網域字典。
- **F-4** `meta.deidentification.notice` 補「群組與角色名稱若在兩字以內不會被遮」。
- **F-5** `Basic` 後純字母者：長度是 4 的倍數且大小寫混合就遮（不再要求含數字；實作時發現原寫的「≥ 16」會放過 `dXNlcjpwYXNz`）；密鑰編碼變體加表單編碼（空白→`+`）。

### 測試 / 驗收（`FullyQualifiedName~RunExportDeidentifier|FullyQualifiedName~RunExportService`）

- F-1：群組「成功」不遮、群組「成功廠區」遮；角色「DB」不遮、「DBA 組」遮；顯示名稱「王明」遮成 `user-00N`；主機名「資料」（兩字 CJK）遮。
- F-2：`HKLM\Software\Microsoft\EventSystem`、`HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet` 原樣；`CORP\alice` 仍遮成 `domain-001\user-001`（確認沒有把一般帳號一起放過）。
- F-3：`DC=brk,DC=local` → 文字「網域 BRK 上」「brk.local」都被遮。
- F-4：序列化後 notice 含該句（用既有 `RunExportServiceTests` 的端到端 harness）。
- F-5：`Basic dXNlcjpwYXNz` 遮成 `Basic secret`；`Basic configuration` 不遮；密鑰 `p@ss w0rd` 的 `p%40ss+w0rd` 形式被遮。
- 突變：F-2 把 atomic group 拿掉 → `HKLM` 案紅；F-1 把種類長度拿掉 → 「成功」案紅。

## 批次A：憑證狀態、前置檢查、PRTG 等待重試、固定警告

### 現況與核對結果

- Sentinel 可用性有三份判定：`Sentinel.CanDiscover`（POCO，`Sentinel.cs:65`，只看非空）、`SentinelServer.CanDiscover`（`AppSettings.cs:200`，解密後明碼判定，探索精靈用）、`NetiqProbeRunner.cs:66` 自己寫一份非空檢查。管線（`NetiqPipelineService.cs:235`）用 POCO 那份，所以解不開會送空密碼。
- PRTG：`HasUsableCredentials` 有 9 個呼叫端（pipeline、Backfill、Probe、Snapshot×2、StructureSync、ResourceGuard、ScopeEstimate），都把 false 當「未設定」。
- 禁用路徑在 `PrtgDailyPipeline.cs:111-118` 早退，**沒有 `PrtgDays`**，所以「原因放逐日統計 Note」不成立；硬補一筆會改變 `LastRunIssue` 與補評估判定（`PrtgReevaluationPlanner.cs:68-87`）。可用載體：趟次層欄位。
- 密文清單已有三份（匯出工廠逐欄列舉、`CryptoKeyBootstrapper` rewrap 列舉、`HealthService` 旗標）。
- `BatchRunStatus.Compute`：`ErrorCount>0` → failed（`BatchRunStore.cs:552`）；前置檢查若寫 Error，憑證壞掉會讓只跑本機分析的趟也「失敗」。
- `PrtgClient` 的 HTTP 錯誤只有訊息字串（`:443-455`），沒有狀態碼欄位；分頁唯一入口是 `PrtgTablePager.FetchAsync`（`:125`），逐顆 historicdata 不走 pager（`PrtgFetchService.cs:1031`）。`PrtgClient` 已有 `TimeProvider` 注入（`:90`）。`PrtgResourceGuardSource.cs:104` 是同步等待。
- 略過憑證驗證的 Warn 只在 client 自己建 handler 時記（`PrtgClient.cs:137`、`SentinelClient.cs:190`）；目前沒有測試斷言等級。
- 可放管線案例的測試類是 `NetiqPipelineBaselineTests`（有 `MakePipeline`＋`FakeSentinelSearchClientFactory`），不是 `NetiqPipelineServiceLookbackTests`。

### 定案

- **憑證狀態只有一個出口**：Core 新增 `CredentialState { Ok, NotConfigured, Undecryptable }`；Sentinel 由 `SentinelConnectionFactory.ToConnectable(s)` 回傳，PRTG 由 `PrtgClientFactory.CredentialState(settings)` 回傳，`HasUsableCredentials` 改為 `state == Ok`（9 個呼叫端不動）。`Sentinel.CanDiscover` POCO 屬性保留為「欄位非空」但不再用於行為判定；管線、ProbeRunner、探索精靈、管理頁 DTO 一律讀工廠狀態。
- **密文清單只有一份**：Core 新增 `SecretInventory`（名稱＋密文＋「這趟用不用得到」），匯出工廠、啟動探測、前置檢查共用。
- **前置檢查寫 Warn 不寫 Error**：失敗的那一路由該路自己的結果表達（NetIQ 計失敗主機日、PRTG `disabled`＋原因），不另外把整趟判失敗。
- **啟動探測**：站台啟動時對 inventory 逐筆 `TryDecrypt` 一次（它本來就會設 `DecryptFailureSeen`），健康頁在夜間之前就 degraded。
- PRTG 等待重試放 `PrtgTablePager.FetchAsync` 單點：每個 pager 執行總共最多 2 次重試（不是每頁 2 次），延遲 15 秒→60 秒（暫定）走注入的延遲函式，只對 `PrtgClientException.Kind` 為 Timeout／Transport／`HttpStatus` 且狀態 5xx 或 429 者重試；401／403／其他 4xx 不重試；取消令牌在等待期間生效。重試只寫 Info（PRTG 慢的夜晚不該讓趟次變「有警告」），最終失敗照舊。`PrtgResourceGuardSource` 的同步路徑不開重試。逐顆 historicdata 不加。
- 略過憑證驗證改 Info，訊息不變。

### 改動

- **A-1 憑證前置檢查**：取數趟開始時（三路之前）對 inventory 中「這趟會用到」的項目檢查：啟用中的 Sentinel 密碼、PRTG 已啟用時的 PRTG 憑證、AI 已設定時的 AI 金鑰。解不開的寫一行 Warn（固定 logger `CredentialPreflight`，列名稱不列值）與 `Output`「⚠ 下列憑證無法解密，本趟略過：…；請到對應設定頁重新輸入」。檢查本身失敗不中斷分析（§11-4）。手動「立即執行」與排程都走同一個入口（兩者都經 `AnalysisOrchestrator.RunAsync`，實作前 grep 確認）。
- **A-1b 啟動探測**：`CryptoKeyBootstrapper` 啟動流程尾端對 inventory 逐筆 `TryDecrypt`，不改任何資料。
- **A-2 Sentinel**：管線、`NetiqProbeRunner`、探索精靈、`SentinelAdminService.ToDto` 改讀 `ToConnectable(s).CredentialState`；`Undecryptable` 時管線訊息「密碼無法解密，略過（轄下 N 台本次不查詢）」、計入 `AddFailed`，**不送任何請求**；管理頁清單 DTO 加 `credentialState`，畫面顯示「密碼無法解密」。
- **A-3 PRTG**：`BatchRun` 新增 `PrtgOutcomeNote`（string?），pipeline 早退時寫原因（「PRTG 未啟用」／「尚未設定認證資訊」／「憑證無法解密」）；執行總表（無逐日統計時退回它）、執行紀錄清單、執行詳情三處對 `disabled` 顯示原因文字在徽章旁（不只 title）。設定 DTO 與排程狀態 DTO 加 `prtgCredentialState`；`runs.js` 刪掉 `hasPrtgConnection` 第二份判定，卡片顯示「已啟用（憑證無法解密）」並連到 PRTG 維護頁連線頁籤。WEB-SPEC 的 `disabled` 語意改為「未啟用或憑證不可用，原因見 `PrtgOutcomeNote`」。
- **A-4 PRTG 等待重試**：`PrtgClientException` 加 `Kind` 與 `StatusCode`；`PrtgTablePager.FetchAsync` 加重試選項（預設關），結構同步與狀態變更查詢的呼叫端開啟；延遲函式可注入。
- **A-5 固定警告降級**：兩處 `Log.Warn` 改 `Log.Info`，訊息不變。

### 測試 / 驗收（`FullyQualifiedName~NetiqPipelineBaseline|FullyQualifiedName~NetiqProbe|FullyQualifiedName~PrtgClient|FullyQualifiedName~PrtgTablePager|FullyQualifiedName~PrtgDailyPipeline|FullyQualifiedName~SentinelClient|FullyQualifiedName~SentinelConnectionFactory|FullyQualifiedName~SentinelAdmin|FullyQualifiedName~BatchRunRecorder|FullyQualifiedName~PrtgRunsCard|FullyQualifiedName~CryptoKeyBootstrapper|FullyQualifiedName~HealthService|FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout`）

- A-1：`NetiqPipelineBaselineTests` 新案：Sentinel 密碼解不開 → 替身 client 呼叫數 0、`Output` 含「無法解密」、失敗主機日數＝轄下台數、該趟 `ErrorCount` 不因前置檢查增加（recorder 斷言）、`WarnCount` +1；AI 未設定時不檢查 AI 金鑰；檢查函式擲例外時分析照常完成。
- A-1b：`CryptoKeyBootstrapperTests`：有解不開密文時啟動後 `DecryptFailureSeen=true`；全部正常時為 false；`HealthServiceTests` 既有 degraded 案沿用。
- A-2：`SentinelConnectionFactoryTests`（新增）三個狀態各一案；`NetiqProbeRunner` 解不開 → 不呼叫 client；`SentinelAdminService` DTO `credentialState` 三值；探索精靈對 `Undecryptable` 顯示「無法解密」而非「設定不完整」。
- A-3：`PrtgDailyPipelineTests`：三種早退原因各自寫進 `PrtgOutcomeNote`，`PrtgDays` 仍為 null（`PrtgReevaluationPlanner` 既有測試全綠）；`RunMonitorServiceTests`：總表、清單、詳情三個 DTO 都帶原因；`PrtgRunsCardAndProbeTests`／行為測試：`prtgCredentialState=undecryptable` 時卡片文字與連結；`hasPrtgConnection` 在 `runs.js` 零命中（靜態斷言）。
- A-4：`PrtgTablePagerTests`：逾時→重試 2 次後成功；第 3 次仍失敗→擲出；整個 pager 多頁只共用 2 次；401 立即擲出且黏住；403 不重試；延遲函式收到 15 秒與 60 秒（零等待）；等待期間取消令牌生效；重試寫 Info 不寫 Warn（NLog MemoryTarget）；`PrtgResourceGuardSource` 路徑重試選項為關。
- A-5：`PrtgClientTests`／`SentinelClientTests` 以不注入 handler 的建構＋MemoryTarget 斷言等級 Info 且訊息逐字不變；`BatchRunRecorderScopeTests` 新案：只有這兩則 Info 時趟次狀態為 `success`。
- 突變：A-2 判定改回只看非空 → 管線案紅；A-4 重試包住 401 → 黏住案紅；A-1 把 Warn 改 Error → `ErrorCount` 案紅。

## 批次B-a：AI 失敗證據、空陣列判定、無文法重試、等級

### 現況與核對結果

- `ChatJsonAsync` 在 `AIService.cs:411-505`；`jsonMode` 三次嘗試都 true；`isEmptyResponse` 走「立即回傳、不進 JSON 重試」（`:433-447`），所以「空陣列視為空回覆」若實作在 `ChatAsync` 的 Polly 層，第二次永遠打不到；判定必須在內容層（`TryParse` 之後）。
- `ChatJsonAsync` 有 4 個呼叫端：`LogAnalysisService:706`、`RiskReportService:274`、`WeeklyCheckupService:126`、`WebAiService:247`（互動）。
- `OpenAIMessage` 同時用於請求序列化與回應解析（`:739-755`）；直接加 `reasoning_content` 會讓請求多一個 null 欄位。
- `AiJson.Candidates` 依序取「第一個能反序列化的物件」；思考文字含 `{}` 會被當成全空的 `AiAnalysisResult`、奇數個 `"` 會讓字串狀態翻轉吞掉真正的物件。
- `BuildRetryPrompt` 結尾寫「嚴格依要求輸出合法的 JSON」（`:569-575`）。
- `TryParseDetailed` 對含孤立代理字元的回覆擲 `ArgumentException`（第 63 輪體檢）。
- 診斷行容量：現行預覽頭 120／尾 60，單行上限 300 字（`MaxErrorResponseSnippetLength` 註解明言為控制日誌庫容量）。

### 定案

- 判定在內容層；只有前一次類別為 `empty`（含空陣列／空物件）或 `no_json` 時，下一次才關文法；內容檢查失敗（`validation`）的重試維持文法。
- 回應改用獨立的只讀型別解析（請求型別不動）。
- 候選順序：有多個括號配對物件時**優先最後一個**（思考在前、答案在後），再退回原順序。
- 診斷行的回覆頭尾維持 1,500／500（只在最終失敗寫一行，與 300 字的錯誤訊息是不同欄位），guide 說明。
- `ChatJsonAsync` 最終失敗 `Log.Error`→`Log.Warn`；`LogAnalysisService.cs:752` 的「主分析完全失敗」（AI 服務不通）維持 Error——「服務掛掉」是失敗、「格式異常」是警告，文件寫明。

### 改動

- **B-a-1 證據**：回應型別加 `reasoning_content`／`reasoning`（只讀）；`AiResponse` 加清洗前原文長度、清洗前頭尾、思考欄位長度；診斷 JSON 加這些欄位，`TryRead` 相容舊格式。
- **B-a-2 空形狀與無文法重試**：內容層新增判定「去空白後是 `[]`、`{}` 或只有括號與換行」→ 類別 `empty`、`emptyShape: array|object`，不進 `validate`；下一次嘗試 `jsonMode:false`，`BuildRetryPrompt` 單一出口依「上次類別」產生兩種尾句（有文法：嚴格輸出 JSON；無文法：可以先思考，最後只輸出一個 JSON 物件）。含孤立代理字元的回覆先換成 U+FFFD 再解析，類別 `parse_other`。
- **B-a-3 候選順序**：`AiJson.Candidates` 改為原文 → 最後一個配對物件 → 其餘配對物件（原順序）→ 修復候選；`TryParse` 結果對既有測試輸入不變（用既有案例守住）。
- **B-a-4 等級**：最終失敗 `Log.Warn`。

### 測試 / 驗收（`FullyQualifiedName~AIService|FullyQualifiedName~AiJson|FullyQualifiedName~BranchOutcomeAndCache|FullyQualifiedName~LogAnalysisServiceRetryAiTests|FullyQualifiedName~WeeklyCheckup|FullyQualifiedName~RiskReportService|FullyQualifiedName~BatchRunRecorder`）

- B-a-1：攔截請求本體不含 `reasoning_content` 鍵；回應含 `reasoning_content` 時診斷記長度；舊格式診斷行 `TryRead` 成功。
- B-a-2：`[\n]`→`empty`＋`array`；`{}`→`empty`＋`object`；第一次請求含 `response_format`、第二次不含；`validation` 失敗後的第二次仍含；重試提示兩種尾句各一案；孤立代理字元回覆不擲例外、類別 `parse_other`、呼叫端拿到失敗結果而非例外（`LogAnalysisServiceRetryAiTests`）。
- B-a-3：「思考文字含 `{}`＋正確 JSON」解析出正確物件；「思考文字含奇數個 `"`＋正確 JSON」解析成功；既有 `AiJson` 全部案例不變。
- B-a-4：`BatchRunRecorderScopeTests`：`ChatJsonAsync` 最終失敗的行進執行紀錄為 Warn、`ErrorCount` 不增；`LogAnalysisService` 完全失敗仍是 Error（既有案維持）。
- 四個呼叫端的既有測試類別全部跑過（上面的篩選詞涵蓋）。
- 突變：B-a-2 第二次仍帶文法 → 紅；B-a-3 候選順序改回「第一個」→ 奇數引號案紅。

## 批次B-b：降級主機日的狀態、欄位、上限、標題、計數、匯出、前端

### 現況與核對結果

- 寫回閘門：`AiAnalysisHostedService.cs:497` 只在 `outcome.AiAnalyzed || outcome.Skipped` 時 `TryAttachAiResult`，否則「維持待補、不寫回」；`AttachAiResultLocked` 寫死 `AiPending=false`（`EfAnalysisRecordStore.cs:583`、`:626`），replay 比對含 `AiPending`。
- `AiAnalyzed` 有 17 個讀取點（提示詞組裝、工作流狀態、週檢、報告 `RiskReportService.cs:100,105,353,380`、PRTG 風險投影、回填器）；`AiAnalyzed=false 且 AiPending=false 且非低風險`會被 `NeedsBackfill` 規則 2（`HostDayPostProcessor.cs:148`）當成缺漏日重補、被 `HostDayWorkflowState.ApplyRecoveredAi`（`:978-1000`）判成永遠 Waiting。**所以降級不能翻 `AiAnalyzed`**。
- 「`AiAnalyzed=true 且 AiPending=true`」是既有合法狀態（強制重新分析、PRTG 指紋變動重標都是這樣），降級沿用它。
- 重標待補的入口：`MarkAllForAiRerun`（`:1737-1754`，單句 `ExecuteUpdate`）、PRTG 升風險或指紋變動（`:741,759,772`）。
- 補跑選擇：`WhereAiPending`（`:1845`）、`SelectNextBatch` 近日層新→舊；「本批全滅」防線（`AiAnalysisHostedService.cs:580-592`）：`batchSuccessCount==0` 且待補總數不變就暫停本輪。
- 前端待補徽章：`format.js:156-158` 用 `headline.includes('AI 待補')`；`records.js:635,649`、`record-detail.js:286`；記錄 DTO（`RecordDtos.cs:38` 附近）沒有次數欄位。
- `SchemaUpgraderModelParityTests` 不涵蓋 `lf_daily_records`（EnsureCreated 基表）。
- `DailyAnalysisRecord` 的欄位同時存在 ContentJson 與資料列欄位的有先例，但 `MarkAllForAiRerun` 是單句 UPDATE，無法同步 JSON 內的值。

### 定案

- **降級狀態**：`AiAnalyzed=true`（AI 段跑過）、`AiPending=true`（要再試）、`ai_degraded_attempts+1`；達 3 次後 `AiPending=false`、attempts 保持 3（放棄）。工作流 `CompleteAi(success:true)`。
- **欄位單一來源**：`ai_degraded_attempts` 只存資料列欄位，`DailyAnalysisRecord` 上的屬性標 `[JsonIgnore]`，由 `Deserialize(row)` 單點帶入；不升 `ExtractVersion`。
- **歸零規則只有一條**：任何「重標待補」都歸零——`MarkAllForAiRerun`（同一句 `SetProperty`）、PRTG 指紋變動重標、統計段 `Append` 重寫。
- **什麼算一次**：同一主機日每次 AI 排程嘗試 +1；同趟內 `ChatJsonAsync` 的 3 次重試不算 3 次；`attempted` 集合保證同輪不重複。
- **標題**：規則層結論由單一函式產生（既有「完全失敗」分支與降級分支共用）：命中規則取規則白話說明，否則取最嚴重問題來源與次數；降級後綴「（AI 摘要待補，第 N 次）」，放棄後綴「（AI 摘要已放棄：連續 3 次格式異常）」。摘要欄維持「（AI 回覆經 N 次嘗試仍未通過 JSON 檢查，保留原文供參考）＋原文」。`RunMainAnalysisAsync` 回傳改成小 record（含 `Degraded`）。
- **計數**：`AiOutcome.AiDegraded`；排程新增 `Degraded` 計數器：`aiFailures = Failed + Degraded`、`aiCalls` 照舊含降級；**降級算有進度**（`batchSuccessCount` 含降級、不觸發全滅防線）；趟次因 `AiFailures>0` 為「有警告」。
- **匯出案例**：`attempts > 0 且（AiPending 或 attempts ≥ 3）`，加上歷史資料的舊標題；已恢復成功的主機日（attempts>0、pending=false、attempts<3）不是案例；對照組排除 `attempts > 0` 與舊標題。
- **前端判定改看欄位**：記錄 DTO 加 `aiDegradedAttempts`（`aiPending` 已有）；`format.js` 單一函式 `aiStateOf(record)` 回 `pending`／`degraded`／`abandoned`／`done`，清單、詳情、儀表板共用，刪掉標題字串比對。

### 改動

- **B-b-1 schema**：`SchemaUpgrader.AddColumnIfMissing("lf_daily_records","ai_degraded_attempts", int NOT NULL DEFAULT 0)`；`DailyRecordRow` 欄位；`EfAnalysisRecordStore` 的 `Deserialize`／`FillRow` 帶入；DB-SPEC 同步；`MarkAllForAiRerun` 與指紋重標路徑同時 `SetProperty(attempts, 0)`。
- **B-b-2 降級寫回**：`AiOutcome` 加 `AiDegraded`；`AttachAiResultLocked` 第三條分支：降級 → attempts+1、`AiPending = attempts < 3`、標題／摘要照定案；replay 比對把 attempts 納入；閘門不變（`AiAnalyzed=true` 本來就會進）。
- **B-b-3 LogAnalysisService**：降級分支改回傳 `Degraded=true`、標題由共用函式產生（由寫入端依 attempts 補後綴，因為 attempts 在寫入時才知道）；完全失敗分支改用同一個規則層標題函式（既有「今日分析摘要暫缺…」語意不變，只是來源共用）。
- **B-b-4 排程計數**：`Degraded` 計數器、`aiFailures`、`batchSuccessCount`、進度訊息「主機 X 日期 AI 降級（第 N 次）」。
- **B-b-5 匯出**：`IAnalysisRecordQuery.QueryByHeadline` 改為 `QueryAiDegradedCases(from, to, take)`（條件如定案，SQL 下推，`ToQueryString` 兩後端測試）；`FindControl` 排除 `attempts>0`；`BuildAiSection` 對無前綴摘要不當成回覆（`reply=null`、`replyShape=null`）；guide 寫明案例兩種來源與「已恢復不算」。
- **B-b-6 前端**：`RecordDtos` 加欄位；`aiStateOf()`；清單、詳情、儀表板「最新狀況」徽章與 title：`degraded`→「AI 摘要待補（第 N 次格式異常）」、`abandoned`→「AI 摘要已放棄」、`pending`→既有「AI 分析中」；說明書 `10-scheduler.ai.md:39` 的徽章文字同步。

### 測試 / 驗收（`FullyQualifiedName~LogAnalysisService|FullyQualifiedName~AiAnalysisScheduler|FullyQualifiedName~SchemaUpgrader|FullyQualifiedName~AnalysisRecord|FullyQualifiedName~RunExport|FullyQualifiedName~HostDayPostProcessor|FullyQualifiedName~HostDayWorkflow|FullyQualifiedName~RecordList|FullyQualifiedName~RecordDetail|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout`）

- **整合驗收（最重要）**：`AiAnalysisSchedulerTests` 新案——以回 `[\n]` 的假 AI 跑一趟排程後，**從 SQLite 讀回**該主機日：`AiAnalyzed=true`、`AiPending=true`、`ai_degraded_attempts=1`、標題為規則結論＋「（AI 摘要待補，第 1 次）」、摘要含原文；再跑兩趟 → attempts=3、`AiPending=false`、後綴為放棄；第四趟不再選到它；期間 `NeedsBackfill` 對該日為 false、工作流 AI 狀態為完成（不是 Waiting）。
- 歸零：強制重新分析後 attempts=0、pending=true；PRTG 指紋變動重標後 attempts=0；統計段重寫後 attempts=0。
- 計數：同批 3 個降級 → `aiFailures=3`、`aiCalls=3`、趟次 `warning`；同批全降級且有積壓 → 不觸發全滅防線、下一批照常選積壓。
- schema：新增 `SchemaUpgraderTests` 案：SQLite 舊庫升級後欄位存在、既有列為 0；`AddColumnIfMissing` 字串與 `DailyRecordRow` 對應（parity 類別補 `lf_daily_records` 這一欄）。
- 匯出：案例判定四種狀態（降級中、已放棄、已恢復、從未降級）各一案；舊標題歷史案例仍算；對照組不選 attempts>0；無前綴摘要 `reply=null`。
- 前端：`aiStateOf` 四態行為案；`records.js`、`record-detail.js`、`dashboard.js` 以 grep 斷言不再含 `'AI 待補'` 字串比對；徽章文字案例。
- 突變：攔掉 attempts 上限 → 第四趟仍選到 → 紅；閘門改成翻 `AiAnalyzed=false` → 整合案的 `NeedsBackfill` 斷言紅；案例判定拿掉「仍未成功」→ 已恢復案紅。

## 批次C：執行總表的執行時間

### 定案（`ui-ux-pro-max`：表格不撐破版面 #71；兩行呈現沿用既有 `withPhaseDuration`）

- 第一行：取數趟的起訖＋**總耗時**（取數趟與**跟隨取數的 AI 趟**（`Trigger=fetch-followup`）聯集，異常中斷不計）。
- 第二行小字：「取數 44 秒 · AI 1 分 27 秒」；當天另有非跟隨的 AI 趟（排程補跑、手動）時再加「另有 AI 補跑 N 趟（M）」，不混進第一行的起訖。
- 展開列：四路明細＋「總計」一列；AI 路在跟隨趟沒有 `Phases` 時用整趟起訖；取數趟全部沒有 `Phases` 的日子，本機／NetIQ／PRTG 顯示「無階段資料（升級前紀錄）」；新舊混合時只用有 `Phases` 的趟計算、不另標示。
- 跨午夜：維持以 `StartedAt.Date` 歸日（既有口徑）。
- 刻意推翻 `RunDayTimingBuilderTests.AI趟只進AI那一路_不算進取數總耗時與起訖`，改寫為新語意。

### 改動

- **C-1** `RunDayTimingBuilder`：`TotalSeconds`／`LastFinishedAt` 涵蓋跟隨 AI 趟；新增 `FetchSeconds`、`FollowUpAiSeconds`、`OtherAiRuns`（趟數＋秒數）、`FetchRunsWithoutPhases`；AI 路退回整趟起訖。
- **C-2** `run-timeline.js`：`dayTimingText` 兩行；`renderDayTiming` 總計與「無階段資料」。
- **C-3** WEB-SPEC §9.10（`:3019-3020`）、說明書 `10-scheduler.md` 執行總表一列。

### 測試 / 驗收（`FullyQualifiedName~RunDayTimingBuilder|FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~RunsTimelineBehaviorTests|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout`）

- `RunDayTimingBuilderTests`：取數 44 秒＋跟隨 AI 87 秒不重疊 → 總計 131、第一行起訖涵蓋 AI 結束；重疊只算一次；白天手動 AI 趟 → 進 `OtherAiRuns`、不進總計與起訖；跟隨 AI 趟無 `Phases` → AI 路為整趟；取數趟全無 `Phases` → `FetchRunsWithoutPhases=N`；新舊混合 → 只算有階段者；異常中斷不計。
- `RunsTimelineBehaviorTests` 新案（登錄 `InlineData`）：兩行文字、「另有 AI 補跑」、「無階段資料」、無趟次為「—」、總計列。
- 瀏覽器：實際資料的 11-02（舊趟）顯示「無階段資料」，11-03 之後顯示四路；375px 欄不撐破。
- 突變：跟隨判定拿掉 → 白天手動趟案紅。

## 批次D：儀表板第一排、主機頁 PRTG 對應連結

### 定案

- **D-1 不量測、不新增變數族**：`revealFirstRow` 加選用回呼 `onCollapsedChange(expanded)`；`dashboard.js` 用它在 strip 上切換 `lf-kpi-strip--collapsed`，CSS `.lf-kpi-strip--row.lf-kpi-strip--collapsed .lf-kpi-group { flex: 1 1 0 }`（三組等寬，只在 `(min-width:576px) and (max-width:1799.98px)`：更寬的畫面全部卡片都放得下，等寬反而讓卡多的組藏卡），展開後移除、回到依卡數分寬。狀態只取決於展開與否，沒有回授迴圈；`revealFirstRow` 本身仍不認得 KPI 群組。
- **D-2／D-3 沿用既有跳轉**：徽章只在使用者有 `ViewAll` 時做成連結，`href = /admin/prtg?focus=unmatched&hostId=N#mirror`；`prtg-admin.js` 讀參數後呼叫既有 `jumpToUnmatched()`，未對應清單 API 加 `pinHostId`（該台釘在第一頁最上面並高亮 1.5 秒，`lf-focus-flash` 用 `--lf-focus-ring`），`replaceState` 移除參數；清單載入失敗不捲動、顯示既有錯誤態。

### 測試 / 驗收（`FullyQualifiedName~DashboardReveal|FullyQualifiedName~FrontendMisc|FullyQualifiedName~PrtgAdminPage|FullyQualifiedName~PrtgHostMapping|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout`）

- D-1：`DashboardRevealBehaviorTests`：收合時 strip 有 `--collapsed`、展開後移除、字級切換事件不改變它；CSS 靜態斷言該規則存在。瀏覽器：1268px、1200px、1024px、768px、375px 五個寬度，收合時三組寬度差 ≤ 2px（`getBoundingClientRect`），連續兩次量測相同；展開後寬度比為 4:4:2（±2px）。
- D-2：hosts 行為測試：有 `ViewAll` → `<a>` 且 href 含 `hostId`；無 → `<span>`；觸控區 `min-height` 36px（既有 `lf-touch-36`）。
- D-3：`PrtgHostMappingAdminControllerTests`（新增）：`pinHostId` 的那台排第一且總數不變；`prtg-admin` 行為測試：帶參數 → 呼叫 `jumpToUnmatched`、高亮該列、`replaceState` 後網址無參數；失敗不捲動；無權限（403）顯示既有錯誤態。瀏覽器：從主機頁點徽章落在該列且高亮。
- 突變：D-1 把 `--collapsed` 切換拿掉 → 行為案紅；D-3 `pinHostId` 不生效 → 控制器案紅。

## 批次E：設定頁說明文字

### 定案

§8.6a 加「讓版面自己說明」：**版面與 label 能講的不再寫；要講的放 `?`；只有後果句常駐**。不重排版面、不改欄位 id、不改 `進階設定` summary。cshtml 元素與 `settings.js` 的對應賦值**一起改**（刪元素就刪 `getElementById`，否則 `modelHint.textContent=` 對 null 拋例外）。首次設定頁 `Setup.cshtml` 的同句（AI 模型提示、`setup-ai-model-hint`）一併對齊。

### 改動（行號為 `Settings.cshtml` 現況，實作前重新核對；JS 位置為 `settings.js`）

**刪除或合併**
- AI：模型 form-text（98；JS :233 的賦值一起刪，供應商切換改只動 label）；金鑰狀態句只留「已設定金鑰」（JS :414-416），「留空＝沿用」由 placeholder 承擔；估費單價 236／242 合成一個共用 `?`。
- AD：386 刪「一行一台」；399 刪；407 刪預設值句。
- 分析參數：307 刪「留空＝預設六頻道」；301 與 296 合併。
- 郵件：SMTP 密碼狀態句（JS :547-549）同金鑰處理；736 刪「一行一位」。
- 層級：48 刪；62-65「一行一條」刪、上限收進 `?`。
- 外觀：905 刪；922 刪重複句（JS :1054 一起）。
- 資源守門：674 前半刪。

**收進 `?`**：層級 32-37；資料保留 473／481／489／497 的範圍定義（硬性限制留常駐）；派工 870／874／878 合成一個 `?`；AI 141 刪除。

**修正與行為不符**：郵件 689「三路」→ 列實際路數（先 grep `MailNotificationService`）、736 同步；827 寫死時刻 → 只留「以逗號分隔，最多四組」；分析參數 269 限縮到它實際管的欄位；AI 133「appsettings 出廠值」→「出廠值」；金鑰／SMTP placeholder 在尚未設定時顯示「選填」而不是「沿用既有」（JS 依狀態切 placeholder）。

**保留常駐（守門清單）**：AI 82 資料外送、88「留空會停用」（改為 form-text 常駐）、92、166、192；AD 375-380 後果句（縮短）；資料保留 462-464 不可逆、508-510 硬性限制、「會佔用資料庫空間」；郵件 689／776／818／866；守門 607-610／654／665 前半；外觀 900／909。

### 測試 / 驗收（`FullyQualifiedName~SettingsPage|FullyQualifiedName~UiTests|FullyQualifiedName~LocalizationLint|FullyQualifiedName~ReportRetentionWebTests|FullyQualifiedName~SetupUx|FullyQualifiedName~SetupAiInline|FullyQualifiedName~HelpContentServiceTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests`）

新增 `SettingsPageWordingUiTests`（三條守門，都是靜態掃描，可重跑）：
1. **後果句守門**：上面「保留常駐」清單的每一句（取關鍵片語）都還在 `Settings.cshtml` 或 `settings.js`。
2. **不重複守門**：同一個欄位的 placeholder、form-text、`settings.js` 字串不得同時含「留空＝」／「一行一」同義句（掃 cshtml＋JS 字串）。
3. **孤兒 id 守門**：`settings.js` 內 `getElementById('…-hint')` 的每個 id 都存在於 `Settings.cshtml`。

數字目標（主代理用 grep 計數驗收）：`Settings.cshtml` 常駐說明（`form-text`＋`small text-muted` 副標）由 45 條降到 ≤ 28 條；AI 頁籤由 15 降到 ≤ 8；`?` 條數可增加。瀏覽器：九個頁籤各一張截圖，檢查項固定三條：沒有空的 form-text 容器、沒有 JS 錯誤（console）、375px 無橫向捲動。既有守門全部綠。

## 批次G：文件與收官

- WEB-SPEC：§9.10 執行時間、PRTG 卡與 `disabled` 語意、`PrtgOutcomeNote`、`prtgCredentialState`；§11 前置檢查（Warn）、警告等級、`CredentialPreflight` logger；AI 降級狀態與 `ai_degraded_attempts`、歸零規則、兩種失敗率；匯出案例判定。
- DB-SPEC：`lf_daily_records.ai_degraded_attempts`；PRTG-SPEC：pager 重試與 `disabled` 原因。
- 說明書：`10-scheduler`（執行時間、PRTG 憑證提示、AI 待補徽章文字、Security 記錄檔警告怎麼消）、`08-hosts`（徽章可點、誰看得到）、`12-settings`、`17-netiq`（密碼無法解密的顯示）、`02-dashboard`（若提到第一排）。
- BACKLOG：§1.7 兩條；第 63 輪三條維持。
- **升級注意**（寫進 PLAN 與記憶）：新欄位由 `AddColumnIfMissing` 建立、既有列 0，可降版；歷史降級主機日不會自動補跑，要補用「強制重新分析」；Sentinel 密碼解不開後行為由「401 失敗」改為「略過並計失敗主機日」，值班看到的字會變。
- 全量 `dotnet test`，結果寫進本文「體檢交接」。

## 批次H：PRTG 數值取數的暫停與補抓、金鑰復原、新鮮度警示可行動

### 現況與核對結果

- `FetchValuesAsync`（`PrtgFetchService.cs:1004-1118`）：`SemaphoreSlim(concurrency)` 併發、每顆 `historicdata` 一次 `GetJsonAsync`，失敗進 `failedSensors`／`timedOutSensors`、記第一則錯誤，結尾一行 ⚠；呼叫端三個：夜間階段 4（`:472`，只在激進策略）、觸發式取數 `FetchValuesForSensorsAsync`（`:510`，`PrtgTriggeredValueFetcher`）、回填 `PrtgBackfillRunner.cs:89,284`。守門 `_guard.WaitIfBusyAsync` 在每顆之前。
- 失敗分類已有型別：`PrtgClientException.Kind`（Timeout／Transport／HttpStatus＋`StatusCode`／Credential），`PrtgTablePager.IsRetryable` 是現成的可重試判定；401 在 client 內黏住。`PrtgClient` 目前沒有讀 `Retry-After`。
- 觸發式取數的新鮮度：「有目標 sensor 卻全部失敗」不記（`PrtgTriggeredValueFetcher.cs:194`）；隔日是否補到失敗的那幾顆，取決於該主機日隔天是否仍是觸發主機（高／中風險），**沒有「失敗 sensor 清單」被留下來**。
- 金鑰：`CryptoKeyBootstrapper.RunCore` 沒有金鑰檔就 `TryCreateKeyFile`，之後 `ClaimOrValidateFingerprint` 發現資料庫已有不同指紋 → `KeyMismatch=true`、提早返回；指紋 blob（`crypto_key_fingerprint`）只在空白時寫入，沒有更新路徑。`CryptoHelper.DecryptFailureSeen` 只在重設金鑰來源時歸零（`CryptoHelper.cs:88`），站台運行中重新輸入密碼不會清掉。
- 新鮮度：`ScheduleFreshnessService.GetScheduleFreshness` 只認 `IsScheduledFetch()`（Trigger==schedule 且非 AI）；手動趟 Trigger 是 `manual:{帳號}`；`BatchRun` 沒有存 `RunScope`，`Args` 對 Web 觸發的趟是空字串。橫幅在 `layout.js:735-760`，只有一句話＋「查看排程作業」連結；`ScheduleFreshnessDto` 沒有原因欄位。健康頁已有 `cryptoKeyMismatch`／`cryptoDecryptFailure`。

### 定案

- **H-1 數值取數的失敗處理在 `FetchValuesAsync` 單點做**，三個呼叫端自動受惠；不新增設定鍵，常數集中在一個靜態類（`PrtgValueFetchRetryPolicy`）。
  - **共用暫停**：任一顆碰到可重試失敗（沿用 `PrtgTablePager.IsRetryable`：Timeout／Transport／5xx／429）就把整批「暫停閘」拉起，所有工作在下一顆送出前（守門 `WaitIfBusyAsync` 之前）都要等閘放行；暫停長度依序 15→60→180 秒（第 4 次起維持 180），加 0～20% 隨機誤差；回應若帶 `Retry-After` 取其與序列值的較大者（由 `PrtgClient` 用 `HttpResponseMessage.Headers.RetryAfter` 讀出秒數或日期，放進 `PrtgClientException.RetryAfter`，取數端不自己解析）。閘已拉起時其他顆的失敗不再疊加序列。成功一顆就把序列歸零。
  - **補抓**：第一輪失敗的 sensor 收進清單，第一輪結束後**一次一顆**補抓一輪（concurrency 1），同樣受暫停閘與配額約束；補抓仍失敗才計入 `failedSensors`。401／403／其他 4xx、Credential 與非 `PrtgClientException` 的失敗不補抓。
  - **停損**：一次 `FetchValuesAsync` 的總暫停時間達 15 分鐘，或可重試失敗連續達 10 顆（中間沒有任何成功），就停止這一次的數值取數：剩餘未送出的顆全部計為失敗、不再送請求，輸出一行 ⚠「PRTG 持續無法回應，本趟停止數值取數（已停止 N 顆），其餘留待下一趟」。最壞情況（PRTG 完全不回、逾時 60 秒）併發 2 約 17 分鐘內停損、併發 1 約 23 分鐘（由總暫停停損先到）。總暫停以時鐘量實際長度，不是排定長度。停損**不是例外**，呼叫端照舊拿到 `(written, failed)`。
  - **停損後冷卻**：同一個 `PrtgFetchService` 實例在停損後 10 分鐘內，後續的 `FetchValuesAsync` 呼叫**不送任何請求**、直接回「全部失敗」（只在第一次冷卻回絕時輸出一行）。原因：觸發式取數每 30 秒用同一個實例輪詢、回填逐日用同一個實例，沒有冷卻的話每次呼叫都重新累計 10 顆，PRTG 掛掉的 8 小時裡會被反覆打。冷卻到期後下一次呼叫照常嘗試（序列與計數歸零）。
  - **進度回呼**：`progress` 的總數固定為第一輪的顆數、`done` 只在第一輪推進，補抓輪不再推進（否則 done 會大於 total）。
  - **新鮮度**：回填的日子若「有目標卻全部失敗」（含停損與冷卻回絕）不記 `PrtgFreshnessStore.Values`。實作時核對：`PrtgBackfillRunner` 本來就只在 `written > 0`／`successDays > 0` 才記，不必改程式，只補測試鎖住。
  - **可觀測**：暫停與補抓寫 Info（不是 Warn，與 A-4 同理）；停損寫 Warn 一行；結尾 ⚠ 那行改為「失敗 N 顆（其中逾時 M、補抓後仍失敗 K）」。
  - **不做**：不改 `PrtgRequestBudget`、不改守門、不改 `PrtgTablePager`（表格分頁仍只有 A-4 的 2 次重試）；不把失敗清單寫進資料庫（下一趟的候選由觸發邏輯決定，與現況相同）；不新增設定鍵，門檻是常數。
- **H-2 金鑰復原**
  - **自動修復**：啟動時 `KeyMismatch` 為真，且 `ProbeSecrets` 對 inventory **每一筆非空密文都解得開**（含 inventory 為空的情況；inventory 含停用的 Sentinel 與 SMTP，所以停用的 Sentinel 也要重新輸入或清空密碼，Warn 會列名稱）→ 以目前金鑰指紋覆寫 blob（`FingerprintRecord` 加 `UpdatedAt` 與 `ReplacedFingerprint`，留下可追查的痕跡）、`KeyMismatch=false`、寫一行 Warn「金鑰指紋已更新為目前金鑰檔（所有憑證已用新金鑰重新輸入；原指紋 X）」。有任何一筆解不開就維持現狀（多站台共用資料庫、各自金鑰的情境不得被另一站台覆寫）。inventory 以外沒有其他加密欄位（grep `CryptoHelper.Encrypt` 只有 AI 金鑰、SMTP、PRTG 三種認證、Sentinel 密碼），覆寫不會蓋掉看不見的壞密文。
  - **運行中復原**：`SecretInventory` 全部解得開時 `DecryptFailureSeen` 要能歸零——`CryptoHelper` 新增 `ReevaluateDecryptFailure(IEnumerable<string> ciphertexts)`（全部 `TryDecrypt` 成功才清旗標與 `ReportedFailures`）；Web 端一個小 helper `CryptoRecovery.Reevaluate(settingsStore, sentinelStore)` 組 inventory 後呼叫它，由 `SystemSettingsService` 的三個會寫密文的儲存入口（一般設定、PRTG 連線、AI）與 `SentinelAdminService.SaveSentinel` 成功後各呼叫一次（同一個 helper，不各寫一份）；健康頁因此在重新輸入後即恢復，不必重啟。`KeyMismatch` 在運行中不變（指紋只在啟動時處理），健康頁文案區分「金鑰不符，重新輸入全部憑證後重啟站台即解除」與「部分憑證解不開」。
  - **遺失辨識**：`TryCreateKeyFile` 在指紋 blob 已非空時被叫到，代表「金鑰檔遺失而新產生」而不是首次啟動：Error 訊息改寫明這一點並點名常見原因（重新部署清掉執行目錄、`DataRoot` 指到部署目錄）；README 部署節加一條「`DataRoot` 放在部署目錄之外，或把 `keys\` 排除在清除範圍」。
- **H-3 新鮮度警示可行動**
  - `ScheduleFreshnessDto` 加 `lastRunStatus`（最近一趟已結束的排程取數趟：success／warning／failed／stopped／stuck／null）與 `credentialIssue`（`KeyMismatch || DecryptFailureSeen`）；橫幅在 stale 時多一句，三種文案：有憑證問題→「最近一趟因憑證無法解密而失敗，請到設定頁重新輸入」，維護者（`hasCapability('Maintain')`）連到設定頁、非維護者沿用既有 `AdminContacts`；沒有憑證問題但有趟次→「最近一趟狀態：失敗／停止／卡住，請看排程作業」；`lastRunStatus` 為 null→「近 14 天沒有任何排程取數執行，請確認站台常駐（IIS 應用程式集區 AlwaysRunning）」。摘要信文字不變。
  - **手動完整執行算數**：`BatchRun` 新增 `Scope`（`full`／`local`／`netiq-hosts`，recorder 由 `RunRequest.Scope` 寫入；舊紀錄與 AI 趟為 null；BatchRun 是 JSON 列，不動 schema）；新鮮度的成功條件改為「取數趟且狀態 success／warning，且（Trigger 是 schedule，或 Trigger 以 `manual:` 開頭且 `Scope==full`）」，判準收在 `BatchRunExtensions.CountsForFreshness()` 一個出口，`IsScheduledFetch()` 保留給其他呼叫端。AI 趟、`console` 舊趟、範圍縮小的手動趟維持不算；執行紀錄清單 DTO 帶 `scope` 顯示「完整／僅本機／指定主機」。
  - 不改 48 小時門檻、不改確認靜音機制、不改摘要信文字。

### 改動

- **H-1** `PrtgFetchService.FetchValuesAsync`：新檔 `PrtgValueFetchRetry.cs` 放兩個小類——`PrtgValueFetchRetryPolicy`（序列、誤差、停損與冷卻常數、`ShouldRetry(ex)` 委派 `PrtgTablePager.IsRetryable`、連續失敗與總暫停計數、`NextDelay(retryAfter)`）與 `PauseGate`（`WaitAsync(ct)`／`RaiseAsync(delay, ct)`／`Reset()`，延遲函式與時鐘可注入）；`FetchValuesAsync` 改成「第一輪 → 補抓輪 → 彙總」三段，各段是私有方法，不再是一個 100 行的 lambda；實例欄位 `_valueFetchCooldownUntil`。`PrtgClientException` 加 `RetryAfter`（`TimeSpan?`），`PrtgClient` 在非成功狀態碼時讀 `Headers.RetryAfter` 填入。回填 `PrtgBackfillRunner` 兩處 `RecordFreshness` 套「全部失敗不記」。
- **H-2** `CryptoKeyBootstrapper`：`RunCore` 指紋不符時先探測，探測全過才 `OverwriteFingerprint`（同一個 blob `Mutate`，寫 `UpdatedAt`／`ReplacedFingerprint`）；`TryCreateKeyFile` 前先讀 blob 判斷是否「新產生」並改 Error 文案。`CryptoHelper.ReevaluateDecryptFailure`；Web `CryptoRecovery` helper；四個儲存入口各呼叫一次；健康頁兩種文案（`settings.js:915` 附近）。README 部署節。
- **H-3** `BatchRun.Scope`＋`BatchRunRecorder` 寫入＋`CountsForFreshness()`；`ScheduleFreshnessService` 條件與 DTO 兩欄；`RunMonitorService` 清單 DTO `scope`；`layout.js` 橫幅三種文案（抽成 `freshnessBannerText(freshness, isMaintainer)` 純函式供行為測試）；WEB-SPEC §11 新鮮度一節；說明書 `22-system-health`（含 AI 版）。

### 測試 / 驗收（`FullyQualifiedName~PrtgFetchService|FullyQualifiedName~PrtgValueFetch|FullyQualifiedName~PrtgTriggered|FullyQualifiedName~PrtgBackfill|FullyQualifiedName~PrtgClient|FullyQualifiedName~CryptoKeyBootstrapper|FullyQualifiedName~CryptoHelper|FullyQualifiedName~HealthService|FullyQualifiedName~ScheduleFreshness|FullyQualifiedName~HealthController|FullyQualifiedName~BatchRunRecorder|FullyQualifiedName~RunMonitorServiceTests|FullyQualifiedName~UiTests|FullyQualifiedName~JsModuleImport|FullyQualifiedName~BehaviorTests|FullyQualifiedName~ApiTimeout`）

- H-1（`PrtgValueFetchRetryPolicyTests` 新增＋`PrtgFetchService` 既有測試類，假 client 可按 objid 排程失敗，延遲注入零等待並記錄）：
  - 10 顆、第 3 顆逾時一次→ 暫停閘收到 15 秒、其餘 9 顆在閘放行後才送出（送出時間戳晚於閘放行）、第 3 顆補抓成功 → `failed=0`、`written` 含全部 10 顆。
  - 連續三顆逾時 → 延遲依序 15／60／180；中間成功一顆後再失敗 → 回到 15。
  - `Retry-After: 120` 的 429 → 暫停 120 秒（大於序列值）；`Retry-After` 為日期格式也解得出。
  - 404 與 403 的顆：不暫停、不補抓、直接計失敗；401 走既有黏住（client 測試已有）。
  - 停損：連續 10 顆可重試失敗 → 第 11 顆起不再呼叫 client（假 client 呼叫數＝10、補抓 0）、`failed`＝全部、輸出含「停止數值取數」、Warn 一行；總暫停達 15 分鐘的路徑（注入時鐘）同樣停損。
  - 冷卻：停損後同一實例立刻再呼叫 → client 呼叫數不變、回全部失敗、輸出只多一行；注入時鐘推進 10 分鐘後再呼叫 → 照常送出。觸發式取數以 `pollSeconds=1`、`singlePass=false` 跑三輪輪詢 → client 總呼叫數＝10（只有第一輪累計到停損）。
  - 進度：補抓輪存在時 `progress` 最後一筆 `done == total`，且 total 等於第一輪顆數。
  - 回填新鮮度：某日目標 5 顆全部失敗 → `PrtgFreshnessStore.Values` 不更新（`ZeroStreak` 不變）；同日 1 顆成功 → 更新。
  - 取消：暫停期間取消令牌生效、不再送出。
  - 三個呼叫端：觸發式（全部失敗仍不記新鮮度）、回填（`PrtgBackfillRunner` 既有案全綠）、夜間階段 4（既有案全綠）。
- H-2（`CryptoKeyBootstrapperTests`）：指紋不符＋全部憑證解得開 → blob 指紋更新且 `ReplacedFingerprint` 等於舊值、`KeyMismatch=false`、密文資料逐欄不變；指紋不符＋一筆解不開（含一台**停用**的 Sentinel）→ 不更新、`KeyMismatch=true`；inventory 為空＋指紋不符 → 更新；金鑰檔不存在且 blob 非空 → Error 訊息含「金鑰檔遺失」，blob 非空且金鑰檔存在 → 訊息不含它。`CryptoHelperTests`：旗標為真後 `ReevaluateDecryptFailure` 全部成功 → 旗標假、`ReportedFailures` 清空（再失敗一次會再記 Error 一次）；有一筆失敗 → 旗標維持真。`SystemSettingsServiceTests`／`SentinelAdminServiceTests`：四個儲存入口成功後 helper 各被叫一次（替身計數），儲存失敗（驗證例外）不叫。健康頁靜態測試：兩種文案字串存在。
- H-3（`ScheduleFreshnessTests`）：手動 `Scope=full` 成功 → 不 stale；手動 `Scope=local`、`netiq-hosts` 成功 → 仍 stale；舊紀錄 `Scope=null` 的手動趟與 `console` 趟不算；手動 full 但 `failed` 不算；`lastRunStatus` 取最近一趟已結束的排程取數趟（手動趟不影響它）、沒有趟次為 null；`credentialIssue` 兩值。`BatchRunRecorder`：三種 `RunScope` 各寫對應字串、AI 趟為 null；`BatchRunStore` JSON 往返保留 `Scope`。行為測試（`freshnessBannerText` 純函式，登錄 `InlineData`）：憑證問題＋維護者 → 含設定頁連結；非維護者 → 不含連結；無憑證問題＋`failed` → 含「失敗」；`lastRunStatus` null → 含「常駐」。`HealthControllerFreshnessCacheTests` 既有快取案全綠。`RunMonitorService` 清單 DTO 帶 `scope` 三值文字。
- 瀏覽器：本機以 Stub 製造 stale（調 `EnabledAt` 與一筆失敗排程趟）看橫幅兩種文案、375px 不撐破；PRTG 無法連線的實測交使用者（寫進體檢交接）。
- 突變：H-1 暫停閘拿掉（各顆各自等）→ 「其餘 9 顆在閘放行後才送出」案紅；停損計數不歸零 → 「中間成功一顆後再失敗回到 15」案紅；冷卻拿掉 → 「三輪輪詢總呼叫數＝10」案紅；H-2 探測有失敗仍覆寫 → 「一筆解不開不更新」案紅；H-3 手動 `local` 也算 → 「仍 stale」案紅。

### 審查（第二版，實作前）

四個角度各自看過定案、改動與驗收，以下是抓到並已吸收進上文的缺口；沒列的代表看過但沒有要改。

| 角度 | 缺口 | 處置 |
|---|---|---|
| 程式面 | 停損只管「一次呼叫」，觸發式每 30 秒、回填逐日都用同一個實例重叫，PRTG 掛掉的 8 小時會被反覆打 10 顆 | 加「停損後冷卻 10 分鐘」（實例層），驗收以三輪輪詢的總呼叫數鎖住 |
| 程式面 | 補抓輪會讓 `progress` 的 `done` 超過 `total` | 補抓不推進進度 |
| 程式面 | `Retry-After` 日期格式自己解析是重複造輪 | 用 `HttpResponseMessage.Headers.RetryAfter`，在 `PrtgClient` 讀、例外帶出 |
| 程式面（DRY） | 運行中復原要在四個儲存入口呼叫 | 一個 `CryptoRecovery` helper，入口只呼叫 |
| 程式面（SRP） | `FetchValuesAsync` 的 lambda 已近 100 行，再塞暫停與補抓會更難讀 | 拆三段私有方法；暫停閘與政策各自一個小類、可單測 |
| 使用者（值班） | PRTG 完全不回時到底多久放棄？原定連續 20 顆配 60 秒逾時要 20 分鐘以上 | 改連續 10 顆，併發 2 最壞約 17 分鐘；說明書寫這個數字 |
| 使用者（值班） | 橫幅只說「失敗」仍不知道做什麼；站台根本沒跑（IIS 回收）是另一種常見原因 | 三種文案，含「沒有任何執行→確認常駐」 |
| 使用者（尖銳） | 「我停用的 Sentinel 也要重輸密碼才解除金鑰不符？」 | 是，因為 inventory 含全部密文；Warn 會列名稱，說明書寫清楚「重新輸入或清空」 |
| 使用者（尖銳） | 「重新輸入後健康頁還是紅的」 | 運行中復原清旗標；`KeyMismatch` 要重啟才清，文案明講 |
| 管理者 | 自動覆寫金鑰指紋是安全相關動作，不能無痕 | blob 記 `UpdatedAt`／`ReplacedFingerprint`，Warn 帶原指紋 |
| 管理者 | 回填期間 PRTG 停機會把 `ZeroStreak` 累上去、新鮮度標成可疑 | 回填「全部失敗不記新鮮度」，與觸發式同判準 |
| 管理者 | 手動趟算數的判準散在兩處會再漂移 | 收成 `CountsForFreshness()` 單一出口 |
| 上帝視角 | H-1 讓激進策略的夜間階段 4 最多多出約 17 分鐘＋補抓；保守策略不跑階段 4，不受影響 | 接受，寫進 PRTG-SPEC §3 與說明書 |
| 上帝視角 | 表格分頁（結構同步）仍只有 A-4 的 2 次重試，沒有共用暫停 | 明確不做（表格分頁已有 pager 層重試與配額）；寫在不做清單 |
| 上帝視角 | `BatchRun.Scope` 是新欄位：匯出、`RunExportService` 的趟次分類、`RunDayTimingBuilder` 都讀 BatchRun | 只新增不改既有欄位語意；匯出本輪不帶 `scope`（BACKLOG 一條：診斷匯出補 `scope`） |
| 上帝視角 | 多站台共用資料庫、各自金鑰：A 站全部重輸後覆寫指紋，B 站變成「不符且解不開」 | 正確行為（B 本來就解不開），README 寫明共用資料庫必須共用金鑰 |
| 上帝視角 | H 三項彼此獨立、與 A～G 只共用 `SecretInventory`、`PrtgTablePager.IsRetryable`、`BatchRunStatus` | 可各自提交；順序 H-1 → H-2 → H-3，每項驗證過即 push |

## 明確不做（本輪定案）

- **改提示詞、`response_format` 改 `json_schema`、調 `AiMaxTokens`**：等 B-a 的一夜證據。判讀準則先寫好：`finish_reason=length` 佔比高 → 先調 `AiMaxTokens`；`reasoning_content` 有值且 `content` 為 `[]` → 文法與思考衝突成立 → 評估 `json_schema` 或關思考。
- **新增 PRTG 結局值**、**逐顆 sensor 取數重試**、**PRTG 停止重新評估對應存在前的日子**（記 BACKLOG）、**設定頁重排版面**、**代號對照表**、**serverAdmin 可見輸出**。
- **Security 記錄檔警告改豁免**：那是真的盲區，不豁免；說明書寫怎麼消。
- **表格分頁（結構同步、狀態變更）加共用暫停與停損**：已有 pager 層 2 次重試與請求配額；數值取數才是逐顆、數量大、沒有任何重試的那一路（批次H-1）。
- **摘要信的新鮮度文字帶原因、診斷匯出帶 `scope`**：本輪只改站內橫幅；兩條記 BACKLOG。

## 規劃完成後複檢（第二版）

- **與既有行為的衝突**：降級不翻 `AiAnalyzed` 後，17 個讀取點行為不變；`RiskReportService` 對降級日仍做 AI 深析（它看 `AiAnalyzed`），可接受並寫進 WEB-SPEC。`PrtgOutcomeDisabled` 語意擴大但值不變，五值消費端不動。`CanDiscover` 行為判定搬到工廠，POCO 屬性保留非空語意，序列化不受影響。
- **第 63 輪契約被推翻的**：F-4「案例＝標題精確等於常數」→ B-b-5；`RunDayTimingBuilderTests` 一案 → C。都在執行紀錄註明。
- **批次之間**：F 改 `RunExportDeidentifier`，A-1 的 `SecretInventory` 要讓匯出工廠改用它（A 批內一起改，F 不動工廠）；B-a 改 `AIService` 的請求／回應型別，B-b 不再碰它；A-3 與 C 同改 `runs.js` 不同函式，依序做。
- **四個坑**：
  - 什麼算一個：B-b「每次排程嘗試 +1」、A-4「每個 pager 共 2 次」、C「跟隨趟才進總計」、F-1 依種類的長度。
  - 破壞性判準：F-2 的反例（一般 `CORP\alice` 仍遮）已列；B-b 歸零規則單一。
  - 單向閘門：attempts 上限 3 次後的繞過路徑＝強制重新分析／資料變動重標（都歸零）；A-4 的 401 黏住不受重試影響。
  - 移除類：`QueryByHeadline` 的呼叫端（`RunExportService:1087`、`EfAnalysisRecordStore:1656`、`HandlingFakes.cs:194`、`StoreFakes.cs:531`、測試類）全在 B-b-5 白名單；`hasPrtgConnection` 的呼叫端在 A-3 白名單；`format.js` 的字串判定三個呼叫端在 B-b-6 白名單。
- **升級與既有資料**：見批次G。
- 複檢結果：第二版無新增待決；兩項「暫定」數值（重試 15／60 秒、attempts 上限 3）由實作事實或第一夜資料推翻時寫進執行紀錄。

## 升級注意

- `lf_daily_records.ai_degraded_attempts` 由 `AddColumnIfMissing` 建立、既有列為 0，可降版。`BatchRun.Scope` 在 JSON 列內，舊紀錄為 null、不算手動更新。
- 歷史降級主機日不會自動補跑，要補用「強制重新分析」。
- Sentinel 密碼解不開後，行為由「401 失敗」改為「略過並計失敗主機日」，值班看到的字會變。
- 金鑰指紋不符時，只要所有憑證都解得開，下次啟動會自動更新指紋並留下原指紋；重新部署清掉 `keys\` 會讓所有憑證解不開，`DataRoot` 要放在部署目錄之外。
- 使用者站台**升級後仍要還原金鑰檔或重新輸入 Sentinel 與 PRTG 憑證**，否則取數繼續失敗、過期告示不會消失。
- 舊的「完全失敗」主機日徽章由「AI 待補」變成「AI 分析中」，失敗原因留在標題文字。

## 體檢交接

- **狀態**：實作完成，待體檢。未併 dev、PLAN 未歸檔、README／CLAUDE.md 基線未動，這些留給體檢方。
- **實作模型**：Opus 5.5 主代理＋Sonnet 5.5 實作代理。批次H 的規劃審查由 Fable 5.1 做（只改規劃文件，沒有改程式），體檢方若是 Fable 5.1，要獨立於那次審查的結論重看實作。
- **範圍**：`0bf12e40..HEAD`（交接 commit 之後又加了 H-4：使用者實測回報 PRTG 空白 OK 頁被說成登入頁，見執行紀錄）。
- **一次流程失誤**：`143a202d` 是在 3 個測試仍紅時被 `dotnet test | grep` 的管線遮掉結果而提交推送的（grep 命中就回 0），下一個 commit 才修綠；體檢方看到那個 commit 的紅燈不是遺漏，是已修。之後的提交鏈改用 `PIPESTATUS`。
- **測試**：基準 7,956／11。最後一次全量（批次H 完成後）8,360 通過、2 失敗、11 略過；兩案（文件敘事守門、回填請求數）已修並定向確認。**修正後的全量被使用者暫停，沒有跑完**：之後的改動是 `runs.js` 清單標示、三個測試檔與文件，只跑過定向。體檢方請先跑一次全量。
- **最沒把握的地方**（優先看，但不要只看這些）：
  1. **無文法重試是否真的救得回 `[\n]`**：只有替身驗證，要等實際跑一晚。判讀準則在「明確不做」。
  2. **H-1 併發時序**：閘放行後才送的順序斷言只在併發 1 成立；併發 2 時剛過閘、還沒送出的請求有微小窗口。補抓輪最後一顆失敗會留下一個沒人等的暫停工作，到期自行結束，確認它在回填與整趟結束後沒有副作用。
  3. **H-1 冷卻是實例層**：夜間與回填各自新建 `PrtgFetchService`，彼此不共用冷卻；同一時段不同路徑仍可能各打一輪。確認這可接受。
  4. **H-2 自動覆寫指紋**：雙行程競態只有單元測試；`Reevaluate` 在真實 SQLite／SQL Server blob 上是否讀到剛寫入的值，只驗過替身與本機流程。金鑰替換情境沒有在真實站台走完。
  5. **降級補跑沒有每日節流**：背景輪詢可能幾分鐘內用完 3 次就放棄（BACKLOG 有記）。AI 回 `[\n]` 是常態時，這可能讓「已放棄」很快大量出現，請評估要不要本輪就處理。
  6. **B-b 舊列徽章**：舊「完全失敗」主機日徽章寫「AI 分析中」，看起來像還在跑。規劃定案如此，但值班體驗值得再看。
  7. **`PrtgProbeSiteCheckTests.EnvironmentProbeOverallDeadline…` 對時序敏感**：整輪上限 12 秒、輪詢 10 秒，與重量級測試類並行時失敗過兩次、單跑與中等批次都綠；不是本輪改動造成，但全量偶發紅時先懷疑它。
  8. **批次E 的 placeholder 切換**：金鑰與 SMTP「已設定」狀態靠攔截回應驗過，沒有用真實已設定的資料走一次儲存。
- **交給使用者實測**：PRTG 真的無回應時的暫停與停損；還原金鑰或重新輸入後，警示解除與健康頁恢復；Security 權限做法；AI 一夜後的 `finish_reason`、`reasoning_content` 證據。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| F | Sonnet＋主代理 | `bf770f32`、`830bb6ea` | 定向綠；突變紅 | F-5 原寫「≥16」會放過 `dXNlcjpwYXNz`，改「長度 4 倍數且大小寫混合」；notice 改「兩字以內」 |
| A-1、A-2 | Sonnet＋主代理 | `c0c56471` | 定向綠；突變紅 | — |
| A-3～A-5 | Sonnet＋主代理 | `8055662d` | 定向綠；突變紅 | — |
| B-a | Sonnet＋主代理 | `6721c9b1` | 定向綠；突變紅 | 「最後物件優先」會讓「答案＋尾端小物件」選錯，加 `TryParseDetailed(raw, accept)`，選第一個通過內容檢查的候選 |
| B-b | Sonnet＋主代理 | `9fd2e903`、`e432fa59`、`99f1722b` | 整合案（假 AI 回 `[\n]` 三趟）綠；突變紅 | 第 63 輪 F-4 案例判定被推翻。規劃比對抓到三項沒做完：完全失敗標題、最新狀況徽章、基表 parity，於 `99f1722b` 補上 |
| C | Sonnet＋主代理 | `d5ba1ddc` | 定向綠；本機瀏覽器 | `RunDayTimingBuilderTests` 一案依定案改寫。本機沒有舊趟資料，「無階段資料」只由行為測試驗證 |
| D | Sonnet＋主代理 | `414e5d4c` | 定向 175 綠；1268／1024／768／375 與 1920 量測；突變紅 | 收合無條件等寬在寬螢幕退化，加 `max-width:1799.98px`；跳轉改等初次載入全部結束才捲。本機沒有「無 PRTG 對應」徽章，徽章連結只由行為測試驗證；1200px 未單獨量 |
| E | Sonnet＋主代理 | `fc1198cc` | 定向 756 綠；九頁籤無空說明、375px；突變紅 | 常駐說明 42→32、AI 15→11，未達 ≤28／≤8；扣掉 7 條版面小標後為 25／6，沒有為數字刪後果句 |
| G 文件段 | Sonnet＋主代理 | `6d74925a` | 文件守門綠 | Security 警告改寫為「群組＋登錄機碼」，因匯出堆疊是登錄存取被拒；**權限做法未實機驗證** |
| H-1 | Sonnet＋主代理 | `7d707012` | PRTG 定向 339 綠；突變四項紅 | 總暫停改量實際長度；回填兩處本來就不記全部失敗，只補測試；主代理補「閘內失敗不疊加」案；最壞時間由約 12 分鐘更正為併發 2 約 17 分鐘 |
| H-2 | Sonnet＋主代理 | `9bff9ded` | 定向 591 綠；突變兩項紅 | DI 走選用建構子參數；真實站台的金鑰替換未實測 |
| H-4（實測追加） | 主代理 | `143a202d`（含 3 個紅燈被誤推）、後續修正 commit | 定向 146 綠 | 使用者實測：重新輸入 passhash 後測試連線回「HTML 而非 JSON」，內文是 PRTG 的空白 OK 頁——第 43 輪已量到這是 PRTG 伺服器端放棄處理，不是認證錯誤，但訊息說成登入頁；改為獨立的 `EmptyPage` 類別、可重試、訊息講明；回填／探測／同步把「解不開」說成「尚未設定」改由 `CredentialProblem` 單點產生 |
| H-3 | 主代理 | `7518d3e8`、`c679d4ad` | 定向 281 綠；突變兩項紅；本機站台實測告示與手動執行解除過期 | 規劃寫 `freshnessBannerText(freshness, isMaintainer)`，實作為 `freshnessReason(freshness)`，權限分支留在 `loadHealthBanner`；告示只給 Maintain／DevMonitor，連結指向 NetIQ、PRTG 維護而非設定頁；清單只標範圍縮小的趟次，完整範圍只在詳情顯示 |
