# 第 76-2 輪批次 E 逐處分類表

本表是事後依程式歷史回溯補上的 E2、E-前置兩份逐列記錄（當時沒有留存）。資料來源：

- E-1 提交 `bf80e179`（例外訊息規則、`UserFacingMessage`、修改外洩點）。
- E-2 提交 `69e13774`（類別名前綴、內部代碼、PRTG 失敗說明）。
- Z 批提交 `c42c5302`（`WorkOrderRuleException`）。
- 守門測試 `LogForesight.Tests/UserFacingMessageGuardTests.cs` 的 `MessageUse`／`LogOnly`／`IsLogContinuation` 規則。

行號除特別註明外，第一節以 `bf80e179^` 為準。回溯方式：用守門測試同一組規則逐檔掃 `bf80e179^` 的 `LogForesight.Web`，得到 79 行命中，另補列守門規則排除的純記錄檔行（附錄）。

## 1. E2 分類

### 1.1 母體：守門規則命中的 79 行

欄位說明：處置「修改」＝該行的例外原文不再進回應、畫面或維護主控台輸出；「保留」＝原文仍外傳，但理由成立（只捕捉專屬型別、自擲中文、不外傳等）。「E-1 暫列」＝E-1 先放進守門白名單（常數 `ForE5`），E-2 才改。現況欄的行號是 `HEAD`（`b96a9155`）。

| 編號 | 檔案:行（`bf80e179^`） | 寫法摘錄 | 處置 | 理由或改法 | 現況 |
|---|---|---|---|---|---|
| E2-01 | Auth/Ldap/LdapCredentialVerifier.cs:60 | `CredentialCheckResult.Fail($"AD 驗證發生例外：{ex.Message}")` | 保留 | `FailureReason` 只在伺服器內判斷成敗，登入回應一律是「登入失敗」，沒有消費端回給使用者 | 同檔:60，白名單 |
| E2-02 | Auth/Ldap/LdapService.cs:180 | `AdSubCodeRegex.Match(ex.Message ?? …)` | 保留 | 只解析 AD 子代碼，不外傳 | 同檔:180，白名單 |
| E2-03 | Configuration/AppSettings.cs:107 | `errors.Add(ex.Message)` | 保留 | 啟動前檢查 appsettings 的 `InvalidOperationException`（自擲中文），只寫啟動失敗輸出，不是 API 回應 | 同檔:107，白名單 |
| E2-04 | Configuration/AppSettings.cs:117 | `errors.Add(ex.Message)` | 保留 | 同 E2-03 | 同檔:117，白名單 |
| E2-05 | Controllers/Api/CalibrationController.cs:70 | `…CapacityExceeded, exception.Message)` | 保留 | 只捕捉 `CalibrationCapacityException`，訊息是該型別自己寫的中文 | 同檔:70，白名單 |
| E2-06 | Controllers/Api/CalibrationController.cs:158 | 同上 | 保留 | 同 E2-05 | 同檔:158，白名單 |
| E2-07 | Controllers/Api/PrtgEffectivenessController.cs:35 | `catch (ArgumentException ex)` → `Fail(ValidationFailed, ex.Message)` | 修改 | 移除整段 try／catch；起訖顛倒改由服務擲 `DomainException.Validation`，交 `ApiExceptionFilter` 轉 400 | 已移除（見第 3 節：日期超過上限的路徑後來才補上） |
| E2-08 | Controllers/Api/PrtgHostMappingAdminController.cs:170 | `catch (PrtgScopeConflictException ex) { throw DomainException.Conflict(ex.Message); }` | 保留 | 只捕捉已登錄的專屬型別 | 同檔:170，白名單 |
| E2-09 | Controllers/Api/PrtgHostMappingAdminController.cs:349 | `ex is PrtgScopeConflictException or PrtgScopePurgeBusyException ? ex.Message : "…固定說明"` | 保留 | 先判型別為已登錄的專屬例外才用原訊息，其餘給固定說明 | 同檔:349，白名單 |
| E2-10 | Controllers/Api/PrtgMonitoringController.cs:214 | `catch (InvalidOperationException ex) { return Conflict(…, ex.Message); }` | 保留 | 只包住 `Store().Update` 的交易，兩處自擲的中文衝突訊息 | 同檔:214，白名單 |
| E2-11 | Controllers/Api/PrtgScopeEstimateController.cs:278 | `console.WriteLine($"…直接查詢 PRTG 失敗（{liveEx.Message}）…")` | 修改 | 原文改寫 `Log.Warn(liveEx, …)`；畫面用 `UserFacingMessage.For(liveEx, "連不上、認證失敗或逾時；原文見記錄檔")` | 同檔:279 |
| E2-12 | Controllers/Api/PrtgScopeEstimateController.cs:298 | `ErrorMessage = $"解析受監看目標失敗：{ex.Message}"` | 修改 | 同上：記錄檔寫原文，畫面用 `UserFacingMessage.For` 備用說明 | 同檔:300 |
| E2-13 | Controllers/Api/PrtgScopeEstimateController.cs:360 | `ErrorMessage = $"PRTG 預覽失敗：{ex.Message}"` | 修改 | 同上 | 同檔:363 |
| E2-14 | Controllers/Api/PrtgScopeEstimateController.cs:511 | `throw DomainException.Validation(ex.Message)` | 保留 | `PrtgScopePurgeStore` 自擲的中文預覽狀態訊息（`when` 排除 `PrtgScopePurgeBusyException`）；捕捉範圍限單一呼叫。殘餘風險：捕捉型別是 `InvalidOperationException`，框架層同型別例外仍會外傳 | 同檔:514，白名單 |
| E2-15 | Controllers/Api/PrtgScopeEstimateController.cs:522 | 同上 | 保留 | 同 E2-14 | 同檔:525，白名單 |
| E2-16 | Controllers/Api/PrtgScopePurgeJobsController.cs:83 | `throw DomainException.Validation(ex.Message)` | 保留 | `PrtgScopePurgeStore` 自擲的中文作業狀態訊息；同 E2-14 的殘餘風險 | 同檔:83，白名單 |
| E2-17 | Controllers/Api/PrtgScopePurgeJobsController.cs:138 | 同上 | 保留 | 同 E2-16 | 同檔:138，白名單 |
| E2-18 | Controllers/Api/PrtgTransferController.cs:200 | `FailureCode(ex.Message)` | 保留 | 只把訊息對成錯誤碼，回應用 `SafeValidationMessage` 的固定文字 | 同檔:200，白名單 |
| E2-19 | Controllers/Api/PrtgTransferController.cs:288 | `Fail(exception.Code, exception.Message)` | 保留 | 只捕捉 `PrtgTransferStoreException`（已登錄專屬型別） | 同檔:288，白名單 |
| E2-20 | Controllers/Api/ScheduleController.cs:476 | `throw DomainException.Validation(ex.Message)` | 保留 | `SentinelQueryBuilder.NormalizeSubnetPrefix` 自擲的中文輸入格式訊息；單一呼叫 | 同檔:476，白名單 |
| E2-21 | Filters/ApiExceptionFilter.cs:28 | `domain.Message` | 保留 | 過濾器本身，只對已登錄型別取訊息 | 同檔:28，白名單 |
| E2-22 | Filters/ApiExceptionFilter.cs:36 | `conflict.Message` | 保留 | 同上 | 同檔:36，白名單 |
| E2-23 | Filters/ApiExceptionFilter.cs:43 | `busy.Message` | 保留 | 同上 | 同檔:43，白名單 |
| E2-24 | Filters/ApiExceptionFilter.cs:50 | `transfer.Message` | 保留 | 同上 | 同檔:50，白名單 |
| E2-25 | Filters/ApiExceptionFilter.cs:57 | `deidentification.Message` | 保留 | 同上 | 同檔:57，白名單 |
| E2-26 | Program.cs:269 | `Console.Error.WriteLine("啟動失敗：" + ex.Message)` | 保留 | 啟動期寫到主控台給部署人員，站台尚未提供任何 API | 同檔:269，白名單 |
| E2-27 | Services/AiAnalysisHostedService.cs:595 | `CompleteWorkflowAiAttempt(…, workflowError.Message)` | 修改 | 改固定句「AI 判定版本檢查失敗；詳細原因見記錄檔。」，原文由同區塊既有的 `Log.Warn(workflowError, …)` 記錄 | 同檔:595 |
| E2-28 | Services/AiAnalysisHostedService.cs:665 | `CompleteWorkflowAiAttempt(…, ex.Message)` | 修改 | 改固定句「AI 分析失敗；詳細原因見記錄檔。」（供應商例外原文可能含位址，會顯示在工作流狀態） | 同檔:666 |
| E2-29 | Services/AiAnalysisHostedService.cs:754 | `_runState.EndRun(false, $"…整批重標失敗：{ex.Message}")` | 修改 | 先 `Log.Error(ex, …)`，`EndRun` 改固定句 | 同檔:756 |
| E2-30 | Services/HealthService.cs:503 | `error = ex.Message` | 修改 | 加 `Log.Warn(ex, …)`，`error` 改「資料庫讀取失敗；詳細原因見記錄檔。」 | 同檔:535 |
| E2-31 | Services/Import/ImportService.cs:64 | `DomainException.Validation($"CSV 解析失敗：{ex.Message}")` | 保留 | `CsvParseException` 的訊息是 `CsvParser` 自己寫的中文（行號與欄位名），不含路徑 | 同檔:64，白名單 |
| E2-32 | Services/IssueFirstSeenSeedHostedService.cs:75 | `Progress.LastError = ex.Message` | 修改 | 改「合併失敗；詳細原因見記錄檔。」（原本就有 `Log.Error`） | 同檔:75 |
| E2-33 | Services/IssueOwnerAdminService.cs:319 | `ex is DomainException ? ex.Message : "代為結案時發生未預期的錯誤。"` | 保留 | 先判型別為領域例外才用原訊息，其餘給固定說明 | 同檔:319，Z 批擴成 `DomainException or WorkOrderRuleException`（第 3 節） |
| E2-34 | Services/NetiqDirectoryClient.cs:86 | `throw new NetiqDiscoveryException(ex.Message)` | 保留 | `NormalizeSubnetPrefix` 自擲的中文輸入格式訊息；同時 E-1 把 `NetiqDiscoveryException` 補登為已登錄型別 | 同檔:86，白名單 |
| E2-35 | Services/NetiqDirectoryClient.cs:307 | 同上 | 保留 | 同 E2-34 | 同檔:309，白名單 |
| E2-36 | Services/NetiqDirectoryClient.cs:550 | `new NetiqDiscoveryException($"掃描 Sentinel「…」失敗：{ex.Message}", ex)` | 修改 | 加 `Log.Warn`；訊息改「…失敗，請檢查位址、帳密與連線；原文見記錄檔。」（Sentinel 回應本文可能夾帶內部資訊） | 同檔:553 |
| E2-37 | Services/NetiqDirectoryClient.cs:585 | `$"（{ex.Message}），本次已改用事件掃描。"` | 修改 | 加 `Log.Warn`；警告改「（原文見記錄檔），本次已改用事件掃描。」 | 同檔:589 |
| E2-38 | Services/NetiqDiscoveryService.cs:149 | `job.Error = ex.Message` | 修改 | 加 `Log.Warn`；改 `UserFacingMessage.For(ex, "探索作業發生未預期的錯誤，詳細原因見記錄檔。")` | 同檔:150 |
| E2-39 | Services/NetiqDiscoveryService.cs:260 | `throw DomainException.Validation(ex.Message)` | 保留 | 只捕捉 `NetiqDiscoveryException`；其內含的底層例外已由 E2-36 改成固定說明 | 同檔:261，白名單 |
| E2-40 | Services/NetiqProbeService.cs:79 | `console.WriteLine($"探測過程發生未預期錯誤：{ex.Message}")` | 修改 | 加 `Log.Warn`；主控台改「探測過程發生未預期錯誤；詳細原因見記錄檔。」 | 同檔:82 |
| E2-41 | Services/PrtgBackfillService.cs:647 | `console.WriteLine($"指定主機回填發生未預期錯誤：{ex.Message}")` | 修改 | 同 E2-40 | 同檔:650 |
| E2-42 | Services/PrtgBackfillService.cs:772 | `error = ex.Message` | 保留 | 只捕捉 `DomainException` | 同檔:775，白名單 |
| E2-43 | Services/PrtgBackfillService.cs:804 | `_state.AppendLine($"初始化 PRTG 連線失敗：{ex.Message}")` | 修改 | E-1 暫列（`ForE5`）；E-2 改 `PrtgFailureText.ForInit(ex)`（`PrtgClientException` 的訊息已是中文說明，其餘固定說明）並加 `Log.Warn` | 同檔:808 |
| E2-44 | Services/PrtgBackfillService.cs:806 | `error = $"初始化 PRTG 連線失敗：{ex.Message}"` | 修改 | 同 E2-43 | 同檔:810 |
| E2-45 | Services/PrtgBackfillService.cs:906 | `console.WriteLine($"回填過程發生未預期錯誤：{ex.Message}")` | 修改 | 同 E2-40 | 同檔:911 |
| E2-46 | Services/PrtgDiagnosticExportActionResult.cs:101 | `error is PrtgTransferStoreException ? error.Message : "…固定說明"` | 保留 | 先判型別為已登錄專屬例外才用原訊息 | 同檔:101，白名單 |
| E2-47 | Services/PrtgHostMapRefresher.cs:67 | `return $"重算今日 PRTG 對應失敗: {ex.Message}"` | 修改 | 改「重算今日 PRTG 對應失敗；詳細原因見記錄檔。」（原本就有 `Log.Warn`） | 同檔:67 |
| E2-48 | Services/PrtgHostMapRefresher.cs:119 | `return $"PRTG 主機對應規則升級失敗：{ex.Message}（舊對應…）"` | 修改 | 改「…失敗（原文見記錄檔）（舊對應…）」 | 同檔:119 |
| E2-49 | Services/PrtgProbeService.cs:260 | `_state.AppendLine($"初始化 PRTG 連線失敗：{ex.Message}")` | 修改 | 同 E2-43（E-1 暫列、E-2 改） | 同檔:263 |
| E2-50 | Services/PrtgProbeService.cs:262 | `error = $"初始化 PRTG 連線失敗：{ex.Message}"` | 修改 | 同 E2-43 | 同檔:265 |
| E2-51 | Services/PrtgProbeService.cs:311 | `console.WriteLine($"站台對照發生未預期錯誤：{ex.Message}")` | 修改 | 同 E2-40 | 同檔:315 |
| E2-52 | Services/PrtgProbeService.cs:337 | `console.WriteLine($"探測過程發生未預期錯誤：{ex.Message}")` | 修改 | 同 E2-40 | 同檔:342 |
| E2-53 | Services/PrtgSnapshotHostedService.cs:988 | `WriteOutput($"…感測器補抓失敗（不影響本輪快照）：{ex.Message}", Warn)` | 修改 | 原文改 `Log.Warn(ex, …)`；`WriteOutput` 改「…；詳細原因見記錄檔。」 | 同檔:989 |
| E2-54 | Services/PrtgSnapshotHostedService.cs:1029 | `WriteOutput($"…狀態補抓佇列處理失敗…：{ex.Message}", Warn)` | 修改 | 同 E2-53 | 同檔:1031 |
| E2-55 | Services/PrtgSnapshotHostedService.cs:1122 | `WriteOutput($"…裝置 {…} 狀態補抓失敗…：{ex.Message}", Warn)` | 修改 | 同 E2-53 | 同檔:1125 |
| E2-56 | Services/PrtgSnapshotHostedService.cs:1236 | `WriteOutput($"…查詢守門覆寫清單感測器的所在裝置失敗…：{ex.Message}", Warn)` | 修改 | 同 E2-53 | 同檔:1240 |
| E2-57 | Services/PrtgSnapshotHostedService.cs:1415 | `DomainException.Conflict("封存失敗，待寫檔維持原狀：" + ex.Message)` | 修改 | 捕捉的是 `IOException or UnauthorizedAccessException`（原文含路徑）；改「…；請檢查磁碟空間、檔案鎖定與服務帳號權限（原文見記錄檔）。」，原本就有 `Log.Warn` | 同檔:1419 |
| E2-58 | Services/PrtgSnapshotHostedService.cs:1445 | `InvalidDataException or PrtgSnapshotJournalLockedException => ex.Message` | 保留 | 兩種例外都是 `PrtgSnapshotJournal` 自己寫的中文（不含路徑）；其餘型別給固定說明 | 同檔:1449，白名單 |
| E2-59 | Services/PrtgSnapshotHostedService.cs:1539 | `$"快照樣本寫入資料庫失敗，{_pendingRows} 列留待下次重試：{ex.Message}"` | 修改 | 改「…留待下次重試（詳細原因見記錄檔）。」（原本就有 `Log.Error`） | 同檔:1543 |
| E2-60 | Services/PrtgSnapshotJournal.cs:821 | `new PrtgSnapshotJournalInspection(false, ex.Message, …)` | 修改 | 加 `Log.Warn`；依例外型別對成固定說明（`JsonException`、`UnauthorizedAccessException`、其餘），`InvalidDataException` 用 `UserFacingMessage.For` | 同檔:829（switch 運算式） |
| E2-61 | Services/PrtgStructureSyncService.cs:369 | `_state.AppendLine($"初始化 PRTG 連線失敗：{ex.Message}")` | 修改 | 同 E2-43（E-1 暫列、E-2 改） | 同檔:370 |
| E2-62 | Services/PrtgStructureSyncService.cs:373 | `error = $"初始化 PRTG 連線失敗：{ex.Message}"` | 修改 | 同 E2-43 | 同檔:374 |
| E2-63 | Services/PrtgStructureSyncService.cs:433 | `console.WriteLine($"同步過程發生未預期錯誤：{ex.Message}")` | 修改 | 改 `Log.Error` 加固定句 | 同檔:435 |
| E2-64 | Services/PrtgStructureSyncService.cs:435 | `failure = ex.Message` | 修改 | `failure` 改固定句「同步過程發生未預期錯誤；詳細原因見記錄檔。」 | 同檔:437 |
| E2-65 | Services/PrtgStructureSyncService.cs:442 | `ErrorMessage = ex.Message`（寫入同步狀態，顯示在維護頁） | 修改 | 改用 `failure` 固定句 | 同檔:444 |
| E2-66 | Services/PrtgStructureSyncService.cs:448 | `console.WriteLine($"保存失敗狀態時發生錯誤：{persistEx.Message}")` | 修改 | 加 `Log.Warn`；主控台改固定句 | 同檔:451（用詞後來改為「儲存」） |
| E2-67 | Services/SchedulerHostedService.cs:566 | `new RunOutcome(false, ex.Message, …)`（取消分支） | 修改 | 取消分支改固定句「執行已取消。」 | 同檔:566 |
| E2-68 | Services/SchedulerHostedService.cs:573 | `new RunOutcome(false, ex.Message, …)`（未預期錯誤分支） | 修改 | 改「執行發生未預期的錯誤；詳細原因見記錄檔。」（原本就有 `Log.Error`） | 同檔:573 |
| E2-69 | Services/SentinelAdminService.cs:241 | `Message = ex.Message`（測試連線結果） | 修改 | 加 `Log.Warn`；只有 `SafeCode == InvalidBaseUrlCode`（位址未設定或格式不正確，訊息只含使用者自己填的值與修正方式）才給原文，其餘固定說明。`SentinelClient` 兩處對應拋出補上該代碼 | 同檔:245-247（命中行 :246，白名單） |
| E2-70 | Services/SystemSettingsService.cs:247 | `Validation($"「AI 額外請求欄位」不是合法的 JSON：{ex.Message}")` | 修改 | JSON 解析原文（行號、位置、輸入片段）只進 `Log.Info`；訊息改為「…不是合法的 JSON，請檢查括號、引號與逗號（例如 {"rep_pen": 1.3}）。」 | 同檔:250 |
| E2-71 | Services/SystemSettingsService.cs:816 | `Message = $"測試連線失敗：{ex.Message}"`（AD） | 修改 | 加 `Log.Warn`；改「測試連線失敗：請檢查網域控制站位址、連接埠與搜尋設定；詳細原因見記錄檔。」 | 同檔:820 |
| E2-72 | Services/SystemSettingsService.cs:854 | `Message = $"測試寄信失敗：{ex.Message}"` | 修改 | 同上，改檢查郵件伺服器位址、連接埠、加密與寄件帳號 | 同檔:859 |
| E2-73 | Services/SystemSettingsService.cs:956 | `Message = $"測試連線失敗：{ex.Message}"`（PRTG） | 修改 | 同上，改檢查 PRTG 位址、連接埠與認證設定 | 同檔:962 |
| E2-74 | Services/WorkOrderBoardService.cs:321 | `Hosts = groupHosts, Message = ex.Message`（立即派工失敗組） | 修改 | 改 `UserFacingMessage.For(ex, "建立交辦單時發生未預期的錯誤，詳細原因見記錄檔。")`，原文由既有 `Log` 記錄 | 同檔:321 |
| E2-75 | Services/WorkOrderCommandService.cs:697 | `catch (ArgumentException ex) { throw DomainException.Validation(ex.Message); }` | 保留 | E-1 判定：協調器／store 自擲的中文業務訊息，捕捉範圍限單一呼叫（殘餘風險：框架層同型別例外會外傳） | 同檔:702，Z 批改只捕 `WorkOrderRuleException`（第 3 節） |
| E2-76 | Services/WorkOrderCommandService.cs:701 | `catch (InvalidOperationException ex) { throw DomainException.Validation(ex.Message); }` | 保留 | 同 E2-75 | 已移除：Z 批改為 `Log.Warn` 後 `throw;` |
| E2-77 | Services/WorkOrderReplyService.cs:120 | `ex is DomainException ? ex.Message : "回覆時發生未預期的錯誤。"` | 保留 | 先判型別為領域例外才用原訊息 | 同檔:121，白名單 |
| E2-78 | Services/WorkOrderReplyService.cs:249 | `throw DomainException.Validation(ex.Message)` | 保留 | 同 E2-75 | 同檔:254，Z 批改只捕 `WorkOrderRuleException` |
| E2-79 | Services/WorkOrderReplyService.cs:253 | `throw DomainException.Validation(ex.Message)` | 保留 | 同 E2-76 | 已移除：Z 批改為 `Log.Warn` 後 `throw;` |

### 1.2 附錄：守門規則排除的純記錄檔行（21 行）

這 21 行含 `ex.Message`，但在 `Log.xxx(` 的參數內（同行或多行呼叫的續行），原文只進記錄檔，不屬守門母體；處置一律「保留」，理由「純記錄檔，不外傳」。E-1／E-2 都沒有改動這些行。

| 檔案:行（`bf80e179^`） | 寫法摘錄 |
|---|---|
| Auth/Ldap/LdapCredentialVerifier.cs:50 | `Log.Debug(ex, "AD 驗證成功但查詢使用者屬性失敗…", account, ex.Message)` |
| Services/AiAnalysisHostedService.cs:668 | `Log.Warn(…)` 的續行 `record.Host, record.Date, ex.Message);` |
| Services/AiProbeService.cs:221 | `Log.Warn(ex, "AI 探活連線失敗：{0}", ex.Message)` |
| Services/CaseDaySyncHostedService.cs:75 | `Log.Error(ex, "[案件逐日同步]…", ex.Message)` |
| Services/CryptoKeyBootstrapper.cs:164 | `Log.Warn(ex, "[Crypto] 啟動探測失敗…", ex.Message)` |
| Services/CryptoKeyBootstrapper.cs:235 | `Log.Warn(ex, "[Crypto] 讀取金鑰指紋記錄失敗…", ex.Message)` |
| Services/CryptoRecovery.cs:27 | `Log.Warn(ex, "[Crypto] 儲存後重新評估憑證解密狀態失敗…", ex.Message)` |
| Services/DailyRecordBackfillHostedService.cs:53 | `Log.Error(ex, "[SQL] lf_daily_records 抽出欄回填失敗…", ex.Message)` |
| Services/HandlingMigrationHostedService.cs:62 | `Log.Error(ex, "[SQL] 處理狀態遷移失敗…", ex.Message)` |
| Services/IssueFirstSeenSeedHostedService.cs:78 | `Log.Error(…)` 的續行 `Progress.Failures, MaxRetries, ex.Message);` |
| Services/PermissionChangeMigrationHostedService.cs:65 | `Log.Error(ex, "[SQL] 權限異動遷移失敗…", ex.Message)` |
| Services/PermissionChangeMigrationHostedService.cs:83 | `Log.Error(ex, "[SQL] 權限異動重剖回填失敗…", ex.Message)` |
| Services/PermissionChangeMigrationHostedService.cs:99 | `Log.Error(ex, "[SQL] 舊彙總列清理失敗…", ex.Message)` |
| Services/PrtgSnapshotHostedService.cs:1708 | `Log.Error(ex, "PRTG 快照執行失敗（第 {Failures} 次連續失敗）…", …, ex.Message)` |
| Services/ReportFileMigrationHostedService.cs:61 | `Log.Error(ex, "[SQL] 既有報告檔遷移失敗…", ex.Message)` |
| Services/SchedulerHostedService.cs:687 | `Log.Warn(ex, "單獨保留清除失敗…", ex.Message)` |
| Services/TopIssueBackfillHostedService.cs:74 | `Log.Warn(ex, "[SQL] 來源鍵啟動快判失敗…", ex.Message)` |
| Services/TopIssueBackfillHostedService.cs:131 | `Log.Error(ex, "[SQL] lf_top_issues 聚合欄回填失敗…", ex.Message)` |
| Services/WebAiService.cs:231 | `Log.Debug(ex, "詳情頁對話呼叫失敗（靜默降級）…", ex.Message)` |
| Services/WebAiService.cs:276 | `Log.Debug(ex, "Web AI 呼叫失敗（靜默降級）…", ex.Message)` |
| Services/WorkOrderBackfillHostedService.cs:57 | `Log.Error(ex, "[SQL] 交辦單整併失敗…", ex.Message)` |

### 1.3 計數與規劃、執行紀錄數字的差異

我數到的數字（以守門測試同一組規則、`bf80e179^`、`LogForesight.Web`）：

| 項目 | 行數 |
|---|---|
| 守門規則命中的母體（1.1） | 79 |
| 其中 E-1 修改（提交 `bf80e179` 當下已改） | 38 |
| 其中 E-2 修改（初始化 PRTG 連線失敗六行，E-1 暫列 `ForE5`，E2-43、44、49、50、61、62） | 6 |
| 修改合計 | 44 |
| 保留 | 35（E-1 當時 41 行保留，含上述 6 行暫列） |
| 純記錄檔行（1.2，不在母體） | 21 |

E-1 當時的白名單有 33 條（含 `ForE5` 三條）；E-2 移除 `ForE5` 三條後為 30 條，現況仍是 30 條。`HEAD` 掃描命中 35 行，全部在白名單內：30 行是自 E-1 起沒變的保留行，3 行是 Z 批改過寫法的保留行（E2-33、75、78），另 2 行是 E-1 新增的 `UserFacingMessage.cs` 規則小幫手本身與 `SentinelAdminService.cs` 的安全代碼分支。

與規劃的 82 處、執行紀錄的 92 行對照，我用不同計數規則重掃 `bf80e179^`，找到兩個剛好吻合的規則：

- **82**：`Controllers/` 與 `Services/` 內含 `\bex\.Message\b` 的行，不排除記錄檔行（只認變數名 `ex`）。吻合，不多不少。
- **92**：`Controllers/`、`Services/`、`Auth/` 內符合守門規則的變數名清單（`ex`、`exception`、`*Ex`、`workflowError`、`error`、`domain`、`conflict`、`busy`、`transfer`、`deidentification`）加 `.Message` 的行，不排除記錄檔行。吻合：71 行守門命中＋21 行純記錄檔行＝92。排除了 `Filters/`（5）、`Configuration/`（2）、`Program.cs`（1），這 8 行在守門母體內。

結論：82 與 92 都能由「以行計、含記錄檔行、範圍只到 Controllers／Services（／Auth）」重現，所以它們是不同階段、不同掃描範圍的計數，不是母體定義矛盾。哪一個規則確實是規劃與執行紀錄當時用的，**無法確定**——這是比對吻合得到的推測，沒有當時的指令可佐證。

「修改 25 處」「其餘 34 項進白名單」**無法重現**：

- 25：我數到 E-1 修改 38 行；若以「一個 catch 區塊算一處」約 37 處；以「檔案」算約 18 個檔案；都不是 25。可能是當時以另一種分組（例如把同檔同類合併、或不含記錄檔已有的固定句）計算，推測而已。
- 34：E-1 提交的白名單是 33 條（用 `git show bf80e179:…UserFacingMessageGuardTests.cs` 數 `^        ("` 開頭的列），與 34 差 1，可能含了 `UserFacingMessage.cs` 之外另算了一項（例如 `SentinelAdminService` 在最後一輪才補入）或計到註解行。
- 25＋34＝59，與 92 差 33，與 79 差 20；無法用上述任何規則補齊，視為執行紀錄的概略數字。
- E-2 的類別名前綴（提交說明寫 24 處）屬 Core，不在 `LogForesight.Web`，**不計入**上述任一數字。我在 diff 中數到前綴移除 22 行（`WorkOrderCoordinator` 20 行、`IssueCaseCoordinator` 2 行），與 24 差 2，**無法確定**差異來源（可能算入同檔註解或多行訊息的續行，屬推測）。

## 2. E-前置清單：改訊息字面後，測試與前端對舊字串的比對

方法：對 E-1、E-2 修改過的每個使用者訊息字面，在修改前版本（`bf80e179^`／`69e13774^`）的 `LogForesight.Tests`（含 `JsBehavior/*.behavior.mjs`）、`LogForesight.Web/wwwroot/js`、`LogForesight.Web/HelpContent` 做固定字串搜尋，再比對同一提交的測試 diff。行號為修改前版本。

### 2.1 E-1（`bf80e179`）

| 舊字串 | 比對位置（修改前版本） | 同一提交是否同步修改 |
|---|---|---|
| `直接查詢 PRTG 失敗（{原文}）` | `LogForesight.Tests/PrtgResourceGuardPreviewEndpointTests.cs:192` `Contains("直接查詢 PRTG 失敗")`；`wwwroot/js/pages/prtg-guard.js:105` 是前端自己的來源說明字串，不是比對後端訊息 | 否，且不需要：前綴「直接查詢 PRTG 失敗」保留，斷言仍成立；同提交新增反向測試（回應不含 `secret-prtg`、記錄檔含原文） |
| `解析受監看目標失敗：{原文}`、`PRTG 預覽失敗：{原文}` | 無 | 無比對；同提交新增斷言 |
| AI 工作流 `ex.Message`／`workflowError.Message`（`AiFailure`） | `AiAnalysisSchedulerTests.cs:974` `Assert.Equal("provider connection reset", workflowState.AiFailure)`（例外來源 :966） | 是：同提交改為 `DoesNotContain` 原文＋`Contains("詳細原因見記錄檔")` |
| `強制重新分析的整批重標失敗：{原文}` | 無（`AiAnalysisSchedulerTests.cs:667` 只是注入例外的訊息，不比對畫面文字） | 無比對 |
| `HealthService` 資料庫可達性探測 `error = ex.Message` | 無（`HealthAndPerformanceTests.cs:180` 是註解） | 無比對 |
| `IssueFirstSeenSeed` `Progress.LastError = ex.Message` | `HealthAndPerformanceTests.cs:269` 只 `Assert.NotNull`、`:303` 自己設值，不比對文字 | 無比對 |
| NetIQ 掃描 `掃描 Sentinel「…」失敗：{原文}`、`（{原文}），本次已改用事件掃描` | 無 | 無比對 |
| `探測過程發生未預期錯誤：`、`站台對照…`、`指定主機回填…`、`回填過程…`（PRTG／NetIQ 主控台行） | 無（`SchedulerTickOrderTests.cs:198` 的「排程輪詢發生未預期錯誤」是另一則訊息） | 無比對 |
| 快照 `…感測器補抓失敗（不影響本輪快照）：{原文}` 等四條 `WriteOutput` | `PrtgSnapshotHostedServiceTests.cs:2102`、`:3031` `Contains("感測器補抓失敗")` | 否，且不需要：新句仍含「感測器補抓失敗」；`:1805` 的「狀態補抓 Queue」是另一個停止原因，不受影響 |
| `封存失敗，待寫檔維持原狀：{原文}` | 無 | 無比對 |
| `快照樣本寫入資料庫失敗，N 列留待下次重試：{原文}` | 無 | 無比對 |
| 待寫檔完整性檢查失敗原文（`PrtgSnapshotJournalInspection`） | `PrtgSnapshotHostedServiceTests.cs:715` `Contains("復原檔")`（`LastSkipReason`，由服務自己組句，現行檔案仍含「先備份並檢查」） | 不需要：該句不是 Inspection 的訊息，斷言內容未受影響 |
| `PrtgStructureSync` `ErrorMessage／failure = ex.Message` | `PrtgStructureSyncServiceTests.cs:531` `Assert.Contains("sentinel 讀取失敗", …LastErrorMessage)`（例外來源 :514） | 是：同提交改為 `DoesNotContain` 原文＋`Contains("詳細原因見記錄檔")` |
| `Scheduler` 的 `RunOutcome` 訊息（取消與未預期錯誤） | 無 | 無比對 |
| Sentinel 測試連線 `Message = ex.Message` | `SentinelAdminServiceTests.cs:310` `Contains("連線位址格式不正確", result.Message)` | 否：測試未改。E-1 初版把整段改成固定說明會使此斷言轉紅，被主代理定向加跑 Sentinel 抓到，改在程式端用 `SentinelClientException.InvalidBaseUrlCode` 只放行位址類訊息（見規劃執行紀錄）；測試維持原樣是對的 |
| `測試連線失敗：{原文}`（AD、PRTG）、`測試寄信失敗：{原文}` | `wwwroot/js/pages/settings.js:1696`、`:1738`、`prtg-admin.js:955`、`setup.js:329`、`:459` 是 `error?.message \|\| '測試連線失敗。'` 的後備文字，不是比對 | 無比對 |
| `「AI 額外請求欄位」不是合法的 JSON：{原文}` | 無（`AIServiceExtraRequestFieldsTests.cs:314`、`:377` 的同名字串是 AI 回覆內容，不是這則訊息） | 無比對；同提交新增斷言（不含 `LineNumber` 等，含「不是合法的 JSON」） |
| `重算今日 PRTG 對應失敗: {原文}`、`PRTG 主機對應規則升級失敗：{原文}` | `HostAdminServiceTests.cs:750`、`:763` 是測試替身自己回傳的字串，不經過這兩行 | 無比對 |
| 立即派工失敗組 `Message = ex.Message` | `WorkOrderBoardServiceTests.cs:731` `Assert.Contains("注入", failed.Message)`（例外來源 :770） | 是：同提交改為 `DoesNotContain("注入")`＋`Contains("記錄檔")`，並新增路徑／網址不外洩與領域例外原樣回的測試 |
| `PrtgEffectivenessController` 對 `ArgumentException` 回 400＋`ex.Message` | `PrtgEffectivenessServiceTests.cs:137`（`Assert.Throws<ArgumentException>` 起訖顛倒）、另有上限超過的 `Assert.Throws<ArgumentOutOfRangeException>` | 部分：起訖顛倒那條同提交改為 `Throws<DomainException>`；「日期範圍超過上限」那條沒有改，服務也沒有改，行為落到 500（見第 3 節 3.2 的缺陷說明） |

### 2.2 E-2（`69e13774`）

| 舊字串 | 比對位置（修改前版本） | 同一提交是否同步修改 |
|---|---|---|
| `WorkOrderCoordinator：…`、`IssueCaseCoordinator：…`（Core 前綴 24 處） | 無（搜尋 `Coordinator：` 零命中） | 無比對 |
| `主機分級「…」不合法，僅接受 core、standard 或 test。`（`HostAdminService`、`NetiqHostService` ×2） | 無（搜尋 `僅接受` 只有不相關的 `RuleValidatorTests.cs:340`） | 無比對；同提交新增斷言 |
| `PRTG 取數策略只能是 conservative 或 aggressive。` | `SystemSettingsServiceTests.cs:2777`、`:2788` `Assert.Contains("conservative", ex.Message)` | 是：同提交改為 `Contains("「保守」或「激進」")`，並新增 `DoesNotContain` |
| `目錄版本權杖無效或已逾期，請重新載入。` | 無 | 無比對 |
| `PRTG 帳號或密碼錯誤。`、`PRTG 帳號憑證無效或權限不足。`、`PRTG API token 無效或權限不足。` | `PrtgClientTests.cs:162` `"token 無效"`、`:493`／`:528` `"帳號或密碼"`、`:828` `"帳號憑證"`；`:569`（`"帳號密碼"`）是「回傳登入頁」另一則訊息，`:693`／`:745` 是建構子檢查 | 否，且不需要：新句「PRTG 認證失敗：API token 無效或權限不足。」「…帳號或密碼錯誤，或帳號憑證無效、權限不足。」仍含這些片段；`PrtgClientTests.cs` 不在 E-2 的修改清單，全量通過佐證 |
| `PRTG 回應 OK 但沒有回資料：PRTG 在伺服器端放棄處理（…）`（`ServerGaveUp`） | `wwwroot` 無；`HelpContent/20-prtg.md:113` 引用「PRTG 回應 OK 但沒有回資料」；`PrtgTablePagerTests.cs:515` 只是註解 | 否，且不需要：開頭句不變（新句為「PRTG 回應 OK 但沒有回資料：PRTG 伺服器放棄處理這個查詢…」） |
| `PRTG 伺服器回應錯誤：HTTP {code}` | `PrtgClientTests.cs:1095` `Assert.Equal($"PRTG 伺服器回應錯誤：HTTP {(int)code}", ex.Message)`（資料列不含 400） | 否，且不需要：除 400 外維持原文，測試資料列也不含 400 |
| `連線 PRTG 伺服器失敗：{原文}`、`讀取 PRTG 回應內容失敗：{原文}`、`讀取 PRTG passhash 回應失敗：{原文}` | `PrtgClientTests.cs:1064` `StartsWith("連線 PRTG 伺服器失敗：")`、`PrtgProbeRunnerTests.cs:389`、`PrtgTablePagerTests.cs:746`、`ExternalCallLimitTests.cs:174`（`DoesNotContain`） | 否，且不需要：`TransportFailure` 保留「階段：」前綴；後三者本就以前綴比對 |
| 逾時時的英文原文「The operation was canceled.」 | `PrtgEndToEndChainTests.cs:583` 註解寫「現況：輸出的原因是英文訊息…沒有『逾時』字樣」 | 是：同提交把註解換成斷言（輸出含「逾時」、不以英文原文開頭） |
| `初始化 PRTG 連線失敗：{ex.Message}`（三處服務） | 守門白名單三條（`ForE5`），沒有其他測試 | 是：同提交移除白名單三條（33 → 30） |

### 2.3 前端

`wwwroot/js` 在修改前版本中，沒有任何一處以字串包含、開頭或相等比對後端錯誤訊息的寫法對上本表的舊字串。僅有的兩類命中：

- 顯示用字串：`prtg-guard.js:105`（來源說明）、`prtg-admin.js:143`／`:317`（前端自己算的日期範圍提示）。
- 後備文字：`error?.message || '測試連線失敗。'` 等五處。

另在 `permission-changes.js:286` 有 `err.message.includes('網段')` 比對，對象是子網段輸入格式訊息（`NormalizeSubnetPrefix` 自擲，未被 E-1、E-2 修改）。

## 3. Z 批後續（`c42c5302`）

### 3.1 對 E2 白名單與守門的影響

| 項目 | 影響 |
|---|---|
| 新增 `WorkOrderRuleException`（Core，繼承 `InvalidOperationException`） | 登錄進 `UserFacingMessage.RegisteredTypes`（已登錄型別由 7 個變 8 個）；協調器 19 個規則擲出點改擲它 |
| E2-75／E2-78（`WorkOrderCommandService`／`WorkOrderReplyService` 的 `throw DomainException.Validation(ex.Message)`） | 處置仍是「保留」，但理由由 `SelfAuthored`（捕捉範圍限單一呼叫的自擲中文）改為 `DomainOnly`（只捕捉 `WorkOrderRuleException`；其餘例外記錄後往上拋），白名單理由字串已更新 |
| E2-76／E2-79（另一個 catch：`InvalidOperationException`） | 該 catch 改為 `catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)`，`Log.Warn` 後 `throw;`，由 `ApiExceptionFilter` 回 500 與固定說明；命中行消失，不再需要白名單 |
| E2-33（`IssueOwnerAdminService:319`） | 處置仍是「保留」，條件擴為 `ex is DomainException or WorkOrderRuleException`；白名單片段與理由同步更新（片段字串改變） |
| E2-77（`WorkOrderReplyService:121`） | 沒有改，仍是 `ex is DomainException`；`WorkOrderRuleException` 不是 `DomainException` 子型別，但其來源 catch 已把它轉成 `DomainException`，不受影響 |
| 白名單條數 | 沒有變（30 條）；兩個改寫的條目、一個改寫的條目理由，無新增、無刪除 |
| 守門母體 | `HEAD` 掃描命中 35 行，全部在白名單內；兩個 `throw;` 行不含 `.Message`，不再命中 |
| E-1 殘餘風險「9 處把 Core 的 `InvalidOperationException`／`ArgumentException` 轉成領域例外」 | Z 只處理了其中交辦類 4 處（`WorkOrderCommandService`×2、`WorkOrderReplyService`×2）。剩下 5 處仍依賴「Core 自擲中文」假設：`PrtgScopeEstimateController`×2（E2-14、15）、`PrtgScopePurgeJobsController`×2（E2-16、17）、`ScheduleController`×1（E2-20）；另 `PrtgMonitoringController`（E2-10）捕捉 `InvalidOperationException` 回 409，也同假設 |
| `WorkOrderRuleException` 測試 | 新增 `WorkOrderGuardLeakTests`（規則例外轉 400 保留訊息、框架層 `InvalidOperation`／`Argument` 不外洩、型別已登錄）；`HandlingNoteBackendTests` 把「領域類」注入例外改為 `WorkOrderRuleException`、`WorkOrderCoordinatorTests` 21 行同步；測試與程式在同一提交 |

### 3.2 發現：E-1 修改了行為但漏改比對處（已在 Z 批修正）

**案例：`PrtgEffectivenessController` 的「日期範圍超過上限」。**

- 修改前：控制器 `catch (ArgumentException)` 把訊息放進 400 回應；`ArgumentOutOfRangeException` 是 `ArgumentException` 的子型別，所以「日期範圍最多 N 天」也走 400，使用者看到中文訊息。
- E-1：把整段 try／catch 拿掉，只把「起始日期不可晚於結束日期」改擲 `DomainException.Validation`；服務裡「日期範圍最多 N 天」仍擲 `ArgumentOutOfRangeException`，沒有改。結果：這個路徑由 400 退成 500，使用者看不到訊息。
- 比對處：`PrtgEffectivenessServiceTests.cs` 對這條仍斷言 `Assert.Throws<ArgumentOutOfRangeException>`（修改前版本的測試位置在起訖顛倒那條之後），所以測試維持全綠，沒有抓到這個退化。
- 修正：Z 批（`c42c5302`）把服務改為 `DomainException.Validation($"日期範圍最多 {MaxWindowDays} 天。")`，測試改斷言 `DomainException`＋「日期範圍最多」，並新增端點測試（經 `ApiExceptionFilter` 回 400 與中文訊息）。執行紀錄記為「PRTG 成效端點日期超過上限改回 400（原本落到 500）」。

其餘 E-1、E-2 修改的字串，我沒有找到「測試或前端仍比對舊字串、卻沒有同步修改」的案例；兩個沒同步改測試的情形（Sentinel 位址格式、PRTG 認證訊息）都是因為新訊息仍包含被比對的片段或程式端保留了原訊息。
