# R1 郵件通知與主機日工作流體檢（唯讀）

範圍：origin/dev..HEAD 的 MailNotificationService、MailNotifyStateStore、prtg-admin.js、HostDayWorkflowState、HostDayPostProcessor（含 MailIssueDigest 一行）。
方法：讀碼推演＋在專案內暫時新增一個探針測試檔（ZzProbeR1Supersede.cs，已刪除；git status 內沒有它）跑 3 組探針。
注意：體檢期間 git status 出現 PrtgRiskProjectionStore.cs、PrtgRiskReviewController.cs、PrtgRiskReviewHostedService.cs 三個已修改檔，不是我改的（可能是平行的其他代理）。

## 結論摘要
工作流寫入減量（UpdateIfChanged／CloseMailPlanIfChanged／ReplaceMailPlanIfChanged／RecordCaseIntentsAndResults）在正常路徑與版本不符、列不存在、交易內例外的最終狀態與改動前一致，未找到高嚴重度缺陷。MailIntentMemo 未發現就地修改紀錄的路徑。主要發現集中在「已被取代」終態讓重試迴圈停手後的補寄時機，以及重試空轉沒有完全消除。

## 發現

### F1 中｜MailNotificationService.cs:261-263（SupersedeUrgentOutbox(null)）＋:365-369（重試閘門）
失敗情境：SMTP 異常使即時通知項目待送 → 管理者關閉「即時通知」（或整體郵件）→ 下一趟（重試或取數結束）把全部非終態項目標為 superseded → 管理者重新開啟即時通知。此時閘門「有非終態項目」為假，每分鐘重試不再執行，信要等「下一次取數結束的 NotifyAfterRunAsync」才寄出。改動前項目維持 pending，重新啟用後一分鐘內即由重試寄出。
重現：是（探針 P1：重新啟用後連跑 5 分鐘 RetryPendingUrgentAsync，Sent=0）。既有測試 `即時通知停用_…` 只用 NotifyAfterRunAsync 驗證，沒涵蓋重試迴圈。
建議：接受為設計取捨則在 WEB-SPEC 郵件節與設定頁說明「重新啟用後於下次取數結束補寄」；或停用時不標 superseded、只讓重試閘門在「即時通知停用」時視為無事可做（閘門多讀設定，項目維持 pending），重新啟用即恢復。

### F2 低-中｜MailNotificationService.cs:1280-1284（supersede 的活鍵集合只含 candidates）＋:1297-1316（建意圖）
失敗情境：高風險日在版本暫時取不到（工作流 Parent 非 Succeeded，WorkflowRecordKey 退回短鍵）、紀錄在恢復等待（recoveryWaitingRecordIds 被濾掉）、或風險複核 pending（QualifiesForUrgentRisk 為假）的那一趟，原本待送的項目鍵不在 liveKeys，被標為 superseded；一旦鍵在狀態恢復後與原鍵相同（例如只有恢復等待、DecisionVersion 與內容不變），該項目不再有非終態項目驅動每分鐘重試，要等下一次取數結束才會被重新建立並寄出（SendUrgent 的建意圖段會把 superseded 改回 pending，所以不會永久漏寄）。
重現：部分。探針 P3 用 ParentFailed→RestoreFromRecord 模擬暫時狀態，因 DecisionVersion 會遞增、鍵本來就換新，該情境下重試仍寄出（Sent=1），未能重現「鍵相同」的延遲；純恢復等待情境為推論，未重現。
建議：supersede 只針對「確定不會再對應」的鍵（紀錄已不在窗口內、或被刪），不要把「這趟因暫時狀態被濾掉」的紀錄算進去——例如 liveKeys 取自濾除前的 records（含 recoveryWaiting 與版本缺失者的鍵以 RecordKey 前綴比對），或對這類暫時狀態的主機日跳過 supersede。

### F3 低-中｜MailNotificationService.cs:365-369、MailNotifyStateStore.cs:202-211
失敗情境：僅用設定內直填信箱（無帳號、統計收件人）時，一位收件人 SMTP 失敗、另一位成功，本趟只要有一封成功就把 UrgentSentKeys 標記（coverage 為空的既有規則），但該意圖狀態保持 pending（b=failed-or-unknown）。鍵仍是現行活鍵、也在已寄集合內，因此不會被 supersede、也不會被重新寄送，卻永遠是非終態，重試閘門每次都通過，每 30 分鐘整套重跑一次（含 14 天窗口查詢與版本讀取），直到紀錄滑出窗口才被 supersede。批次 0 想消除的「空轉」在此形狀仍在，只是被退避壓成 30 分鐘一次。
重現：是（探針 P2b：兩位收件人、b 失敗；first 之後 UrgentSentKeys=1、意圖 pending，改動前後的 NotifyAfterRun 都不會再寄 b，狀態維持 pending）。b 不會被補寄是既有行為，不是本批新增。
建議：supersede／終態判定把「鍵已在 UrgentSentKeys 且仍 pending」的項目視為不需重試（標終態或讓閘門排除），或在 MarkSent 時一併更新意圖狀態。

### F4 低｜prtg-admin.js:3791-3796 與 PrtgOperationsController.cs:24-30
失敗情境：superseded 項目保留當時的收件人狀態，畫面顯示「已被取代，不再寄送｜… 等待重試 1 位」，兩段語意互相矛盾（labels.pending 是「等待重試」）。另外 SupersedeUrgentOutbox 對所有項目一次改 UpdatedAtUtc，維護頁依 UpdatedAtUtc 取前 100 筆，大量 superseded 會把其他仍有意義的項目擠出清單。
重現：否（讀碼推論）。
建議：status 為 superseded 時 API 不回收件人分組（或前端略過），並在 supersede 時不改 UpdatedAtUtc 或維護頁排序把終態排後。

### F5 低｜MailNotificationService.cs:2132-2145（LegacyKeySent）
失敗情境：每個「尚未寄出」的候選紀錄每趟會再算 8 組舊旗標鍵，每組都重做整份紀錄指紋序列化，且 legacyFlags 非空時繞過 memo。SMTP 異常時待送項目累積，每次重試（最慢 30 分鐘，最快 1 分鐘）× 候選數 × 8 次序列化；versions 為 null（短鍵）時 8 組結果與第一次檢查完全相同，是純白做工。
重現：否（效能推論）。
建議：只在 sentKeys 含有「舊格式鍵」時才算（例如 sentKeys 任一含 `|content:` 且該紀錄鍵前綴有命中才展開），或每個紀錄對 8 組旗標只做一次 ForRecordWithIdentity 的分段計算；versions 為 null 時直接 return false。

### F6 低｜HostDayWorkflowState.cs:861-883（ReplaceMailPlanIfChanged）
失敗情境：此方法契約為「只能在緊接著呼叫 CloseMailPlanIfChanged 時使用」。若 ClosePlans 在兩者之間丟例外（TrackAcceptedContributionWorkflow／TrackDurableUrgentStatisticsReceipts 失敗，外層只記 WARN），改動前會留下 MailPlanClosed=false（計畫未結案），現在保留原本的 closed=true 與舊原因。這是對舊狀態較好的方向，但「最終狀態與改動前相同」在例外路徑不成立；契約只靠註解維持。
重現：否（推論）。
建議：保持現狀亦可，但在方法註解註明例外路徑的差異；或把 closingPass 的兩步合併成 PlanAndClose 的單一入口，消除「須搭配」的隱性契約。

### F7 低（過度設計／死碼）
- MailNotifyStateStore.cs:202-211：PruneTerminalUrgentIntents 的文件仍寫「未知寄送結果與混合收件人狀態會保留」，superseded 現在不論收件人狀態一律可清除，註解與行為不一致（含 sending-result-unknown 的收件人也會於保留期後被清）。
- HostDayWorkflowState.cs:927 `CloseMailPlan`：正式碼已無呼叫端，只剩測試（HostDayWorkflowCloseMailPlanTests）使用，等於只為測試存在。
- MailNotificationService.cs:2189 `MailIntentMemo(probe)` 建構參數與 `IntentComputedProbe` 屬性：僅供測試計數的注入點，放在正式碼。可改由測試以反射或計數用子類別處理；若保留，留 internal 即可，現狀已是。
- `MailIntentMemo` 在 ClosePlans 內因為 `_records.Query` 回新物件而幾乎必然未命中，對結尾那一輪收益有限（只在 SendUrgent／SendRunSummary 同一批物件內有效），記憶體則跟著保留該趟所有紀錄參考。

## 已驗證沒問題的項目
- UpdateIfChanged：與 Update 的鎖、交易、大小檢查相同；列不存在時一樣建立；版本不符時 change 為 no-op，內容相同則不寫。UpdatedAt 與 blob Version 無讀取端（grep 全 Web／Core）。
- CloseMailPlanIfChanged：版本比對改由 MatchesVersion 完成，與原先的 CaptureDeliveryVersion 比對條件逐項相同（Parent=Succeeded、非恢復等待、ParentRecordId、DecisionVersion）。
- RecordCaseIntentsAndResults：僅限 expectedCaseKeys 為空，兩段輸入皆為空集合，與原先兩次呼叫的最終狀態相同；AttachNewDay 失敗時原本的 catch 補寫也是空集合。
- MailIntentMemo：記憶鍵含通道、父紀錄、決策版本、情境指紋，以參考比對；MailNotificationService 內改寫一律在 JsonSerializer 複本（:1376、:2326、:2445、:2646、:2661）；寄送迴圈全程單執行緒（無 WhenAll／Parallel），字典無競爭。
- 升級：LegacyKeySent 涵蓋 8 種旗標組合；升級當天已寄鍵不重寄（既有測試與讀碼一致）。工作流舊意圖在 ClosePlans 內被 Replace 成新意圖，已寄項目靠 AcceptedMailContributionReceipts 重投影；沒有逐議題收據的舊已寄日在執行總表可能暫顯「待處理」，只影響顯示（IsComplete 只被 RunMonitorService 讀取）。
- 重試退避：設定版次每次儲存換新 Guid（ISystemSettingsStore.cs:26），改設定後下一分鐘即回 1 分鐘；取數結束入口不受退避；站台重啟後 _retryNextUtc 為 MinValue，立即重試；通知與重試共用 _notifyGate，並發被序列化，工作流寫入走同一個 WorkflowMutex。
- 收件人改變：route 雜湊進鍵，舊鍵被 supersede、新鍵建立，已接受的部分由工作流收件部分與逐議題收據擋重寄（讀碼）。
- 只開即時／只開摘要：ClosePlans 對未啟用的通道換成空計畫並結案，與改動前最終狀態相同。
- MailIssueDigest 的 AggregateHostTotals 換呼叫不在本次推演範圍，未審。

## 探針結果
- P1（停用→啟用→重試迴圈）：Sent=0，見 F1。
- P2b（兩位直填收件人、b 失敗）：意圖 pending、已寄鍵已標記、b 不再被寄；停用／啟用不改變此結果，見 F3。
- P3（ParentFailed→Restore）：暫時狀態下一筆 superseded、一筆 pending，恢復後重試寄出 1 封，無重複。
