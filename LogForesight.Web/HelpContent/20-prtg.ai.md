# PRTG 維護（AI 檢索版）

PRTG 維護頁有「連線」、「擷取參數」、「鏡像狀態」、「環境探測」、「指定主機補值」和「使用效果」六個頁籤，權限為 Maintain。PRTG 取數下拉同時控制模組開關與取數範圍；資源守門在「系統管理 > 設定 > 資源守門」，因為它也節制 NetIQ 取數。

## 操作與判讀

- 取樣停用時仍可手動同步結構及對應，前提是已保存來源網址與認證；同步不會啟用取樣。首次設定先完成同步與作用範圍，再做快照及 Profile 容量試測，通過共同容量後儲存啟用。來源、範圍或啟用狀態在途中改變會安全取消同步。

- 連線頁支援 API token、帳號密碼與帳號＋passhash。留空的密碼欄沿用已存值；清除密碼要使用頁面提供的清除操作。
- 擷取參數頁的保守策略每 15 分鐘以快照供應數值，激進策略每 5 分鐘快照並對觸發主機逐顆查詢歷史值。歷史回填永遠是手動離峰作業。
- 容量估算會同時顯示快照、Profile 分項量測與共同容量結果。分項各自符合條件後，還要通過 Table API 每秒 2 次的共享配額、快照固定期限、Profile 刷新期限，以及最多 5 顆 sensor 共用的 30 秒探測期限；已核准方案沿用其保留速率，設定改變時改用候選設定重新估算。缺少或不符的契約會顯示待驗證並阻止啟用。
- 既有採集方案失敗後另有有限復原：必須仍是同一已核准方案、來源／設定／範圍／策略，Profile 資格有效，取數範圍僅 1–50 顆，且有 1–4 筆 24 小時內同契約的真實快照批次結果。系統以設定的最壞請求逾時、目前共享配額及 25% 餘裕重新准入；每輪只允許一批快照，先不執行 sensor 補抓或狀態 Queue。這些結果不會被補造成成功樣本；完整作業仍須等到 5 筆新鮮且相符的成功快照。沒有批次證據、證據過期、範圍擴大或契約改變時，維持等待並顯示原因。

- 鏡像狀態頁的同步會更新裝置、sensor、狀態變更與主機對應。`lf_prtg_devices` 監看裝置鏡像依完整裝置同步結果判定，`lf_prtg_sensors` sensor 鏡像依「數值取數對象」的取數範圍判定。結構同步與主機對應全部成功，且監看裝置範圍非空、不是部分處理、裝置鏡像本趟成功更新，並通過既有監看裝置數基準的縮小保護時，`PrtgScopePurge.RunAfterStructureSync` 會立即呼叫 `DeleteOutOfScopeData`，清除取數範圍外的 `lf_prtg_values` 與 `lf_prtg_state_changes`。首次沒有基準或縮小保護擋下時不自動清除；頁面提供預覽與「確認清除」人工退路。範圍計算失敗、空範圍、部分處理或裝置鏡像未成功更新時不得清除。
- 環境探測只讀 PRTG，輸出包含 API、分頁、狀態變更、取數與守門目標的量測。探測結果中的「無法量測」是未知，不能轉成零。
- 快照診斷讀最近 24 個完整小時的持久回報寫入量；無逐 sensor identity 證據，不能據此證實 sensor 層 100% coverage。目標數為 0 是無目標，不能當成健康或故障；`Unknown` 是證據不足，健康摘要空白時補為 `unknown`「尚無完整小時診斷資料」。兩種摘要都不代表 sensor 的 28 日資料準備度。
- `GET api/prtg/disk-readiness` 每頁最多列 100 顆；第 1 頁 readiness 摘要最多抽樣前 100 顆，不是全站計數。逐 sensor 28×24 小時缺口以 bitmask 回傳，不發 PRTG 請求。`api/prtg/disk-verification` 可抽查單顆或最多 5 顆昨天的主頻道、單位及歷史值；批次依序執行，取消會停止後續項目。`requestId` 去重只在目前行程記憶體保留，最多 512 筆／15 分鐘，重啟後失效。未知單位須人工核實，人工確認需有效比對候選與理由，並留稽核；有限語意驗證不等於真實趨勢／預警效果已驗證。
- 指定主機回填需先預覽主機、日期和估計請求數；可查狀態及取消。它只補值，不追溯建立 finding／交辦。
- 規則頁 `POST /api/rules/disk-trend-preview` 支援停用草稿唯讀試算，日期範圍 1–730 個已完成日，依每批約 10 萬個預期歷史點分批，結果按「完成日 × sensor」配對分頁。`UnsuppressedHitRowCount` 僅計本頁未抑制命中配對列；一列可能涉及多位負責人，不是派工量上界或交辦張數。依目前抑制設定估計抑制數，不含靜音與實際派工，也不寫 finding、風險、案件或交辦。磁碟趨勢規則預設停用；正式啟用後端要求至少一顆 sensor 有 28 日有效資料和有效語意證據，正式評估仍逐顆檢查。尚未用真實 PRTG 28 天資料／頻道語意驗證。
- `GET api/prtg/effectiveness` 預設近 30 天，按各自口徑列 finding、案件、交辦、回覆、抑制及佐證主機日；`lowCoverageSampledHours` 只計區間內已觀測到、quality=`sampled` 且 coverage 低於可用門檻的 hourly 列，不計完全缺值、缺少的小時、sensor-days 或 finding。各指標分母不同，不能把它們相除宣稱轉換率或解決率。
- 先前探測只取得約 6 天資料，且當時 sensor 單位為 null；這是歷史探測結果，並非目前來源現況。來源主要頻道、單位與時基的缺項須由同版本有界環境探測核實。功能與固定情境在隔離環境驗收，合成資料不證明原生語意或真實事故效果。

所有外部輪詢都必須有每次請求逾時、取消訊號與頁數／批次上限；局部回填停止只取消自己啟動的 run。隔離 SQL Server 已驗證原子主機日附掛與診斷搬運的備份還原；正式站台、原生 PRTG 28 日／頻道與完整容量仍未通過。文件不把「等待直到外部系統回應」視為完成條件。

- 第二輪現況：正式問題只補充明確成功的 NetIQ 主機日，零事件成功仍合格；本機、Unknown、失敗、執行中或缺日都不獨立建案。使用者新增決策優先於前述歷史說明。
- 管理者必須在正式判定試點確認 Core、來源時區／語系及主機／sensor；來源與資源世代、逐顆 messages 涵蓋及前導狀態才可支持持續 Down。ACK／14 天不能降低故障風險。
- 磁碟趨勢須同來源／資源／typed 語意暖機涵蓋 28 日；磁碟低水位使用可信的兩個完成小時與期間覆蓋，不要求趨勢的 28 日。近期觀測看最近兩個完成小時，每日正式判定檢查該 NetIQ 主機日內各個合格兩小時窗口；白天已達門檻後恢復的歷史事實仍保留，與目前 episode 分開。來源與分析時區有明確證據後，歷史先轉 UTC，再依服務主機 Local 時區歸入 NetIQ 主機日；只納入完整落在窗口內的小時，所有 slot 的量測與接收時間均須不晚於該日截止。時區缺失或歧義顯示 Unknown，未知與 no-hit 都不能宣稱資源健康。只有可信完整重評能撤回舊 finding，案件人工結論仍保留。
- 補追加、案件及通知有持久重試，過期只摘要。每名收件人前重查規則、靜音、scope、權限與 SMTP；已接受、結果未知與未嘗試分開保存，途中靜音不撤銷已取得的接受紀錄。結果不明允許同身分重寄，但可能重複，不能說確定未寄出或保證僅寄一次。歷史弱佐證不升級，待重評可分頁下鑽；AI 舊輸入結果不能覆寫新 PRTG 證據。
- 匯入只診斷，不搬信任與處置；保留／快照隔離／升級回退操作以使用者說明「保留與復原」為準。現場效益由獨立事故四基準與人工驗收證明，測試數不能證明 1＋1＞2。

Acceptance default segment includes build, parser semantics and visible pilot resource/mapping/disk semantic warm-up fingerprints. Resource or semantic changes split evidence groups; routine probe timestamps do not. Preserve historical incident segments when reviewing old evidence. SMTP acceptance timestamps never imply mailbox delivery.

- 來源變更提供 new／continue／unknown 明確模式及唯讀影響預覽。continue 必須同 Core、時區與語系，附人工身分證據／確認；不是 API 自動信任。unknown 停止正式判定，new 重新暖機，憑證輪替不自動重設。既有案件／人工處置保留。
- 實際作用範圍預覽共用正式主機資格與規則分類；列未對應／衝突／範圍／待 NetIQ／可信資料缺口，100 筆分頁、上限 500、總數及完整性明示。可評估不保證 finding／交辦／通知；preview 不取數、不解除磁碟當輪語意／28 日守門。
- 作業版本列工作 ID、採用與期望設定／範圍摘要、最後完成階段、安全取消狀態；只限目前程序及最近 32 個結束作業。通知另列意圖設定／目前版本。重啟不能從這份記憶體列表推論歷史成功。
- checkpoint v2 checksum、跨程序生命週期 lease 與過期寫入摘要比較已接線；v1 未知完整性保留並停止，不自動升級。升級／隔離依使用者版說明，不改識別強迫重播；同 Core 搬址亦不盲目改綁舊待寫數值。

- 1.0.53.2 完整 PRTG 探測 JSON 同時含 `deployment_resources`、`storage_environment` 與 `identity_preserving_raw_history`。total_available_memory_bytes 是 .NET runtime/GC 可用記憶體，不是實體 RAM；SQLite owned-data-root 是本機資料根磁碟，不是遠端 SQL volume。PRTG 維護 → 環境探測的「完整環境探測」才會帶出這份 context，小範圍資料流驗證不會。另至 NetIQ 維護 → 診斷，選定 Sentinel 執行「安全欄位形狀探測」，複製獨立報告；下一輪交接需分別提供兩份證據，不需連入正式站台。raw history 僅摘要昨天第一小時 avg=0 原始 XML 的有限通道 ID／值／原始時間及接收時間；解析成功不授予 primary、單位／縮放或時基資格，無資料／逾時／不符格式須保留狀態。新診斷匯入採 4 MiB 分片與持久續傳；完成保留 7 日，接收中／驗證中自建立起滿 7 日且工作及搬運 lease 均已過期才可清理，失敗／明確放棄自終止起保留 1 日。取消、伺服器放棄與本機忘記是三種不同操作。原生串流匯出需 SQL snapshot isolation 或 SQLite WAL，資料庫不合條件時先拒絕；匯入不覆寫正式來源或風險資料。
- 離線交接核對腳本 `scripts/Verify-PrtgProbeEvidence.ps1` 需要 PowerShell 7，唯讀最多 64 KiB，要求版本 1.0.53.2 及完整 40 位 build revision，可用 `-ExpectedRevision` 核對指定提交。摘要只輸出固定欄位、列數與狀態；exit 0 表示版本、raw identity 與適用 metadata 形狀完整，2 表示 unknown／partial／timeout／截斷／權限不足等未齊，1 表示 JSON、provider 配對、欄位形狀或版本不符。SQL Server 遠端主機實體資源仍不可由服務程序觀測，會明示 unknown 並回未齊；SQLite 的本機資料根 volume 不會被當成 SQL 主機 volume。此腳本不授予來源信任或風險資格。

校準卡「PRTG 狀態規則門檻」僅重算 down／flapping／warning；silent、磁碟趨勢及資源期間規則明示未評估，不能解讀為零命中。匯出保留完整規則目錄，以 ThresholdKind 區分門檻種類，趨勢及期間規則的純量 Threshold 為 null。規則目錄開關不代表來源可信、試算合格或已授權正式風險。

- 診斷匯出前提：SQL Server在服務資料庫查`sys.databases.snapshot_isolation_state_desc`必須ON；DBA依實際DB名稱設定ALLOW_SNAPSHOT_ISOLATION後重查，RCSI逐敘述快照不能替代整份一致快照。SQLite核Storage:SqliteWal=true及PRAGMA journal_mode=wal，依部署重啟生效；下載端不臨時改正式DB設定。前提通過不授予來源或容量資格。
