# 規模化改版體檢的修復規劃（2026-08-06）

> **狀態：A～D 批全部實作完成（2026-08-06），各項的「實作結果」小節記錄實際落地方式。**
> 對象是 [SCALE-REVIEW-2026-08-06.md](SCALE-REVIEW-2026-08-06.md) 找到的 14 項問題。
> 基準：分支 `feature/scale-issue-first`（起點 1448 測試綠，完成後 1484 綠）。
>
> 每一項寫到「改哪個檔、改什麼、為什麼是這個作法、會不會動到別人、怎麼驗收」。
> 已確定的架構決策：**排程維持在 Web 行程內執行**，不拆獨立 worker。
>
> 收尾體檢時發現 §六的批次表漏列了 D3（§一有規劃、批次表沒收），實作跟著批次表走
> 因此一度漏做——已於全案體檢輪補齊，教訓是**批次表要從章節清單機械式產生，不要手抄**。

---

## 零、先講整體：這批修復對專案的影響輪廓

修復本身有三種性質，混在一起做會互相干擾，所以下面刻意分成四批：

| 性質 | 項目 | 特徵 |
|---|---|---|
| **純錯誤修正** | D1／D2／D3／D4／D5 | 行為明確錯誤，改法沒有爭議，測試釘得住 |
| **升級路徑的安全性** | S-1／G1／S-2／G2 | **會動到啟動流程與資料搬移**，是唯一可能丟資料的一群，必須整組一起設計 |
| **需要產品決策** | D6／D7＋H1／W1 | 技術上有多種作法，選哪個取決於「使用者期待什麼」 |
| **長期營運** | G4／S-4／S-3／G3 | 不修不會壞，但上線三個月後會回頭咬人 |

**最重要的一件事**：S-1／G1／S-2／G2 這四項**不能拆開做**。
它們共同回答同一個問題——「從舊版升級到這一版的那一次啟動，發生了什麼、
中途掛掉會怎樣、搬到一半使用者看到什麼」。目前這個問題沒有一個完整的答案，
四項各修一半反而會製造出更難查的中間狀態。§三整組設計。

---

## 一、第 0 批：合併 dev 之前必須修

### D1 使用者詳細頁的 H4 修復失效

**檔案**：`LogForesight.Web/wwwroot/js/pages/user-detail.js`

**現況**（第 113~115 行附近）：

```js
function viewerHasNoBusinessScope() {
    return getCurrentUser()?.isServerAdmin === true;   // ← Promise，恆為 undefined
}
```

**改法**：把目前登入者納入 `load()` 既有的 `Promise.all`，存成模組變數，
其餘兩個呼叫點（`renderKpi`／`renderOpenWork`）改讀該變數。

```js
let currentUser = null;                     // 模組層，與 records.js／handler-detail.js 同一個慣例

async function load() {
    ...
    const [detail, workload, me] = await Promise.all([
        api.get(`/api/admin/users/${userId}/detail`),
        api.get(`/api/handlers/${userId}/workload?includeResolvedDays=true`),
        getCurrentUser()
    ]);
    currentUser = me;
    ...
}

function viewerHasNoBusinessScope() {
    return currentUser?.isServerAdmin === true;
}
```

**為什麼不是 `await getCurrentUser()` 就地取用**：`renderKpi`／`renderOpenWork` 是同步的
渲染函式，改成 async 會讓呼叫端跟著變成 async，牽動整個 `load()` 的順序；
而 `getCurrentUser()` 本來就有快取，放進既有的 `Promise.all` 是零額外成本。
**這也是全站其他 4 個呼叫點的既有寫法**，統一比較不會再有人踩到。

**影響面**：只有這一頁。不動 API、不動其他角色。

**驗收**：以 `svc-lfadmin` 開 `/admin/users/5`——
KPI 的「處理中案件」「逾期」顯示 **「—」**＋「您的帳號沒有業務資料範圍，無法計算」，
「處理中項目」區塊顯示說明卡而非空表格；以 `demo-admin` 開同一頁維持原本的數字。

**防再犯**：`getCurrentUser` 是全站唯一容易誤用的 async helper。
建議在 `core/api.js` 的函式註解補一行「**必須 await**；它有快取，放進 Promise.all 零成本」，
並在本輪的 review 文件留下這個案例。

> **實作結果（A 批）**：照上述改法落地，含 `core/api.js` 的防再犯註解。

---

### D2 問題排行的「分類」欄空白

**檔案**：
- `LogForesight.Core/Persistence/IIssueAggregateQuery.cs`（`IssueAggregate` 加欄位）
- `LogForesight.Core/Persistence/Sql/EfIssueAggregateQuery.cs`（GROUP BY 帶出）
- `LogForesight.Web/Services/IssueRankingBuilder.cs`（填進 DTO）

**現況**：`IssueRankingBuilder` 寫死 `Category = string.Empty`，註解說「由呼叫端補」，
但沒有任何呼叫端補。儀表板重點問題卡與報表問題排行的「分類」欄因此空白。

**改法**：`lf_top_issues` 本來就有 `category` 欄且既有列都有值（不必回填）。
在既有的 `GroupBy` 投影裡加一個聚合：

```csharp
// 同一個 (Source, EventId) 的 category 實務上恆定（規則決定），
// 取 MIN 只是為了在 SQL 端有個確定性的選法——不是「隨便挑一個」，
// 而是「這一組本來就只有一個值，任何確定性的選法都對」
Category = g.Min(x => x.Category),
```

**為什麼不用「出現次數最多的」**：那要多一趟 GROUP BY，而分類在同一簽章下不會變
（`KnownIssueCatalog.Classify` 依規則決定，規則以簽章為鍵）。
真的出現兩種值代表規則被改過，此時取哪一個都不影響「這是什麼類別」的判讀。

**影響面**：`IssueAggregate` 是本輪新增的型別，只有 `IssueRankingBuilder` 一個消費者。
儀表板卡與報表表格的前端不必改（它們本來就在讀 `i.category`）。

**驗收**：`GET api/dashboard/summary` 與 `GET api/reports/summary` 的
`topIssues[].category`／`issueRanking[].category` 不再是空字串；
畫面「分類」欄顯示中文類別名（`CATEGORY_NAMES` 對照）。

**補測試**（同時擋下 G3）：新增 `IssueRankingBuilderTests`，
至少涵蓋 分類帶出／`IsNew` 與前期對比／`HostRatio` 分母為 0 時不除零／
`OpenHostCount` rollup 跨多個完整簽章合併。

> **實作結果（A 批）**：照上述改法落地，`IssueRankingBuilderTests` 10 項（G3 一併結案）。

---

### D3 並發衝突回 500 而不是 409

**檔案**：
- 新增 `LogForesight.Core/Persistence/ConcurrentUpdateException.cs`
- `LogForesight.Core/Persistence/Sql/EfIssueHandlingStore.cs`／`EfIssueCaseStore.cs`／`EfRecordHandlingStore.cs`
- `LogForesight.Web/Filters/ApiExceptionFilter.cs`

**現況**：三張新表設了 `IsConcurrencyToken`，衝突**偵測得到**（拋
`DbUpdateConcurrencyException`），但沒有人攔——`ApiExceptionFilter` 只認 `DomainException`，
其餘一律 500＋通用訊息。規劃寫的是「回 409 並提示重新整理」。

**為什麼不能直接丟 `DomainException`**：它在 `LogForesight.Web.Models`，
而 store 在 Core——Core 不能反向參照 Web（§4.2 分層責任邊界）。

**改法**：

1. Core 新增一個語意明確的例外：

```csharp
/// <summary>
/// 樂觀鎖衝突：讀取之後、寫入之前，同一列被別人改過。
/// 這**不是**故障，是多人同時操作的正常結果——呼叫端應該讓使用者重新整理後再試，
/// 而不是回一個沒有上下文的伺服器錯誤。
/// 放 Core 而非 Web：拋出點在 store，Core 不能參照 Web 的 DomainException。
/// </summary>
public sealed class ConcurrentUpdateException : Exception { ... }
```

2. 三個 store 的 `SaveChanges()` 包 try/catch，把 `DbUpdateConcurrencyException`
   轉成它，訊息帶上「哪一筆」（主機／日期／問題），讓使用者知道是哪一件被搶先改了。

3. `ApiExceptionFilter` 的 switch 加一支：

```csharp
ConcurrentUpdateException conflict => (
    StatusCodes.Status409Conflict,
    ApiErrorCodes.Conflict,
    conflict.Message),
```

`ApiErrorCodes.Conflict → 409` 的對應在 `StatusCodeFor` 已經存在，不必新增。

4. 前端：`api.js` 對 409 已經走一般錯誤 toast 路徑，訊息直接顯示即可；
   風險日詳情的處理面板另外在收到 409 後**自動重新載入當日資料**——
   使用者要的是「看到最新狀態」，不是自己按 F5。

**影響面**：只影響三個新 store 的寫入路徑。既有的 `EfJsonBlobStore.Mutate`
有自己的重試迴圈（那是 blob 層，不受影響）。

**驗收**：測試以兩個 `DbContext` 讀同一列、各自修改、依序 `SaveChanges`，
第二次應拋 `ConcurrentUpdateException`；Web 端整合測試確認回 409 且訊息可讀。

#### 實作結果（全案體檢輪補做）

這一項在 §六批次表被漏列、實作跟著批次表走因此一度漏做，收尾體檢時發現補齊：

- Core 新增 `ConcurrentUpdateException`（訊息固定為「〈哪一筆〉剛剛已被其他人修改…請重新整理後再操作一次」）。
- 三個 store 的 `SaveChanges` 包轉換：`EfRecordHandlingStore.Save`（主機＋日期）、
  `EfIssueHandlingStore.SaveMany`（批次取第一筆衝突列描述）、`EfIssueCaseStore` 的
  Save／SaveMany（案件標籤＋主機）。
- `ApiExceptionFilter` 加 409 分支；`ConcurrentUpdateTests` 釘住配線兩端
  （EF 權杖真的攔得下後寫、例外經 Filter 變成 409＋可讀訊息）。
- 前端 `handling-panel.js` 的指派與狀態儲存路徑在 409 後自動 `initHandlingPanel` 重載當日資料。

---

### D6 報表選「未處理」時，KPI 歸零而問題排行仍顯示全部

**檔案**：`LogForesight.Web/Services/ReportService.cs`、`wwwroot/js/pages/reports.js`

**現況實測**：

| `handlingScope` | KPI 問題總數 | 主機排行 | 問題排行 |
|---|---|---|---|
| `all` | 605 | 1 台 | 55 種 |
| `open` | **0** | **0 台** | **55 種** |

同一屏數字打架，與體檢 H4 同一類「畫面說謊」。

**兩個方案（需要你選）**：

| | 甲：畫面誠實說明（建議先做） | 乙：問題排行也套 scope |
|---|---|---|
| 作法 | 問題排行卡加一行常駐說明「此排行不受上方『顯示範圍』影響，一律呈現期間內全部問題」 | 把處理狀態 join 進問題聚合，`scope` 映射到 `OpenHostCount`（未處理主機數）後過濾 |
| 成本 | 極小（一行文案） | 中（等同做完 §10.6 的資料面） |
| 語意 | 誠實，但兩個區塊仍是不同母體 | 整頁同一個母體，符合使用者對「選擇器管整頁」的心智模型 |
| 風險 | 使用者可能仍覺得奇怪 | 「這個問題影響幾台」在 scope 下變成「未處理的有幾台」，需要同步改欄位標題 |

**建議**：**第 0 批先做甲**（把說謊變成誠實，成本一行），
**乙併入 §10.6 一起做**——因為 §10.6「排除已有結論的問題」本來就要把處理狀態
join 進聚合，屆時 scope 自然接得上，欄位標題也可以一次改對。

**為什麼不是現在就做乙**：§10.6 需要決定「部分處理」怎麼呈現
（`N 台未處理／M 台已處理`），那是產品決策不是技術問題；
為了修一個顯示矛盾而先把資料面做一半，會讓 §10.6 之後再改一次。

> **實作結果（A 批）**：甲案落地（`reports.js` 問題排行卡常駐說明）；乙案併 §10.6，記在 BACKLOG。

---

### S-1 升級時中斷會靜默遺失資料（見 §三，與 G1／S-2／G2 一起設計）

---

## 二、第 0 批的收尾：小修

### D4 依問題視角的 CSV 與畫面欄位不同步

**檔案**：`LogForesight.Web/wwwroot/js/pages/records.js`（`csvHeader()`／`csvRow()`）

**改法**：`issue` 分支的欄位與表格對齊：

```
['來源','Event ID','分類','嚴重度','主機數','涵蓋範圍起','涵蓋範圍迄',
 '出現天數','期間天數','總次數','最近出現','距今天數','處理概況','處理人']
```

**為什麼「涵蓋範圍」拆成兩欄、「出現密度」拆成兩欄**：CSV 是給人再加工的
（貼進 Excel 排序、樞紐分析），`2026-05-06 ~ 2026-07-28` 與 `3/98` 這種
「畫面上好讀」的合併字串在試算表裡是死的。畫面用合併、匯出用拆開，
是同一份資料的兩種正確呈現。

**驗收**：切到依問題視角按「複製為 CSV」，貼上後欄數與表頭一致，
日期欄可在 Excel 直接排序。

> **實作結果（A 批）**：落地（`csvHeader`／`csvRow` 的 issue 分支，日期與密度皆拆獨立欄）。

---

### D5 歷程續號的健壯性

**檔案**：`LogForesight.Core/Persistence/Sql/EfRecordHandlingStore.cs`（`ReadLastLogId`）、
`Sql/EfJsonLogStore.cs`（`ReadLastLine`）

**現況**：只讀最後一行；那一行若損毀就回 0，續號從 1 重來 → 與既有歷程 **LogId 重號**，
同一天的歷程排序（`ThenBy(l => l.LogId)`）會錯亂。

**改法**：`ReadLastLine()` 改為 `ReadLastLines(int count)`（取最後 N 行，預設 20），
`ReadLastLogId()` 由新到舊逐行嘗試解析，第一個成功的就是續號起點；
全部失敗才回 0 並記一筆 Warn（那代表歷程尾端整段損毀，值得被看見）。

**為什麼不回到「整份讀」**：那正是 N4 要修掉的啟動阻塞。
20 行是「損毀通常是連續一小段」與「不要退回全表掃描」之間的取捨，
而且是索引 `(log_key, seq)` 的一次反向 seek，成本與讀 1 行幾乎相同。

> **實作結果（A 批）**：落地（`ReadLastLines(20)` 由新到舊逐行試解析，全敗記 Warn）。
> D 批的 `處理歷程_清理後續號不重來` 測試另外釘住「清最舊端不影響續號」。

---

## 三、第 1 批：升級路徑（S-1／G1／S-2／G2 整組設計）

> 這一組是**唯一可能丟資料**的部分，也是唯一必須整組一起想的部分。
> 目前四項各有一半的答案，合起來反而有中間狀態。

### 3.1 現況的三個缺口

1. **G1**：`HandlingBlobMigrator` 在 `StorageBackend` 建構式裡**同步**執行。
   資料量大時（2000 台約 108 萬列／350 MB）要數分鐘，
   Windows 服務啟動逾時預設 30 秒——會被 SCM 砍掉。
2. **S-1**：冪等判斷是 `if (ctx.IssueHandlings.Any()) return;`——**全表**判斷。
   被砍掉時已經 `SaveChanges` 的批次留在表裡，下次啟動看到「表非空」
   **永遠跳過剩下的資料**。
3. **S-2**：搬完不刪 blob（正確，當備份），但**沒有任何標記**說它已失效。
   日後若有舊版執行檔或某段殘留程式再以 blob 路徑寫入，會產生兩份互不相通的處理狀態。

### 3.2 設計：一次搬完、可續跑、期間明確唯讀

**四個組成**：

**(a) 遷移狀態成為一等公民**
新增 blob key `handling_migration`（單一物件，隨 `JsonBlobSingleton` 慣例）：

```
{ "state": "pending" | "running" | "done",
  "startedAt": ..., "completedAt": ...,
  "issueHandlingDone": bool, "issueCasesDone": bool, "recordHandlingDone": bool }
```

「有沒有搬完」從此**是一個被明確寫下的事實**，不是從「表空不空」反推。
S-1 與 S-2 都由這一項解決：續跑看得懂進度，blob 也有了失效標記。

**(b) 每一份 store 的遷移在單一交易內完成**
`ctx.Database.BeginTransaction()` 包住「整份 AddRange ＋ SaveChanges ＋ 標記該份完成」。
被砍掉 → 整份回滾 → 表仍為空 → 下次啟動重來。
**不做分批 commit**：分批就要記「搬到第幾筆」，而 blob 是無序的整份 JSON，
沒有穩定的續跑游標；三份各自一個交易在資料量上是可行的
（單份最大約 108 萬列，SQLite／SqlServer 都撐得住一個交易）。

**(c) 遷移移出啟動路徑，改由背景服務執行**
比照 `TopIssueBackfillHostedService`：`StorageBackend` 建構式只做
「建表／升級 schema／判斷是否需要遷移並把狀態設為 pending」（毫秒級），
實際搬移由新的 `HandlingMigrationHostedService` 在背景做。

**(d) 遷移未完成期間，處理狀態相關的寫入一律擋下**
新增 `MigrationGateMiddleware`（放在 `UseAuthorization` 之後、`CsrfHeaderMiddleware` 之前）：

- 狀態非 `done` 時，對 `/api/records/*/handling*`、`/api/handling/*` 的
  **非 GET** 請求回 **503**＋「資料搬移中（已完成 N/M），請稍候再試」；
- 其餘請求照常（查詢面讀新表，遷移中會看到部分資料——這是可接受的，
  因為讀到的都是已經搬好的，不會是錯的，只是不完整）；
- 前端在收到 503 時顯示一致的說明，不要退化成通用錯誤。

**為什麼要擋寫而不是擋全站**：升級後第一件事通常是有人登入看畫面。
整站 503 會讓人以為升級失敗；只擋處理狀態的寫入，讀得到的內容仍然正確，
而唯一會產生「新舊兩份資料」的路徑被關死。

### 3.3 G2「統計中」的標示（同一組，因為使用者感受到的是同一件事）

**現況**：`/api/health/detail` 有 `backfillInProgress` 等三個欄位，
但**前端零使用**，而且那支端點需要 `Maintain`——一般使用者看不到。

**改法**：把「數字還不準」帶進**看得到數字的那些 DTO**：

- `DashboardDto` 與 `ReportSummaryDto` 各加一個 `IssueStatsPending`（bool）＋
  `IssueStatsPendingHint`（可直接顯示的字串，例：「問題統計回填中（已完成 664/1,200），
  次數與影響範圍可能偏低」）；
- 由 `IssueRankingBuilder` 注入 `TopIssueBackfiller` 後填入（它已是 Singleton）；
- 前端在重點問題卡與報表問題排行卡的標題旁顯示這行提示。

**為什麼不讓前端去打 `/api/health/detail`**：那支要 `Maintain`，
而看排行的是全部角色；而且「這張卡的數字準不準」屬於這張卡的資料，
不是站台健康資訊——放在同一份 DTO 裡，就不可能有人忘了查。

**同一個機制也用在遷移**：遷移未完成時同樣把 `IssueStatsPending` 設為 true，
提示改為「處理狀態搬移中」。使用者不需要分辨是哪一種背景工作，
他只需要知道「現在看到的數字還不是最終值」。

### 3.4 影響面與風險

| 影響 | 說明 |
|---|---|
| `Program.cs` | 新增一個 middleware 註冊（順序有要求，見 3.2-d） |
| `StorageBackend` | 建構式**變快**（不再搬資料）；新增遷移狀態判斷 |
| 三個 EF store | 不改 |
| 全新安裝 | 完全不受影響（無 blob → 狀態直接 `done`） |
| **風險** | middleware 順序放錯會擋到登入；遷移狀態 blob 自己損毀時要能安全退回 `pending`（重搬一次是冪等的，因為表空） |

**驗收**：
1. 造一份 100 萬列的舊格式 `issue_handling` blob，啟動 → 服務**在數秒內就緒**，
   背景遷移進行中，處理狀態寫入回 503，儀表板顯示「搬移中」；
2. 遷移途中強制中止行程 → 重啟後**從頭重搬**且最終筆數正確（不是少一半）；
3. 遷移完成後 `handling_migration` 標記為 `done`，重啟不再搬。

#### 實作結果（B 批）

整組照 §3.2 落地：`HandlingBlobMigrator` 拆成 `Evaluate()`（毫秒級判定，留在
`StorageBackend` 啟動路徑）＋`Run(CancellationToken)`（背景，
`HandlingMigrationHostedService` 延遲 3 秒後跑）；狀態存 `handling_migration` blob
（`HandlingMigrationState`，`Unknown/Pending/Running/Completed`＋逐部分完成旗標＋
`LastError`）；`MigrationGateMiddleware` 註冊在 `UseAuthorization` 之後、
`CsrfHeaderMiddleware` 之前，未完成時對 `/api/handling`＋`/api/records` 的**非 GET** 回 503
——**只擋寫入不擋讀**，搬移期間畫面照常可看。每個部分單一交易搬完
（blob 是無序 JSON、沒有穩定的續搬游標，「半批重來」比「單一交易重來」更難對）；
G2 的「統計中」由 `IssueRankingBuilder.StatsPending()` 把遷移＋回填合併成一個旗標。

**升級路徑實測**（`升級路徑_百萬列blob的遷移` 基準）：309 MB／100 萬列 blob，
啟動（Evaluate）969 ms、背景遷移 262 秒、遷移後筆數逐列核對相符；
中止重跑冪等（`HandlingBlobMigrationTests` 10 項）。

---

## 四、第 2 批：需要決策或成本較高

### W1 M2（1024px 動作欄看不到）——本輪惡化，建議提前處理

**實測**：體檢當時 1307px 內容／705px 可視（溢出 602px）；
本輪加兩欄後 **1512px／709px（溢出 803px）**。

**兩層修法**（建議兩層都做，成本都小）：

1. **捲動可見性**（`core/ui.js` 的 `renderTable` ＋ `site.css`）：
   `.lf-table-wrap` 內容溢出時，右緣加漸層陰影＋一行「← 可左右捲動」提示，
   捲到底自動消失。**一處改、全站表格受惠**，也順帶解掉體檢 M2 的原始抱怨。
2. **動作欄固定在列末**（`position: sticky; right: 0`）：
   只對有動作欄的表格套用（依問題視角、主機清單）。
   動作是「看到問題→做點什麼」的終點，它不該是最容易被捲掉的那一欄。

**為什麼不改成「把三顆按鈕收成一顆 ⋯ 選單」**：那會讓每個操作多一次點擊，
而 admin 在依問題視角上做的正是批次操作——把最常用的動作藏起來是反效果。
sticky 欄位保住了可見性又不增加點擊。

#### 實作結果（C 批）

兩層都做了。1024×768 實測：內容寬 1536px／可視 709px，
`documentElement.scrollWidth` 未超過視窗（**頁面本身不橫捲**，只有表格容器內捲），
動作欄 `position: sticky; right: 0` 生效，`← 可左右捲動`
在初次渲染即出現、捲到右底自動消失、捲回再出現。

**過程中揪出一個原設計的缺陷**：`bindScrollAffordance` 原本靠
`requestAnimationFrame` 做首次量測、並且只 `ResizeObserver.observe(wrap)`。
兩者都不可靠——

* `wrap` 的寬度由版面決定，**內容再寬它都不會變**，
  所以「內容變寬」這個真正的觸發條件永遠偵測不到；
* `requestAnimationFrame` 在**非前景分頁**會延後到分頁可見為止，
  而那時 `wrap` 尺寸沒變、`ResizeObserver` 不會補一次 → 提示**永久缺席**。

實際在瀏覽器量到的症狀就是這個：class 只有在使用者**已經捲過一次**之後才出現，
也就是提示只在「已經發現可以捲」之後才願意告訴你可以捲。
改為掛進 DOM 後**同步量一次**（讀 `scrollWidth` 本來就會強制重排），
並額外 `observe(table)`。

### D7＋H1 指派給沒有處理能力的人

**現況**：`mgr-wang`（只有 `ViewAll`）側欄顯示徽章「1」，點進去看得到案件、
但沒有任何可操作的按鈕（H2 已修）。**M7 把 H1 的後果從隱性變成每天可見**。

**改法**（體檢 H1 的原建議，本輪未做）：
`BulkAssignIssueCase`／日層級指派解析處理人時，一併以 `RoleCapabilityMap`
（含負責人隱含能力）檢查對方是否具 `Handle`；
沒有的話比照既有的 `assigneeNoAccess`，回一份 `assigneeCannotHandle` 清單，
前端在指派 modal 顯性提示——**不擋，但要講**。

**為什麼不擋**：把工作「知會」給主管是合理用法；
問題不在於能不能指派，而在於指派的人不知道對方動不了。

**與 M7 的關係**：H1 修好之後，徽章仍然會顯示——那是正確的（他確實名下有工作），
但至少指派當下有人被告知了。若實測後仍覺得困擾，再考慮「無 Handle 者的徽章改為
灰色＋tooltip 說明」，但那是後話。

#### 實作結果（C 批）

`BulkAssignIssueCaseResultDto.AssigneeCannotHandle`（每位處理人一筆，
帶 `HandlerName` 與受影響的 `HostCount`），前端在指派成功的 toast 之後
另發一則 warning，與既有的 `assigneeNoAccess` **分開講**——
「看不到」還能靠授與範圍補救，「動不了」則是工作進了對方清單卻做不了任何事，
兩者的後續處置不同，合併成一句話會讓人不知道該去改哪裡。

**能力判定抽成 `LogForesight.Web/Auth/UserCapabilityResolver`**：
「群組角色聯集 ∪ 負責人隱含 User 角色」這條規則原本在
`IdentityService.ResolveCapabilities` 與 `UserAdminService.GetUserDetail`
各有一份，這次要用時本來會出現第三份——**H3 就是這樣壞的**。
改為單一來源，`IdentityService` 轉為委派。
這也避免了把登入相關依賴拖進處理服務（原本打算直接注入 `IdentityService`）。

### S-3 排程留在 Web 行程內的隔離措施（本輪決策的配套）

決策已定：**不拆 worker**。規劃 §8.4 甲案的三項配套目前一項都沒做：

| 項目 | 改法 | 為什麼 |
|---|---|---|
| 連線池隔離 | `StorageBackend` 提供第二個 DbContext 工廠，連線字串加 `Max Pool Size=N`（建議 4），供 `AnalysisOrchestrator` 使用 | 夜間分析不該把連線池吃光，讓使用者請求排隊等連線 |
| 主機間讓出＋NetIQ 並行度 | 每台主機處理完 `await Task.Yield()`（或可設定的間隔）；`NetiqOptions.MaxParallelServers` 已存在，補上「Web 內執行時的上限」 | 讓 thread pool 有機會處理前景請求 |
| 執行中的畫面標示 | `SchedulerRunState.IsRunning` 已有，且 `/api/health/detail` 已能看到；補到儀表板頂部一行「分析進行中（第 N/M 台），畫面回應可能較慢」 | 使用者知道為什麼變慢，就不會以為壞了 |

**驗收**：6000 台資料集下觸發一次完整分析，同時以另一個瀏覽器操作——
儀表板與問題查詢的回應時間不應超過 P0 基準的兩倍。

#### 實作結果（C 批）

| 項目 | 落地方式 |
|---|---|
| 連線池隔離 | `StorageBackend` 建構式新增 `maxPoolSize`（`ApplyMaxPoolSizeIfUnset`），`AnalysisOrchestrator` 以 `AnalysisMaxPoolSize = 4` 建立自己的 backend |
| NetIQ 平行度上限 | `NetiqPipelineService.ResolveParallelism`＝`Clamp(設定值, 1, AnalysisOrchestrator.MaxParallelServersInWeb = 3)`，被夾住時 console 明講 |
| 主機間讓出 | NetIQ 逐台（`RunBatchDayAsync` 內）與本機逐日（`AnalysisOrchestrator`）各加一次 `await Task.Yield()` |
| 執行中的畫面標示 | 新端點 `GET /api/run-activity`（**任何登入者**皆可讀，不掛 `[Permission]`）＋儀表板 `#dashboard-run-activity` 一行告示，30 秒輪詢、跑完自動消失 |

**連線池隔離的重點不是「限制 4 條」**：SqlServer 的連線池**以連線字串為鍵**，
加上 `Max Pool Size` 之後分析才真正擁有**自己的池**，兩件事是同時發生的。
使用者已自行指定 `Max Pool Size` 時尊重設定不覆寫，但那也代表兩邊共用同一個池，
因此該情況會寫一筆 log 讓事後查得到。

`/api/run-activity` 刻意與 `/api/admin/schedule/status` 分開：後者是維運視角
（觸發來源、下次觸發、上次成敗、可否停止），需要 DevMonitor／Maintain；
前者只回答「現在慢是不是因為在跑分析、跑到哪了」。變慢的是**所有人**的畫面，
只讓維運看得到原因等於沒有配套。

**尚未執行**：6000 台實機併發量測（需要壓測資料集與第二個瀏覽器工作階段，
屬於實測階段）。已完成的是機制本身與其單元驗證。

---

## 五、第 3 批：長期營運

### G4 `handling_log` 從來不會被清理

**檔案**：`LogForesight.Core/Service/AnalysisOrchestrator.cs`（清理段）、
`LogForesight.Core/Models/SystemSettings.cs`、`RetentionOptions`

**現況**：清理涵蓋 `lf_daily_records`／`batch_runs`／`import_logs`／`audit`／
`lf_risky_events`／export 報告檔，**沒有 `handling_log`**。
6000 台環境下它是千萬列級且無上限成長，而 P3 的 `GetLogs` 最佳化
（以插入時間在 SQL 端窄化）效果隨表成長而遞減。

**改法**：沿用既有樣式，在清理段加一行

```csharp
var handlingLogPruned = new EfJsonLogStore(...).Prune(cutoff);
```

保留天數**與稽核同組**（`AuditRetentionDays`，預設 730）而不是 `RunLogRetentionDays`：
處理歷程是「誰在什麼時候把這個問題標成什麼、為什麼」——那是**追責用的證據**，
性質接近稽核，不是「這次跑了什麼」的執行歷程。

### S-4 三張處理狀態表沒有保留天數

**現況**：`lf_daily_records` 有 `RetentionDays`（預設 120），
但 `lf_issue_handling`／`lf_record_handling` 沒有——分析紀錄被清掉之後，
對應的處理狀態變成永遠不會被讀到的孤兒，卻一直佔著空間並拖慢查詢。

**改法**：清理段在 `historyService.Prune(...)` **之後**，
刪掉 `record_date < cutoff` 的處理狀態列（與分析紀錄同一個 `RetentionDays`）。
`lf_issue_cases` **不依日期刪**，改為刪「已結案且 `closed_at` 早於 cutoff」的案件——
進行中案件不論多舊都要留著（它代表「還沒處理完」）。

**沿用 P0.5 的作法**：只撈主鍵、分批 `ExecuteDelete`、單次上限、超過留待下次。

**文件**：`DB-SPEC.md` 的「資料量推估」與「清理策略」要補這三張表——
目前那兩節完全沒提到它們，等於承諾了一個沒有人負責的成長曲線。

#### 實作結果（D 批，G4＋S-4 一起）

清理段新增一區 `1b-2`，**排在 `historyService.Prune` 之後**（順序有意義：
前者決定哪些日期已過期，反過來的話這一輪會漏掉剛過期的那幾天）：

| 對象 | 方法 | 保留天數 |
|---|---|---|
| `lf_issue_handling` | `EfIssueHandlingStore.Prune` | `RetentionDays`（120） |
| `lf_record_handling` | `EfRecordHandlingStore.Prune` | `RetentionDays` |
| `lf_issue_cases` | `EfIssueCaseStore.Prune`（**只刪已結案、依 `closed_at`**） | `RetentionDays` |
| `handling_log` | `EfRecordHandlingStore.PruneLogs` | `AuditRetentionDays`（730） |

分批刪除的骨架抽成 `BatchedPrune`——只撈主鍵、分批 `ExecuteDelete`、
單次上限 20 萬列、超過留待下次，並在超過上限時把剩餘筆數寫進 log。
抽出來的理由不是省行數，是**不讓第五個清理對象再自己發明一次**。

**進行中案件的舊逐日列一併刪、案件本身留著**：看似不一致，實則是同一條原則——
案件的狀態、處理人、說明都在 `lf_issue_cases`，逐日列只是「那天的那個問題結了沒」，
而那天的分析紀錄已經不在了。「還沒處理完」這件事不會因為清理而消失。

**歷程從最舊的一端刪不影響續號**：`ReadLastLogId` 讀的是**最後**幾行。
已補測試模擬「清理後站台重啟再續寫」，確認不會從 1 重號。

`DB-SPEC.md` 的保留策略一節同步改寫：原文寫的是 2026-07-20 的構想
（`Storage.DbRetentionDays` 統一 730），與實際落地的**四個保留期**不符，
已改為以實作為準並補上四張表的容量推估。

### G3 `IssueRankingBuilder` 沒有單元測試

已併入 D2 的作法（見 §一 D2 末段）。

---

## 六、建議的執行順序與批次

| 批次 | 內容 | 閘門 | 結果 |
|---|---|---|---|
| **A** | D1、D2（＋G3 測試）、D4、D5、D6 甲案 | 純錯誤修正，改完即可合併 dev | ✅ `d183c6a` |
| **B** | S-1／G1／S-2／G2 整組（§三） | **需要造大 blob 實測**才算完成；這是唯一可能丟資料的一組 | ✅ `cfac339`（100 萬列實測：啟動 969ms、背景搬 262s） |
| **C** | W1、D7＋H1、S-3 | 需要你確認 D6 走甲或乙、W1 是否接受 sticky 欄位 | ✅ `b0db6b4` |
| **D** | G4、S-4（含 DB-SPEC 更新） | 上線前完成即可 | ✅ `d721e44` |
| **（補）** | **D3**——本表當初漏列（§一有規劃），實作跟著本表走因此一度漏做 | 全案體檢輪發現補齊 | ✅ |

**A 與 B 不要混在同一個 commit**：A 是行為修正、B 會動到啟動流程，
混在一起的話，升級出問題時無法快速判斷是哪一類改動造成的。

---

## 七、需要你決定的四件事（已全數定案，2026-08-06「依據建議執行」）

1. **D6 走甲（一行說明）還是乙（問題排行也套 scope）？**
   → **甲**已做（reports.js 常駐說明）；乙併入 §10.6 一起做（見 BACKLOG）。
2. **W1 是否接受「動作欄 sticky 固定在列末」？**
   → **接受**，已實作並於 1024×768 實測驗證（見 W1 的實作結果）。
3. **G4 的處理歷程保留天數要跟稽核（730 天）還是執行歷程（90 天）？**
   → **跟稽核（`AuditRetentionDays`，730）**——它是追責證據，證據不能比被追究的事件早消失。
4. **B 批要不要先造 100 萬列的舊格式 blob 做升級實測？**
   → **要**，已實測：309 MB blob、100 萬列，啟動 969ms、背景遷移 262 秒（見 §三的實作結果）。
