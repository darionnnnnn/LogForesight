# NetIQ Web 維護、群組功能擴充與 Jsonl 後端退役規劃

> 規劃日期:2026-07-24。本文件收整四項需求的討論定案與六個實作 Phase:
> (1) Sentinel(NetIQ)連線設定改由 Web 維護,含「新增即掃描匯入」精靈;
> (2) 使用者群組編輯時可勾選可見主機群組(授權矩陣的初步設定入口);
> (3) 主機群組成員檢視/移除 modal;
> (4) 多對多歸屬確認(主機↔主機群組、使用者↔使用者群組——**模型層已是多對多,零改動**)。
> 並依討論擴大範圍:**Jsonl 檔案後端全面退役**(含 Txt 主機清單模式)、
> **NetIQ 匯入佇列(D-3)退役改即時落盤**。
>
> 本文件修訂了三份既有文件的決策,對照見「既有決策修訂」節;
> 各文件的修訂註記在 Phase 6 補上。

## 背景:為什麼既有決策可以改

| 既有決策 | 當時前提 | 前提的變化 |
|---|---|---|
| NETIQ-HOSTLIST-WEB-PLAN 決策 E:Sentinel 名單以批次 appsettings.json 為單一事實來源,Web 唯讀、不建管理表 | 批次與 Web 靠 DataRoot 共用**檔案**,設定檔就是共用點 | Phase C 之後預設 Sqlite、正式 SqlServer,共用點已是**資料庫**;`NetiqServerCatalog` 讀 `{DataRoot}\appsettings.json` 在 SqlServer 模式(Web 與批次可能不同機)本來就脆弱 |
| SCALE-2000-PLAN §5.3 D-3:NetIQ 匯入排入佇列、批次時段才落盤 | 防白天 Web 寫主機檔與正在跑的批次互踩(JSONL 檔案時代) | 跨程序檔案鎖已實作、SQL 後端有交易;主機列本身輕量,重的部分(規則檢查紀錄)本來就要等下次批次 |
| Storage.Type 三選一(Jsonl/Sqlite/SqlServer) | JSONL 是現行格式,SQL 是新軌 | Sqlite 已是預設與主測試路徑、正式環境 SqlServer;Jsonl 相容模式沒有服役對象,新功能還要多寫一份檔案實作 |
| 決策 D:Txt 主機清單模式(HostListSource=Txt)為交接過渡 | 清單主人從 txt 交接到 Web 需要過渡期 | Sentinel 連線設定都進 Web 之後,「主人在 txt」的定位消失;txt 內容用 Web 批次貼上即可匯入 |

## 定案彙整(2026-07-24,全部經使用者確認)

| # | 決策 | 內容 |
|---|---|---|
| 1 | Sentinel 改 Web 維護 | 名稱/BaseUrl/帳密存共用儲存層,Web CRUD(admin、稽核);批次與 Web 都改讀 store;`appsettings.NetIq.Servers` 只剩一次性種子用途 |
| 2 | Sentinel 存法 | **lf_blobs 文件(key=`sentinels`)**,不建真表——與其他 webdata store 同模式,名稱唯一性在 store 邏輯驗,**零 DDL** |
| 3 | 密碼保存 | Core 共用加解密 Helper(AES,金鑰內嵌程式,密文 `enc:v1:` 前綴)。防翻 DB、不防取得程式的人,邊界誠實註明。前端 write-only:已設定僅顯示「已設定」,留空=不變;稽核不記密碼 |
| 4 | 主機參照 Sentinel | `WebHost` 加 `SentinelId`(HostId 的前例:PK 參照,改名不斷鏈);`NetiqServer` 字串降為顯示快照;啟動時依名稱一次性回填 |
| 5 | Sentinel 刪除/停用 | 刪除=確認視窗明示「轄下 N 台將停用並標記孤兒」,走既有 `OrphanedFromSentinel` 流程;另提供「停用」(暫停輪巡、主機不動)作過渡選項 |
| 6 | 新增精靈 | 「自動掃描匯入」checkbox 預設勾;**掃描即帳密驗證**(對 Sentinel 唯一會用的 API 就是列主機);掃描成功**當下建立 Sentinel**,精靈中途取消=Sentinel 留著、主機沒匯 |
| 7 | 匯入即時落盤 | D-3 佇列退役;精靈「匯入」直接套用主機異動,結果記入匯入紀錄;儀表板要有內容仍等下次批次(使用者已知悉) |
| 8 | 匯入時群組指派 | 各網段選既有群組/建新群組/跳過;**新群組送出當下即建立**(空群組無害,可先設授權);跳過=未分組=僅 admin 可見(畫面提示);**既有主機的群組一律不動**(匯入不是隱性改權限) |
| 9 | 無回報告警豁免 | 新主機 `CreatedAt` 未滿一個批次週期不列入「無回報主機」告警,避免整批匯入即告警洪水 |
| 10 | Jsonl 後端退役 | 刪除 `Storage.Type="Jsonl"` 全部檔案實作;**不做 Jsonl→SQL 遷移工具**(無服役中的 Jsonl 正式資料);設成 Jsonl→啟動明確報錯 |
| 11 | lf_blobs 不正規化 | 「JSON 文件存 DB 一列」維持現狀;正規化觸發點見「未來觀察點」 |
| 12 | Txt 清單模式退役 | `HostListSource`/`HostListDirectory` 設定、`TxtHostListProvider`、`--import-hosts` 一併刪除 |
| 13 | Schema 升級機制 | 本輪零 DDL,**不建機制**;方針(未來採自製冪等 DDL,不用 EF Migrations)寫入 DB-PLAN |
| 14 | 掃描全部 Sentinel | 不做(新增即掃已涵蓋初次接入;日常巡檢需求出現再議) |
| 15 | 使用者群組勾主機群組 | 僅 `Role=User` 群組顯示(WEB-SPEC 決策 #13 不變);寫入沿用 `PUT /api/admin/access/{userGroupId}`,與授權矩陣同一條路 |
| 16 | 主機群組成員 modal | 「目前成員」(依 /24 分組、勾選移除、未分組警示)+「加入成員」(既有流程)兩頁籤 |
| 17 | 左側選單 | 「CSV 匯入」→「資料匯入」,NetIQ 匯入/Sentinel 管理/匯入紀錄整併入內;主機頁保留捷徑 |

### 既有決策修訂對照

- **NETIQ-HOSTLIST-WEB-PLAN 決策 E** → 修訂:單一事實來源由「批次 appsettings.json」改為「共用儲存層(sentinels blob)」。「同一時間只有一個主人」原則不變,主人換位。
- **NETIQ-HOSTLIST-WEB-PLAN 決策 D** → 廢止:Txt 模式退役(定案 12)。
- **SCALE-2000-PLAN §5.3 D-3** → 廢止:匯入即時落盤(定案 7),前提變化見背景節。
- **DB-PLAN / WEB-SPEC §10** → Jsonl 後端退役、`--import-history` 確定不做;schema 升級方針補記。

## 已盤點的現況事實(實作前驗證過)

1. `WebHost.GroupIds` 與 `WebUser.GroupIds` 均為 `List<long>`,`GroupAccess` 多對多——**需求 4 零改動**。
2. 掃描精靈(掃描→/24 分組→勾選→佇列)已存在於主機頁;缺的是 Sentinel Web 維護、群組指派、即時落盤與搬家。
3. SQL 模式下 webdata 全部是 `lf_blobs`/`lf_log_lines` 文件,**沒有** `lf_hosts` 等真表;`SentinelId`/`CreatedAt` 只是 JSON 屬性,零 DDL。
4. `EfJsonBlobStore.Mutate` 在 SqlServer 預設隔離等級下「讀→改→寫」**擋不住更新遺失**(兩行程同讀舊值、後寫蓋先寫);SQLite 因資料庫級寫入鎖+busy 重試無此問題。檔案時代的 `.lock` 防的正是這件事,換 DB 後防線沒跟上——Phase 1 以 `UpdatedAt` 併發檢查補上。
5. `FilePermissionSnapshotStore`(permission_snapshot.json)目前**所有模式都走檔案**,屬「JSON 作為資料庫」殘留,一併收進 blob。
6. `EnsureCreated()` 只在資料庫不存在時建 schema,對既有 DB 不加表不加欄——本輪零 DDL 所以無事,但這是未來的地雷,方針記入 DB-PLAN(定案 13)。
7. 沒有任何 `--import-history` 遷移工具存在;確認不做(定案 10)。
8. 保留的檔案輸出(不屬「JSON 作為資料庫」):export\ 報告全文 txt、logs\、appsettings、Sqlite .db 本體。

## 資料模型變更

| 項目 | 變更 |
|---|---|
| `Sentinel`(新,Core) | `SentinelId`(long)、`Name`(唯一,不分大小寫)、`BaseUrl`、`Username`、`PasswordEnc`(密文)、`Active`、`CreatedAt`/`UpdatedAt` |
| `ISentinelStore`(新) | CRUD+配號;實作走 `IJsonBlobStore`(key=`sentinels`),與其他 webdata store 同模式 |
| `WebHost` | 新增 `SentinelId`(long?,null=待歸屬)、`CreatedAt`(告警豁免依據);`NetiqServer` 字串降為顯示快照(讀取端改吃 SentinelId) |
| `SentinelServer`(設定類) | 保留唯讀:種子匯入來源;`CanDiscover` 邏輯移到 `Sentinel` |
| `NetiqImportQueueEntry`/`INetiqImportQueueStore` | 刪除(佇列退役);匯入結果改記 `IImportLogStore` |
| `CryptoHelper`(新,Core) | `Encrypt`/`Decrypt`+`IsEncrypted`(`enc:v1:` 前綴);AES-256,金鑰內嵌 |
| `lf_blobs` | `UpdatedAt` 設為 EF ConcurrencyToken(更新遺失→例外→既有重試迴圈接手) |
| 設定 | 刪除 `NetIq.HostListSource`/`HostListDirectory`;`NetIq.Servers` 註解改為「僅供一次性種子匯入,維護請至 Web」;`Storage.Type` 合法值=Sqlite/SqlServer |

## 新增 Sentinel 精靈(定案流程)

1. **步驟 1:連線設定**——名稱(即時驗唯一)、BaseUrl、帳密、「自動掃描匯入」checkbox(預設勾)。
   未勾→「建立」單純存檔結束。勾選→「下一步」=以**尚未存檔**的帳密呼叫掃描(admin 專屬端點,帳密僅過境不落地):
   失敗→留在本步顯示錯誤(=帳密驗證失敗);成功→**當下建立 Sentinel**(進稽核),進步驟 2。
2. **步驟 2:勾選網段**——/24 分組,每網段顯示台數與「其中 N 台已登錄」;網段可展開至單台
   (徽章:已登錄/原屬 XX 已停用);預設全勾。
3. **步驟 3:指派群組**——各網段下拉:既有群組/建立新群組(送出當下即建)/跳過;
   跳過提示「未分組=僅 admin 可見」。整步可「跳過」。
4. **送出「匯入」**——即時套用:新增/復活主機寫 `SentinelId`、指派群組;既有主機只更新
   DisplayName/歸屬、**群組不動**;結果摘要(新增/更新/復活/略過各幾台)記入匯入紀錄+稽核。
5. 邊界:掃描結果 30 分鐘效期,逾期送出→退回步驟 2 要求重掃,已選網段盡量保留;
   精靈中途關閉→Sentinel 留存、主機未匯(之後可從 NetIQ 匯入頁籤補掃)。

## 實作步驟(六個 Phase,每個結束時建置零警告+測試全綠)

### Phase 1:Jsonl 後端退役 ✅ 已完成(2026-07-24)

建置零警告、**654 項單元測試**全數通過、`--selftest`(99 項)通過。

| 項目 | 內容 | 狀態 |
|---|---|---|
| 設定收斂 | `Storage.Type` 合法值 Sqlite/SqlServer(`IsValidType`);`Jsonl`/未知→啟動報錯(批次與 Web 兩邊的設定驗證) | ✅ |
| StorageFactory | `Blob`/`LogStore` 移除檔案分支與 `jsonlPath` 參數;全部 `Create*` 收斂;`CreateRecordStore`/`CreateRuleStore`/`CreateSuppressionStore` 改吃 `dataRoot` | ✅ |
| 刪除 | `FileJsonBlobStore`、`FileJsonLogStore`、`JsonlAnalysisRecordStore`、各 store 的檔案路徑便利建構子 | ✅ |
| 快照入庫 | `FilePermissionSnapshotStore` → `JsonPermissionSnapshotStore`(blob,key=`permission_snapshot`),檔案版刪除 | ✅ |
| 併發防線 | `lf_blobs.UpdatedAt` 加 `IsConcurrencyToken()`;新增 `Blob並發衝突_過期寫入被擋下` 測試,直接驗證 `DbUpdateConcurrencyException` | ✅ |
| 測試 | 刪 `JsonCollectionFileTests`、`JsonlAnalysisRecordStore*Tests`;各合約測試收斂為僅 Sqlite fixture;`SelfTestRunner` 移除讀 rules.json/suppressions.json 檔案的分支,改固定驗內建種子/不連 DB | ✅ |
| 佈線清理 | 批次 `Program.cs`、Web `ServiceCollectionExtensions.cs`/`Program.cs` 移除檔案路徑佈線;appsettings(批次/Web/Development/.example)與 README 註解改寫 | ✅ |
| **Txt 清單模式退役**(原訂 Phase 2,提前一併完成) | `TxtHostListProvider`、`NetiqTxtImporter`、`--import-hosts`、`NetIq.HostListSource`/`HostListDirectory`/`UsesWebHostList` 全數刪除;`StoreHostListProvider` 成為唯一來源;`HostListCli` 精簡為僅 `--host-list` | ✅ |

### Phase 2:Sentinel Web 維護 ✅ 已完成(2026-07-24)

Txt 退役已提前併入 Phase 1(見上)。建置零警告、**687 項單元測試**全數通過、`--selftest`(99 項)通過,並實際啟動 Web 端到端驗證(新增/編輯/停用/刪除全走過一輪,確認密碼不進 API 回應與稽核明細)。

| 項目 | 內容 | 狀態 |
|---|---|---|
| Core | `Sentinel` 模型、`ISentinelStore`+blob 實作(key=`sentinels`)、`CryptoHelper`(AES-256,`enc:v1:` 前綴,定案 2、3) | ✅ |
| WebHost | `SentinelId`(long?)/`CreatedAt` 屬性;`SentinelIdBackfiller` 一次性回填(冪等,批次與 Web 啟動時都跑);`NetiqHostList.PendingAssignment`/`Pollable` 改吃 SentinelId,`Pollable` 新增可選的 `isSentinelActive` 排除停用 Sentinel 的主機 | ✅ |
| Web API | `SentinelAdminService`+`AdminController` 的 Sentinel CRUD(Maintain 能力、稽核、密碼 write-only);刪除直接孤兒化轄下主機(不沿用批次向的 `NetiqOrphanSweeper`——那支帶有「現存名單整個是空就安全跳過」的欄杆,會誤擋「刪除最後一台」這個合法操作);停用=暫停輪巡,主機不動 | ✅ |
| Web UI | 新頁面 `/admin/sentinels`(Sentinels.cshtml+sentinels.js,List+Modal 模式);「新增即掃描」精靈留待 Phase 4 資料匯入頁整併時做 | ✅ |
| Catalog | `NetiqServerCatalog` 改讀 `ISentinelStore`(介面不變,呼叫端零改動,密碼在此解密供探索用戶端使用);`SentinelServer` 加 `Id` 欄位 | ✅ |
| 種子 | `SentinelSeeder`:sentinels blob 為空時於 Web 啟動時自批次 `appsettings.NetIq.Servers` 一次匯入(密碼順手加密);找不到/解析失敗批次設定檔不擋啟動 | ✅ |
| 批次 | `NetiqOrphanSweeper`/`NetiqImportApplier` 改吃 SentinelId;`HostListProviders.cs` 的 `HostListSelection.FromStore` 改注入 `ISentinelStore`,分組鍵用 Sentinel 現存名稱(不是可能落後的 NetiqServer 快照),Sentinel 停用時排除並列警告 | ✅ |
| 寫入路徑 | `NetiqHostService.AddHost`/`BulkAddHosts`、`HostAdminService.SaveHost`(含依 Sentinel 篩選)一律解析 Name→SentinelId 後兩者一起寫;`SentinelAdminService` 改名時同步所有掛在該 Sentinel 下主機的 NetiqServer 顯示快照 | ✅ |

**實作期間的修正**:`SentinelAdminService.SaveSentinel` 原本在 `_sentinels.Upsert(...)` 呼叫**之後**才比較 `existing.Name` 是否變了——DB 後端的 `Read()` 每次回全新物件所以沒事,但簡單的記憶體型測試替身共用物件參考,`Upsert` 內部的變動會回頭污染 `existing` 這個先前抓到的參照,導致「改名」永遠判定為「沒改名」。改成呼叫 Upsert 前先把舊名稱存成區域變數,不依賴物件參考在多次呼叫之間保持不變——這對兩種後端都正確,而不是湊巧在其中一種上可行。

### Phase 3:匯入佇列退役+即時匯入 ✅ 已完成(2026-07-24)

建置零警告、**687 項單元測試**全數通過、`--selftest`(99 項)通過,並實際啟動 Web 以 Stub 探索用戶端走完整條端到端流程(新增 Sentinel → 掃描 60 台 → 勾選匯入 59 台 → 立即出現在主機頁 → 匯入紀錄與稽核皆正確記錄 → 刪除 Sentinel → 轄下主機正確孤兒化)。

| 項目 | 內容 | 狀態 |
|---|---|---|
| 刪除 | `INetiqImportQueueStore`/`JsonNetiqImportQueueStore`、`NetiqImportQueueEntry`/`NetiqImportQueueStatuses`、`NetiqImportQueueCli.cs`、`--apply-netiq-imports`、批次啟動時的佇列處理區塊、主機頁佇列卡與取消 UI、`NetiqQueueEntryDto`、稽核動作 `NetiqImportEnqueue`/`NetiqImportCancel` | ✅ |
| 即時套用 | `NetiqDiscoveryService.Enqueue` → `Import`:直接呼叫 `NetiqImportApplier.Apply`(簽章簡化為 `(serverName, selectedIps, hosts, sentinels)`,不再依賴佇列實體);token 用過即丟,同一次掃描不能重複套用兩次 | ✅ |
| 匯入紀錄 | 結果寫入既有的共用 `IImportLogStore`(`Kind="Netiq"`,`FileName` 借用欄位顯示 Sentinel 名稱;新增 `RevivedCount` 欄位)——與 CSV 匯入共用同一份「資料匯入」頁的稽核軌跡,不是另立一份 | ✅ |
| 告警豁免 | `HostAdminService.NewHostGracePeriod`(24 小時,public 常數)供 `DashboardService.BuildSilentHosts` 與 `HostAdminService` 的 `silent` 篩選共用;LastReportAt 為 null 時改看 CreatedAt 是否超過寬限期,已回報過的主機不受影響、沿用原本 2 天判定;連 hosts.js 的「尚未回報」紅字樣式也套用同一寬限期,避免「不算告警但畫面還是一片紅」的半吊子修法 | ✅ |
| 群組指派 | **刻意不做**(範圍收斂):決策 8 的「依網段指派群組」是「新增 Sentinel 精靈」步驟 3 的畫面設計,與現有這支泛用掃描精靈是兩個不同的 UI 流程;Phase 3 只做後端即時落盤,新匯入主機一律落在「未分組」安全預設,群組指派留給 Phase 4 的新精靈一併做 | ⏸ |
| 文件 | `docs/SCALE-2000-PLAN.md` §5.3 D-3 標記已廢止(附前提變化說明,原文保留供歷史對照);README「NetIQ 匯入佇列套用」章節改寫 | ✅ |

**實作期間的範圍判斷**:計畫原文的「即時套用」列著「依網段指派群組」,但推敲上一輪討論紀錄後發現那其實是「新增 Sentinel 精靈」(Phase 4)步驟 3 的設計,不是這支既有泛用掃描精靈的既定行為——加了會是提前把 Phase 4 的 UI 決策做掉，而不是 Phase 3「退役佇列」本身的份內事。改成只做後端(立即落盤),維持「新匯入主機未分組」的既有安全預設，群組指派整段留給 Phase 4 一次做。

### Phase 4:精靈+資料匯入頁整併 ✅ 已完成(2026-07-24)

建置零警告、**693 項單元測試**全數通過、`--selftest`(99 項)通過,並實際啟動 Web 走完整端到端流程(「新增 Sentinel」精靈勾自動掃描 → 60 台分兩網段 → 一網段選既有群組、一網段建新群組 → 完成匯入 23 新增+36 復活 → 主機/群組頁核實正確 → 「掃描匯入」對既有 Sentinel 走同一精靈 → 刪除 Sentinel 清理)。

| 項目 | 內容 | 狀態 |
|---|---|---|
| 導覽 | `layout.js` 選單「CSV 匯入」→「資料匯入」;移除獨立的「Sentinel 管理」項目與 `/admin/sentinels` 路由 | ✅ |
| 頁面整併 | `Imports.cshtml` 加 `nav nav-tabs`(CSV 匯入/NetIQ 匯入,沿用 `ui.js` 既有的 `bindTabs` 頁籤 helper,未再手刻);NetIQ 分頁含 Sentinel 清單(CRUD 由 `Sentinels.cshtml`/`sentinels.js` 併入 `imports.js`)+精靈進入點;`Sentinels.cshtml`/`sentinels.js` 整支刪除 | ✅ |
| 統一精靈 | `新增 Sentinel`(連線設定→可選自動掃描)與既有 Sentinel 的「掃描匯入」共用同一個 3 步驟 modal(連線/選主機/指派群組),不拆兩套 UI;`POST netiq/create-and-scan` 驗名稱唯一→裸帳密掃描→成功才建立 Sentinel(定案 6);網段選主機沿用原 hosts.js 掃描清單樣式;群組指派面板(未分組/既有群組/建立新群組)只影響本次新增的主機 | ✅ |
| 搬家 | 掃描精靈與匯入紀錄自 hosts.js 移至 imports.js;主機頁移除整套掃描 modal,改為連到 `/admin/imports` 的純連結;主機頁待辦卡(待歸屬/IP 衝突/未分組)保留不動 | ✅ |
| 退役 | 掃描精靈改為「進 Sentinel 詳情才掃」後,`netiq/scan-targets` 端點、`GetScanTargets`、`NetiqScanTargetDto`、`INetiqServerCatalog.GetServers()` 全鏈路失去唯一呼叫端,一併刪除(而非留著等未來用到) | ✅ |

### Phase 5:群組功能(需求 2、3) ✅ 已完成(2026-07-24)

建置零警告、**698 項單元測試**全數通過,並實際啟動 Web 走完整端到端流程(新增 User 角色群組並勾主機群組→授權矩陣核實勾選正確落地→切換角色即時看到勾選框與說明文字互換→主機群組「目前成員」頁籤依 /24 展開、勾選主機看到即時「N 台將變未分組」警示→移出成員→主機頁核實該主機變回未分組→「加入成員」頁籤原有功能不受影響→復原測試資料)。

| 項目 | 內容 | 狀態 |
|---|---|---|
| 使用者群組 | 編輯 modal 內嵌主機群組勾選(僅 Role=User;其他角色即時切換顯示「此角色可檢視全部主機」);寫入走既有 `PUT access/{userGroupId}` API,不另開新端點;新建群組兩段式——群組本體與存取範圍分兩支請求送出,後者失敗會有獨立的警示 toast(「群組已儲存，但設定可檢視的主機群組時發生錯誤，請至授權矩陣頁籤手動設定」)並仍視為建立成功;新群組沒勾任何主機群組時略過第二支請求，避免留下「由（無）改為（無）」的空稽核紀錄，編輯既有群組則一律送出(含全部取消勾選＝收回全部授權) | ✅ |
| 主機群組 | 群組名稱本身變成點擊入口(改用連結樣式按鈕，原本並列的「加入成員」按鈕移除);開啟的 modal 用 `ui.js` 既有 `bindTabs` 分兩頁籤:「目前成員」(新，依 /24 用 `<details>` 摺疊分組，逐台勾選，`OtherGroupCount=0` 的主機顯示「移除後將未分組」徽章，勾選時即時算「N 台將變未分組」提示)/「加入成員」(既有網段批次加入邏輯原樣搬進頁籤，行為不變);新增 `GET host-groups/{id}/members`(依 `OtherGroupCount` 供前端判斷)與 `POST host-groups/{id}/members/remove`(只動被移出的那個群組 id，其餘既有群組不受影響，稽核走既有 `HostUpdate` 動作) | ✅ |

### Phase 6:文件與收尾 ✅ 已完成(2026-07-24)

建置零警告、**698 項單元測試**全數通過、`--selftest`(99 項)通過,實際啟動 Web 確認各頁面
（總覽儀表板、資料匯入、群組與授權）無主控台錯誤——本 Phase 全程只動文件,程式碼零改動,
Phase 4/5 已各自做過精靈與群組功能的完整瀏覽器端到端驗證,本輪不重複執行,只做啟動健檢。

| 項目 | 內容 | 狀態 |
|---|---|---|
| `WEB-SPEC.md` §10 儲存章改寫 | §10.2 儲存介面表全面改寫(檔案路徑→`lf_blobs`/`lf_log_lines` key,補 `ISentinelStore`、退役 `INetiqImportQueueStore`);§10.3 移除「JSONL 查詢期即時聚合」的替代路徑敘述;§10.4 重寫為「Jsonl 退役與 blob 併發防線」(`ConcurrencyToken`+`Mutate` 重試機制取代原「檔案單一寫入者+原子替換」);§10.5 `Storage.Type` 由三選一改二選一;§1 決策表、§2/§3 系統全貌與 SOLID 對應表一併補記退役狀態 | ✅ |
| `SCALE-2000-PLAN` D-3 廢止註記 | 已於 Phase 3 完成(§5.3 標記已廢止並保留原文) | ✅（沿用既有） |
| `NETIQ-HOSTLIST-WEB-PLAN` 決策 D/E 修訂註記 | 決策 D(Txt 清單模式)標記已廢止、決策 E(Sentinel 唯讀來源)標記已修訂,各附前提變化說明,原文保留供歷史對照 | ✅ |
| `DB-PLAN` 補記 | 頂部免責宣告加 Jsonl 退役補記;「匯入器」`--import-history` 標記確定不做;新增「Schema 升級機制（定案 13）」小節說明未來自製冪等 DDL 的方針;決策狀態彙整表補三列(Jsonl 退役/`--import-history` 不做/schema 升級機制) | ✅ |
| `README.md` 部署章 | 「歷史資料庫（history.txt）」章節標記已退役,加現況說明(備份標的從檔案改為 DB)、欄位級說明原樣保留供資料模型參考;「多台伺服器」「DB 後端」兩則後續方向澄清 Sentinel/主機清單管理已完成、`Storage.Type` 改二選一 | ✅ |
| 收尾體檢(commit 前全面複查) | `JsonCollectionFile<T>` 基底類別更名 `JsonBlobCollection<T>`(名稱裡的「File」已名不符實——底層全走 `IJsonBlobStore`,且本表下方驗收清單明列此名不該殘留);一併清掉散落各 store docstring 的檔案時代語言(「JSONL 後端實作:webdata\xxx.json」「原子替換＋跨程序鎖」→「blob/log key=xxx」「原子讀改寫」),批次 Program.cs 資料根目錄註解同步更正。歷史章節(WEB-SPEC §14、HISTORY-STORE-FIX-PLAN 等)中的舊類別名依慣例保留 | ✅ |

**未在本輪處理**（明確排除,理由）：docs/WEB-SPEC.md §14「實作進度與過程中的定案」與
docs/DB-PLAN.md 各處「txt ↔ DB 一致性保證」等按時間戳記的歷史決策/進度記錄，均為**當下時序的
如實記錄**（如 2026-07-21 的 Phase 5 SQL 後端「暫緩」決定，事後已被 SCALE-2000-PLAN Phase C 推翻，
該推翻已記錄在 SCALE-2000-PLAN 自己的文件裡），逐條回頭改寫會讓「決策是什麼時候做的」這個資訊
本身失真，不符合本專案一路採用的「標記退役/修訂＋原文保留」慣例。

## 測試與驗收重點

- **併發**:blob ConcurrencyToken 案例(兩個 store 實例交錯 Mutate,兩筆變更都存活)。
- **加解密**:roundtrip、`enc:v1:` 前綴辨識、未加密舊值(種子匯入前手填)的相容讀取。
- **SentinelId 回填**:名稱對得到→補 id;對不到→維持 null(=待歸屬)並列警告。
- **刪除 Sentinel**:轄下主機全部停用+`OrphanedFromSentinel` 正確;復活重綁流程不受影響。
- **精靈**:掃描失敗不建 Sentinel;成功建立後中途關閉不匯主機;token 逾期退回重掃;
  既有主機群組不被匯入覆蓋;跳過群組的主機僅 admin 可見。
- **告警豁免**:新匯入主機不觸發無回報告警;滿一週期後恢復納入。
- **退役完整性**:全庫 grep 無 `Jsonl`/`JsonCollectionFile`/`HostListSource`/`apply-netiq-imports` 殘留(註解與文件除外);`Storage.Type="Jsonl"` 啟動報錯訊息可理解。
- **群組**:Role=User 以外的群組編輯不出現勾選;移除成員的未分組警示數正確。

## 未來觀察點(本輪不做,記錄觸發條件)

1. **lf_hosts 正規化**:NetIQ 2000 台上線後,夜間批次每台 `TouchNetiq` 都是整份 hosts blob 重寫(一晚約 2000 次)。若實測批次時間或鎖衝突異常,第一個正規化 lf_hosts(DB-PLAN 表設計現成,`IHostStore` 介面遮蔽、服務層零改動)。
2. **Schema 升級機制**:第一次要動真表時建自製冪等 DDL(EnsureCreated 建的庫無 `__EFMigrationsHistory`,採 EF Migrations 需假基線;雙 provider migrations 維護成本高)。
3. **掃描全部 Sentinel**:日常「巡一輪找新機器」需求出現再議(定案 14)。
4. **報告全文入庫**(DB-PLAN `lf_reports`):export\ txt 維持檔案交付物;Web 需全文檢索時再議。
5. **密碼加密強化**:金鑰改環境變數(真加密)——內嵌金鑰的防護邊界已文件化,營運上有要求時升級。
