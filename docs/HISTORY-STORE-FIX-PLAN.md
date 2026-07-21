# history.txt 儲存層修正規劃（A1 原子寫入／A2 查詢語意／A3 合約測試基底）

> 規劃日期：2026-07-21。狀態：**已實作完成（2026-07-21）**，實作紀錄見文末。
> 三項都是「已上線程式碼」的問題，與 NetIQ 功能無關；A2 語意選項與規劃範圍
> 已於 2026-07-21 與使用者確認（顯式錨定日期／本輪只涵蓋 A1–A3）。
> 本案同時是 docs/DB-PLAN.md「txt ↔ DB 一致性保證」機制 #2（介面語意即規格）與
> #3（合約測試基底）在 `IAnalysisRecordStore` 上的落實，實作完成後回寫 DB-PLAN。

## 問題與定案總覽

| # | 問題 | 定案 |
|---|---|---|
| A2 | `ReadRecent(days)` 實作是「最近 N **筆**」（`OrderByDescending(Date).Take(days)`），介面註解卻寫「近 N **天**」；且 `TrendAnalyzer` **不會過濾 targetDate 之後的紀錄**，回補中間缺漏日時未來紀錄會混入該日的趨勢基準 | 介面改為 **`ReadRecent(DateTime anchorDate, int days)` 顯式錨定**，語意＝anchor 往回 N 天（含 anchor 當日）的日期區間 |
| A1 | `AttachWeeklyCheckup`／`Prune` 用 `File.WriteAllLines` 整檔重寫，Web 同時在讀同一份檔案，重寫瞬間可能讀到截斷內容；`TryParse` 對壞行**靜默丟棄**——Web 少幾天資料且無任何跡象 | 整檔重寫改「寫 temp → `File.Replace`」原子替換＋讀取端 tolerant share＋壞行顯性記 WARN；**不引入** `.lock` 跨程序鎖（理由見下） |
| A3 | `JsonlAnalysisRecordStoreTests` 仍是具體類別，`IAnalysisRecordStore` 的語意（ReadRecent 窗口、HasRecord 冪等、Prune 邊界、AttachWeeklyCheckup 更新語意）沒有合約測試釘住，DB 實作屆時無從驗收 | 仿 `HostStoreContractTests` 模式抽 **`AnalysisRecordStoreContractTests` 抽象基底**，A2 的新語意以合約案例固定 |

---

## A2：`ReadRecent` 顯式錨定日期

### 潛在 bug 的具體情境（為什麼「以今天為錨」也不對）

`TrendAnalyzer.Apply`（[TrendAnalyzer.cs:44]）拿到 history 後只排除 `DataIncomplete` 與
Security 無權限日，**不會過濾日期**——`ReadRecent` 給什麼它就拿什麼當基準。
回補流程是「找出近 14 天缺漏的日子、由舊到新分析」，中間缺漏日的情境：

> 三天前的執行在寫入當日紀錄前中斷 → 該日缺紀錄，但昨天、今天都有。
> 下次執行回補該日時，`ReadRecent(14)` 取「最近 14 筆」＝**大多是該日之後的紀錄**，
> 未來的資料進了它的 14 日平均與「首次出現」判定。

以「今天」或「最新一筆」為錨的日期窗修不掉這個（錨仍落在缺漏日之後）；
只有把錨交給呼叫端（分析哪一天就錨在哪一天）才是結構性的修法。

### 介面變更

```csharp
public interface IAnalysisRecordReader
{
    /// <summary>
    /// anchorDate 往回 days 天（日期區間 [anchor-(days-1), anchor]，含兩端）內的紀錄，依日期升冪。
    /// 錨定日之後的紀錄一律不回傳——呼叫端分析哪一天，基準就只能是那一天之前的世界。
    /// DB 實作對應：WHERE date >= @anchor - (days-1) AND date <= @anchor ORDER BY date。
    /// </summary>
    List<DailyAnalysisRecord> ReadRecent(DateTime anchorDate, int days);

    /// <summary>是否存在任何紀錄（首次執行判定用）。DB 實作對應：EXISTS</summary>
    bool HasAnyRecord();

    bool HasRecord(DateTime date);              // 不變
    DateTime? LastWeeklyCheckupDate();          // 不變
}
```

- **舊簽名 `ReadRecent(int days)` 直接移除、不留預設 anchor 的便利多載**——留著的話
  「忘了傳 anchor」編譯照過、行為照舊錯，正是本次要關掉的失誤路徑。呼叫端只有
  3 處＋1 個測試替身，全數明改。
- **含 anchor 當日**的理由：體檢在當日分析寫入之後執行，窗口必須含當天剛寫入的紀錄；
  每日分析呼叫時當日紀錄尚未寫入（`HasRecord` 防重複），自然不會撈到自己，兩情境同一語意即可。
- **新增 `HasAnyRecord()`** 的理由：現在 `WeeklyCheckupService.ShouldRun` 用
  `ReadRecent(1).Count == 0` 表達「從未有任何紀錄」；改日期窗後 `ReadRecent(today, 1)`
  的意思變成「**今天**有沒有紀錄」，語意不再等價。用意圖明確的方法取代，不讓
  「有沒有歷史」搭在查詢窗口的便車上。

### 呼叫端調整（共 3 處＋測試替身）

| 位置 | 改法 | 行為影響 |
|---|---|---|
| `LogAnalysisService.AnalyzeDayAsync`（[LogAnalysisService.cs:129]） | `ReadRecent(targetDate, historyDays)` | **中間缺漏日 bug 修復**；順序回補時（檔案裡都是 targetDate 之前的紀錄）「往回 14 天」與「最近 14 筆」在無缺漏環境完全相同 |
| `WeeklyCheckupService.RunAsync`（[WeeklyCheckupService.cs:73]） | `ReadRecent(checkupDate, intervalDays)` | 無（體檢日就是最新日） |
| `WeeklyCheckupService.RunAsync` 找上次體檢結論（[WeeklyCheckupService.cs:92]） | `ReadRecent(checkupDate, Math.Max(21, intervalDays * 3))` | 上次體檢若落在 21 天窗外會找不到→「無延續脈絡可帶入」，本來就定義為非錯誤，可接受 |
| `WeeklyCheckupService.ShouldRun`（[WeeklyCheckupService.cs:60]） | 改用 `HasAnyRecord()` | 語意由「有沒有任何紀錄」明確承接，行為不變 |
| `WeeklyCheckupServiceTests` 的 `FakeReader` | 跟隨新簽名 | — |

### 行為變更的誠實申報（實作時寫進 commit 訊息）

有缺漏日的既有環境，A2 上線後第一次執行的趨勢基準會與之前不同：

- 窗外的舊紀錄不再墊進基準 → `DaysSeenInHistory` 變小、原本被誤判為「重複發生」的
  簽章可能改判「首次出現」——**這是修正不是退化**（README 對趨勢層的描述本來就是
  「近 14 日」，本次讓名實相符）。
- 無缺漏的環境（絕大多數日子）行為完全不變。

---

## A1：整檔重寫原子化與讀取容錯

### 前提釐清：為什麼**不需要** hosts.json 那套 `.lock` 跨程序鎖

`hosts.json` 有批次與 Web **兩個寫入者**，所以需要跨程序互斥（步驟 2 已做）。
`history.txt` 不同：**寫入者只有批次**（`Append`／`Prune`／`AttachWeeklyCheckup` 全在批次；
Web 經 `IAnalysisRecordQuery` 唯讀），且批次自身有 `Global\LogForesight` 單一執行個體
互斥鎖——寫入者對寫入者的競態**已在結構上排除**。剩下的唯一問題是
「讀者讀到重寫到一半的檔案」，這用原子替換就能解，加 `.lock` 只會讓 Web 的每次查詢
與批次寫入互相排隊，是純粹的代價沒有收益。

### 變更明細（全部在 `JsonlAnalysisRecordStore`，介面不動）

1. **`RewriteAtomic(string[] lines)` 私有方法**：寫 `history.txt.tmp`（UTF-8 無 BOM）→
   `File.Replace(tmp, path, null)`；目標不存在時 `File.Move`。`AttachWeeklyCheckup`（:77）
   與 `Prune`（:116）的 `File.WriteAllLines` 改呼叫它。與 `JsonCollectionFile.WriteAtomic`
   同一手法，但不共用實作（一個是整份 JSON 陣列、一個是 JSONL 行集合，強行抽共用只會
   多一層無意義的抽象）。

2. **`File.Replace` 的 sharing violation 重試**：讀者持檔的瞬間 Replace 會擲 `IOException`。
   短退避重試（10 次 × 50ms 量級，實作時定常數）；超過仍失敗就**讓例外外拋**——
   單寫入者環境下衝突只可能來自秒級的讀取，重試必然成功；真的失敗代表有未知的長時間
   持檔者，Prune/體檢附掛靜默放棄比顯性失敗更糟。

3. **讀取端 tolerant share**：`ReadAll` 的 `File.ReadLines`（預設 `FileShare.Read`）改為
   `FileStream(path, Open, Read, FileShare.ReadWrite | FileShare.Delete)` ＋ `StreamReader`
   逐行——讀者從此不會擋住寫入端的 Replace（第 2 點的重試因此極少真的觸發），
   也容忍批次同時 Append。檔案不存在回空清單（維持現狀）。

4. **壞行顯性化**：`ReadAll` 統計 `TryParse` 失敗行數，>0 時記 WARN（含檔案路徑、
   壞行數、前幾筆的行號）。防 log 洪水：記住上次的壞行數，**數字有變化才記**——
   Web 每次查詢都全檔重讀，同一批壞行每次都刷 WARN 等於把訊號淹掉。

5. **`Append` 不變**（`File.AppendAllText`）：與第 3 點的讀者 share 相容；讀者恰好讀到
   「append 寫了半行」時該行 TryParse 失敗被略過，下一次讀取即恢復——瞬時現象，
   由第 4 點的 WARN 可見，可接受，不為此引入行級鎖。

---

## A3：`AnalysisRecordStoreContractTests` 合約測試基底

### 結構

仿 `HostStoreContractTests`／`AnalysisRecordQueryContractTests` 既有模式：

```csharp
public abstract class AnalysisRecordStoreContractTests : IDisposable
{
    protected abstract IAnalysisRecordStore CreateStore();
    // ...共用的 Record 建構 helper
}

public class JsonlAnalysisRecordStoreContractTests : AnalysisRecordStoreContractTests { ... }
```

既有 `JsonlAnalysisRecordStoreTests` 的 5 個案例處置：

- 搬進基底（屬合約，兩後端都必須一致）：無風險日精簡（`RecordStorageShaper` 是共用
  規則，DB 後端也呼叫同一份）、風險中以上完整保留、週體檢附掛＋`LastWeeklyCheckupDate`、
  `Host`/`DeepDives` 序列化讀回、`HasRecord`。
- JSONL 特定（留在衍生類別或獨立測試類別）：壞行略過＋WARN 行為、原子重寫的併發案例。

### 新增合約案例（釘住 A2 語意與既有未釘語意）

| 案例 | 釘住的語意 |
|---|---|
| `ReadRecent_錨定日往回N天_窗外較舊紀錄不回傳` | 缺漏日不得由更舊紀錄補位（14 日平均的分母誠實） |
| `ReadRecent_錨定日之後的紀錄不回傳` | **中間缺漏日 bug 的迴歸測試**——實作時先寫、A2 改完由紅轉綠 |
| `ReadRecent_含錨定當日` | 體檢窗口含當天剛寫入的紀錄 |
| `ReadRecent_依日期升冪` | 呼叫端（prompt 組裝）依賴的順序 |
| `HasAnyRecord_空為false_有紀錄為true` | `ShouldRun` 的首次執行判定 |
| `HasRecord_同日不同時刻視為同一天` | 回補冪等的日界比對 |
| `Prune_保留天數邊界_cutoff當天保留` | 邊界日不被誤刪（`>= cutoff`） |
| `AttachWeeklyCheckup_日期不存在_不擲例外不寫入` | 「安靜略過＋WARN」是契約不是實作巧合 |

### JSONL 特定案例（A1 的驗證）

- `整檔重寫時有讀者持檔_寫入仍成功`：以 tolerant share 開一個讀取 handle 不放，
  執行 `AttachWeeklyCheckup` → 應成功（重試生效）且內容正確——手法同
  `JsonCollectionFileLockTests` 的外部持有者模擬。
- `壞行_略過且其餘紀錄照常讀回`：檔案中間插一行垃圾，`ReadRecent` 回傳其餘紀錄。

---

## 檔案異動清單

| 檔案 | 變更 | 對應 |
|---|---|---|
| `Core/Persistence/IAnalysisRecordStore.cs` | `ReadRecent` 新簽名＋語意註解（含 DB WHERE 對應）、新增 `HasAnyRecord` | A2 |
| `Core/Persistence/JsonlAnalysisRecordStore.cs` | `ReadRecent` 日期窗實作、`HasAnyRecord`、`RewriteAtomic`＋重試、tolerant share 讀取、壞行 WARN | A1+A2 |
| `LogForesight/Service/LogAnalysisService.cs` | `ReadRecent(targetDate, historyDays)` | A2 |
| `LogForesight/Service/WeeklyCheckupService.cs` | 三處呼叫改錨定＋`ShouldRun` 改 `HasAnyRecord` | A2 |
| `Tests/WeeklyCheckupServiceTests.cs` | `FakeReader` 跟隨新簽名 | A2 |
| `Tests/AnalysisRecordStoreContractTests.cs`（新） | 合約基底＋上表案例 | A3 |
| `Tests/JsonlAnalysisRecordStoreTests.cs` | 改為衍生類別＋JSONL 特定案例 | A3 |
| `docs/DB-PLAN.md` | 一致性機制 #2/#3 標注已於 `IAnalysisRecordStore` 落實 | 收尾 |

不需要動的：README（趨勢層「近 14 日」的描述本來就是 A2 之後的正確語意）、
`IAnalysisRecordQuery` 及其合約測試（另一介面，步驟 1 已完成）、`RecordStorageShaper`。

## 實作順序與驗收

1. **A3 先行（紅燈）**：建合約基底、搬既有案例、寫 A2 的新案例——
   「錨定日之後不回傳」此時應失敗，證明測試真的在測。
2. **A2**：介面＋實作＋3 個呼叫端一次改完（行為變更集中在同一個 commit，好回溯）。
   → 全測試轉綠。
3. **A1**：`RewriteAtomic`＋tolerant share＋WARN＋JSONL 特定案例。
4. **驗收**：建置零警告、全部單元測試、`--selftest` 76 項；手動驗證一項——
   批次執行中（可用回補大量日期製造長寫入窗）同時重整 Web 問題查詢頁，確認無缺天、
   無例外。

## 實作紀錄（2026-07-21 完成）

建置零警告、**490 單元測試**（新增 24 個）、76 項 `--selftest` 全數通過；
完整套件連跑 3 次、併發測試連跑 5 次均穩定。

**兩個迴歸測試已實測會分辨新舊行為**（沿用本專案「實測拿掉一欄會 FAIL」的做法）：
暫時還原 `Take(days)` → 3 個 `ReadRecent` 合約案例失敗；暫時還原 `WriteAllLines` →
`重寫期間_先前開啟的讀取handle仍看到完整舊內容` 失敗。

### 規劃時未預見的三件事（都由併發測試逼出來）

規劃把「批次寫入期間 Web 查詢」列為**手動**驗收（重整頁面確認）。實作時改寫成
可重複執行的併發測試（持續重寫 × 持續讀取），結果一次抓出三個規劃階段沒想到的問題——
人工重整只能碰運氣撞上那幾毫秒，不可能發現這些：

1. **讀取端也需要重試**。規劃只在寫入端加了重試。實測讀者在 `File.Replace` 的瞬間開檔會
   擲 `IOException`（共用違規）——那會讓 Web 查詢在重寫瞬間直接噴錯誤頁，比原本的
   torn read 更糟。已補上 `OpenForRead` 的重試。
2. **`File.Replace` 有「目的檔短暫不存在」的空窗**。修掉第 1 點後，322 次併發讀取仍有
   **79 次讀到 0 筆**——空窗期的 `FileNotFoundException` 被當成「檔案不存在」回空清單。
   最終解法：讀取端對「檔案不見」也重試，但**只在此檔曾被成功開啟過時**
   （`_fileSeen` 旗標）——首次執行真的還沒有 history.txt 時不空等。
3. **`File.Move(overwrite)` 不是更好的替代**。中途改用它（無空窗）後，
   兩個「讀者持檔」測試轉為失敗：MoveFileEx 只要目的檔被開啟就直接失敗，
   Web 持續查詢時寫入端會反覆重試到放棄。

**最終取捨（寫進程式碼註解）**：選 `File.Replace` 而不是 `File.Move(overwrite)`，因為
「寫入端因讀者而失敗」無法在讀取端補救，而「替換空窗」可以——讀取端重試即可。
兩邊要一起看才成立，單獨改一邊都不對。

### 一併修正

- `File.Replace`／開檔的重試改為同時涵蓋 `UnauthorizedAccessException`（與 `IOException`
  同屬暫時性碰撞，`JsonCollectionFile` 的鎖檔取得也是這樣處理）。
- 併發測試本身加保險絲：writer 的 `done.Cancel()` 移進 `finally`＋`CancelAfter(30s)`——
  第一版 writer 擲例外時 reader 會無限迴圈，把一個失敗的測試變成掛住整個測試回合。

### 實際檔案異動

與規劃清單一致，額外多了 `_fileSeen`／`MissingFileRetryCount` 兩個欄位，
以及測試檔的併發案例。`docs/DB-PLAN.md` 一致性機制 #2／#3 已於本案在
`IAnalysisRecordStore` 落實。

## 風險與回退

- A2 是行為變更：有缺漏日的環境，第一次執行後趨勢判定可能與前次不同
  （「重複發生」→「首次出現」方向），屬修正，已於上文申報；無缺漏環境零差異。
- A1 純防禦性，無行為變更；`File.Replace` 重試失敗的例外外拋是新的失敗模式，
  但它取代的是「靜默寫壞檔案」，失敗方向正確。
- 回退：三項各自獨立 commit，任一項有問題可單獨 revert（A3 依賴 A2 的語意定案，
  但測試案例本身不影響產品行為）。
