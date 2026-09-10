# 回饋第 41 輪規劃：PRTG 位址解析對裝置側亂值做 DNS、阻塞式逾時

> 狀態：全案完成已併 dev（體檢輪修正 11 條、終檢見文末）
> 基準：dev@cde4791（3669 綠）→ 本輪收斂 3725 綠（略過 6）
> 來源：使用者實測——設定頁「資源守門 › 自動偵測並填入」在偵錯器內於 `PrtgAddressResolver.DnsLookupWithTimeout`
> 停在 `SocketException: 無法識別這台主機`，`host` 是 PRTG 裝置 host 欄位的值（形如 `10.2xx.x.x`），不是使用者填的 NetIQ 網址。
> 實作方式：Claude 自己做（3 檔＋測試）。

## 核對結果（整體）

| 事實 | 位置 |
|---|---|
| 自動偵測對**每一台 PRTG 裝置**的 host 呼叫 `resolver.Resolve`；不是合法 IP 就送 DNS | `PrtgResourceGuardTargets.FindDevicesForHost`（[PrtgResourceGuardTargets.cs:286](../LogForesight.Core/Service/PrtgResourceGuardTargets.cs)） |
| 主機對應也對每台裝置 `Resolve`，而且是**規格定案**（§4「device 的分組鍵用解析層的結果」，R40） | `PrtgHostMapper.MapForDate`（[PrtgHostMapper.cs:95](../LogForesight.Core/Service/PrtgHostMapper.cs)）、`docs/PRTG-SPEC.md` §4 |
| DNS 走 `Task.Run + Wait(2000)`：逾時後 task 不取消、佔執行緒；例外在 Task 內擲出，偵錯器 Just My Code 視為「使用者未處理」而中斷。非偵錯執行時被 `catch` 吞掉，功能不失敗但每個解析不到的值付 2 秒 | `PrtgAddressResolver.DnsLookupWithTimeout`；BACKLOG「PRTG 位址解析與守門即時來源改非同步」已記錄同一問題，本輪為其觸發時機 |
| resolver 每個請求 `new`，快取只在單一實例內 | `SettingsController.PreviewPrtgResourceGuard`、`PrtgHostMapRefresher`、`PrtgStructureSyncService`、`PrtgDailyPipeline` |
| `PrtgAddress.HostToken` 只切 scheme／第一個 `/`／單一冒號 port；不檢查剩餘字串是否像主機名稱。`10.2xx.x.x`、`10.2.3.4 (舊機)`、`10.2.3.256`、`10.2.3.4.5` 之類都會原樣送 DNS | `PrtgAddress.cs` |
| 使用者側的來源位址（Sentinel `BaseUrl`、`PrtgUrl`）形式為 `https://IP:port` 與 `https://內部憑證主機名:port`，經 `Uri.Host` 取出後皆正常；問題不在來源側 | `PrtgResourceGuardTargets.Resolve` 步驟 2 |

## 討論項目（已定案）

**D1. 裝置側要不要保留 DNS？**
- 我上一則建議的 A（裝置側完全不 DNS）會推翻 §4 的 R40 定案：填 DNS 名稱的裝置在主機對應會退回「略過（無 IP）」，名稱型裝置永遠對不到主機。
- **改建議 A′（本 PLAN 依此寫）**：兩處都保留三段比對，但 DNS 只在「值看起來是主機名稱」時才做（批次A），逾時改非同步可取消且降到 1 秒（批次B），守門偵測再加一層「裝置側 DNS 只在字面比對落空後才做、且整趟有總預算」（批次C）。主機對應是排程／背景作業，不加總預算。
- 若你仍要 A，批次C 改為「裝置側只走純語法＋字面比對」，並同步改 §4 定案與兩個既有測試（`MapForDate_device的Ip為DNS名稱且能解析時對到主機` 會反轉）。

**D2. 亂值的實際內容。** 使用者確認偵錯器裡的 `host` 就是字面的 `10.2xx.x.x`——PRTG 上某台裝置 host 欄位的佔位值。它依 RFC 是合法主機名稱字元，光靠字元集擋不住，因此批次A 在字元集之外另加「壞掉的 IPv4」判定；規則本體以批次A 定案（經體檢輪修正）為準。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A | `PrtgAddress` 新增「可送 DNS 的主機名稱」判定，resolver 據此守門 | 小 | 無 |
| B | resolver 的 DNS 改 `GetHostAddressesAsync`＋`CancellationToken` 逾時 1 秒，移除 `Task.Run + Wait` | 小 | 無 |
| C | 守門偵測：裝置側 DNS 延後到字面比對落空、整趟總預算、統計訊息 | 中 | A、B |
| D | 文件：PRTG-SPEC §4／§6／§7／§12、WEB-SPEC §9.9e／§9.10、BACKLOG 該條結案 | 小 | A–C |
| E | 四角度回檢修正 | 小 | A–D |
| F | PRTG 模組總開關找不到、同步在未啟用時擲例外 | 小 | 無 |
| G | 排程頁三張卡的按鈕互斥（執行中只留「停止」） | 小 | 無 |

順序 A → B → C → D → E → F → G，全部完成。

## 批次A：主機名稱守門

### 現況與核對結果
`PrtgAddress.HostToken` 回傳任何非空字串；`PrtgAddressResolver.Resolve` 對 `Normalize` 為 null 的一切 token 送 DNS。

### 定案
- 新增 `PrtgAddress.IsDnsCandidate(string? token) : bool`（純語法、無 IO）：
  1. token 非空、長度 ≤ 253、只含 `[A-Za-z0-9._-]`（Windows 內網名稱偶有 `_`，解析器實務上接受）；
  2. 以 `.` 拆段，每段 1–63 字、不以 `-` 開頭或結尾；
  3. **不得**是「整串沒有任何字母」或「第一段全數字且每段只含數字或 x」的形狀（那是壞掉的 IPv4，如 `10.2xx.x.x`、`192.168.1.100x`、`10.2.3.256`；
     這條規則改了三次：原案「每段以數字開頭」擋不住字面的 `10.2xx.x.x`；實作改「第一段全數字」，體檢抓到誤擋 `1.dc.corp.local`；
     體檢改「每段 ≤ 3 字」，終檢抓到既誤擋 `163.com` 又漏擋 `192.168.1.100x`——最後收窄到「只有數字與 x」才同時擋得住佔位值、放得過真網域）；
     結尾單一個點在 `HostToken` 統一去掉（三層共用同一把鍵）；只接受 ASCII（IDN 不送 DNS，規格 §4 明寫）；
  4. 含 `:` 者（裸 IPv6 拆 port 後仍含冒號）一律 false——它若合法早在 `Normalize` 通過。
- `Resolve` 在查快取前先過 `IsDnsCandidate`，false 直接回 null（**不寫快取，因為沒付 IO**）。
- `HostToken` 不改語意（字面比對仍要用它）。

### 改動
1. `LogForesight.Core/Service/PrtgAddress.cs`：加 `IsDnsCandidate`。
2. `LogForesight.Core/Service/PrtgAddressResolver.cs`：`Resolve` 步驟 2 之後加守門。

### 測試／驗收
- `PrtgAddressTests`（既有或新建）Theory：`srv-a.example.local`／`srv-a`／`a-b.c` → true；`10.2xx.x.x`／`10.2.3.256`／`10.2.3.4.5`／`10.2.3.4 (old)`／`-bad.host`／`bad-.host`／`a..b`／空白 → false。
- `PrtgAddressResolverTests`：注入會記錄呼叫的假 DNS，餵 `10.2xx.x.x` → 回 null 且假 DNS **零次呼叫**；餵 `https://netiq.corp.local:8443/` → 假 DNS 收到 `netiq.corp.local`。

## 批次B：DNS 逾時改非同步可取消

### 現況與核對結果
`Task.Run(() => Dns.GetHostAddresses(host))` + `task.Wait(2000)`；逾時 task 不取消；例外在 lambda 內擲出。

### 定案
- `DnsLookupWithTimeout` 改為：`using var cts = new CancellationTokenSource(DnsTimeout)`；`Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token).AsTask().GetAwaiter().GetResult()`，`catch` 一律回空陣列。
  - 只查 `InterNetwork`，省掉 AAAA 查詢；`Resolve` 內的 IPv4 篩選保留（假 DNS 仍可回混合陣列）。
  - `IPrtgAddressResolver` **維持同步簽章**：改 async 會連動 `PrtgHostMapper.MapForDate`、`PrtgResourceGuardTargets.Resolve`、`IPrtgResourceGuardSource` 與全部呼叫端（BACKLOG 條目的「全面 async」），本輪不做；此改法已解決「逾時後執行緒不回收」與「偵錯器中斷」（例外改在框架內部擲出）兩個實際痛點。
- 逾時常數 `DnsTimeout = 1 秒`（來源位址少、裝置側經批次A／C 已大幅減量）。

### 改動
1. `PrtgAddressResolver.cs`：重寫 `DnsLookupWithTimeout`，補註解說明為何維持同步簽章。

### 測試／驗收
- 既有 5 個 `PrtgAddressResolverTests` 全綠（假 DNS 路徑不受影響）。
- 新增：假 DNS 擲 `SocketException` → 回 null 不擲出（既有）；假 DNS 阻塞超過逾時 → 這條真 DNS 路徑不可單元測（private static），以「解析不存在的 `.invalid` 網域回 null、耗時 < 3 秒」做一條整合性 Fact（標 `Trait("Category","Network")`，離線也應通過因為 `.invalid` 保證失敗）。

## 批次C：守門偵測的裝置側解析節流

### 現況與核對結果
`FindDevicesForHost` 對每個來源位址都把全部裝置 `Resolve` 一遍（多個來源位址 × 全部裝置；快取讓第二輪免費，但第一輪仍是全表 DNS），然後才退回字面比對。守門偵測跑在 HTTP 請求執行緒上，也跑在每趟批次的 `PrtgResourceGuard`。

### 定案
- 比對順序改為：
  1. 來源位址 `Resolve`（可能 DNS，最多幾筆）。
  2. 裝置側先只做 `PrtgAddress.Normalize`（純語法）與來源 IP 比。
  3. 沒命中 → 字面比對（`HostToken` 相等）。
  4. 仍沒命中 → 才對「`IsDnsCandidate` 為 true」的裝置逐一 `Resolve` 比對，受**整趟預算**限制：`DeviceDnsBudget = 20` 次（未快取的解析次數；含失敗）。預算用盡即停止，並輸出一行警告：「裝置側 DNS 解析已達上限 N 次，其餘 M 台名稱型裝置未解析；建議在 PRTG 以 IP 設定裝置或改用覆寫清單」。
  - 規格 §12 三段語意不變（IP 比 → 名稱解析比 → 字面比），只是把字面比提前於**裝置側**解析，並加預算。裝置側 DNS 受預算限制，超出預算的名稱型裝置可能漏掉（文件化的取捨，見驗收最後一條）；既有測試不受影響。
- 預算是常數不是設定（無消費端紅線；它是保險絲不是閘門），與 `PrtgLiveGuardSource.MaxPages` 同性質。
- `UnresolvedHint` 只查來源位址，維持。
- `PrtgHostMapper` **不動**（背景作業、§4 定案不變）；它得到的是批次A／B 的守門與逾時縮短。

### 改動
1. `PrtgResourceGuardTargets.cs`：`FindDevicesForHost` 依上述四步重寫；新增預算計數（跨多個來源位址共用一個計數器，放在 `Resolve` 區域變數傳入）；統計訊息。
2. 對應註解與 `SourceHint` 不變。

### 測試／驗收
- 既有 `PrtgResourceGuardTargetsTests` 全綠（含 `Resolve_device的Ip帶port時仍命中`、`Resolve_位址比對不分大小寫`）。
- 新增：
  - 裝置 host 為 `10.2xx.x.x` 型亂值、來源位址為 IP：假 resolver 記錄呼叫，斷言**裝置側零次解析**且命中結果與純語法相同。
  - 來源為名稱、裝置為同名稱、DNS 全失敗：字面比對命中，且裝置側解析發生在字面比對之後（記錄呼叫順序）——實際上因字面已命中，裝置側解析次數為 0。
  - 30 台名稱型裝置、皆對不到：解析次數 ≤ 20，訊息含「已達上限」。
  - 來源 IP 對到第 25 台名稱型裝置（其餘皆解析失敗）：驗證預算確實會漏掉它並提示，行為可預期（文件化的取捨）。

## 批次D：文件

1. `docs/PRTG-SPEC.md` §4：解析層補「僅對通過主機名稱語法判定的值送 DNS；壞掉的 IPv4 形狀不送」、逾時 1 秒、非同步可取消。
2. `docs/PRTG-SPEC.md` §12：比對順序改寫為四步＋預算常數與警告訊息。
3. `docs/BACKLOG.md`「PRTG 位址解析與守門即時來源改非同步」：改寫為只剩「`PrtgLiveGuardSource` 同步阻塞 async／介面全面 async」，DNS 部分註記已於本輪處理。
4. 完工後本檔 `git mv` 進 `docs/archive/` 並補索引。

## 批次E：四角度回檢（整體專案／程式面／使用者／管理者）

實作完成後再逐條檢視 diff 的結果，修正三處、記錄兩處：

| # | 角度 | 發現 | 處置 |
|---|---|---|---|
| E1 | 程式面 | 預算的「略過台數」在多個來源位址時重複計（每個來源位址各掃一遍裝置，同一台略過 N 次） | 改為不重複 token 集合；新增測試「兩個來源位址共用預算_略過台數不重複計」 |
| E2 | 使用者 | 名稱型裝置恰好 20 台時印「已達上限…其餘 0 台」，讀起來像出了問題 | 只在真的有裝置被略過時印；新增測試 |
| E3 | 管理者 | 探測「IP 覆蓋概要」用 `IPAddress.TryParse` 直接判，與主機對應的純語法層不同：`10.1.2.3:8080` 被算成「DNS 名稱」，`10.2xx.x.x` 也混在同一桶——管理者從探測看不出有壞值 | 改用 `PrtgAddress.Normalize`／`IsDnsCandidate` 分三桶，「無法判定」列前 5 筆 objid 與原值；更新探測測試與 §6 說明 |
| E4 | 整體專案 | 主機對應對名稱型裝置的 DNS 成本只是減半（2 秒→1 秒），幾百台解析不到的名稱仍是每趟數分鐘；§4 定案不能拿掉裝置側 DNS | 不在本輪改簽章；BACKLOG 新增條目含觸發時機與方向（批次並行或跨趟負向快取） |
| E5 | 程式面 | `Resolve_真DNS解析保留網域` 是碰真 DNS 的測試，全套已有偶發紅的抱怨 | 保留但掛 `Trait("Category","Network")` 可過濾；`.invalid` 為 RFC 2606 保留網域，離線與有網路都應立刻失敗。若實機 CI 有 DNS 劫持再降級為略過 |
| E6 | 使用者 | 主機對應的候選判定沒有專屬測試證明佔位值不送 DNS | 新增 `MapForDate_device的Ip為打壞的IPv4佔位值_計入略過且不送DNS` |

回檢時確認無誤、不動的：候選判定拒絕非 ASCII（IDN）主機名稱，內網環境極少見且拒絕只是不 DNS、字面比對仍可命中；
`GetHostAddressesAsync` 的取消在 Windows 會真的中止 `GetAddrInfoExW`，Linux 只放棄等待，兩者都不再佔執行緒；
偵錯器不再中斷是推論（例外改在框架內擲出、由使用者程式碼接住），實測時請以偵錯模式按一次「自動偵測並填入」驗證。


## 第二段回饋（2026-09-09 實測）

### 討論項目（已定案）

**D3. 立即執行的「PRTG 尚未啟用」提示要不要看連線設定？** → **依建議實作**：只在連線已設定但模組關閉時提示。
使用者要求：手動立即執行時若 PRTG 未啟用，跳出「PRTG 尚未啟用，是否要開始排程」讓人確認。
照字面做的話，**根本沒有 PRTG 的站台每次手動執行都會被問一次**，這種每次都要多按一下的提示是最招人罵的那種。
  判定用 `hasPrtgConnection()`（位址＋依認證方式檢查 token／帳密／passhash 旗標），與後端
  `PrtgClientFactory.HasUsableCredentials` 同義。

**D4. 排程作業頁的總開關要不要留？** → **依建議移除**。switch、`bindPrtgEnabledSwitch`、
`PUT settings/prtg-enabled` 端點、`ISystemSettingsService.SetPrtgEnabled` 與三個測試替身的實作全部拿掉；
`SystemSettingsServiceTests` 的三條 `SetPrtgEnabled` 測試改寫為對 `UpdatePrtg` 驗證，另補兩條
（關閉時不送範圍不清值、不送開關時開關不變）。

## 批次F：PRTG 啟用併入維護頁取數範圍下拉、未啟用時的操作閘與訊息指路

### 現況與核對結果
- 總開關是排程作業頁**下方「排程設定」表單**裡「PRTG 擷取」區的 switch（[Runs.cshtml:320](../LogForesight.Web/Views/Pages/Runs.cshtml)），
  切換即存走 `PUT settings/prtg-enabled`（[SettingsController.cs:95](../LogForesight.Web/Controllers/Api/SettingsController.cs) →
  `SystemSettingsService.SetPrtgEnabled`，開啟時驗證位址與認證齊備）。頁面頂端的 PRTG 狀態卡只顯示「未啟用」，沒指路。
- 取數範圍下拉在 PRTG 維護頁「擷取參數」頁籤（[Prtg.cshtml:172](../LogForesight.Web/Views/Pages/Prtg.cshtml)，`prtg-value-fetch-scope`，
  三選一 `triggered`／`all-mapped`／`triggered-plus-list`），隨該頁籤「儲存」走 `PUT settings/prtg`（`UpdatePrtg`），
  而 `UpdatePrtg` **目前刻意不動 `PrtgEnabled`**（`effectivePrtgEnabled = before.PrtgEnabled`）。
- `PrtgEnabled` 的消費端共 7 處（`PrtgDailyPipeline:43` 短路、`PrtgBackfillService:134`、`PrtgStructureSyncService:222`、
  `PrtgHostMapRefresher:47`、`CalibrationService` 三處）＋設定服務的驗證／稽核；`PrtgValueFetchScope` 消費端 6 處。
  **兩個欄位都保留，只合併 UI**——改成單一欄位要動十幾個消費端與 DB 設定 blob 相容性，換來的只是少一個 bool。
- 「同步結構與對應」「開始回填」在模組關閉時沒閘（[runs.js:1229](../LogForesight.Web/wwwroot/js/pages/runs.js)），按下去後端以
  「PRTG 未啟用。」擲 `DomainException.Validation`（設計中的 400 路徑；偵錯器停在那裡是使用者程式碼擲出、中介軟體接住，正常）。
  維護頁「鏡像狀態」頁籤的同步鈕（[prtg-admin.js:1110](../LogForesight.Web/wwwroot/js/pages/prtg-admin.js)）同樣沒閘。
- 立即執行 modal（`run-now-form` submit，[runs.js:1490](../LogForesight.Web/wwwroot/js/pages/runs.js)）已有重新分析模式的 `confirmAction` 二次確認可沿用；
  頁面載入時已取得 `/api/admin/settings`（含 `prtgEnabled`、`prtgUrl`、`prtgAuthMode`、`prtgUsername`；token／密碼只有「已設定」旗標）。
- 網段範圍的手動執行（僅 NetIQ 主機）也會走 `PrtgDailyPipeline`（它只看設定不看範圍），提示條件不分範圍。

### 定案
- **F1 維護頁「擷取參數」頁籤的下拉改為四選一**，標籤改成「PRTG 擷取」：
  `off`「關閉（預設）」／`triggered`「只抓觸發主機」／`all-mapped`「全部已對應主機」／`triggered-plus-list`「觸發主機＋指定清單」。
  說明文字：「選擇任一範圍即啟用 PRTG 擷取；關閉時夜間擷取、歷史回填與結構同步都不執行，環境探測與資源守門預覽不受影響。」
  - 前端對應：`off` ⇢ `prtgEnabled=false`、`prtgValueFetchScope` **維持原值不送**（關閉再開時範圍不用重選）；
    其他 ⇢ `prtgEnabled=true`＋該範圍。載入時 `prtgEnabled=false` 就選 `off`，否則選 `prtgValueFetchScope`。
  - 範圍估算（`prtg-fetch-scope/estimate`）與「額外主機」textarea 在 `off` 時隱藏。
  - 位置放在頁籤最上方（它決定其餘參數有沒有意義）。
- **F2 `UpdatePrtgSettingsRequest` 增加 `PrtgEnabled?`**，`UpdatePrtg` 有送才更新；開啟時的驗證（位址＋認證齊備）與
  `SetPrtgEnabled` 抽成同一個私有方法共用，訊息改為「請先在「連線」頁籤完成 PRTG 位址與認證設定，再選擇取數範圍。」
  稽核 detail 已含 `PrtgEnabled` 前後值，不另加。
- **F3 移除排程頁的 switch 與 `PUT settings/prtg-enabled`**（D4）：`Runs.cshtml` 的「PRTG 擷取」section、`bindPrtgEnabledSwitch`、
  Controller 端點、`ISystemSettingsService.SetPrtgEnabled` 與三個測試替身的實作一起拿掉。
- **F4 排程頁 PRTG 卡的「模組狀態」改為可指路的狀態**：關閉時顯示「未啟用」＋一行連結「到 PRTG 維護頁「擷取參數」選擇取數範圍以啟用」
  （走 `appUrl('admin/prtg')#params`，不寫死 `/`）；啟用時顯示「已啟用：只抓觸發主機」這種帶範圍的文字（三個標籤與維護頁下拉同一份字串，
  放 `core/` 共用模組，不在兩頁各寫一份）。
- **F5 未啟用時的操作閘**：模組關閉時「同步結構與對應」「開始回填」`disabled`，卡片說明行同 F4 的連結；維護頁鏡像狀態頁籤的同步鈕依
  載入的 `prtgEnabled` 同樣閘住並附同一句。頁面沒有即時同步兩頁狀態的機制，仍保留後端拒絕作為第二道。
- **F6 後端訊息補指路**：`PrtgStructureSyncService.TryStart`、`PrtgBackfillService` 的「PRTG 未啟用。」改為
  「PRTG 擷取未啟用，請先在 PRTG 維護頁「擷取參數」選擇取數範圍。」
- **F7 立即執行的提示**（D3）：`run-now-form` submit 時，若 `prtgEnabled=false` **且**位址與認證已設定，先 `confirmAction`：
  標題「PRTG 尚未啟用」、內容「這次執行不會做 PRTG 擷取（連線已設定但擷取未啟用）。仍要開始執行嗎？」、
  確認鈕「仍要開始」。取消就留在 modal。判定「認證已設定」用既有的 DTO 旗標（token 已設定或帳密模式帳號非空＋密碼已設定），
  與後端 `HasUsableCredentials` 同義；前端只是提前問，後端短路行為不變。
- 規格：`PrtgEnabled` 仍是「模組總開關」，只是**入口改為維護頁的範圍下拉**；「啟用不自動啟動同步」的定案不變。

### 改動
1. `Prtg.cshtml`／`prtg-admin.js`：下拉四選一與說明、載入／存檔對應、估算與額外主機在 `off` 時隱藏、鏡像狀態頁籤同步鈕閘。
2. `wwwroot/js/core/prtg-scope-labels.js`（新）：四個值的顯示字串，兩頁共用。
3. `SettingsDtos.cs`：`UpdatePrtgSettingsRequest.PrtgEnabled?`；移除 `SetPrtgEnabledRequest`。
4. `SystemSettingsService.cs`：`UpdatePrtg` 接 `PrtgEnabled`、共用啟用驗證；移除 `SetPrtgEnabled`。`SettingsController.cs` 移除端點。
5. `Runs.cshtml`／`runs.js`：移除表單 switch 與綁定；PRTG 卡狀態文字＋連結；兩鈕閘；立即執行提示。
6. `PrtgStructureSyncService.cs`、`PrtgBackfillService.cs`：訊息指路。
7. 文件：`PRTG-SPEC` §7 設定表（`PrtgEnabled` 入口）、操作介面表（排程頁列拿掉總開關、維護頁列加「PRTG 擷取」下拉）、§5a 入口與前置條件訊息；
   `WEB-SPEC` §9.9e 擷取參數頁籤、排程頁三張卡分工（PRTG 卡：狀態＋指路）、立即執行 modal 段加提示條件。

### 測試／驗收
- `SystemSettingsServiceTests`：`UpdatePrtg` 開啟時位址空→擲例外且訊息指向連線頁籤；齊備→寫入 true；關閉時不驗證；不送 `PrtgEnabled` 時不變。
  三條 `SetPrtgEnabled` 測試改寫為以上。
- `PrtgAdminPageUiTests`：斷言下拉含四個 value、`off` 為第一個；`runs.js` 不再含 `prtg-enabled` 端點；`Runs.cshtml` 不再含 `id="prtg-enabled"`；
  兩頁都 import 共用標籤模組（`JsModuleImportTests` 形狀檢查）。
- 端點測試替身：移除 `SetPrtgEnabled` 實作後編譯即為驗證。
- 手動驗收：維護頁選「只抓觸發主機」存檔→排程頁卡片顯示「已啟用：只抓觸發主機」、兩鈕可按；選「關閉」→卡片顯示未啟用＋連結、兩鈕灰；
  連線未設就選範圍→存檔被擋且訊息指向連線頁籤；連線已設、模組關閉、按立即執行→出現確認框；連線未設→不出現。

## 批次G：排程頁按鈕互斥

### 現況與核對結果
- 取數卡：「立即執行」永遠可見可按（[Runs.cshtml:54](../LogForesight.Web/Views/Pages/Runs.cshtml)，JS 沒依 `isRunning` 處理）；「停止執行」依 `canStop` 顯示。執行中兩顆並排。
- AI 卡：「立即補跑 AI」「強制重新分析」執行中改 `disabled`（[runs.js:1019-1022](../LogForesight.Web/wwwroot/js/pages/runs.js)），「停止」依 `canStop` 顯示——執行中三顆並排、兩顆灰。
- PRTG 卡：「同步結構與對應」「開始回填」執行中 `disabled`；歷史回填沒有停止鈕（核對 `Runs.cshtml`、`PrtgBackfillService`、Controller 皆無）。

### 定案
- 規則統一為「**執行中只顯示停止，閒置只顯示啟動類**」，用 `d-none` 切換而非 `disabled`：
  - 取數卡：`isRunning` → 隱藏「立即執行」、顯示「停止執行」（沿用 `canStop`；`isRunning && !canStop` 的短暫窗口兩顆都隱藏，狀態列已有「停止中」文字）。
  - AI 卡：`isRunning` → 隱藏「立即補跑 AI」「強制重新分析」、顯示「停止」；閒置反之。
  - PRTG 卡維持 `disabled`（沒有停止鈕可換，灰掉是唯一能表達「在跑」的方式）。
- 無 `Maintain` 的使用者三顆都隱藏，規則不變（`d-none` 的加減要與 `data-maintain-only` 的隱藏不互相蓋：唯讀模式下狀態渲染不再碰這些鈕）。

### 改動
1. `runs.js`：取數卡狀態渲染加 `schedule-run-now` 的 `d-none` 切換；AI 卡兩顆從 `disabled` 改 `d-none`；三處都以 `canMaintainSchedule` 為前提。
2. `docs/WEB-SPEC.md` 三張卡分工段加一句互斥規則。

### 測試／驗收
- Runs 頁 JS 字串測試：斷言 AI 卡不再對 `schedule-ai-run-now`／`schedule-ai-force-rerun` 設 `disabled`、改 `d-none`；取數卡對 `schedule-run-now` 有 `d-none` 切換。
- 手動驗收：執行中三張卡各自只剩「停止」（PRTG 卡灰鈕）；停止後恢復；唯讀帳號三張卡沒有任何動作鈕。

## F、G 實作結果（與規劃的差異）

| # | 規劃 | 實作 | 差異原因 |
|---|---|---|---|
| F2 | 啟用驗證「抽成同一個私有方法共用」 | 不需要抽：`ValidatePrtgSettings` 早已接 `effectivePrtgEnabled` 並做完整驗證，`UpdatePrtg` 只要把 `before.PrtgEnabled` 改成 `request.PrtgEnabled ?? before.PrtgEnabled` | 既有程式碼已是共用的，再抽一層是多餘 |
| F1 | 前端「`off` 時 `prtgValueFetchScope` 維持原值不送」 | 用條件展開 `...(enabled ? { prtgValueFetchScope: scopeValue } : {})` 寫在 payload 物件內 | 既有 UI 測試斷言 `prtgValueFetchScope:` 這個字面，條件展開同時滿足「不送」與「字面可檢查」 |
| F5 | 只在 `renderPrtgModuleState` 設閘 | 另外接上兩處輪詢：`refreshPrtgSyncStatus`（`status.isRunning \|\| prtgModuleEnabled === false`）與 `renderPrtgBackfillStatus`（`startButton.disabled = prtgModuleEnabled === false`） | **同一顆按鈕的 `disabled` 有三個寫入點**，只改一處會在下一次 3 秒輪詢把閘打開；新增測試 `PRTG未啟用時同步與回填按鈕在所有寫入點都被閘住` 鎖住這件事 |
| F5 | — | 兩顆鈕的 click handler 各加一道 `prtgModuleEnabled === false` 早退 | 兩個分頁狀態不同步時按鈕可能還是可按的；前端二道、後端三道 |
| F6 | 只改 `TryStart` 與回填的訊息 | 另改 `ValidatePrtgSettings` 的「啟用 PRTG 時，PRTG 位址不可為空」→「啟用 PRTG 擷取前，請先在「連線」頁籤設定 PRTG 位址。」 | 開關入口移到「擷取參數」後，這句是使用者在該頁籤選範圍時會撞到的訊息，不指路等於沒說 |
| G | 取數卡「`isRunning && !canStop` 的短暫窗口顯示『停止中…』文字」 | 不另加文字 | 狀態列（`schedule-run-state`）本來就會顯示「停止中」，再加一份是重複 |
| F5 | 維護頁鏡像頁籤的說明「附同一句」 | 兩頁各寫合乎所在位置的句子：排程頁「到 PRTG 維護頁「擷取參數」選擇取數範圍即可啟用」、維護頁「PRTG 擷取未啟用，無法同步。請到「擷取參數」頁籤選擇取數範圍」 | 使用者已經在維護頁時，再叫他「到 PRTG 維護頁」是廢話。共用的是**標籤字串**（`prtg-scope-labels.js`），不是指路句 |
| F4 | 指路連結用 `appUrl('admin/prtg')` | 連結寫在 `Runs.cshtml` 用 `@Url.Content("~/admin/prtg")#params` | CLAUDE.md 的路徑規則：cshtml 走 `~/` 或 `@Url.Content`，`appUrl()` 是 JS 組裝連結時用的。連結是靜態的，不需要進 JS |

實作後的既有測試修正三條：`守門自動偵測與取數範圍的元素與接線齊備`（改斷言四選一與 `prtgEnabled:`）、
`PRTG啟用但位址空白時拒絕` 與 `Update_PRTG沿用既有啟用狀態時位址驗證仍生效`（訊息改為斷言「「連線」頁籤」）、
`TryStart_PRTG未啟用時拒絕`（斷言指路字樣）。

## 瀏覽器實地驗證（dev server，Stub 驗證）

以 `.claude/launch.json` 的 `web` 設定起站，實際點過兩頁，逐項確認：

| 驗證項 | 結果 |
|---|---|
| 維護頁「擷取參數」首欄下拉 | 四個選項齊備、標籤「PRTG 擷取」、預設 `off`、位於卡片第一欄 |
| 下拉四種切換 | `off` 隱藏估算鈕與額外主機；`triggered`／`all-mapped` 只顯示估算鈕；`triggered-plus-list` 額外主機出現 |
| 維護頁鏡像頁籤 | 未啟用時同步鈕 `disabled`＋說明行可見 |
| 排程頁 PRTG 卡（未啟用） | 狀態「未啟用」、指路行可見且連結為 `/admin/prtg#params`、同步與回填兩鈕灰掉、舊 switch 已不存在 |
| 排程頁 PRTG 卡（啟用 triggered） | 狀態「已啟用：只抓觸發主機」、指路行隱藏、兩鈕解鎖 |
| 兩顆鈕的閘撐過輪詢 | 連續多輪 3 秒／10 秒輪詢後 `disabled` 仍為 true（三個寫入點都有接） |
| 批次G 閒置 | 取數卡只有「立即執行」、AI 卡只有「立即補跑 AI」「強制重新分析」，停止鈕隱藏 |
| 批次G 執行中（攔截狀態 API 模擬） | 兩張卡的啟動鈕 `d-none`、停止鈕出現，且啟動鈕**不是**只被 `disabled` |
| F7 連線已設定但未啟用 | 按「立即執行 → 開始執行」跳出「PRTG 尚未啟用／仍要開始」確認框 |
| F7 完全沒設 PRTG | 同樣操作不跳確認框（沒有 PRTG 的站台不會每次被問） |
| F2 存檔動線 | 未設連線就選範圍 → 擋下並回「啟用 PRTG 擷取前，請先在「連線」頁籤設定 PRTG 位址。」；補上連線後選 `all-mapped` → 啟用成功；改選「關閉」→ 停用且 `PrtgValueFetchScope` 仍保留 `all-mapped` |
| 網路請求 | 頁面載入全部 200，含新模組 `js/core/prtg-scope-labels.js` |

## 規劃項目逐條回檢（Claude 親做）

把本檔每個批次的「改動」與「測試／驗收」逐條拿去對實際程式碼，不看 diff——
代理看 diff 只看得到「做了什麼」，看不到「規劃有寫、diff 沒有」。結果補三處：

| # | 規劃寫了 | 回檢發現 | 已補 |
|---|---|---|---|
| R1 | 批次F 改動 7：`WEB-SPEC` **立即執行 modal 段加提示條件** | 完全沒寫。F7 的行為只存在於程式碼，規格上查不到「為什麼有些站台會被問、有些不會」 | §9.10 立即執行 modal 段補上提示條件、判定依據與「連線沒設不提示」的理由 |
| R2 | 批次F 測試：斷言下拉含四個 value、**`off` 為第一個** | 只斷言四個 value 都在，沒鎖順序。`off` 是預設值，排到後面會讓沒存過設定的站台看起來像已啟用 | `PrtgAdminPageUiTests` 加「第一個 `<option>` 必須是 `off`」 |
| R3 | 批次F3：`SetPrtgEnabled` 與**三個**測試替身的實作一起拿掉 | 實際有**四個**替身，`LogForesight.Tests/TestDoubles/VisibilityFakes.cs` 漏掉。介面成員移除後它只是多出來的公開方法，**編譯照樣過**，全套測試也不會紅——只有逐條比對抓得到 | 移除該方法 |

回檢確認**已到位**、不需補的：批次A 四條判定規則與兩組測試；批次B 的 `InterNetwork` 篩選在
`Resolve` 內保留、`.invalid` 測試掛 `Trait("Category","Network")`；批次C 的四步比對、
`UnresolvedHint` 維持只查來源位址、`PrtgHostMapper.cs` **完全沒動**（`git diff` 為空，符合定案）、
四條新測試齊全；批次D 三項文件；批次E 六項全部；批次F 的 F1–F7 定案文字逐字相符；
批次G 三處切換都以 `canMaintainSchedule` 為前提。
`JsModuleImportTests` 自動掃描 `core/` 全部匯出，新模組 `prtg-scope-labels.js` 無需額外接線即納入檢查。

**批次D 第 4 項（本檔 `git mv` 進 `docs/archive/` 並補索引）刻意未做**——那是收尾步驟，
等使用者實測通過、併 dev 時才執行。

## 體檢交接

- 實作：批次A–E 由 Fable 5.1 實作；批次F–G 由 Opus 5 實作（使用者中途 `/model` 切換）。
- 體檢：Fable 5.1（使用者切回後下收尾指令）。A–E 與體檢方同一模型，依使用者「進行收尾流程」的指示
  由使用者覆寫「換模型體檢」紀律；補救方式是三個掃 diff 的 subagent 全部派 **Opus low**（使用者指定），
  換一個視角掃 A–E 的程式碼，Fable 只做取捨與修。
- 實作方最沒把握的地方：`IsDnsCandidate` 的判定邊界（會不會誤擋真主機名稱）、同一顆按鈕的 `disabled`
  有幾個寫入點沒盤全、探測步驟 6 改判定後有無誤傷。體檢確實在這三處都抓到問題（見下）。

## 體檢輪修正

三個 Opus low subagent（Core diff、Web diff、文件稽核）回報後由 Fable 逐條取捨。修 11 條、記錄不修 4 條：

| # | 哪裡 | 症狀 | 怎麼修 | 迴歸測試 |
|---|---|---|---|---|
| C1 | `PrtgAddress.IsDnsCandidate` | 「第一段全數字」誤擋 `1.dc.corp.local`、`0.pool.ntp.org` 這種真 FQDN——三個呼叫端（主機對應、結構同步、守門）都會把這種裝置從「對應成功」退化成「略過」 | 體檢輪先收緊為「每段 ≤ 3 字」，終檢再改（見終檢輪） | `PrtgAddressTests` Theory 擴充 |
| C2 | 同上 | root-qualified FQDN `srv.corp.local.` 因結尾空 label 被擋 | 判定層先去掉結尾點（終檢移到 `HostToken`，見終檢輪） | 同上 |
| C3 | `PrtgProbeRunner` 步驟 6 | IPv6 裝置被列進「無法判定，建議到 PRTG 修正」——它是合法位址、主機對應比得到 | 加第四桶「IPv6 位址者」 | `PrtgProbeRunnerTests` 加一台 `[fe80::1]:8080` |
| C4 | `PrtgResourceGuardTargets.Resolve` | `GetAllDevices` 沒有 `OrderBy`，「哪 20 台吃到預算」取決於 DB／API 回傳順序；`Resolve_命中裝置在預算之後` 那條測試其實靠插入序才綠 | 進入比對前依 objid 排序 | 既有預算測試改為確定性 |
| C5 | `FindDevicesForHost` 步驟 3 | skip 條件寫成 `Normalize(HostToken(dev.Ip))`，與步驟 1 的 `Normalize(dev.Ip)` 靠 `HostToken` 冪等才等值 | 改為直接 `Normalize(dev.Ip) != null` | — |
| C6 | `PrtgAddressResolverTests` 真 DNS 測試 | 有 NXDOMAIN 劫持的網路會給 `.invalid` 一個假 IP，`Assert.Null` 會紅 | 只斷言「不擲例外且 < 3 秒」 | 同一條 |
| W1 | `prtg-admin.js` `renderStructureSyncStatus` | 同步鈕的閘被輪詢打開：非執行中一律 `disabled = false`，頁面載入一秒後閘沒了、說明行還亮著——與 F5 同型，第三個寫入點漏盤 | 改 `disabled = !prtgEnabled`；click 加第二道 | `PrtgAdminPageUiTests.維護頁同步鈕在所有寫入點都尊重PRTG開關` |
| W2 | `SystemSettingsService.UpdatePrtg` 稽核 | 移除 `SetPrtgEnabled` 後「誰關掉 PRTG 擷取」從此查不到——`UpdatePrtg` 的 audit before/after 欄位沒有 `PrtgEnabled` | 兩個匿名物件各加該欄 | — |
| W3 | `SystemSettingsService.cs:28` | `SetPrtgEnabled` 的 XML 註解成了孤兒，疊在下一個成員上 | 刪 | — |
| W4 | `CalibrationService` 三處 | 說明文字仍寫「於排程作業頁啟用 PRTG 擷取」——F6 只盤到兩處，同型第三處漏掉 | 改指向「擷取參數」頁籤 | `CalibrationServiceTests` 斷言更新 |
| W5 | `prtg-scope-labels.js` | `PRTG_SCOPE_OPTIONS` 是死匯出；模組宣稱「不各寫一份」但 option 其實在 cshtml | 刪死匯出，明寫 cshtml 是 option 來源 | `PrtgAdminPageUiTests.取數範圍下拉的value集合與狀態標籤模組一致` 鎖住兩邊集合 |
| W6 | `SettingsController.EstimatePrtgFetchScope` | `scope=off` 經 `Normalize` 退回 `triggered`，回一組「關閉狀態下的估算值」 | 明確回 `Success=false` | — |
| D1–D6 | 現況文件 | PRTG-SPEC 端點表仍列 `PUT prtg-enabled`／`PUT prtg` 寫「不含總開關」；WEB-SPEC §9.9b 與端點清單仍說總開關在排程頁；BACKLOG 兩處敘事字眼；CLAUDE.md 基線；PRTG-SPEC 與 WEB-SPEC 之間三組重複段落 | 全部修正；重複段落留 PRTG-SPEC、WEB-SPEC 改一行連結 | — |

不修、留紀錄：
- `catch (Exception)` 全吞與全站 `when (ex is not OperationCanceledException)` 慣例不一致——`Resolve` 的契約是「一律不擲」，逾時本身就是 OCE，收窄後也只是落到外層 catch-all，淨效果為零。
- 候選判定在快取之前重複執行——純字串掃描，成本可忽略。
- `canStop` 恆等於 `isRunning`（`ScheduleController`），「兩顆都隱藏」的空窗不存在。
- 排程頁的模組狀態與閘只在載入時讀一次，跨分頁改開關要重整——記進 WEB-SPEC §9.10 為已知取捨；有第二、三道擋誤送。

修正後全套 3710 綠（略過 6）。

## 終檢輪

併 dev 後派 scan-low（Opus low）掃體檢修正 commit 本身，抓到體檢修正引入的三條、疑慮六條；修五條：

| # | 哪裡 | 症狀 | 怎麼修 | 迴歸測試 |
|---|---|---|---|---|
| T1 | `IsDnsCandidate` | 體檢輪的「每段 ≤ 3 字」既**誤擋** `163.com`、`104.com.tw`、`1.dc.hq.tw`（兩字母國碼＋短子網域），又**漏擋** `192.168.1.100x`、`10.2xxx.x.x`——規則被調成只擋測試裡那一個字面形狀 | 改為「第一段全數字且每段只含數字或 x」；有任何非 x 字母就當主機名稱送 DNS，寧可多付一次逾時 | Theory 真值加 `163.com`／`104.com.tw`／`1.dc.hq.tw`／`10.2.3.4-old`／大寫 root-qualified；假值加 `192.168.1.100x`／`10.2xxx.x.x`／`10.2.3.4x`／`.a.b`／`10..2.x`／`.` |
| T2 | `HostToken` | 結尾點只在判定層剝，`srv.corp.local.` 與 `srv.corp.local` 在字面比對、預算、解析快取裡是兩把鍵——同一台裝置吃兩次預算、明明同名卻掉到 DNS | 在 `HostToken` 統一剝單一個結尾點（三層共用入口） | `HostToken_只去掉單一個結尾點`、`Normalize_IP帶結尾點_視為該IP` |
| T3 | `prtg-admin.js` `bindStructureSync` | `withBusy` 的 `restore()` 在輪詢之後執行，同步進行中的灰掉被 restore 打開三秒（既有寫法，但體檢新加的測試 `DoesNotContain("disabled = false")` 掃不到這條路徑，會誤以為已鎖死） | restore 移到輪詢之前 | UI 測試斷言順序 |
| T4 | `prtg-admin.js` `syncStructureSyncGate` | 只看開關不看執行中：存檔後 `renderPrtgFields` 重跑 gate 會把同步中的按鈕打開 | 加 `structureSyncRunning` 模組變數，gate 取兩者聯集 | 同上 |
| T5 | `PrtgAdminPageUiTests` | 標籤鍵 Regex `[a-z-]+` 遇到含數字的鍵會靜默漏抓；後端 estimate 的 `"off"` 是字面、前端是常數，兩邊沒鎖 | Regex 放寬；加斷言鎖住兩邊都是 `off` | 同一條測試 |

不修、留紀錄：初始化時 `loadSettings` 與 `refreshStructureSyncStatus` 併發，設定 API 失敗會停在「未啟用」——fail-closed，可接受；`Normalize(dev.Ip)` 在步驟 1 與 3 各算一次——純字串，可忽略；`IsDnsCandidate` 剝尾點後才比 253——符合 DNS 慣例。

終檢後全套 3725 綠（略過 6）。

## 明確不做（本輪定案）
- 不把 `IPrtgAddressResolver`／`IPrtgResourceGuardSource` 改 async（BACKLOG 保留）。
- 不做跨請求的程序級 DNS 快取（§4 定案：DNS 變更要能在下一趟生效）。
- 不改 `PrtgHostMapper` 的裝置側解析策略（D1 選 A 時才改）。
- 不新增任何 `SystemSettings` 欄位。
- 不為歷史回填加「停止」鈕（第二段回饋核對時發現沒有，另開項目；BACKLOG 記一行）。
- 不把 `PrtgEnabled` 與 `PrtgValueFetchScope` 合併成單一欄位（十幾個消費端與設定 blob 相容性，收益只有少一個 bool）。
