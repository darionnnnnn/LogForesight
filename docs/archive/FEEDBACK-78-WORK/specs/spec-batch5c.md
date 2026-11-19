# 第 78 輪批次 5c 實作規格：報表整期載入的記憶體尖峰

專案：/Users/wayne/project/LogForesight（.NET 8），分支 `feature/feedback-78`。先讀 `CLAUDE.md`（快取與授權維度、`RecordRepository` 顯示規則相關各條）。
說明與註解用台灣繁體中文。忽略 `docs/archive/`。**不得 git commit／push／stash／checkout／restore**，主代理會驗收後提交。看到白名單外的未提交改動不得還原。

本輪原則：**不改運作邏輯**。回應內容逐欄相同，只降低單次請求的記憶體尖峰。
對帳測試一律**先在未修改的程式上寫好並確認通過**再開始改（可用 `git worktree add` 在 scratchpad 建 HEAD 乾淨副本；不要用連結把主專案目錄接過去；用完 `git worktree remove`）。

白名單：
- `LogForesight.Web/Services/ReportCategoryTrendService.cs`
- `LogForesight.Web/Services/ReportEffectivenessService.cs`
- 新增測試檔；既有測試檔只能新增案例，不得改既有斷言（必須調整時停下來回報）。

## 背景（主代理已讀碼確認）

`ReportCategoryTrendService.Build` 以 `_repository.QueryLightweight(new RecordQueryFilter { From = from, To = to })` 一次載入整個期間（報表期間上限 366 天）所有風險等級的主機日，每筆連帶全部問題子列，再逐筆累計；去重集合 `seen` 的鍵含日期。`ReportEffectivenessService` 同樣一次載入期間＋14 天觀察期。3000 台 × 一年的上限情境是一次在記憶體建出約百萬個主機日物件與其問題子列。

`QueryLightweight` 經過 `RecordRepository`，會套用可見範圍、日風險顯示設定、嚴重度顯示設定等規則；**不要繞過它**，也不要把這些規則在別處重寫一份。

## 契約

### 5-6a 問題類型趨勢：依日期分段載入
- 把期間切成連續、不重疊的日期段（暫定每段 7 天；段的切法以日期為界，不跨日），逐段呼叫同一個 `QueryLightweight`（篩選條件除了 `From`／`To` 之外完全相同），逐段累計後丟棄該段的紀錄。
- 這樣做等價的理由要在註解寫明：累計只依各筆紀錄本身與「（主機, 問題鍵, 日期）去重」，去重鍵含日期，不同日期段之間不會互相影響；`HostLookup`、靜音判斷、分桶對每筆紀錄獨立。
- 輸出（列、各格計數、各格最高嚴重度、排序、`CellMax`）逐欄相同。

### 5-6b 處理成效：先分析能否分段，能才做
- 先讀懂 `ReportEffectivenessService` 的計算：哪些累計只依單筆紀錄、哪些需要跨日期（例如再發生率需要「之後是否又出現」、觀察期延伸 14 天）。
- **只有在能證明分段後結果完全相同時**才分段（例如只把「建索引所需的少數欄位」逐段收集進較小的結構，再用原本的計算）。不能證明就**不做**，在回報說明原因。不得為了能分段而改變計算方式。

## 驗收（全部寫成測試並跑綠；主代理會重跑並做突變）

| # | 驗收 |
|---|---|
| A1 | 問題類型趨勢：30／90／180 天、期間長度不是 7 的倍數、跨月、同一（主機, 問題, 日期）在不同紀錄重複出現、合併主機（舊識別的紀錄歸到存活主機）、靜音區間跨段落邊界、可見範圍受限、嚴重度全站隱藏、日風險顯示只開高風險各一例，回應與改動前逐欄相同 |
| A2 | 問題類型趨勢的配置量：同一份資料下 180 天期間的單次 `QueryLightweight` 最大回傳筆數降為約一段的量（以計數替身或 `MemoryProbe` 證明，不寫毫秒門檻） |
| A3 | 處理成效（若有做）：同 A1 的情境類型＋觀察期跨段落邊界、再發生的那一天落在下一段，回應逐欄相同 |
| A4 | 兩支報表的快取鍵與授權維度不變；既有測試全綠 |

驗收指令（全部要綠）：
```
dotnet test --filter "FullyQualifiedName~ReportAnalyticsTests|FullyQualifiedName~ReportServiceTests|FullyQualifiedName~ReportCategoryTrend|FullyQualifiedName~ReportEffectiveness|FullyQualifiedName~PrtgEffectivenessServiceTests|FullyQualifiedName~RecordRepository|FullyQualifiedName~Authorization"
```

## 停止條件與回報
- 卡住超過兩次嘗試、或契約與程式實況矛盾，停下來回報，不要繞路。
- 完成前自己做突變（例如把段落邊界改成重疊一天、或拿掉去重鍵中的日期，確認測試轉紅，再用檔案備份還原並 cmp 確認）。
- 回報（250 字內）：改了哪些檔案、每條驗收對應的測試名稱、5-6b 有沒有做與理由、突變結果、驗收指令最後一行結果、未解項。
