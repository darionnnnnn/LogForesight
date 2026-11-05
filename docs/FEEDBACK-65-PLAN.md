# 回饋第 65 輪規劃：macOS／Windows 雙平台開發測試一致

> 狀態：實作完成，待換模型體檢與併 dev
> 基準：dev@e0ef0e1f（macOS 實測 8,498 通過／14 略過／1 失敗；Windows 基線 8,502 通過／11 略過）
> 來源：在 macOS 首次跑全量，檔案鎖案例失敗；另發現行為測試對 Node 版本敏感
> 實作方式：主代理自己做（改動小、機械；不派 subagent）

## 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A | 快照復原佇列鎖在 Unix 不互斥 | 正式碼 2 處＋測試 | 無 |
| B-1 | Node 版本下限單一判定 | `NodeRuntime`＋測試 | 無 |
| B-2 | 移除實驗旗標＋守門 | 23 檔機械改＋守門測試 | B-1 |
| B-3 | CI 釘 Node、加 macOS job | `ci.yml` | B-2 |
| C | 文件與全量收官 | 純文件 | A、B |

## 批次 A：快照復原佇列鎖

### 現況與核對結果

- `PrtgSnapshotJournal.OpenWriterLock` 以 `FileShare.Read` 開 `.lock`。實測 macOS／.NET 8：第二個持有者開檔成功；`FileShare.None` 時不論第二者用什麼模式都擲 `IOException`。
- 根因：Unix 上 .NET 以 `flock` 模擬共用模式，只有 `None` 是獨佔鎖。DB-SPEC 寫「以 OS 排他寫入保護」，意圖本來就是排他。
- `.lock` 沒有其他讀取點；正式碼沒有列舉復原目錄的路徑。
- 鎖失敗時 `JournalFailed` 對操作者顯示通用訊息「請檢查磁碟空間、檔案鎖定與服務帳號權限」，`OpenWriterLock` 自己的說明只進記錄檔。
- `RestoreJournal` 每輪都會再呼叫，成功時清除 `_journalError`，所以鎖衝突是逐輪重試，不是永久停擺。

### 定案

1. `.lock` 改 `FileShare.None`，兩平台語意一致。
2. 鎖取不到時擲專屬例外 `PrtgSnapshotJournalLockedException : IOException`，`JournalFailed` 直接顯示其訊息。不用比對訊息字串（專案既有規則）。
3. `OpenWriterLock` 的 catch 會接住所有 `IOException`（也含磁碟類錯誤），不以平台專屬 HResult 區分，訊息措辭寫「可能」並同時點出磁碟與權限。不選 HResult：Windows 與 Unix 的錯誤碼不同，多一條平台分支換不到多少精確度。

### 驗收

- A-1 `dotnet test --filter "FullyQualifiedName~PrtgSnapshotJournalTests|FullyQualifiedName~PrtgSnapshotHostedServiceTests"` 在 macOS 全綠，含原本失敗的案例。
- A-2 新測試：另一個 journal 持有鎖時 `TickAsync` 不採集，`LastSkipReason` 含鎖衝突說明；持有者釋放後下一輪恢復（`LastSkipReason` 不再是鎖衝突）。
- A-3 突變：`None` 改回 `Read`，macOS 上 A-1 必須紅；還原後綠。
- A-4 Windows：CI 的 Windows job 綠。

## 批次 B：Node 版本與行為測試

### 現況與核對結果

- 23 檔 25 處傳 `--experimental-default-type=module`。Node 26 已移除，258 筆同時紅，訊息是 node 的 `bad option`，看不出是版本問題。
- 實測 Node 22.23 不帶旗標可跑行為腳本且 stderr 為空；stdin 案例只需 `--input-type=module`。
- `NodeRuntime.Probe` 只看 `--version` 結束碼，不判斷版本。
- CI 用 runner 預設 Node，版本隨 image 漂移；CI 只跑 Windows，Unix 檔案語意沒有自動守門。

### 定案

1. 版本下限只在 `NodeRuntime` 判定一處（暫定 22.7，ESM 語法偵測預設開啟的版本）。低於下限視同不可用，沿用既有「`LF_REQUIRE_NODE=1` 失敗、否則略過」的分流，訊息含偵測到的版本與下限。
2. 移除全部實驗旗標，不另做包裝。stdin 案例保留 `--input-type=module`（它不是實驗旗標）。
3. 守門測試：`LogForesight.Tests` 的 `.cs` 不得出現 `--experimental-`。
4. CI 以 `actions/setup-node` 釘 22，矩陣加 `macos-latest`。

### 驗收

- B-1 `dotnet test --filter "FullyQualifiedName~NodeRuntimeTests"` 綠；版本判定 Theory 涵蓋 `v22.7.0`通過、`v22.6.9`不通過、`v26.13.0`通過、空字串／亂碼／`null` 不通過。
- B-2 `UiTests|JsModuleImport|BehaviorTests|ApiTimeout|NodeRuntime` 篩選在 Node 22 全綠且命中數 > 0。
- B-3 突變：下限改 99，行為測試紅且訊息含 99 與偵測到的版本；還原後綠。
- B-4 突變：任一測試檔塞回舊旗標，守門測試紅；還原後綠。
- B-5 以 Node 26 跑同一篩選全綠，確認下限不封頂。
- B-6 CI 兩個 job 都綠。

## 批次 C：文件

### 改動

- README macOS 節：Node 需求 22.7 以上；`dotnet` 裝在 `~/.dotnet` 時的 PATH；`python3`。
- CLAUDE.md：基線補 macOS 數字；「不要做」補 `FileShare` 一行與 Node 旗標一行。
- DB-SPEC 快照 journal 段：鎖被占用時該輪略過、下一輪重試。

### 驗收

- C-1 `grep` 確認 README、CLAUDE.md、DB-SPEC 含上述內容，且現行文件沒有「第 65 輪」敘事字眼（本 PLAN 除外）。
- C-2 全量 `dotnet test`：macOS 0 失敗，略過 14；通過數記入下方執行紀錄。

## 明確不做

- 不引入 `IClock`、不處理 Linux 大小寫敏感、不動 `\r\n` 字面：本輪沒有症狀。
- 不做 Node 旗標集中包裝：刪掉比包起來簡單。
- 不以真實 Node 22.7 驗證下限：22.7 這個數字來自 Node 版本說明，本機只驗到 22.23 與 26，因此標暫定。

複檢完成：與 `RestoreJournal` 逐輪重試、`LF_REQUIRE_NODE` 分流、`.gitattributes` 皆無衝突；批次間只有 B-2 依賴 B-1 的訊息，順序已排。

## 執行紀錄

| 段 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|
| A | `97a8e1fc` | A-1 106 筆綠；A-2 新測試綠；A-3 突變 2 筆紅、還原後綠；A-4 CI Windows 綠 | |
| B-1 | `86ce3027` | B-1 22 筆綠；B-3 突變下限 99，訊息含 `v22.23.4` 與 `99.0`，還原後綠 | B-3 突變只跑 `StorageBehaviorTests`（5 筆）：判定點只有一處，不必跑整批 |
| B-2 | `88ecdb04` | B-2 568 筆綠；B-4 突變守門紅；B-5 Node 26.13 下 271 筆綠 | 篩選詞漏 `HelpManualLayoutTests`、`AiStateFrontendTests` 這類會啟動 node 的類別，B-2／B-5 另以「實際呼叫 `NodeRuntime.Run` 的 24 個類別」跑 271 筆補上 |
| B-3 | `fa32c90a` | B-6 CI run 42025829809 兩個 job 皆綠 | |
| C | 待提交 | C-1 敘事字眼 0 筆；文件相關測試 17 筆綠；C-2 macOS 全量 8,511 通過／14 略過／0 失敗；CI Windows 8,514 通過／11 略過、CI macOS 8,511 通過／14 略過 | 待確認：使用者的 Windows 開發機 Node 版本是否達 22.7；下限未以真實 22.7 驗證 |
