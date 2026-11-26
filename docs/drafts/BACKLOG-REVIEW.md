# BACKLOG 重新評估（2026-11-26，dev@7e7d3950）

> 這是評估草稿，不是現行文件：逐條對照現行程式後的建議，待使用者確認才動 `docs/BACKLOG.md` 或程式。
> 做法：BACKLOG 依行號切六區，各由一位唯讀審查者逐條查證；主代理抽查「已解決」的證據並彙整。第 81 輪留下、尚未進 BACKLOG 的項目列在第 3 節。

## 1. 總覽

| 區 | 範圍 | 條數 | 已解決→移除 | 需改寫 | 值得現在處理 | 無法判定（要正式站台或實機） |
|---|---|---|---|---|---|---|
| A | 下一輪預定、待決缺陷、交辦多人、營運規模（1～118 行） | 86 | 2 | 14 | 3 | 6 |
| B | 營運與規模各小節（119～287） | 43 | 3 | 7 | 2 | 5 |
| C | 記憶體、NetIQ、AI、偵測規則（288～426） | 43 | 3 | 10 | 1 | 10 |
| D | 報表、交辦派工、權限、郵件、效能（427～634） | 64 | 1 | 10 | 1 | 7 |
| E | 設計債、前端（635～765） | 約 60 | 1 | 約 11 | 0 | 5 |
| F | PRTG、升級期機制（766～925） | 58 | 0 | 6 | 0 | 3 |
| 合計 | | 約 354 | 10 | 約 58 | 7 | 36 |

其餘約 240 條仍成立、建議保留。

## 2. 重點清單

### 2.1 已解決，建議從 BACKLOG 移除（10 條）

| 行號 | 項目 | 證據 |
|---|---|---|
| 14 | 待派數混入「嚴重度未達」的問題（G9-E2-05） | 待派分三段，儀表板卡只計前兩段（`WorkOrderBoardService.FillSections`、`DashboardService`） |
| 14 | Linux／PRTG 問題顯示成「kernel 0」「PRTG:down 0」（G9-E2-06） | `IssueNames` 單一出口，`IssueNameParityTests` 守門 |
| 225 | 稽核起日晚於迄日仍顯示今天的紀錄 | `AuditController` 走 `ParseDateRange`，起迄顛倒會對調 |
| 234 | 新增使用者填已存在帳號會直接更新 | `UserAdminService` 新增撞號回驗證錯誤（第 81 輪自查修正） |
| 265 | PRTG 鏡像沒有感測器單位 | 結構同步已取並存 `unit`（`PrtgFetchService.cs:1037、1069`）；舊列等下次同步補值 |
| 318 | NetIQ 試點項目 3（標「已完成，編號保留」的空殼） | 內容已在 NETIQ-API-REFERENCE 與 LINUX-RULES；刪前確認沒有地方引用編號 |
| 379 | AI 設定頁沒有測試連線 | 設定頁已有「測試連線」（`#ai-test-btn`、`ai-probe` 端點） |
| 421 | blob 內容 header 誤判陷阱（7-S2） | 現行只判內容非空白，找不到 header 判斷——信心中等，移除前再確認一次原始所指位置 |
| 522 | 被指派歷程不含已被改派走的案件 | `UserAdminService.BuildAssignmentHistory` 已以 `GetReassignments` 補出「已改派給 X」 |
| 729 | 來源觀測超過 64 筆前端靜默截斷 | 後端標 `SourceObservationsTruncated`，前端顯示「清單已截斷」 |

### 2.2 仍成立、成本低、建議現在處理（7 條）

| 行號 | 項目 | 影響 | 工作量 |
|---|---|---|---|
| 14 | 儀表板「24 小時登入失敗」下鑽看不到密碼錯誤與帳號不存在（E1-01） | 卡片數字與點進去的清單對不上 | 小 |
| 15 | PRTG 資料搬運（建立、放棄、完成）沒有寫稽核（E6-10） | 稽核動作常數已登錄卻沒有任何呼叫——有常數沒消費端 | 小～中 |
| 17 | 前端行為測試的輸出管線只等 5 秒，逾時靜默回空字串（T-02） | 全量測試偶發紅的來源之一 | 小 |
| 220 | 「只能檢視自己的交辦」等空狀態用了不存在的圖示 `lock` | 圖示空白 | 小 |
| 232 | 沒有知識庫的問題，佐證區段標題與按鈕都叫「AI 判讀」 | 兩個同名的東西 | 小 |
| 351 | 掃描匯入精靈的下拉仍列出密碼解不開的 Sentinel | 選了必定失敗 | 小 |
| 564 | 權限異動清單的排序鍵（主機名大小寫的排序位置） | 順序不直覺；不會漏列 | 小 |

### 2.3 仍成立但敘述過時，建議改寫（約 58 條）

主要是三類，逐條見第 4 節各區表格：
- **數字或檔名已變**：例如 `runs.js` 行數、`[FromQuery]` 個數、舊徽章寫法處數、`DateTime.Now` 處數、`DueSoonDays` 所在檔案。
- **前提已部分消失**：例如資料版本戳已改成排除法；次要文字對比 4.37 已不成立（現為 5.3 以上）；並行競態已有共用鎖、只剩背景逐日同步；Jwt 金鑰前提已消失；報表已有伺服端 CSV。
- **混入過程敘事**：約 10 處「已改成／已完成／第二波／規劃…實作則是」，BACKLOG 是現行文件，只該寫現況與觸發條件。

另有兩組重複可合併：464 與 475；D 區第 6、9 條。

### 2.4 無法判定（36 條）

都需要正式站台、真實 PRTG／Sentinel／SQL Server 或使用者回報，程式本身判不出來。最需要你確認的是：
- **升級期一次性機制（904～919 行，8 個）**：程式都還在，退場條件是「所有正式站台都升級過、健康頁遷移卡顯示完成」。最舊的是 2026-08-06 加入的。若各站台都已升級，可以排一輪退場清理。
- **PRTG 取數範圍上線後待實機確認（778～786 行，4 條）**：探測工具已備妥，只差在正式站台跑一次。

### 2.5 待決的已知缺陷（41～54 行）

四個探針缺陷都仍會重現：登入 cookie 有效但資料庫讀取失敗時的回應形狀、CSV 解析行號、報表下鑽計數、PRTG 快照封存計數的複合情境。這四條留著是對的，但它們已經被確認是缺陷而不是取捨，建議排進下一輪而不是一直放著。

## 2.6 再評估：小分支處理，還是排到下一輪

| 類別 | 內容 | 規模與風險 | 建議 |
|---|---|---|---|
| BACKLOG 文件整理 | 移除 10 條、改寫約 58 條、補進第 81 輪 4 條、合併 2 組重複 | 只動一份文件，沒有程式風險；逐條都有查證結果可照著改 | **小分支** |
| 6 個小修 | 登入失敗卡下鑽、PRTG 搬運寫稽核、行為測試輸出等待、空狀態圖示、匯入精靈下拉、權限異動排序 | 各自獨立、各一小時內；不動資料表、不加設定、不碰派工邏輯；合計約 10 個程式檔加測試 | **小分支** |
| 「AI 判讀」同名 | 區段標題與按鈕其中一個改名 | 改動很小，但要先決定文案，並同步說明書 | **小分支**（附文案建議請使用者點頭） |
| `EffectiveFrom` | 見第 3 節第 1 條 | 重新設計屬中等以上；維持常數則只改一行 | 不做重新設計；**部署時改一行** |
| 4 個探針缺陷與其餘待決缺陷 | 資料庫讀不到時的空白 500、CSV 行號、報表熱度格與下鑽單位、PRTG 封存筆數；另有執行失敗原因含例外原文等 | 每條都有兩三種修法要選，有的要改讀取器或驗證流程 | **下一輪**（要先定修法） |
| 升級期一次性機制退場 | 8 個機制 | 要先確認每個正式站台都升級過；其中一個是現行讀取閘門的依賴 | **下一輪**（等站台確認） |
| 儀表板摘要效能 | 主體約 2～3 秒 | 要先量測再定做法 | **下一輪或有人反映慢時** |

結論：這次分析出來要動手的部分不大，一個小分支（`feature/feedback-81-3`，零星修補只加修訂）就能做完；需要先做決定或要等正式站台資訊的三類留給下一輪。

## 3. 第 81 輪留下的項目

| # | 項目 | 目前記在哪 | 建議 | 理由 |
|---|---|---|---|---|
| 1 | `DismissalRelease.EffectiveFrom` 寫死 2026-11-25，部署前要人工改成上線日 | BACKLOG 65 行順帶提到 | **部署時改一行；不重新設計** | 再評估後修正：改成自動記錄日期要把一個靜態常數換成注入的狀態，牽動 36 處引用（派工閘門、不再自動派工清單、保留清除三處必須同一口徑），風險與工作量都不是小修。上線日確定後改 `DismissalRelease.cs` 一行即可 |
| 2 | 立即派工或人工追加併入已過期的舊單時，新成員沿用過期的期限 | 沒進 BACKLOG | 加進 BACKLOG | 夜間路徑已修；這條路徑要另查每台的嚴重度，且管理員操作時看得到期限 |
| 3 | 儀表板摘要主體約 2～3 秒（重點問題排行占大宗），多人同時最慢 3.05 秒 | 沒進 BACKLOG | 加進 BACKLOG | 主機數再增加就會明顯超過 3 秒；觸發條件：主機數成長或有人反映慢 |
| 4 | 站台剛啟動、AI 連不上時，第一波今日焦點要等 3～4 秒 | 沒進 BACKLOG | 加進 BACKLOG（併入第 3 條） | 冷卻建立後就是毫秒級；只影響啟動後第一次 |
| 5 | 摘要信逐張掃成員 | BACKLOG 106 行已有一半 | 改寫 106 行 | 該行的另一半（不再自動派工清單）已解決 |
| 6 | 問題鍵在資料庫端區分大小寫 | 沒進 BACKLOG（632 行有相近的定序問題） | 併入 632 行 | 同一類問題 |
| 7 | 保留清除一次讀入全部「30 天解除」候選 | 沒進 BACKLOG | 加進 BACKLOG | 舊的不處理案件很多時才有感 |
| 8 | 批次改派逐張寄通知 | BACKLOG 77 行 | 保留 | — |
| 9 | 補跑只能以回望天數涵蓋資料日 | BACKLOG 73 行 | 保留 | — |
| 10 | 「不再打擾」放行以執行日判定 | BACKLOG 74 行 | 保留 | — |
| 11 | Linux 有事件鍵時存庫名稱顯示規則代碼 | BACKLOG 已有 | 保留 | — |
| 12 | 「清單有更新」提示在重載後到下一次徽章更新之間（最長 60 秒）有盲窗 | 沒進 BACKLOG | 不記 | 影響極小，照建議改反而誤報 |
| 13 | 協調器保底略過以主機計（只在競態出現） | 沒進 BACKLOG | 不記 | 罕見，只多報一台 |
| 14 | 必填標示有文字與星號兩種寫法並存 | 沒進 BACKLOG | 加進 BACKLOG 設計債 | 低優先 |
| 15 | F10 5 步、F14 5 步、F05 4 步未達原目標 | 歸檔文件 | 不記 | 已決定接受，多的是安全步驟 |
| 16 | 「今天新上報」舊資料以更新日判斷、待派卡兩段相加、夜間派不出去名稱不帶規則說明 | 歸檔文件 | 不記 | 都是刻意設計，有測試或驗收守著 |
| 17 | 第 81 輪沒驗到的情境：MU13 處理人側、F10「非負責主機略過」的實例、F11 其餘提醒類別 | 歸檔文件 | 不記 | 受測試資料形狀限制，功能本身有單元與行為測試 |

## 4. 各區逐條結果

### A 區（1～118 行）

### 區 A 結果：docs/BACKLOG.md 第 1～118 行

查證基準：repo HEAD 7e7d3950（dev）。唯讀；未執行測試、未建置。
縮寫：狀態＝已解決／仍成立／部分／無法判定；建議＝移除／保留／改寫／現在處理。

#### 下一輪預定（第 8～39 行）
| # | 行號 | 項目（一句話） | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 1 | 12 | G4 其餘規則嚴重度（E4-02～07，六個子項） | 仍成立 | `LogForesight.Tests/Scenarios/SeverityScenarioTests.cs` 仍有 10 筆 `Known(s, …)`（H2/H4/H8/H9/H10/H12/I3/I4/I6/N2/N3），對應 E4-02～07 全數未修 | 保留 | 產品判定取捨，需使用者決定 | 無 |
| 2 | 13 | G5-E6-01：prompt 沒帶程式判定的風險等級與依據 | 仍成立 | `AnalysisPromptBuilder.BuildPrompt`（約第 47 行）簽章沒有風險等級／依據參數，只寫「已完成風險判定」 | 保留 | 與嚴重度呈現一起設計（中，約半天） | 無 |
| 3 | 13 | G5-E6-02：AI 補跑重建關聯一律 `Severity=High`、`ElevatesDayRisk=false` | 仍成立 | `LogAnalysisService.cs` RetryAi 區段（約第 596～600 行）重建 `CorrelationFinding` 仍寫死 High，附註稱「既有簡化」 | 保留 | 需持久化關聯嚴重度才能修 | 無 |
| 4 | 13 | G5-E6-03：程式判定依據只在 hover title、AI 標題未標示 | 仍成立 | `wwwroot/js/pages/record-detail.js:533-536` 仍把 `riskBasisText` 放在徽章 `title` | 保留 | 屬 G5 設計 | 無 |
| 5 | 13 | G5-E6-04：AI 格式異常降級的殘文顯示 | 部分 | `LogAnalysisService.BuildAiFormatAnomalySummary`（第 43 行）已加「（AI 回覆經 N 次嘗試仍未通過 JSON 檢查，保留原文供參考）」前綴，標題改規則層結論＋「AI 摘要待補，第 N 次」後綴（`AiDegradation.ComposeHeadline`）；但仍顯示截斷原文 | 改寫 | 已有明確標註，剩「是否仍顯示殘文」一點；縮成一句 | 無 |
| 6 | 14 | G9-E2-01：「全選」只選本頁，提示誤導 | 部分 | `handler-detail.js:1049` 標籤已是「全選本頁主機」；原提示句「全部處理好可按『全選』」grep 不到；勾選跨頁保留（`createPageSelection`）。但仍沒有「選取整張單全部 N 台」 | 改寫 | 誤導已消除，剩「無法一鍵涵蓋整張單」 | 無 |
| 7 | 14 | G9-E2-02：可展開列 `role="button"` 讓列內控制項對輔助科技失效 | 仍成立 | `wwwroot/js/core/ui.js:1122-1123` `makeRowActivatable` 仍設 `tr.setAttribute('role','button')`，`ui.js:922` 使用中 | 保留 | 無障礙缺陷，屬 G9（中） | 無 |
| 8 | 14 | G9-E2-04：有上報成員的單，詳情頁首仍寫「進行中」 | 仍成立 | `work-order-detail.js:226` 仍 `statusBadge('進行中','primary')`；列表上報只在成員欄小字（`work-orders.js:469`）；列表已無獨立「狀態」欄，所以「狀態欄」半句過時 | 改寫 | 改成「詳情頁首與列表只在成員欄小字標示上報」（小） | 無 |
| 9 | 14 | G9-E2-05：待派數把「嚴重度未達」永不派的列混進來 | 已解決 | `WorkOrderBoardService.FillSections`（約第 313～330 行）分「可以派／派不出去／不會派」三段，嚴重度未達歸「不會派」；`work-orders.js:102` `gate_severity: '嚴重度未達'`；儀表板卡只計前兩段（`DashboardService.cs:127`） | 移除 | 前提已不成立 | 無 |
| 10 | 14 | G9-E2-06：Linux／PRTG 問題顯示「kernel 0」「PRTG:down 0」 | 已解決 | `LogAggregator.SourceEventLabel`（第 71～77 行）PRTG 走 `IssueNames.PrtgKindText`、Linux 走「來源（事件鍵）」；`IssueNames.cs`、`IssueNameParityTests` 守門 | 移除 | 前提已不成立（相關的規則名稱落差另見第 75 行項目） | 無 |
| 11 | 14 | G9-E2-07：處理人儀表板與管理者同一張 11 欄分析表 | 仍成立 | `dashboard.js:640-651` `renderTopIssues` 欄位不分角色，一律 11 欄 | 保留 | 屬畫面設計（中） | 無 |
| 12 | 14 | G9-E2-08：手機看「我的交辦」KPI 卡占滿螢幕 | 部分 | `handler-detail.js:300-312` `renderKpi` 已改為兩條 `kpiStrip(..., { singleRow: true })`；窄螢幕實際呈現沒有截圖，讀碼無法確認 | 保留 | 需 DESIGN-SYSTEM §8.7 量測五個寬度，過了就移除（小） | 無 |
| 13 | 14 | G9-E1-01：儀表板「24 小時登入失敗」下鑽只帶 `result=Denied` | 仍成立 | `dashboard.js:425-431` url 仍 `/audit?result=Denied`；卡片數字 `DashboardService.cs:106` 統計 `LoginFailed` 動作（`IdentityService.cs:87,96,106` 記 `Failed`，`:112` 記 `Denied`），密碼錯誤與帳號不存在點進去看不到。`audit.js:42-44` 已支援 `actions=` 網址參數 | 現在處理 | 下鑽改帶 `actions=login_failed` 並拿掉 `result`，小（一小時內）；需連動斷言該連結的測試 | 無 |
| 14 | 14 | 「新視覺方向第二階段」指向 | 仍成立 | 指向的條目在 BACKLOG 第 754 行存在（非本區範圍） | 保留 | 交 B 區等範圍核對 | 無 |
| 15 | 15 | G10-E6-06：縮短保留天數的確認與 PRTG 保留天數靜默改 180 | 部分 | `settings.js:1484-1520` 縮短時已有 `confirmAction`（列出「進入刪除範圍」）；但 `prtg-admin.js:1090` 仍 `\|\| 180` 靜默回退且 PRTG 保留沒有確認框；說明書 `HelpContent/10-scheduler.ai.md:65`、`12-settings.ai.md:85` 仍寫「每次執行／批次啟動時清除」，而排程另有 `RetentionOnlyRunner`（`SchedulerHostedService.cs:656`） | 改寫 | 縮到「PRTG 保留天數無確認＋空值靜默 180＋說明書時機」；PRTG 兩點可現在處理（小） | 無 |
| 16 | 15 | G10-E6-07：首次啟動精靈的「全部就緒」過寬 | 部分 | `SetupReadinessService.cs`：AI 步驟已改探活（`AiConfigured`、`AiProbeDetail`）、PRTG 要結構同步＋有對應；仍可跳過資料來源、完成判定多數只看設定存在、無加密金鑰／救援帳號密碼公開測試值檢查。證據所稱「Jwt 金鑰仍是公開測試值」前提已消失：`AppSettings.cs:200-201` 登入 cookie 改 Data Protection，Jwt 區段已無簽章金鑰 | 改寫 | 刪掉 Jwt 金鑰與 AI 不測連線兩點；其餘保留 | 無 |
| 17 | 15 | G10-E6-10：PRTG 搬運、NetIQ 掃描等未寫稽核 | 仍成立 | `PrtgTransferController.cs` 的 `_audit` 只傳給匯出（第 65 行）；Create／Abandon／Complete（77、94、162 行）無稽核。`AuditActions.PrtgDataImport` 常數與標籤（`AuditQueryService.cs:132`）已登錄，但全 repo 沒有任何 `audit.Record(AuditActions.PrtgDataImport…)` 呼叫——有常數無消費端，違反「有設定無行為」紅線精神 | 現在處理 | Complete／Abandon 補 `PrtgDataImport` 稽核，小～中（約一小時）；NetIQ 掃描啟停與精靈跳過另補 | 無 |
| 18 | 15 | G10-E6-12：錯誤碼字面字串、前端以訊息文字判斷 | 部分 | `ApiErrorCodeGuardTests` 已守門（Fail／DomainException 不得用字面碼，Core 字面碼須登錄）；但 `permission-changes.js:286` 仍 `err.message.includes('網段')` | 改寫 | 只剩前端字串判斷一點（小） | 無 |
| 19 | 15 | G10-E6-13：批次確認稽核摘要只有筆數 | 仍成立 | `PermissionChangeService.cs:396-411` summary 仍只有「批次確認 N 筆」；detail 已含 ChangeIds／HostNames／Categories／Skipped | 保留 | 瑕疵級；明細已有，可隨 G10 順手（小） | 無 |
| 20 | 15 | G10-E6-14：「處理日」概念用詞三套 | 仍成立 | `runs.js:318`「執行日與時間」；`HelpContent/10-scheduler.md:109`「處理的那一天」；DTO／匯出為 RunDate／資料日（CLAUDE.md 已定義兩者） | 保留 | 用詞整理（小） | 無 |
| 21 | 15 | G10-E5-05：沒有監看裝置數基準時範圍外資料只增不減，畫面要提示 | 部分 | 同步輸出已提示（`PrtgScopeDevices.CheckShrink` 第 214 行「尚無監看裝置數基準（第一次）…請到 PRTG 維護頁執行預覽並確認」）；維護頁預覽顯示「尚無基準（還沒清除過）」（`prtg-admin.js:2422-2423`）；是否在狀態卡／健康頁主動提示未確認 | 改寫 | 改成「首次基準尚未確認時，是否要主動提示」；影響小 | 無 |
| 22 | 16 | G11-E5-01：PRTG 狀態以英文顯示文字比對 | 仍成立 | `PrtgConstants.cs:12-13` 英文常數；`PrtgEndToEndChainTests.cs:779-800` 現況測試仍記錄「警告」＋`status_raw`=4 認不出；`Core`／`Web` 正式碼沒有讀 `status_raw` | 保留 | 觸發條件（改語系）未發生；實機是英文 | 無 |
| 23 | 17 | G12-T-01：UI 量測情境互相影響（滑鼠位置、捲軸 11px） | 無法判定 | 要實跑 `tools/ui-shots.mjs` 全矩陣才知道是否仍偶發 | 保留 | 缺跑量測工具的結果 | 無 |
| 24 | 17 | G12-T-02：`NodeRuntime.RunCore` 程序結束後只等輸出管線 5 秒，逾時靜默回空字串 | 仍成立 | `LogForesight.Tests/NodeRuntime.cs:174-176` `Task.WaitAll(..., 5s)`，未完成就回 `string.Empty`；`CLAUDE.md` 也把它當已知偶發 | 現在處理 | 逾時改為出聲（`RunOrFail` 判定 stdout 空且 exit 0 不等於成功）或加長，小（一小時內） | 無 |
| 25 | 19 | 序言：3000 台規模效能最佳化「不改運作邏輯」的部分已完成 | 部分 | 內容為過程結論 | 改寫 | 只留「以下是刻意未做的項目」，背景連結留一行 | 有（「已完成」） |
| 26 | 21 | 會改運作邏輯：AI 待補同日高風險先做、主機輪替、連續失敗中止、積壓放棄策略 | 仍成立 | `AiAnalysisHostedService.SelectNextBatch`（第 451 行）只依日期層與七日塊排序，無風險等級或主機輪替；失敗只進本輪 `attempted` 集合；放棄只有格式異常 `AiDegradation.MaxAttempts` | 保留 | 要使用者決定 | 無 |
| 27 | 22 | 資料保留清掉待補時留下紀錄 | 仍成立 | `EfAnalysisRecordStore.cs:1159` `SetProperty(AiPending,false)`；Log 只印清除筆數（1166～1179 行），沒有 ai_pending 數 | 保留 | 小；加一個計數即可，等使用者要才做 | 無 |
| 28 | 22 | 執行輸出超過 2,000 行時保留失敗訊息 | 仍成立 | `BatchRunRecorder.cs:272-275` 前 1500 行＋尾 500 行，中段略過 | 保留 | 行為取捨 | 無 |
| 29 | 23 | NetIQ 窗口放不下先做最新一天；Sentinel 逾時也切批 | 部分 | 截斷時切半重查已有（`NetiqPipelineService.cs:699-714`）；「窗口放不下先做最新一天」「逾時切批」沒找到對應實作 | 保留 | 未見實作；待有實測再議 | 無 |
| 30 | 24 | 工作流恢復深層巡掃預算（約 55 天走完一輪） | 仍成立 | `HostDayWorkflowRecoveryHostedService.cs:79,159-164` 仍以 `DeepPagesPerSlice` 預算分頁巡掃 | 保留 | 與第 38 項同源 | 無 |
| 31 | 24 | 待派 20,000、統一標記 5,000 等上限放寬 | 仍成立 | `WorkOrderBoardService.cs:31` `DefaultMaxOccurrences=20000`；`IssueHandlingCommandService.cs:522` `MaxBulkCloseDayWrites=5000` | 保留 | 以規模實測為準 | 無 |
| 32 | 24 | NetIQ 匯入預覽分頁 | 仍成立 | `netiq-import-wizard.js`、`NetiqImportRequest` 找不到預覽分頁相關程式 | 保留 | 低優先 | 無 |
| 33 | 25 | 平行度、連線池、GC 參數預設值 | 無法判定 | 需正式站台量測 | 保留 | 缺正式環境探測 | 無 |
| 34 | 25 | 資料版本戳推進粒度（任何成功的非 GET 都使全站快取失效） | 部分 | `Program.cs:303-326` `DataVersionStampPolicy` 已是排除法：端點可貼 `[SkipDataVersionBump]`（已用於 `PrtgMonitoringController`、`HelpController`、`AiController`、`CspReportController`、`HandlingController` 等）；仍是預設推進，非「任何」 | 改寫 | 描述過時：改成「預設推進、僅少數端點免推進；是否擴大免推進範圍」 | 無 |
| 35 | 26-30 | 等價難證或牽動太廣的十餘項（工作流 Update 不變不寫、多次更新合併、版本戳快取等） | 仍成立 | 抽查：`HostDayWorkflowState.cs` 仍有 10 處 `PrtgAttemptAtUtc` 引用；其餘為設計取捨，沒有逐項重驗 | 保留 | 屬否決紀錄；逐項重驗成本高，沒有新證據 | 無 |
| 36 | 31-32 | 需要資料表／欄位／索引變更（預先彙總、PRTG 時間起頭索引、`OwnedRows` 鍵欄位等） | 仍成立 | `LfDbContext.cs:704-725` PRTG 狀態變更與數值表索引都以 sensor 起頭，沒有 `changed_at` 起頭；`EfAnalysisRecordStore.OwnedRows` 仍在 | 保留 | 需執行計畫證據才動 | 無 |
| 37 | 33-35 | 取數與工作流恢復寫入的狀態不一致（每新主機日多一次寫入） | 無法判定 | 觸發條件是「恢復服務單趟寫入量成為瓶頸」，要正式站台數據；描述未逐行重驗 | 保留 | 缺正式量測 | 無 |
| 38 | 36 | 資料保留清除不清 `workflow_*` blob，只靠孤兒清掃 | 仍成立 | Core 內沒有針對 `workflow_` blob 的保留／清除程式；`HostDayWorkflowRecoveryHostedService` 的 Orphan 巡掃是唯一路徑 | 保留 | 觸發條件是 `lf_blobs` 列數成長 | 無 |
| 39 | 37-38 | 網頁請求內重複讀取（評估後不做） | 仍成立 | 未逐項重驗（可見主機清單、設定讀改寫、處理狀態主機索引） | 保留 | 無新證據 | 無 |
| 40 | 39 | 正式主機實測與 `docs/drafts/ENV-PROBE-PLAN.md` | 仍成立 | 檔案存在 | 保留 | — | 無 |

#### 待決的已知缺陷（第 41～54 行）
| # | 行號 | 項目（一句話） | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 41 | 45 | AI 寫回樂觀檢查含 NetIQ 嘗試欄位 | 仍成立 | `HostDayWorkflowState.cs:1346-1357` `ForRecordWithIdentity` 預設 `includeAttempt=true`，`ForRecord` 被 `AiAnalysisHostedService.cs:605,624` 與 `EfAnalysisRecordStore.cs:516,563,663` 用於 AI 寫回檢查；`ForMailContent` 才排除嘗試欄位 | 保留 | 只浪費一次 AI 呼叫、不損資料；修法要拆兩種用途（中） | 無 |
| 42 | 46 | 執行失敗原因含例外原文 | 仍成立 | `RunFailureReasons.cs:21` `Describe` 非記憶體不足回 `ex.Message`；`UserFacingMessageGuardTests.cs:20` 仍以「已知待檢討」放行，註解內帶「第 80 輪批次 D」 | 保留 | 需盤點例外型別（中）；注意測試註解有輪次敘事但不在 BACKLOG | 無 |
| 43 | 47 | 驗證訊息仍可能露出 C# 屬性名 | 仍成立 | `DefaultInputLimitsFilter.cs:33,35` 仍 `「{property.Name}」`；`ChineseModelBindingMessages.cs:46-47` `[Required]` 仍用 `{0}`（無 `[Display]` 時是屬性名） | 保留 | 需 DTO 補顯示名稱（中～大） | 有（「其他驗證屬性已改成…」） |
| 44 | 48 | 資料庫讀不到時帶 cookie 的頁面得到空白 500 | 仍成立 | `CookieSessionEvents.ValidatePrincipal` 第 79、86 行 `_users.Get(userId)`、`_versions.Current(...)` 仍沒有 try/catch；`Program.cs:206-208` 仍 `UseExceptionHandler` 重跑 `/error`；`AuthDbFailureProbeXTests.cs:37` 仍 `Skip`；讀測試與碼判斷缺陷仍會重現（未執行） | 保留 | 資料庫中斷時才會見到；修法選項已寫明 | 無 |
| 45 | 49 | CSV 匯入錯誤「第 N 行」在空行之後偏小 | 仍成立 | `CsvParser.cs:101-120` 仍以 `TextFieldParser.LineNumber`；`CsvParserLineNumberProbeXTests.cs:21` 空行案例仍 `Skip`；跨行引號案例未略過，與敘述相符 | 保留 | 只影響錯誤訊息行號（小～中，自寫行計數） | 無 |
| 46 | 50 | 問題類型熱度格（問題日）與下鑽清單（主機日）單位不同 | 仍成立 | `reports-category.js:17` `HEAT_HELP` 與 `cellAriaLabel` 只講「問題日」，未提下鑽清單單位；`records.js` 下鑽仍是明細視角；`ReportDrillCountProbeXTests.cs:34` 仍 `Skip`（3 對 2） | 保留 | 最便宜的修法是在說明加一句（小） | 無 |
| 47 | 51 | PRTG 快照手動封存在「落盤失敗後又遇來源變更」少報筆數 | 仍成立 | `PrtgSnapshotHostedService.cs:1448-1470` `GetJournalPreview` 仍只用 `_journal.Inspect`（已落盤列）；`PrtgSnapshotArchiveCountProbeXTests.cs:45` 複合情境仍 `Skip`，一般路徑測試（第 17 行）為有效守門 | 保留 | 複合情境罕見 | 無 |
| 48 | 52 | 排程服務時鐘三種寫法並存 | 仍成立 | `SchedulerHostedService.cs` 多處 `DateTime.Now`（101、190、198、209、232、250、338…）、`internal Func<DateTime> Clock`（653）、`RetentionOnlyRunner`（656）；`Services/Mail/MailNotificationService.cs` 用 `TimeProvider` | 保留 | 整檔改 `TimeProvider` 為中（半天）；沒有使用者可見影響 | 無 |
| 49 | 53 | 整列連結截斷欄若同時有格內互動元素，完整文字提示出不來 | 仍成立 | `core/ui.js:1092-1099` `attachTruncateTitle` 在 `hasInteractiveDescendant` 時只設 td.title、不包連結；註解寫明目前沒有表格是此組合 | 保留 | 預防性紀錄 | 無 |
| 50 | 54 | `NetiqSourceAttempt.Mark` 只有測試使用 | 部分 | `NetiqSourceAttempt.cs:353` `Mark` 仍在，正式碼無呼叫；但呼叫端只有 2 個測試檔（`RunMonitorServiceTests.cs`、`NetiqPipelineBaselineTests.cs`，共 4 處），不是「三個測試類別」 | 改寫 | 更正為兩個測試類別 | 無 |

#### 交辦與多人使用情境（刻意未做）（第 56～79 行）
| # | 行號 | 項目（一句話） | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 51 | 58 | 背景指向 `FEEDBACK-81-PLAN.md` §10 | 仍成立 | `docs/archive/FEEDBACK-81-PLAN.md:747` 有「10. 明確不做」 | 保留 | 一行連結，合規 | 無 |
| 52 | 60 | 統一標記的背景分批（F16-3） | 仍成立 | `IssueHandlingCommandService.cs:522` `MaxBulkCloseDayWrites=5000`，沒有背景作業 | 保留 | 觸發條件未發生 | 無 |
| 53 | 61 | 主機負責人通知偏好（F10-2）、暫停接單恢復日（F14-5） | 仍成立 | grep `PausedUntil`／`ResumeDate`／`NotifyPref` 於 Core／Web 皆無；暫停接單僅有開關（`AuditActions.UserDispatchPaused`、`work-orders.js:448`） | 保留 | 需新欄位，無需求佐證 | 無 |
| 54 | 62 | 「為什麼看不到」查詢（F18-2）、未分組主機建議群組（F17-1） | 仍成立 | grep 無對應功能 | 保留 | 觸發條件未發生 | 無 |
| 55 | 63 | 報表依處理人群組時效（F20-2）、權限異動更正（F09-3）、成員分布摘要 | 仍成立 | 未逐項重驗（無新證據；沒找到對應功能） | 保留 | 屬否決紀錄 | 無 |
| 56 | 64 | 整合式 Windows 驗證（Q17） | 仍成立 | `LogForesight.Web` 無 `Negotiate`／Kerberos 相關程式 | 保留 | 取決於部署決策 | 無 |
| 57 | 65 | 預設期限等做成設定：常數清單 | 仍成立 | 全部點名符號都在：`DefaultDueDays`（High=3、Medium=7）、`DispatchContext.ContinuityDays=30`、`AdminReminderService.RecurrenceReminderThreshold=3`（第 39 行）、`WorkOrder.DueSoonDays=3`、`DismissalRelease.Days=30`／`EffectiveFrom`、`AiRequestGate.NetworkFailureCooldown=60s`、`WorkOrderBulkReassignService.MaxOrders=100`、`ReportCsv.MaxRows=50_000`。注意 `WorkOrderQueries.DueSoonDays` 實際在 `Core/Models/WorkOrder.cs:379`，不在 `WorkOrderQueries` | 改寫 | 修正 `DueSoonDays` 所在類別名稱 | 無 |
| 58 | 66 | 結案後自動重開舊案件、觀察到期自動結案 | 仍成立 | 重開僅為成員層手動選項（`WorkOrderCoordinator.cs:229-234` `member.Reopen`），無自動重開 | 保留 | 屬決策 | 無 |
| 59 | 67 | 逐人未讀狀態、既有案件回填預設期限 | 仍成立 | 無逐人未讀表；`badges.js` 的未讀是單層級計數 | 保留 | 屬決策 | 無 |
| 60 | 68 | 派工選人更多負載因子、成員表每頁筆數、回覆送出前再確認、重點問題分數 | 仍成立 | 未逐項重驗；沒有新證據 | 保留 | 屬決策 | 無 |
| 61 | 69 | 6,000 台規模效能 | 無法判定 | 需正式站台環境探測 | 保留 | 缺正式環境 | 無 |
| 62 | 70 | 「不再顯示這個問題…」的邊界（30 天回看、長期走機房結論、多步不回滾） | 仍成立 | 未逐項重驗；機房結論機制仍在（`IssueOwnerAdminService.SetConclusion`） | 保留 | 觸發條件未發生 | 無 |
| 63 | 71 | 報表未結案三段的近似與上限 | 仍成立 | `ReportAnalyticsDtos.cs:61` 仍有「不需處理」口徑說明；Z23 另補「拆不開時分原因」（`WorkOrderBoardService`） | 保留 | 屬近似取捨 | 無 |
| 64 | 72 | 規則試算只到問題子列 | 仍成立 | 未逐項重驗 | 保留 | 屬取捨 | 無 |
| 65 | 73 | 執行總表補跑只能以回望天數涵蓋資料日、名單超過 200 台退回全部 | 仍成立 | 未逐項重驗 | 保留 | 屬取捨 | 無 |
| 66 | 74 | 「不再打擾」放行以執行日判定 | 仍成立 | `DismissalRelease.cs` `IsReleased` 以 `(asOf.Date - ClosedAt.Date).Days > Days` 判定，`asOf` 為呼叫端日期；`EffectiveFrom`＝2026-11-25 | 保留 | 觸發條件未發生 | 無 |
| 67 | 75 | Linux 有事件鍵時存庫名稱顯示規則代碼而非規則名稱 | 仍成立 | `LogAggregator.cs:75` `EventId == 0 && EventKey.Length > 0 ? $"{Source}（{EventKey}）"`；畫面端 `IssueNames.ForRules` 用規則名稱 | 保留 | 觸發條件未發生 | 無 |
| 68 | 76 | Linux 事件編號 0 的 30 天解除判定保守 | 仍成立 | `KnownIssueCatalog.FindRuleByAggregateKey`（第 424～432 行）EventId 0 走 `FindLinuxRuleByProgram`，找不到或說不出是哪一條回 null → `DismissalRelease.AppliesTo` 為 false | 保留 | 觸發條件未發生 | 無 |
| 69 | 77 | 批次改派每張單各發一次通知、停用前確認框疊在使用者視窗上 | 部分 | 通知：`WorkOrderBulkReassignService.cs:113` 逐張呼叫 `Reassign`（含通知）屬實；視窗疊開未重驗 | 保留 | 通知半句確認；疊窗半句需實跑畫面 | 無 |
| 70 | 78 | 「只有上報中的」代為結案不含停用主機上的成員 | 無法判定 | `WorkOrderDtos.cs:586` 說明為「檢視者看得到的主機」；未實跑確認停用主機是否排除 | 保留 | 缺實跑 | 無 |
| 71 | 79 | 第二波效能驗收 VM D2 資料集量測，MU01／MU15 留待正式站台 | 無法判定 | 需正式站台環境探測 | 改寫 | 純過程紀錄且以「第二波」命名，改成「不再自動派工清單 2,000 台 1 秒內只在 SQLite 驗證，實際加速待正式站台量測」 | 有（「第二波」「MU01、MU15」） |

#### 營運與規模擴充（未排期）（第 81～117 行）
| # | 行號 | 項目（一句話） | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 72 | 83 | EVTX 離線匯入 | 仍成立 | Core／Web 無 EVTX 匯入程式（僅 `RunExportDeidentifier` 提到字樣） | 保留 | 等需求 | 無 |
| 73 | 84 | 伺服器端 CSV 匯出（清單頁） | 部分 | 報表已有伺服端 CSV（`ReportCsv.cs`，上限 50,000 列）；問題查詢清單仍只有前端「複製為 CSV」（`records.js:2408-2409`） | 改寫 | 限縮為「問題查詢清單頁」，避免與報表 CSV 混淆 | 無 |
| 74 | 86 | Windows SMART | 仍成立 | 無相關程式；屬資料源問題 | 保留 | 等需求 | 無 |
| 75 | 90 | 初次上線歷史回補（120 天×3000 台約五天） | 仍成立 | `SystemSettings.cs:199` `DefaultInitialHistoryDays=120`；屬部署程序 | 保留 | 屬分批上線程序 | 無 |
| 76 | 95 | 年度同期比較的資料前提 | 仍成立 | `SystemSettings.cs:212` `DefaultRetentionDays=180`、`:244` `DefaultRawEventRetentionDays=120`；`reports.js:281` 仍有 `comparisonOutOfRetention` 提示 | 保留 | 屬容量決策 | 無 |
| 77 | 99 | 多執行個體排程仍不支援 | 仍成立 | `EfAnalysisRecordStore.cs:61` 程序內鎖＋`write_revision`；仍無跨程序租約 | 保留 | 屬架構限制 | 無 |
| 78 | 100 | 停用主機排除條件在聚合查詢重複展開 | 仍成立 | `WorkOrderQueries.DisabledHostKeys` 仍被 Core／Web 約 16 處引用（`AdminReminderService.cs:258` 等）；沒有量測 | 保留 | 觸發條件已寫明量測做法 | 無 |
| 79 | 102 | 處理人看已取消／已移交單時成員主機名仍是連結 | 仍成立 | 成員 DTO 無「可開啟」旗標（grep `canOpen` 僅 `host-orders.js:109` 針對單） | 保留 | 觸發條件未發生；補旗標約小～中 | 無 |
| 80 | 104 | 夜間單升級成群組範圍續掛（E3-16） | 仍成立 | 無範圍變更動作 | 保留 | 使用情境少 | 無 |
| 81 | 106 | 管理者提醒與不再自動派工清單的查詢量 | 部分 | `AdminReminderService.Compute`（第 153～162 行）仍逐張上報單查成員；`DismissedIssueService.cs:80-99` 的 `ClosedByOf` 已只對當頁（`pageGroups` Skip/Take）計算，所以「對每列各讀處理歷程」半句過時（只剩頁內每列一次） | 改寫 | 刪掉「只對當頁 50 組」這個已達成的建議 | 無 |
| 82 | 108 | 案件整列儲存會覆寫 `BlocksRedispatch`／`EscalatedAt` | 仍成立 | `EfIssueCaseStore.cs:280` `WorkOrderId` 有保護，`:284-285` `BlocksRedispatch`、`EscalatedAt` 仍整欄覆寫 | 保留 | 窗口很窄；加保護小（一小時內），等觸發 | 無 |
| 83 | 110 | PRTG「感測器全部回空」警告在合法無感測器時誤報 | 仍成立 | `PrtgFetchService.cs:19`、`PrtgStructureSyncStatus.cs:13` 仍有此警告 | 保留 | 觸發條件是正式站台誤報 | 無 |
| 84 | 112 | Windows 事件欄位只隔單一空白的來源 | 仍成立 | `LogAggregator.cs:656` 邊界仍是連續兩個以上空白 | 保留 | 觸發條件未發生 | 無 |
| 85 | 115 | 詢問 AI 的 AI 路由 | 仍成立 | `ChatFocusResolver.cs:13` 仍是確定性規則 | 保留 | 等實測回饋 | 無 |
| 86 | 116-117 | AI 主分析首次回覆為 `[]`（物件根 schema 未做） | 仍成立 | `AiGrammarMemory.cs:25` 仍在；`AIService.cs:884` 送 `response_format`；診斷匯出 `aiFormatAnomalies` 仍在（`RunExportDtos.cs:384`）；指向的「AI 回覆格式異常的成因尚未處理」在第 139 行存在 | 保留 | 等診斷匯出數據 | 無 |

### B 區（119～287 行）

#### 營運與規模擴充（BACKLOG 119～287 行，區 B）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 1 | 119 | 重新分析 run-preview 只回台數、無重跑範圍預估 | 仍成立 | ScheduleController.RunPreview：onlyMissingOrFailed=false 只回 HostCount、AiDisabled；RunPreviewDto 只有兩欄 | 保留 | 觸發條件（回饋）未出現 | 無 |
| 2 | 126 | CSP 只用 report-uri | 仍成立 | Program.cs:360 只加 report-uri；SecurityHeadersTests:212 | 保留 | 瀏覽器尚未停用 | 無 |
| 3 | 129 | serverAdmin 在執行詳情看得到 Output | 仍成立 | ForServerAdmin 有 Maintain；RunsController 無 RequireViewAll；RunMonitorService.GetDetail 回 logs（含 Logger=Output） | 保留 | 小改（對無 ViewAll 隱藏 Output）約小，但屬權限取捨，觸發條件未到 | 無 |
| 4 | 134 | 診斷匯出無代號對照表 | 仍成立 | RunExportService 仍僅檔內代號，無站內查詢 | 保留 | 刻意設計 | 無 |
| 5 | 139 | AI 回覆格式異常成因未處理 | 部分 | AiGrammarMemory 存在（Core/Analysis/AiGrammarMemory.cs），WEB-SPEC §9.10 有診斷；超長／型別／截斷成因仍待資料 | 保留 | 與 L116 有重疊，可合併 | 「已處理的部分」為現況敘述，無過程敘事 |
| 6 | 145 | PRTG messages 讀數少於 treesize 只是提醒 | 仍成立 | PrtgFetchService.cs:1247-1250 仍只寫提醒 | 保留 | 需實機證實 | 無 |
| 7 | 150 | System 記錄保留不足、sensor 逾時提示 | 仍成立 | AnalysisOrchestrator.cs:1422 仍有保留不足訊息 | 改寫 | 屬「持續觀察」非待辦，可精簡為一句或併入 PRTG／健康文件 | 無 |
| 8 | 155 | 階段快照使每趟資料量放大約 5 倍 | 仍成立 | BatchRunRecorder.cs:211-216 頂層階段結束仍 WriteSnapshot | 保留 | 數字為舊量測，無法重量（唯讀） | 無 |
| 9 | 162 | 自動補跑只看最近一個已結束窗口 | 仍成立 | SchedulerHostedService.cs:277 只取 LastEndedWindowInstance | 保留 | 無 | 無 |
| 10 | 167 | 窗口到點時 PRTG 結構同步中，下一窗口重做 | 無法判定 | 需實機 PRTG 看同步是否撐得過窗口 | 保留 | 缺正式站台資訊 | 無 |
| 11 | 172 | 跨午夜窗口午夜後的趟不沿用 PRTG 同步 | 仍成立 | PrtgReevaluationPlanner.cs:147 以 StartedAt.Date != today.Date 判定 | 保留 | 無 | 無 |
| 12 | 177 | 交辦協調器多步寫入不同交易 | 部分 | WorkOrderCoordinator.cs:129-160 仍逐步 InsertOrAdopt／MembersInto／AppendEvent；僅有零成員建單失敗的補償取消（catch when created…），無交易 | 改寫 | 補一句「僅有零成員空單的補償關閉」；多步原子性仍未做 | 無 |
| 13 | 182 | 每主機日行程內互斥鎖 | 仍成立 | EfAnalysisRecordStore.cs:68 LockFor，多處 lock | 保留 | 多節點才需要 | 無 |
| 14 | 187 | 同日續補不更新同步狀態／新鮮度／範圍 | 無法判定 | 未逐段讀同日續跑路徑；需實機行為 | 保留 | 未深查 | 無 |
| 15 | 192 | 未輪到的 Sentinel 與異常中斷不留 NetIQ 回執 | 仍成立 | NetiqSourceAttempt／NetiqPipelineService 仍存在，描述的缺口未見修補（未逐行證明） | 保留 | 低把握 | 無 |
| 16 | 197 | 本機資料不完整的日子不會自動重跑 | 仍成立 | 搜尋未見本機取數狀態欄（LocalIncomplete 類符號無命中） | 保留 | 無 | 無 |
| 17 | 202 | 全量測試耗時觀察 | 仍成立 | tools/test-timing.py、三個點名測試檔仍在 | 改寫 | 屬觀察紀錄與做法，大半已寫在 CLAUDE.md 測試慣例，可精簡 | 無 |
| 18a | 210 | 長文字區塊只看字數，可能無更多內容仍有漸層 | 仍成立 | ui.js collapsibleText 以 textContent.length 判準＋CSS clamp | 保留 | 無 | 無 |
| 18b | 211 | 漸層終點色只兩種 | 仍成立 | 同上 CSS 未改（未逐行查 CSS） | 保留 | 低把握 | 無 |
| 18c | 212 | 重點速報丟棄規則不擋 IPv6／主機名 | 仍成立 | 無 IPv6 專屬處理（CidrMatcher 明言 IPv4 only） | 保留 | 無 | 無 |
| 19a | 217 | recordDetailUrl 案例放在側軌測試類別 | 仍成立 | SideRailBehaviorTests.cs:25,86-91 | 保留 | 純整理，可併入下次動測試時 | 無 |
| 19b | 218 | 處理面板重繪後焦點回頁首 | 仍成立 | handler-detail.js 無重繪後設焦點到面板標題（只有 :966 input.focus） | 保留 | 小 | 無 |
| 19c | 219 | 實心 Bootstrap 徽章（約 25 處） | 部分 | 現仍有約 20 處（prtg-calibration.js:21-29、prtg-admin.js:1634-1640/1877/3644、Prtg.cshtml:512-518/660-730、rules.js:1480、dashboard.js:729、settings.js:1152）；ui.js:523 為 toast 不算；DesignSystemGuardUiTests 的 DynamicSemanticClass 只擋動態樣板，未禁靜態 `badge bg-` | 改寫 | 更新數字與檔案清單；排程作業頁的實心紅「失敗」未在 grep 命中，描述可能過時 | 無 |
| 19d | 220 | 「只能檢視自己的交辦」空狀態 icon 'lock' 不存在 | 仍成立 | handler-detail.js:177,186 用 'lock'；img/icons.svg 無 lock | 現在處理 | 小（補圖示或改既有圖示）；另 L177 無處理能力空狀態同樣受影響，補述 | 無 |
| 20a | 225 | 稽核起日晚於迄日仍顯示今天的紀錄 | 已解決 | AuditController.cs:37 呼叫 QueryStringParsing.ParseDateRange，from>to 時自動對調（QueryStringParsing.cs:~68） | 移除 | 前提不成立（未驗畫面是否同步回填對調值，影響小） | 「探針已重現」屬過程字眼 |
| 20b | 226 | 無效網段時列表換成「沒有待確認的權限異動」 | 仍成立 | permission-changes.js:286-291 isSubnetErr 時 lastResult 設空並 render | 保留 | 小；可於觸發時處理 | 同上 |
| 21a | 231 | 「送出並回覆下一張」下一張列展開後被就地更新抹掉 | 部分 | handler-detail.js 現行「回覆」一律開回覆彈窗（replyOrder:761-790），不再展開列；openNextOrder:870-878 在換頁路徑 await lastRefresh；同頁路徑立即 replyOrder | 改寫（或移除，需實測確認） | 描述的「列展開」前提已過時；彈窗不受重建清單影響 | 「探針已重現」 |
| 21b | 232 | 無知識庫問題佐證區段標題與按鈕都叫「AI 判讀」 | 仍成立 | record-detail.js:1655 標題 'AI 判讀'；:2457 按鈕 makeButton('AI 判讀') | 現在處理 | 小（改其一文案，注意 UI 測試斷言字串） | 同上 |
| 21c | 233 | 觸控裝置看不到「不是來源時間窗」說明 | 仍成立 | record-detail.js:1580-1584 口徑只在 title | 保留 | 小 | 同上 |
| 21d | 234 | 新增使用者填入已存在帳號會直接更新 | 已解決 | UserAdminService.cs:235-237 request.IsNew 且已存在擲驗證錯誤；users.js:491 送 isNew | 移除 | 有守門 | 同上 |
| 22 | 239 | lf-text-meta 在滑過／選取列底色對比 4.37:1 | 部分 | base.css:113 --lf-text-muted 已改 #5b6678，我算對白底 5.81、對 --lf-primary-soft 5.34（皆 ≥4.5），描述數字過時；AccessibilityUiTests 仍只驗 card-bg／gray-50 等，未含 primary-soft | 改寫 | 對比問題本身已不存在；只剩「把 primary-soft 加進守門」小工作（小） | 無 |
| 23 | 244 | 日誌類 store 查詢窄化綁寫入時間 | 無法判定 | 未逐一查稽核／執行紀錄 store 的窄化條件，需讀各 store | 保留 | 未深查 | 無 |
| 24 | 249 | 測試時鐘與時區抽象 | 部分 | 現況：測試 165 檔／1,063 處 DateTime.Now（相符）；產品碼 DateTime.(Now/Today/UtcNow) 368 處（敘述 380）；HandoffClockGuardTests 存在 | 改寫 | 更新數字 | 無 |
| 25 | 254 | Linux 平台差異 | 仍成立 | ci.yml matrix 僅 windows-latest、macos-latest；`\r\n` 字面共 186 處（含測試，敘述 148 為舊值；僅 Core+Web 為 15） | 改寫 | 更新數字並註明口徑 | 無 |
| 26 | 258 | 主機日內容大小 | 無法判定 | DB-SPEC:547 逐列讀取已落地；站台大小分布需正式資料 | 保留 | 缺正式站台資料 | 無 |
| 27 | 262 | PRTG 每日固定短暫中斷型態 | 仍成立 | 無對應規則（搜尋無命中） | 保留 | 需 28 天資料 | 無 |
| 28 | 265 | PRTG 鏡像沒有感測器單位 | 已解決 | PrtgFetchService.cs:1037 columns 含 unit；:1069 Unit = GetString(el,"unit")；PrtgDiagnosticExportSource.cs:118 匯出含 Unit | 移除 | 已於結構同步存入；舊鏡像列下次同步補值 | 無 |
| 29 | 268 | 快照待寫檔被鎖時立即顯示「已停止」 | 仍成立 | PrtgSnapshotHostedService.cs:50,1523 仍以 Unavailable 為停止原因碼，無獨立「鎖被持有」碼 | 保留 | 需正式站台回收觀察 | 無 |
| 30 | 272 | 處理成效在嚴重度全站隱藏時與總覽數字不同 | 仍成立 | ReportEffectivenessService.cs:81 套 GetVisibleSeverities；總覽走 SQL 彙總（口徑未統一） | 保留 | 少見情境 | 無 |
| 31 | 276 | 授權矩陣沒驗 AI 提示內容 | 仍成立 | Tests/Authorization/AuthorizationReadMatrix.cs 仍是授權與編號直取，無擷取提示的列 | 保留 | 無 | 無 |
| 32 | 280 | 報表 chip 在處理成效／問題類型也重載 | 仍成立 | reports.js:1103-1115 onScopeChipClick 一律 reload() | 保留 | 無 | 無 |
| 33 | 283 | 再發生率「觀察滿」以全站最後資料日判定 | 仍成立 | ReportEffectivenessService.cs:221,251 lastDataDay = loaded.Max(...) 全站 | 保留 | 無 | 無 |
| 34 | 286 | 去年同期跨閏日前期趨勢差一天 | 仍成立 | ReportService.cs:129 註解明言以本期長度為準；:325 AddYears(-1) | 保留 | 無 | 無 |

### C 區（288～426 行）

#### 記憶體用量（遞延）（288～298）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 1 | 292 | 本機路徑一次載入整個掃描區塊、無筆數上限 | 仍成立 | `AnalysisOrchestrator.cs:1066` 以 `DefaultScanChunkDays`（=14，`EventLogService.cs:388`）分塊，區塊內無筆數上限 | 保留 | 觸發條件（memoryEnd 明顯偏高）仍合理；可補寫「區塊 14 天」 | 無 |
| 2 | 293 | PRTG 狀態變更單日無筆數上限 | 仍成立 | `PrtgDailyPipeline.cs:1404` `PrtgStateChangeWindow` 仍存在，逐日讀、不分批 | 保留 | 需動 `PrtgRuleEvaluator` 輸入形狀，無實測觸發 | 無 |
| 3 | 294 | PRTG 範圍檢查點呼叫次數未節流 | 仍成立 | `PrtgClient.cs:100` `Checkpoint` 於 273/281/303/478/491/506/553/591 多處呼叫、4096 位元組讀取迴圈（587）仍在 | 保留 | 取消靈敏度為產品行為 | 無 |
| 4 | 295 | FetchValuesFirstRound 每顆 sensor 建一個 task 再限流 | 仍成立 | `PrtgFetchService.cs:1413-1433` `Enumerable.Range(...).Select(async …)` + SemaphoreSlim | 保留 | 觸發條件（十萬顆）遠超現況 | 無 |
| 5 | 296 | 互動 AI 的 AIService 換設定後 2 分鐘才釋放 | 仍成立 | `SettingsBoundClient.cs:46` 預設 2 分；互動用戶端逾時固定 8 秒、chat 固定 60 秒（`WebAiService.cs:128-160`），非設定可調 | 改寫 | 前提「互動逾時設定被放寬」目前無對應設定入口（`AiTimeoutSeconds`=1200 屬背景批次），可改寫成「互動逾時為程式常數 8/60 秒，改常數超過 120 秒時同步調 `disposeGrace`」 | 無 |
| 6 | 297 | 記憶體標記峰值為行程累計、無行程識別 | 仍成立 | `MemorySnapshot.cs:71` 用 `proc.PeakWorkingSet64`；全 repo 無 `ProcessStartedAt` | 保留 | 觸發條件（第一晚數據不足）仍有效 | 無 |
| 7 | 298 | 稽核查詢有篩選時讀整段範圍再篩 | 部分 | `AuditLogStore.cs:62-125`：已加「無起日預設 90 天下界」`DefaultRangeApplied`，篩選仍記憶體過濾＋排序 | 改寫 | 補寫已有 90 天預設下界，剩餘問題限「自選長區間」 | 無 |

#### NetIQ 整合：試點驗證與遞延項目（300～355）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 8 | 302-315 | NetIQ 接線：取數與 pipeline 已實作、尚未經真實 Sentinel 端到端驗證 | 無法判定 | 類別存在（`SentinelFieldMap`、`NetiqPipelineService.cs:25`、`NetiqOptions.MaxParallelServers`=2、`MissingDateFinder`）；真實驗證需正式 Sentinel | 改寫 | 「探索方案…已解決：改用…」整句是過程敘事，應移到 NETIQ-API-REFERENCE §3.4，BACKLOG 只留「尚未實機驗證」與編號清單 | 有（「已解決：改用」、「現有…回答不了」） |
| 9 | 316 | 1. sev Warning/Error 門檻為候選值 | 無法判定 | 需真實 Sentinel | 保留 | 試點才能驗 | 無 |
| 10 | 317 | 2. Defender/RDP Operational 頻道有無進 Sentinel | 無法判定 | 需真實 Sentinel | 保留 | 同上 | 無 |
| 11 | 318-319 | 3. Linux 欄位與 sev 對應（已完成） | 已解決 | 項目文字自述已完成；`NETIQ-API-REFERENCE.md` §4a、`LINUX-RULES.md` 在 | 移除 | 「編號保留」的空殼，刪除並讓後續編號不再被引用（先確認無處引用 3.） | 有（「已完成，編號保留」） |
| 12 | 320-322 | 4. 真實 watchlist 查詢耗時與命中量；timing collector 不含 Sentinel 查詢 | 無法判定 | `NetiqHostDayTimingCollector.cs` 仍在；需真實環境 | 保留 | 試點量測項 | 無 |
| 13 | 323 | 5. dt 時間邊界人工核對 | 無法判定 | 需 Sentinel Web UI | 保留 | | 無 |
| 14 | 324 | 6. 8.5 apidoc 有無聚合端點 | 無法判定 | 需廠商文件／實機 | 保留 | | 無 |
| 15 | 325-329 | 6b. NOT 子句是否被 Lucene 接受；已加偵測 | 部分 | `NetiqDirectoryClient.cs:711` 「NOT 排除語法…未生效」警告已實作；真實環境是否曾出現無法在此驗證 | 保留 | 偵測已就位，剩試點核對警告 | 無 |
| 16 | 330 | 7. 多網卡主機回報哪個 IP | 無法判定 | 需實機 | 保留 | | 無 |
| 17 | 331-332 | 8. token 實際壽命、主動換發值不值得做 | 部分 | 被動重認證已做：`SentinelClientTests.cs:182` 401 重放測試 | 保留 | 剩未知＝實際壽命，需實機 | 無 |
| 18 | 333-336 | 9. 2000 台缺漏日判定總耗時 | 仍成立 | `HostDayPostProcessor.cs:19-23` 每台一次 `store.ReadRecent`；`NetiqPipelineService.cs:383,486` 呼叫 | 保留 | 需規模實測 | 無 |
| 19 | 337-338 | 10. Security 頻道未知失敗 ID 不會被撈入；`NOT xdasoutcome:0` 待評估 | 仍成立 | `SentinelFieldMap.cs:70` 僅宣告 `XdasOutcome`，查詢端無使用 | 保留 | 待評估項 | 無 |
| 20 | 339-341 | /26 的 128 子句未實測 | 無法判定 | `NETIQ-API-REFERENCE.md:133` 仍列子句數申報；需實機 | 保留 | | 無 |
| 21 | 342-344 | 掃描工作多行程情境未處理 | 仍成立 | `NetiqDiscoveryService.cs:489-501` Jobs 為行程內 `ConcurrentDictionary` | 保留 | 單行程部署前提不變 | 無 |
| 22 | 345-349 | NetIQ 取數以整個 job 事件桶為單位 | 仍成立 | `NetiqPipelineService.cs:638-772` `eventsByIp` 整批持有；`NetiqOptions.MaxResultsPerJob`=100_000 | 保留 | 觸發條件仍合理 | 無 |
| 23 | 350 | CandidateArrayKeys 猜測式解析 | 仍成立 | `SentinelClient.Paging.cs:88,209` 仍遍歷候選鍵 | 保留 | 待實機外層鍵 | 無 |
| 24 | 351-352 | 掃描精靈下拉仍列解不開密碼的 Sentinel | 仍成立 | `netiq-import-wizard.js:29` 只篩 `active && canDiscover`，檔內無 `credentialState`（`netiq.js:62` 已處理清單頁） | 現在處理 | 小：一小時內，下拉改讀 `credentialState==='undecryptable'` 標註並停用；`SentinelDtos.cs:19` 已提供欄位。也可維持「碰壁才做」保留 | 無 |
| 25 | 353-355 | NetIQ 主機缺體檢輪巡、跨主機關聯、機房總覽報告 | 仍成立 | WEB-SPEC §3237 體檢以本機主機與本機紀錄為對象；`AnalysisOrchestrator.LocalCheckupSkipReason`；無 NetIQ 體檢程式 | 保留 | 使用者決策項；末句「見〈NetIQ 接線〉」可刪 | 無 |

#### AI 整合（357～384）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 26 | 359 | AI 摘要用語過重 | 無法判定 | 提示詞與模型行為，需使用者回報／換模型 | 保留 | 觸發條件明確 | 無 |
| 27 | 360-366 | MCP server 供外部 AI 客戶端 | 仍成立 | 全 repo 無 MCP server 實作（grep 無命中） | 改寫 | 前半「評估時結論是不採…」是決策歷程，可精簡為一行現況：「未提供 MCP server；觸發＝出現明確外部 AI 客戶端使用情境」，理由留在 WEB-SPEC §9.3 | 有（「評估…結論是不採」） |
| 28 | 368-373 | AI 用量表缺「AI 整理件數」欄 | 仍成立 | `AiUsageDtos.cs:47-72` 僅有呼叫與 token 欄；無件數欄 | 改寫 | 「規劃 B2 契約寫…」為規劃敘事，改成「用量表以呼叫數呈現，無『AI 整理件數』（計量點在 AIService 看不到件數）」 | 有（「規劃 B2」「實作只有五欄」） |
| 29 | 374-375 | callsWithoutUsage 有進 DTO 但畫面未逐日用 | 仍成立 | `AiUsageDtos.cs:47,62`；畫面僅 `settings.js:354` 讀 `usage.total.callsWithoutUsage` | 保留 | 小且低價值 | 無 |
| 30 | 376-378 | 問題簽章級白話說明快取 | 仍成立 | 全 repo 無簽章級快取（`AiCacheStore` 為 Web 加值輸出快取） | 保留 | 低優先 | 無 |
| 31 | 379-381 | AI 設定頁「測試連線」未做 | 已解決 | `Settings.cshtml:113` `#ai-test-btn`；`settings.js:1817` AI 測試連線；`SetupController.cs:38` 同步探測（10 秒） | 移除 | 已實作，描述「只做儲存驗證」已不符 | 無 |
| 32 | 382-383 | 格式異常降級補跑無每日節流 | 仍成立 | `AiAnalysisRunState.cs` `BackoffBase`=5 分、`BackoffMax`=15 分；`AiDegradation.MaxAttempts`（`EfAnalysisRecordStore.cs:727`）；無每日限制 | 保留 | 觸發條件明確 | 無 |
| 33 | 384 | AI 快取整份 blob 讀寫 | 仍成立 | `AiCacheStore.cs:5-11` `JsonBlobCollection`、`MaxEntries=500` | 保留 | 未排期 | 無 |

#### 偵測規則與分析判定（386～426）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 34 | 388-391 | KeyDetails 字串剖析留作舊資料 fallback | 仍成立 | `ResidualCredentialDetector.cs:225` `issue.KeyAccounts ?? ExtractAccountsFromKeyDetails(...)`；`CorrelationAnalyzer.cs:412 ExtractIps`；`RawEventRetentionDays` 存在 | 保留 | 保留期（預設 120 天）過後可移除 | 無 |
| 35 | 392-394 | 殘留判定門檻無設定入口 | 仍成立 | `ResidualCredentialDetector.cs:41-48` 皆 `private const` | 保留 | 紅線取捨未變 | 無 |
| 36 | 395-397 | 舊資料截斷失真 | 仍成立 | 同檔「滿 50 組且無總量」保守判定仍用於舊資料（隨保留期淘汰） | 保留 | 可與 #34 合併為「舊資料相容」一條 | 有（「早期寫入」輕度） |
| 37 | 398-401 | 4768/4769/4776 刻意不加規則 | 仍成立 | `KnownIssueSeed.cs` 無這些 ID | 保留 | 規則模型缺 EntryType 維度的前提未變 | 無 |
| 38 | 402-405 | 4688/4663/5140/5145/4648/4616/4104 不加 | 仍成立 | `KnownIssueSeed.cs` 無這些 ID | 保留 | | 無 |
| 39 | 406-407 | Linux passwd/chage 不納入 | 仍成立 | seed 只有 gpasswd（`KnownIssueSeed.cs:919`），無 passwd／chage | 保留 | | 無 |
| 40 | 408-413 | 抑制影響面預覽泛型化 | 仍成立 | `RuleAdminService.cs:955-963` 非 Rule 型限 Host 範圍；`PreviewSuppression` 仍僅 Rule 型 | 改寫 | 敘述正確；「回饋十六輪」不在此條，無需動，只可再精簡 | 無 |
| 41 | 414-420 | SlowTrendAnalyzer 離群值敏感度 | 仍成立 | `SlowTrendAnalyzer.cs:86-91,115` 仍是兩窗口總和比較、`priorTotal > 0` 守門 | 保留 | 有實際誤判案例再議；可精簡後半論述 | 無 |
| 42 | 421-424 | 7-S2 殘餘：blob header 誤判陷阱 | 已解決 | 現行 `HandlingBlobMigrator.cs:286 HasBlobContent` 只判 `!IsNullOrWhiteSpace(Read())`，沒有 header 判斷；`EfJsonBlobStore.Exists()`（56）以資料列存在判定；`[]` 屬有內容，不重疊 | 移除 | 前提（header 特徵）在現行程式找不到；信心中等，因原審查引用的具體位置不明 | 無 |
| 43 | 425-426 | Linux 白話說明以 program 前綴粗略比對 | 部分 | `KnownIssueCatalog.cs:460-478` `FindLinuxRuleByProgram` 命中兩條以上回 null（不再配錯說明），只有「說不出」；問題名稱與風險報告走 Id | 改寫 | 「可能配錯」已不成立，改為「同 program 多條規則時不給白話說明」 | 無 |

### D 區（427～634 行）

### 區 D 結果（BACKLOG.md 427～634，HEAD 7e7d3950）

狀態縮寫：已解決／仍成立／部分／無法判定。敘事字眼欄：有＝混入過程敘事。

#### 報表、問題清單與主機分組（427～510）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 1 | 429 | 本機主機無「首次分析前預先建檔＋分組」批次途徑 | 仍成立 | `ImportKind.Hosts` 仍標【已退役】（ImportModels.cs:14），本機主機仍由批次 Touch 登錄 | 保留 | 觸發條件（有大批需求）未出現 | 輕微（「退役」「已知損失」） |
| 2 | 436 | 問題排行「顯示範圍」固定排除已有結論、無切換器 | 仍成立 | `IssueRankingBuilder.ExcludeConcluded`（:318）仍被 DashboardService:208、ReportService:227 固定呼叫；前端無切換 | 保留 | 行為如述；括號內實作出處可精簡 | 輕微（「已由…接上」） |
| 3 | 444 | MTTA／MTTR 未呈現 | 仍成立 | 全 repo 無 MTTA/MTTR 消費端；`IssueHandling.CreatedAt` 僅 INSERT 落值（LfDbContext.cs:393，註解自稱「本輪不建查詢消費它」）；`IssueCase.ClosedAt` 存在（IssueCase.cs:59） | 改寫 | 首句說「仍缺結案時間欄」與下文「資料基礎已具備（案件結案時間軸）」互相矛盾，應改成只講待決四問題；報表「處理成效」（ReportEffectivenessService）已有帳齡與再發生率 | 有（標題「…指標輪」） |
| 4 | 454～457 | `IssueRankingCache` 鍵不含 `hostSnapshot`（含前置註記，SummaryCache 仍在） | 仍成立 | `KeyOf` 無 hostSnapshot（IssueRankingCache.cs:46）；`Build` 的 hostSnapshot 仍是可選參數（IssueRankingBuilder.cs:110、:177 null 時 `_hosts.GetAll()`）；兩呼叫端皆傳 visibleHosts；SummaryCache.cs 仍在 | 保留 | 仍只有兩個呼叫端，風險未顯現 | 無 |
| 5 | 461 | 快取回傳淺副本，DTO 共用 | 部分 | 現 `TryGet`／`GetOrAdd` 回 `IReadOnlyList`（IssueRankingCache.cs:57-63），清單層已不可改；`IssueRankingDto` 欄位仍是可寫 setter（DashboardDtos.cs:126起） | 改寫 | 風險縮到「DTO 欄位可被改」，把「複製清單」那半句刪掉 | 無 |
| 6 | 464 | 報表非全部顯示範圍仍記憶體推導 | 仍成立 | ReportService.cs:187-211 仍 `QueryLightweight`（高／中下推）再 `_handling.FilterByScope` | 保留 | 與第 9 項同一件事，兩條可合併 | 無 |
| 7 | 468 | 一次報表請求 4 次整份主機表走訪 | 仍成立 | ReportServiceTests.cs:975-993 `MaxHostGetAllPerSummary = 4`，兩條測試守門 | 保留 | 契約上限仍是 4 | 輕微（測試註解有「改動前是 13 次」，但 BACKLOG 本文沒有） |
| 8 | 471 | 群組風險概況列連結與「未處理」欄口徑不同 | 仍成立 | dashboard.js:919 `rowHref` 仍是 `/records?groupIds&riskLevels=高,中`，無 `statuses=open`；欄位仍是 `unhandledCount`（:916） | 保留 | 需版面動線決策 | 無 |
| 9 | 475 | `scope != all` 報表路徑仍在記憶體 | 仍成立 | 同第 6 項，ReportService.cs:199-211 | 改寫 | 與第 6 項合併成一條，避免兩處重複敘述 | 無 |
| 10 | 479 | 有合併主機時問題聚合逐列物化 | 仍成立 | `NoMergedHostsInScope`（EfIssueAggregateQuery.cs:582、589、1920）仍是慢路徑判斷 | 保留 | 觸發條件需正式站台量測 | 無 |
| 11 | 483 | 記錄查詢預設下界 90 天 | 仍成立 | RecordListQueryService.cs:250 `DefaultLookbackDays = 90`、:245 | 保留 | 行為如述 | 無 |
| 12 | 487 | 「未處理問題」KPI 下鑽母體不同 | 仍成立 | dashboard.js:372 下鑽仍用 `view=issue&statuses=open&riskLevels=高,中,低`（嚴重度語意），卡片母體仍是 ActionableOccurrences、列表為 LatestOccurrences；未新增獨立日風險參數 | 保留 | 需產品決策 | 無 |
| 13 | 504 | 依問題視角日風險子查詢重複 | 部分 | 索引已有（SchemaUpgrader.cs:188、LfDbContext.cs:306）；`ApplyRiskLevels` 現在在 EfIssueAggregateQuery 有 10 處呼叫（:178、250、397、703、772、1788、2445、2482、2519…） | 改寫 | 刪掉「索引部分已解決」，只留「同子查詢多次」並把「四次」改成「多次」 | 有（「已解決」） |

#### 交辦與派工（511～553）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 14 | 513 | 使用者名稱格式剩餘敘事句 | 部分 | 歷程「處理人：」已走 `NameFormat.UserLabel`（HandlingHistoryQueryService.cs:88-103）；跨主機略過清單 `HandlerNames` 已走 UserLabel（DayHandlingCommandService.cs:412-419）；只剩案件敘事「由 ○○ 處理」仍是裸 DisplayName（RecordDetailQueryService.cs:324、506；record-detail.js:739） | 改寫 | 三個子項已解決兩個，只留「案件歷史／詳情的 handlerName」；小改（兩處 `_users.Get(..)?.DisplayName` 換 `NameFormat.UserLabel`）可順手做 | 有（「§9 未竟」「使用者實測後」） |
| 15 | 518 | 無案件的跨日觀察 | 仍成立 | `AttachNewDay` 只掛進行中案件與 AutoApply 機房結論（IssueCaseCoordinator.cs:174-270），無主機×簽章記憶 | 保留 | 需求未明 | 無 |
| 16 | 522 | 被指派歷程不含已被改派走的案件 | 已解決 | `UserAdminService.BuildAssignmentHistory` 以 `_handlings.GetReassignments(userId, cutoff)` 反查 `case_reassign`，補出「已改派給 X」列（UserAdminService.cs:197-215）；`EfRecordHandlingStore.GetReassignments`（:186）、`RecordHandlingLog.PreviousHandlerId`；歷程仍以案件為主、改派列來自歷程表 | 移除 | 唯一缺口是舊紀錄無原處理人者「不猜測」（保留期內）；若要記可改成一行備註放「明確不做」附近 | 無 |
| 17 | 528 | 多問題交辦單畫面入口 | 仍成立 | `CreateWorkOrderRequest` 仍只有單一 `Source`／`EventId`（WorkOrderDtos.cs:11-17） | 保留 | 觸發未出現 | 無 |
| 18 | 530 | 交辦單依主機群組篩選 | 仍成立 | `WorkOrderListRequest.GroupId` 仍是「處理人所屬使用者群組」（WorkOrderDtos.cs:350） | 保留 | 觸發未出現 | 無 |
| 19 | 532 | 交辦單操作殘餘並行競態 | 部分 | 改派、取消、回覆、拆單等全在同一把 static `OrderMutationLock`（WorkOrderCoordinator.cs:44、433、776 等），第一個殘留（改派撞唯一索引）在單行程內已被序列化；背景逐日同步 `IssueCaseCoordinator` 無此鎖（該檔無 lock），第二個殘留（取消與逐日同步交錯）仍在 | 改寫 | 把敘述改成「改派／取消已共用鎖；殘留僅背景逐日同步未取鎖＋多行程」 | 輕微（「已改為」） |
| 20 | 537 | 多單寫入沒有整體交易 | 仍成立 | WorkOrderReplyService.cs:112-135 逐張 `_coordinator.Reply`，中途失敗 break 並回報已寫入 | 保留 | 回應已交代成功／失敗／未處理，屬已知取捨 | 無 |
| 21 | 539 | 交辦單回填與協調層並行 | 仍成立 | WorkOrderBackfiller.cs:144-147 階段二 `.ToList()` 一次讀全部候選 | 保留 | 觸發未出現 | 無 |
| 22 | 541 | 待派試跑沒有模擬 ① 與 ③ | 部分 | WEB-SPEC.md:1853 仍寫「不模擬 ① 與 ③」；但 `RunTrial` 已加「最近出現日已被處理人結案者整筆不進結果」（WorkOrderBoardService.cs:700-714、:732），涵蓋了觸發條件所指「已有人標過結論」的主要情境 | 改寫 | 縮窄成「仍不模擬：當日有非結案標記（如觀察中）與 AutoApply 機房結論」；觸發條件改成這兩種 | 無 |
| 23 | 543 | 明確不做（處理人變更歷史查詢、站內即時通知、負載加權與個人容量上限、後端 CSV 匯出端點、靜音主機群組範圍） | 仍成立 | 五項皆無實作（WorkOrder 僅記最近一位 `PreviousHandlerId`，WorkOrder.cs:35；無站內通知端點；Controllers 僅 ImportsController 有 CSV 範本下載） | 保留 | 屬決策記錄，仍有效 | 無 |
| 24 | 545 | 新日掛接於案件被結案後仍可能先寫逐日列 | 仍成立 | IssueCaseCoordinator.cs:269-271：`AppendLogs`、`SaveMany(toSave)` 先於 `ReloadCasesForAttach`（:283-），重讀只擋案件列覆寫 | 保留 | 描述的「同一趟迴圈內」現為「批次累積後一次寫」，語意仍成立 | 無 |
| 25 | 548 | 日層級指派與夜間派工略過原因順序不同 | 仍成立 | DayHandlingCommandService.cs:390-393 順序（抑制→雜訊→不再打擾→靜音）vs WorkOrderDispatcher.cs:78-86（靜音在最前） | 保留 | 觸發未出現 | 無 |
| 26 | 551 | 日層級指派靜音判定多行程可能過時 | 仍成立 | DayHandlingCommandService.cs:52、377 讀 `IIssueExclusionSource.Current()`；`IssueExclusionProvider` 以 `DataVersionStamp`＋today 為鍵（IssueExclusionProvider.cs:19-31） | 保留 | 單行程不成立，條件式 | 無 |

#### 權限異動檢核（555～564）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 27 | 557 | 權限異動未存來源 IP／機器名 | 仍成立 | `PermissionChangeRecord` 無 sip／shn 欄位（Models/PermissionChangeRecord.cs 全欄位列表）；`SentinelFieldMap.Q1ProjectionFields` 在 :73 | 保留 | 需 DDL＋實機驗證 | 無 |
| 28 | 559 | 使用者自訂異動類別 | 仍成立 | `PermissionCategory` 固定六類＋Summary／Other（PermissionCategory.cs:10-19） | 保留 | 刻意不做 | 無 |
| 29 | 560 | 既有 SQL Server 庫可能留有重複索引 | 無法判定 | `RemoveDuplicateIndexes` 對 SQL Server 仍只警告（SchemaUpgrader.cs:500-502）；正式機是否真有重複索引需看升級日誌 | 保留 | 缺正式站台資料 | 無 |
| 30 | 561 | 高風險（特權目標）涵蓋範圍待議 | 仍成立 | `PermissionCategory.IsPrivilegedTarget` 仍只認 `changeType == "成員新增"` 且 Target 命中關鍵字（PermissionCategory.cs:108-127） | 保留 | 需使用者決策 | 無 |
| 31 | 562 | 批次確認上限 500 是否放寬 | 無法判定 | `MaxBatchConfirmChanges = 500`（PermissionChangeService.cs:189）；需看正式站台合併後實際待確認量 | 保留 | 觀察項 | 無 |
| 32 | 563 | 權限異動查詢側效能未實測 | 無法判定 | 需真實大量資料／SQL Server | 保留 | 缺正式資料 | 無 |
| 33 | 564 | 排序在兩 provider 定序不同 | 部分 | `OrderBy(r => r.HostName).ThenByDescending(DetectedAt).ThenBy(Id)`（PermissionChangeStore.cs:405-406），次序鍵含唯一 Id，所以同一 provider 內翻頁順序是穩定的；差別只是「大小寫排序位置」；`HostNameKey` 欄位已存在（:111、:172） | 現在處理 | 「翻頁漏列或重複」描述過重，實為顯示順序在兩 provider 不同；改成 `OrderBy(r => r.HostNameKey)` 工作量小（<1 小時），順手改並更新敘述 | 無 |

#### 郵件通知（566～576）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 34 | 568 | Email 以外的推播（Telegram／Teams） | 仍成立 | 全專案無 telegram／teams／webhook 發送碼（grep 僅命中 AiAnalysisResult 文字） | 保留 | 無需求 | 有（「Email 部分已解決」「重構」，可刪） |
| 35 | 573 | 三份 `Recheck` 刻意不合一 | 仍成立 | MailNotificationService.cs:1290、1439、2168（RecheckDigest）三份各自規則 | 保留 | 刻意決策 | 無 |
| 36 | 574 | 帳本 attempted／unknown 不自動釋放 | 仍成立 | MailNotifyStateStore.cs:64、119、135、150 狀態機；無 `AttemptedAtUtc` | 保留 | 觸發未出現 | 無 |
| 37 | 575 | 郵件貢獻帳本管理出口與結構 | 仍成立 | 無釋放端點／逾時釋放／blob 改表（僅 :1712 內部釋放「已準備」預約） | 保留 | 條件未出現 | 無 |
| 38 | 576 | 寄前重核逐收件人重查整個窗口 | 仍成立 | MailNotificationService.cs:1290-1318 `Recheck` 每收件人 `_records.Query` 整窗口 | 保留 | 量測前不動 | 無 |

#### 報告全文改存資料庫（遞延）（578～585）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 39 | 580 | 報告全文不壓縮 | 仍成立 | `lf_reports.content` 純文字；無 gzip（Persistence/Sql 內無相關） | 保留 | 容量估算仍合理 | 無 |
| 40 | 583 | 舊 `export\` 檔案不自動刪除 | 仍成立 | ReportFileMigrator.cs:124 「舊檔保留未刪，僅作備份」 | 保留 | 一次性遷移備註 | 有（「遷移後」） |
| 41 | 584 | 既有檔名碰撞舊資料不修復 | 仍成立 | 遷移器忠實搬入（同上） | 保留 | 一次性歷史資料備註，可考慮縮成一行 | 有（「升級前」「升級後」） |

#### 效能與資源（需量測）（587～634）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 42 | 589 | 慢查詢與系統健康：缺 PRTG 批次寫入埋點時再補 | 仍成立 | `TopSlowCapacity = 10`（SqlPerformanceMonitor.cs:37）；入口確實存在 | 改寫 | 句子主要在說「不列待辦」，可縮成一行條件式（缺埋點才補） | 無 |
| 43 | 592 | SqlServer 未實測 | 無法判定 | 測試僅 SQLite，需正式 SQL Server | 保留 | 缺正式環境 | 無 |
| 44 | 596 | 效益未實機量測（`blob:hosts:Read/ReadVersion`，前端三頁未瀏覽器實測） | 部分 | `blob:{key}:Read／ReadVersion` 埋點仍在（EfJsonBlobStore.cs:51、74）；但「前端三頁只做 JS 語法檢查」已過時：現有 `UiTests`、`*.behavior.mjs`、UI 資料集＋截圖量測（CLAUDE.md 慣例） | 改寫 | 刪除前端未實測那半句，保留「正式站台 blob 讀取次數需實機確認」 | 輕微（「本來」） |
| 45 | 601 | 夜間掛接逐主機日寫入 | 仍成立 | `WriteNightlyMembersLocked` 仍逐主機日在鎖內寫（WorkOrderCoordinator.cs:631）；AttachNewDay 內 SaveMany 已批次（IssueCaseCoordinator.cs:269-271）但新成員寫入仍逐日 | 保留 | 觸發條件（逼近窗口）未出現 | 無 |
| 46 | 603 | `JsonBlobSingleton` 每次 Get 查版本 | 仍成立 | JsonBlobSingleton.cs:55 `_blob.ReadVersion()` 每次 `Get` | 保留 | 未普查 | 無 |
| 47 | 605 | `NextUnhandledSequenceCache` 鍵無靜音權杖 | 仍成立 | `KeyOf` 只含主機、嚴重度、日風險（NextUnhandledSequenceCache.cs:38-46）；版本戳＋TTL 30 秒兜底 | 保留 | 觸發未出現 | 無 |
| 48 | 607 | 靜音排除在 SQL Server 大表執行計畫 | 無法判定 | 需正式機慢查詢 | 保留 | 缺正式環境 | 無 |
| 49 | 609 | 已結案案件延續性查詢無前導索引 | 仍成立 | `GetResolvedSince` 以 `ClosedAt >= since && Status == Resolved`（EfIssueCaseStore.cs:159-167）；LfDbContext.cs:427-442 無以 ClosedAt 為前導的索引 | 保留 | 觸發（案件表數十萬）未出現 | 無 |
| 50 | 611 | 統一標記逐筆多查進行中案件 | 仍成立 | IssueHandlingCommandService.cs:266、292 逐筆 `_cases.GetOpen`；上限 `MaxBulkCloseDayWrites = 5000`（:522） | 保留 | 觸發未出現 | 無 |
| 51 | 613 | NetIQ 匯入預覽一次畫出全部列 | 仍成立 | imports.js:167-177 `renderTable(... rows: currentPlan.rows)` 無分頁 | 保留 | 觸發（>1000 台）未出現 | 無 |
| 52 | 615 | 統一標記 5000／待派試跑 20,000 上限 | 仍成立 | IssueHandlingCommandService.cs:522；WorkOrderBoardService.cs:31 `DefaultMaxOccurrences = 20000` | 保留 | 數字與行為皆符 | 無 |
| 53 | 617 | 卡在「PRTG 待補」的過去主機日不自動回復 | 無法判定 | 無一次性對帳碼（grep 無）；實際是否仍有大量需正式站台；現版本已是 1.0.81 | 改寫 | 敘述寫死「升級到 1.0.77」，改成不綁版號的條件式 | 有（「升級到 1.0.77 之前已被重設」） |
| 54 | 619 | 問題查詢展開每次換頁重讀主機日 | 仍成立 | RecordListQueryService.cs:871 `IssueHostDays` 每次呼叫、無快取 | 保留 | 觸發未出現 | 無 |
| 55 | 621 | 執行總表展開某日每頁重建全部主機列 | 仍成立 | RunMonitorService.cs 無快取（grep cache 零命中） | 保留 | 觸發未出現 | 無 |
| 56 | 623 | 待派試跑下界在孤兒主機或解析失敗時偏高 | 仍成立 | WorkOrderBoardService.cs:656 `lowerBound = Sum(HostCount) - openKeys.Count`，無扣孤兒 | 保留 | 保守方向，觸發未出現 | 無 |
| 57 | 625 | 父指紋重算與 `LegacyParentFingerprints` | 仍成立 | HostDayWorkflowState.cs:649、1112、1273 | 保留 | 版本已 1.0.81，保留期是否滿需正式站台時間；條件未到 | 無 |
| 58 | 627 | 建單改背景作業 | 仍成立 | WEB-SPEC.md:2070 建單約 2.2 秒、仍同步 | 保留 | 觸發（>30 秒）未出現 | 無 |
| 59 | 628 | 背景服務與前景共用連線池 | 仍成立 | 無 keyed DI（grep `AddKeyed*` 零命中）；四支服務仍注入共用 `StorageBackend` | 保留 | 觸發需正式 SQL Server | 無 |
| 60 | 629 | `OwnedRows` 的 `host_id OR UPPER(host_name)` 無法用索引 | 仍成立 | EfAnalysisRecordStore.cs:86-93 原樣 | 保留 | 需實機先查同名列數 | 無 |
| 61 | 630 | PRTG／NetIQ 無條件 catch OCE | 仍成立 | PrtgDailyPipeline.cs:299、345、551、717、826、PrtgScopeDevices.cs:317 仍無 `when (ct.IsCancellationRequested)` | 保留 | 條件式備忘，用戶端已轉換逾時 | 無 |
| 62 | 631 | 日層級指派逐問題建單 | 仍成立 | DayHandlingCommandService.cs:427-470 逐候選呼叫 `_workOrders.Create` | 保留 | 先量測 | 無 |
| 63 | 632 | SQL Server 不分大小寫定序下的出現點分組 | 無法判定 | 需確認正式環境是否為 SQL Server 與定序 | 保留 | 缺正式環境 | 無 |
| 64 | 633 | `BatchRunStore` 查舊執行全表串流掃描 | 仍成立 | BatchRunStore.cs:465、567 `EnumerateLines(null, null)` | 保留 | 觸發未出現 | 無 |

#### 統計
- 狀態：已解決 1、部分 7（5、13、14、19、22、33、44）、無法判定 7（29、31、32、43、48、53、63）、仍成立 49。共 64 條（第 6、9 兩條為同一議題，建議合併）。
- 建議：移除 1、改寫 10（3、5、9、13、14、19、22、42、44、53）、現在處理 1（33）、保留 52。

### E 區（635～765 行）

#### 設計面債務（未排期）（635～745）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 1 | 637 | 其他控制器 [FromQuery] 未收斂 | 部分 | AdminController 21 個（非 20）、Audit 9、Rules 7、Handling 0 | 改寫 | 數字微調 20→21，其餘仍成立 | 有「已改」 |
| 2a | 639 | SchemaUpgrader 版本化遷移 | 仍成立 | 無 SQL Server 環境（ci.yml 無 mssql） | 保留 | 觸發條件未達 | 無 |
| 2b | 640 | PrtgSnapshotJournal 換 SQLite | 仍成立 | Web/Services/PrtgSnapshotJournal.cs 仍在 | 保留 | 無損壞回報 | 無 |
| 2c | 641 | mini-dom 換 jsdom | 仍成立 | Tests/JsBehavior/mini-dom.mjs 仍在 | 保留 | 無 | 無 |
| 2d | 642 | 授權 filter 換 policy | 仍成立 | Filters/PermissionAttribute.cs、RequireViewAllAttribute.cs 仍是 filter | 保留 | 無 | 無 |
| 2e | 643 | LDAP 換 Protocols | 仍成立 | csproj 仍用 System.DirectoryServices(.AccountManagement) | 保留 | 無 | 無 |
| 2f | 644 | 帳本／outbox 改資料表 | 仍成立 | outboxDropped 仍在 HealthAttention | 保留 | 無 | 無 |
| 2g | 645 | 密文綁定欄位 | 仍成立 | CryptoHelper 無欄位綁定 | 保留 | 需全庫重加密 | 無 |
| 2h | 646 | PRTG 認證改標頭 | 無法判定 | 需 PRTG 實機 | 保留 | 缺實機 | 無 |
| 2i | 647 | 權限異動去重鍵加唯一約束 | 仍成立 | LfDbContext.cs:607 DedupeKeyHash 僅一般索引，唯一的是 ChangeId | 保留 | 無 | 無 |
| 2j | 648 | SQL Server 容器測試 | 仍成立 | .github/workflows/ci.yml 無 SQL Server 服務 | 保留 | 無 | 無 |
| 3 | 649 | 詢問 AI 現場事件快取是手寫靜態字典 | 仍成立 | SentinelEventFetchService.cs:41 ConcurrentDictionary、:90 滿了 Clear、:81 Gate.WaitAsync(0) | 保留 | 理由與現況一致 | 無 |
| 4 | 650 | 行內切字串約 55 處 | 仍成立 | TruncationSourceGuardTests 基準表加總 = 55 | 保留 | 數字吻合 | 無 |
| 5 | 651 | RunLogQueue.WriteInline 靜態旗標 | 部分 | RunLogCapture.cs:130 仍在；`new BatchRunRecorder(` 測試端現為 78 處（非約 60） | 改寫 | 只需改數字 | 無 |
| 6 | 652 | 豁免記錄與警告共用 2,000 額度 | 仍成立 | RunLogCapture.cs:121 DefaultCapacity=2000，:172 單一計數 _unwrittenCaptured | 保留 | 無 | 無 |
| 7 | 653 | IPv6 擷取全數字含 :: 字串 | 仍成立 | IpExtractor 候選過 IpSyntax.TryParse，`1::2` 語法合法 | 保留 | 無 | 無 |
| 8 | 654 | 救援帳號帳號維度鎖定 | 仍成立 | LoginThrottle.cs:25 AccountThreshold=10、BlockDuration 5 分 | 保留 | 無 | 無 |
| 9 | 655 | 權限異動讓背景輪詢分頁重得閒置時限 | 仍成立 | CookieSessionEvents.cs 註解：就地更新走 ShouldRenew，不受背景標頭影響 | 保留 | 無 | 無 |
| 10 | 656 | NetIQ 回執寫入失敗只留警告 | 仍成立 | NetiqPipelineService.cs:351、:375 只 Log.Warn | 保留 | 無 | 無 |
| 11a | 657 | 數字欄位接受 0x10／1e3、不看 min/max | 仍成立 | core/number-field.js 以 Number(raw) 轉換，無 min/max | 保留 | 小；一小時內可改，但無使用者回報 | 無 |
| 11b | 657 | hash 開交辦單頁籤載入兩次 | 部分 | work-orders.js:1746 bindTabs 以 hash 觸發 loadTab，:1750 `!loadedTabs.has` 再載；active 頁籤已在 :301 同步預標而避開，load(:581)／gaps(:766) 的 add 在 await 之後，hash 指向這兩頁仍重複 | 改寫 | 縮成「#load／#gaps」；小 | 無 |
| 11c | 657 | 執行紀錄頁手動更新後輪詢最晚 10 秒才切 3 秒 | 仍成立 | runs.js:1395 間隔函式依 scheduleAnyRunning，下一輪才重算 | 保留 | 無 | 無 |
| 11d | 657 | 執行告示輪詢失敗時告示被清掉 | 仍成立 | layout.js:671-683 失敗時 activity=null 仍 renderRunActivity | 保留 | 無 | 無 |
| 11e | 657 | 整頁導覽被登出時登入頁無原因 | 仍成立 | CookieSessionEvents.cs:140 整頁 302 不帶原因；原因只在 api.js 401 路徑存 sessionStorage | 保留 | 無 | 無 |
| 12 | 658 | SourceKeyOf 固定長度硬切 | 仍成立 | Models/WorkOrder.cs:305 `source[..SourceMaxLength]` | 保留 | 無 | 無 |
| 13 | 659 | 預設輸入上限只查第一層 | 部分 | DefaultInputLimitsFilter.cs 已含集合元素數上限（CountOf），仍不查巢狀物件與集合元素內字串 | 改寫 | 補一句「集合筆數已有上限」 | 無 |
| 14 | 660 | NU1901～1904 讓建置轉紅 | 仍成立 | Directory.Build.props:40-41 NuGetAudit；無 NuGetAuditSuppress | 保留 | 無 | 無 |
| 15 | 661 | 說明書問答問題放資料界線內 | 無法判定 | 需實際模型與正式站台比例 | 保留 | 無 | 無 |
| 16 | 662 | 偵測行為變更等資料 | 無法判定 | NETIQ-API-REFERENCE 步驟 7b 需實機回報 | 保留 | 無 | 無 |
| 17a-d | 663-667 | 四項合併（hex、啟動延遲、blob 版本、時鐘注入） | 仍成立 | 觸發條件均未發生；未逐處重數 | 保留 | 無 | 無 |
| 18 | 668 | KnownIssueCatalog static 可變狀態 | 仍成立 | Analysis/KnownIssueCatalog.cs:247-265 三個 private set 靜態屬性，Initialize 逐一覆寫 | 保留 | 折衷方案（快照原子替換，小～中）可擇期；現無症狀 | 「改為實例注入」屬評估語氣，非過程敘事 |
| 19 | 678 | visibleSeverities 仍選填 | 部分 | IIssueAggregateQuery.cs 選填 `= null` 在 80、92、148、157、169、198、209 共 7 處（非 10） | 改寫 | 更新為 7 處 | 無 |
| 20 | 683 | UpsertFirstSeen SQLite 日期字串比較 | 仍成立 | EfAnalysisRecordStore.cs:337、:398 `first_seen > {0}` 原生 SQL | 保留 | 無 | 無 |
| 21 | 687 | 批次4 專案外觀 | 仍成立 | repo 根無 LICENSE | 保留 | 使用者已定案 | 「批次 4」為歷史代號 |
| 22 | 689 | 來源鍵長來源截斷未全面一致 | 仍成立 | 與 #12 同源 | 保留 | 可與 #12 合併 | 無 |
| 23 | 696 | SQLite upper() 僅 legacy fallback | 仍成立 | SchemaUpgrader.MergeIssueFirstSeenSeed 等仍存在 | 保留 | 無 | 無 |
| 24 | 704 | 五個 RunState 抽基底（已否決） | 仍成立 | SchedulerRunState、AiAnalysisRunState、Prtg*、NetiqProbeRunState 皆在 | 保留 | 否決備忘 | 「已否決」＋解決過程，屬決策紀錄 |
| 25 | 710 | 並行負載不穩定測試 | 仍成立 | 三個測試檔仍在；無法重現紅燈 | 保留 | 無 | 無 |
| 26 | 718 | UI 字串測試偵測不到形狀問題 | 仍成立 | 兩測試檔仍在；未重數 Contains 次數 | 保留 | 無 | 無 |
| 27 | 723 | 已停寫的 PRTG 欄位 | 仍成立 | 正式碼無讀寫（僅 SchemaUpgraderPrtgTests、DB-SPEC） | 保留 | 無 | 無 |

##### 首見日的「完整重算」入口（725～733）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 28 | 727-733 | force=true 沒有使用者入口 | 仍成立 | StorageBackend.cs:397 force 參數；唯一正式呼叫 IssueFirstSeenSeedHostedService.cs:70 不帶 force | 改寫 | 敘事偏多（「改增量後」「經查證確認」），可縮成現況＋觸發條件 | 有（「改增量後」「經查證確認」） |

#### 前端與畫面（735～765）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 29 | 737 | 詢問 AI 收合時回覆無提示 | 仍成立 | pages/chat-panel.js、record-detail.js 無未讀標記 | 保留 | 無 | 無 |
| 30 | 738 | 來源觀測超過 64 筆前端靜默截斷 | 已解決 | Core/Models/SourceEvidence.cs:94 上限 64 並輸出 truncated；RecordDetailQueryService.cs:676,709 填 SourceObservationsTruncated；record-detail.js:1698 把旗標傳進摘要、:1664 顯示「清單已截斷」 | 移除 | 後端已標旗標、前端已顯示 | 無 |
| 31 | 739 | 跨午夜窗口的趟歸列 | 部分 | RunMonitorService.cs:114、:138、:140 仍用 StartedAt.Date；補跑行與複查已改 WindowDay(:261)／LiveRunDay(:212,334) | 保留 | 描述與現況吻合 | 「已改以…」略帶歷程 |
| 32 | 740 | 字級系統未做的延伸 | 仍成立 | DESIGN-SYSTEM §3a：最大 1.5 倍，無密度切換 | 保留 | 無 | 無 |
| 33 | 742 | 切字級時圖例色點不即時更新 | 仍成立 | core/charts.js:110 boxWidth 於建立時取值 | 保留 | 無 | 無 |
| 34 | 745 | 報表頁 1280×800 需捲動約 30px | 無法判定 | 需實際量測（ui-shots）；圖表 130px 下限未動 | 保留 | 缺量測 | 無 |
| 35 | 747 | 維護頁輪詢未啟用背景暫停與退避 | 仍成立 | pauseWhenHidden：runs.js 1 處、prtg-admin.js 0、netiq.js 0；pollWhile 於 prtg-admin.js:407,2650,3027、netiq.js:433 | 保留 | 與描述吻合 | 無 |
| 36 | 748 | 窗口排不進複查把安靜主機算缺漏 | 仍成立 | ReportService.cs 完整度未排除待複查；未實機驗 | 保留 | 無 | 無 |
| 37 | 749 | 複查趟里程碑數字不可比 | 仍成立 | 僅顯示用，未逐行證實 | 保留 | 無 | 無 |
| 38 | 750 | 大螢幕深色主題 | 仍成立 | css 無深色變體 | 保留 | 無 | 無 |
| 39 | 752 | 大螢幕長效登入／展示帳號 | 仍成立 | WEB-SPEC:329 24 小時絕對上限（SessionCookie.AbsoluteLifetime） | 保留 | 無 | 無 |
| 40 | 755 | 深色模式（明確不做） | 仍成立 | 同 38 | 保留 | 無 | 無 |
| 41 | 756 | PRTG 診斷匯出上限 64 MiB | 仍成立 | core/api.js:43-45 上限 64 MiB | 保留 | 無 | 「改為」略帶歷程 |
| 42 | 761 | 排程作業頁拆檔 | 部分 | runs.js 現為 2608 行（描述約 1600） | 改寫 | 更新行數 | 無 |
| 43 | 766 | 新視覺方向第二階段 | 無法判定 | 對照稿 LogForesight-design-mock 不在 ~/project（repo 外，找不到）；records.js 更多選單元件是否仍在未逐項核 | 保留 | 需使用者確認對照稿位置與是否仍預定 | 「下一輪預定」「已改成」屬歷程 |
| 44 | 770 | 218 處寫死 .cshtml 的按鈕改 button() | 部分 | Views 中含 btn 類別的 class 屬性現約 280 處 | 改寫 | 更新數字（或改寫成「約 280」） | 無 |
| 45 | 771 | alert-* 遷移共用元件 | 部分 | `alert alert-` Views 14＋JS 24＝38（描述 44） | 改寫 | 更新數字 | 無 |
| 46 | 772 | 快捷期間按鈕組外觀 | 仍成立 | core/date-range.js:52-56 只切 .active | 保留 | 無 | 無 |
| 47 | 773 | input-group 按鈕接縫較深 | 仍成立 | components.css input-group 無接縫處理 | 保留 | 無 | 無 |
| 48 | 774 | 未勾選勾選框邊界淡 | 仍成立 | base.css 只覆寫 :checked；邊線 --bs-border-color=--lf-border(.10) | 保留 | 小（<1h）可順手；無回報 | 無 |
| 49 | 775 | 舊 Bootstrap 徽章寫法 | 部分 | `badge bg-`／`text-bg-` JS 共 15 處（prtg-admin 5、prtg-calibration 5、其餘 5），非 16＋10 | 改寫 | 更新數字 | 無 |
| 50 | 776 | 切換群組補 aria-pressed | 部分 | aria-pressed 已在 ui.js、settings.js 等 19 處；date-range.js setActiveChip 仍只切 .active | 改寫 | 把「多數」改成實際剩餘清單 | 無 |
| 51 | 777 | 高風險日逐日色條 | 仍成立 | 無對應 DTO／元件 | 保留 | 無 | 無 |

### F 區（766～925 行）

#### PRTG 整合：實機校準與遞延項目（BACKLOG 766～903）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 1 | 768-771 | 現況概述＋四條趨勢規則門檻暫定（待 28 個有效日） | 無法判定 | 校準匯出 PrtgDiagnosticExportSource／PRTG-SPEC §11 還在；真實站台資料缺 | 保留 | 需正式站台累積資料 | 無 |
| 2 | 778-780 | 9d-5 逐物件查詢驗證是否含底下感測器訊息 | 部分 | PrtgProbeRunner.cs:553-650 探測已實作；`StateChangeQueryObjects` 仍在 PrtgFetchService；結論仍需實機跑 | 保留 | 工具已就緒，結論待實機 | 無 |
| 3 | 781-782 | 第一趟同步清除量與耗時（[S1]/[S2]） | 部分 | PrtgProbeSiteCheck.cs:61「其中範圍外 N 顆」、:93-99 [S2]；PrtgFetchService.cs:1256 平均每台行 | 保留 | 同上 | 無 |
| 4 | 783-784 | 資源守門找得到 PRTG／Sentinel 主機（[S3]） | 部分 | PrtgProbeSiteCheck.cs:88 [S3]；PRTG-SPEC:335 | 保留 | 同上 | 無 |
| 5 | 785-786 | 主機清單未回報 PRTG 提示非「無資料」 | 無法判定 | hosts.js:459 仍有 `PRTG：無資料` 分支；需畫面實機確認 | 保留 | 只能實機看畫面 | 無 |
| 6 | 790-793 | 手動同步／回填／探測逐行輸出不持久 | 仍成立 | BatchRunRecorder 只出現在分析流程（Orchestrator/NetIQ/AI/Guard）；三種獨立工作無持久紀錄（未找到對應 recorder，信心中） | 保留 | 觸發條件明確 | 無 |
| 7 | 794-795 | 週檢是取數內唯一同步 AI 呼叫 | 仍成立 | AnalysisOrchestrator.cs:479、801-821 仍同步 WeeklyCheckup | 保留 | — | 無 |
| 8 | 796-797 | 同一資料兩種取法（單次大 count vs 分頁） | 仍成立 | PrtgResourceGuardSource.cs:100-124 單次取回＋treesize 警告；結構同步用 PrtgTablePager | 保留 | — | 無 |
| 9 | 798-799 | 解密失敗旗標可能被舊快照舉回 | 仍成立 | CryptoRecovery.Reevaluate 只在儲存後呼叫（SystemSettingsService:503/649、SentinelAdminService:89/116）；健康頁讀旗標無節流；CryptoHelper.RecordFailure 仍會舉旗 | 保留 | — | 無 |
| 10 | 800-801 | 去識別化不遮後接路徑的未登錄帳號 | 仍成立 | RunExportDeidentifier.cs:102 DomainUserRx 尾端 `(?![\\/])`；:863 IsRegistryHive | 保留 | — | 無 |
| 11 | 802-803 | 新鮮度摘要信／診斷匯出缺原因與 scope | 仍成立 | MailNotificationService.cs:2087-2125 過期文字只有最近成功時間；ExportAbnormalRunDto（RunExportDtos.cs:338）無 Scope，BatchRun.Scope 在 BatchRunStore.cs:116 | 保留 | — | 無 |
| 12 | 804-805 | 對應存在前的日子每晚重評注定失敗 | 仍成立 | PrtgDailyPipeline.cs:1190、1222 仍回 NoteNoHostMap；PrtgReevaluationPlanner 無「該日以前無對應則標完成」 | 保留 | — | 無 |
| 13 | 806 | 值型基線偏移未實作 | 仍成立 | PRTG-SPEC:583 的「值型基線」是校準匯出，非規則 | 保留 | — | 無 |
| 14 | 807-808 | Traffic 進數值規則前補 historicdata 探測 | 仍成立 | PRTG-SPEC:376、455 Traffic 不在白名單；PrtgConstants.cs:118-120 Traffic 仍是分類 | 保留 | — | 無 |
| 15 | 809-810 | 激進策略快照容量上限 | 仍成立 | PrtgSnapshotTargetResolver.BatchSize=100、PrtgSnapshotHostedService.cs:737 在途 3；無自動降批 | 保留 | — | 無 |
| 16 | 811-813 | 資源守門改讀快照 | 仍成立 | PrtgResourceGuard／PrtgLiveGuardSource 仍即時查（LiveGuardSource 測試在 PrtgLiveGuardSourceTests） | 保留 | — | 無 |
| 17 | 814-815 | 結構同步改 objid 清單比對 | 仍成立 | PrtgClient.cs:220 僅用 columns=objid 於 count=1 探活；結構同步無清單比對 | 保留 | — | 無 |
| 18 | 816-817 | messages 改推送 | 仍成立 | 無 HTTP action 接收端 | 保留 | — | 無 |
| 19 | 818-819 | 流量類多頻道 | 仍成立 | 同 #14，快照與 historicdata 只取主頻道 | 保留 | — | 無 |
| 20 | 820-821 | 快照 DB 不可用後行程重啟 | 仍成立 | DB-SPEC.md:1106「快照待寫 journal」；描述的邊界仍符合 | 保留 | — | 無 |
| 21 | 822-825 | sensor 逐顆人工分類 UI 未做 | 仍成立 | PrtgCategorySources.Auto、EfPrtgStore.cs:252「不洗掉」契約、prtg-admin.js:753/1117 只有 type 補充對照；無逐顆畫面 | 保留 | — | 無 |
| 22 | 826-828 | 伺服器硬體健康 sensor | 仍成立 | PrtgConstants.cs:37 唯一硬體 type 仍是 SNMP Cisco System Health | 保留 | — | 無 |
| 23 | 829-830 | 範圍補抓每輪讀整份感測器鏡像 | 仍成立 | PrtgSnapshotHostedService.cs:942-980 經 PrtgMirrorGuardSource.GetSensors→EfPrtgStore.GetAllSensors（PrtgResourceGuardSource.cs:41）；無待補也照跑 | 保留 | — | 無 |
| 24 | 831-832 | 範圍補抓失敗裝置無節流 | 仍成立 | PrtgSnapshotHostedService.cs:1012-1030 只記 EmptyDevices，FailedDevices 每輪重試 | 保留 | — | 無 |
| 25 | 833-836 | 正式站台校準後門檻調整（4 子項） | 無法判定 | PrtgRuleCatalog.cs:40-47 CrossDay*／ChronicDownDays 仍為常數；需真實資料 | 保留 | — | 無 |
| 26 | 837-838 | PRTG 失聯台數跟鏡像失效落後一個 TTL | 部分 | 手動「同步結構與對應」成功會 `_versionStamp.Bump()`（PrtgStructureSyncService.cs:420）；夜間管線與快照寫入沒有 Bump（PRTG 相關 Bump 只有該處與 RiskReview）。「寫入不推進版本戳」已不全對 | 改寫 | 改成「只有手動結構同步會推進；夜間與快照寫入仍落後」 | 無 |
| 27 | 839-840 | 分類規則抑制預覽只能依代碼計數 | 仍成立 | lf_top_issues 列（LfDbContext.cs:936 附近）只有 Category，無 rule_id | 保留 | — | 無 |
| 28 | 842-843 | 跨來源精確事件／資源／時間對齊 | 仍成立 | PRTG-SPEC §9 仍是同主機日弱佐證 | 保留 | — | 無 |
| 29 | 844 | 舊日期新增佐證不自動掃全歷史 | 仍成立（設計取捨） | 無全歷史重掃機制 | 保留 | — | 無 |
| 30 | 845-849 | 先備欄位尚無寫入邏輯（5 項） | 部分 | Untrusted 無寫入點；PrevStatus 固定 null（PrtgFetchService.cs:1163）；historicdata min/max=null（:1829）；ThresholdsJson 無寫入；但快照 sampled 列有寫 min/max（PrtgSnapshotAccumulator.cs:203），正文已寫明 | 保留 | 描述正確 | 無 |
| 31 | 850-851 | 回填狀態變更區間不隨水位縮小 | 部分 | PrtgBackfillRunner.cs:203-208 狀態變更整趟只翻一次（FetchStateChangesRangeAsync），不是「續跑整段重取」要再確認是否續跑時仍整段；未讀完整續跑邏輯 | 保留 | 信心中，需看續跑分支 | 無 |
| 32 | 852-853 | 探測／回填／同步／取數互斥是 TOCTOU | 仍成立 | IPrtgManualWorkGate 只涵蓋取數等同步／回填，啟動端「先看再開始」無共同鎖 | 保留 | — | 無 |
| 33 | 854-855 | 多台 PRTG core server | 仍成立 | 設定仍為 SystemSettings 單例 | 保留 | — | 無 |
| 34 | 857-858 | 快照暫停門檻固定 15 分鐘 | 仍成立 | PrtgSnapshotHostedService.cs:118 PauseReportThreshold=15 分 | 保留 | — | 無 |
| 35 | 859-864 | 名稱型裝置多且解析不到每趟付 N 秒 | 仍成立 | IPrtgAddressResolver 仍同步 `Resolve`（PrtgAddressResolver.cs:11）；PrtgHostMapper 無並行；負快取 60 秒 | 保留 | — | 無 |
| 36 | 865-867 | 衝突清單分頁後端全表載入 | 仍成立 | PrtgHostMappingAdminController.cs:61、:92 與 PrtgScopeEstimateController.cs:86/384 仍 GetLatestHostMapWithDate 全讀 | 保留 | — | 無 |
| 37 | 868-873 | 位址解析與守門即時來源改非同步 | 仍成立 | 同 #35 簽章仍同步；PrtgLiveGuardSource 同步阻塞 | 保留 | — | 無 |
| 38 | 874 | sensor type 以顯示名稱比對 | 仍成立 | 全程式無 type_raw／TypeRaw | 保留 | — | 無 |
| 39 | 875-878 | 完整回填預覽與快照補抓佇列仍受規則版本影響 | 仍成立 | PrtgScopeRevisionReader.cs:35 Read() 含規則；PrtgBackfillService.cs:459/473、PrtgSnapshotHostedService.cs:443/1068/1092/1134 仍用 Read()；另有 ReadStructural／ReadRawSemantic | 保留（可精簡） | 「已改用」一詞屬過程敘事，可改成現況句 | 有（「已改用」） |
| 40 | 879-881 | 翻頁期間 PRTG 刪資料判未收斂 | 仍成立 | PrtgTablePager.cs:167 只在第一頁記 treesize | 保留 | — | 無 |
| 41 | 882-884 | 整批感測器皆已刪判契約不符 | 仍成立 | PrtgSnapshotResponseContract.cs:55「空回應仍視為不相容」 | 保留 | — | 無 |

#### 已知未修項（886～902）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 42 | 891-893 | `PrtgFinding.Magnitude` 刻意不落 Count | 仍成立 | PRTG-SPEC §11 提及用途；為設計取捨 | 保留（或可併入 SPEC 不必留在 BACKLOG，因為是「非缺陷」） | 屬現況說明而非待辦 | 無 |
| 43 | 894-895 | 匯出不能選資料類別 | 仍成立 | PRTG-SPEC:570 匯出為固定全類別、128 MiB 額度 | 保留 | — | 「規劃允許…實作則是」輕微敘事 |
| 44 | 896-898 | 觸發式取數收尾掃描多跑一次 | 仍成立 | PrtgTriggeredValueFetcher.cs:205-209 迴圈後收尾掃描（`!singlePass`）；註解已說明意圖（AI 事後上調風險），描述「分析一開始完成會掃兩次」仍真，但這是刻意設計 | 改寫 | 應改成「收尾掃描是刻意的，代價是偶爾多一次查詢」；已非缺陷 | 無 |
| 45 | 899-901 | SQLite 多執行緒寫 lf_prtg_values 可能踩寫鎖 | 部分 | SqlitePragmaInterceptor.cs:40 已設 busy_timeout=5000，緩解多數情況；PrtgFetchService.cs:1520 UpsertValues 仍併發 | 改寫 | 補上 busy_timeout 5 秒已緩解 | 無 |
| 46 | 902 | 併發 passhash 測試只斷言請求計數 | 仍成立 | PrtgClientTests.cs:388-426 只斷言 passhashCount==1 與請求數 11 | 保留 | 低價值；若要處理屬小（<1h） | 無 |

#### 升級期一次性機制與退場條件（904～919）
共通結論：下列機制的程式**全都還在**（逐一 grep 確認，見各列）。退場條件都是「所有站台升級過／正式機確認旗標完成」，程式碼與版號 1.0.81.7 無法判定——需要使用者確認各正式站台目前版本與健康頁「遷移」卡（HealthAttention 鍵 `Migration`）是否全為完成。最舊的機制於 2026-08-06 加入（距今約 3.5 個月）、最新的 PermissionChangeDedupeBackfiller 於 2026-11-20 加入（第 79 輪）。

| # | 行號 | 機制 | 狀態 | 證據 | 建議 | 理由／工作量 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 47 | 910 | HandlingBlobMigrator／HandlingMigrationHostedService | 仍成立（程式在） | Core/Persistence/Sql/HandlingBlobMigrator.cs（2026-08-06 起）、Web/Services/HandlingMigrationHostedService.cs；MigrationGateMiddleware.cs 仍有處理狀態分支（HandlingGuardedPrefixes）；三份旗標 IssueHandlingDone／IssueCasesDone／RecordHandlingDone 與表述一致 | 保留 | 退場條件無法由程式判定，需確認所有站台已升級；退場時要連 MigrationGateMiddleware 處理分支與 Program.cs 註冊一併處理 | 無 |
| 48 | 911 | PermissionChangeMigrator／PermissionChangeReparser／PermissionLegacySummaryCleanupState | 仍成立（程式在） | 三者皆在 Core/Persistence；PermissionChangeMigrationHostedService 在；MigrationGateMiddleware 另有權限異動分支（`/api/permission-changes`）——表中 #47 只說拿掉「處理狀態分支」，此列退場時也要拿掉權限異動分支，表上沒寫 | 改寫 | 補上「退場時一併拿掉 MigrationGateMiddleware 的權限異動分支；整個 middleware 可刪」 | 無 |
| 49 | 912 | ReportFileMigrator／ReportFileMigrationHostedService | 仍成立（程式在） | ReportFileMigrator.cs（ExportDirectoryName="export"，:37）、ReportFileMigrationHostedService；「報告全文」段在 BACKLOG:578 仍在；遷移不刪舊 export 檔（ReportFileMigrator.cs:124） | 保留 | 「正式機驗證報告機制後」無法由程式判定；與 BACKLOG:578-585 報告段交叉引用仍有效 | 無 |
| 50 | 913 | TopIssueBackfiller（來源鍵回填與首見日重整） | 仍成立（程式在） | TopIssueBackfiller.cs（TryFastReady :59、旗標 risky_events_source_key_done，SchemaUpgrader.cs:803-808）；CLAUDE.md 的 K 就緒閘門依賴它 | 保留 | 表內描述與程式一致；就緒閘門是現行讀取路徑依賴，移除前要先改掉閘門，不只是刪回填 | 無 |
| 51 | 914 | IssueFirstSeenSeedHostedService | 仍成立（程式在） | Web/Services/IssueFirstSeenSeedHostedService.cs；MaxRetries=3（:19）；Skipped／Completed 狀態（:96-98、:146-147）與表一致；HealthService 仍引用 | 保留 | — | 無 |
| 52 | 915 | WorkOrderBackfiller／DailyRecordBackfiller | 部分 | 兩者都在；但「回填版本達現行值」只適用 DailyRecordBackfiller（`CurrentVersion = 3`，DailyRecordBackfiller.cs:36）；WorkOrderBackfiller 沒有版本，是以 `CountPending()==0` 判定完成（WorkOrderBackfiller.cs 約 :50-58、:83） | 改寫 | 判定依據欄拆成兩種寫法 | 無 |
| 53 | 916 | PermissionChangeDedupeBackfiller | 仍成立（程式在） | PermissionChangeDedupeBackfiller.cs:30 旗標 `permission_changes_dedupe_hash_done`；PermissionChangeStore 仍有就緒前以原鍵比對路徑 | 保留 | 第 79 輪（2026-11-20）才加入，最年輕，退場最晚 | 無 |
| 54 | 917 | SchemaUpgrader.RemoveRetiredPrtgBlobs／SQL Server 人工對應自增修正／rule_id 長度放寬 | 仍成立（程式在） | SchemaUpgrader.cs:424 呼叫 RemoveRetiredPrtgBlobs（:465）；FixSqlServerPrtgManualMapIdentityIfNeeded（:1615-1630 附近）；SqlServerWidenRiskyEventRuleIdDdl（:1765） | 保留 | 皆冪等、成本低；退場條件「所有站台升級到含修正的版本」無法由程式判定 | 無 |
| 55 | 919 | CaseDaySyncHostedService 非升級機制 | 仍成立 | Web/Services/CaseDaySyncHostedService.cs 在，註冊於 ServiceCollectionExtensions | 保留 | — | 無 |
| 56 | 906 | 表頭說明「每次啟動都會檢查一次」 | 部分 | 多數是背景服務；部分以啟動快判短路（TopIssueBackfiller.TryFastReady、ReportFileMigrator 的 export 目錄快判）。敘述大致正確 | 保留 | — | 無 |

#### 使用方式（921～925）
| # | 行號 | 項目 | 狀態 | 證據 | 建議 | 理由 | 敘事字眼 |
|---|---|---|---|---|---|---|---|
| 57 | 923-925 | 使用說明，連結 docs/archive/HISTORY.md | 仍成立 | docs/archive/HISTORY.md 存在 | 保留 | — | 無 |
| 58 | 889 | 已知未修項引言連結 docs/archive/PRTG-1-PLAN.md | 仍成立 | 檔案存在 | 保留 | — | 無 |

