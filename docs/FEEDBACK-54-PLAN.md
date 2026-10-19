# 第 54 輪規劃(重寫版):PRTG 回歸正軌

> 狀態(2026-10-16):作業 0 環境探測已執行(1.0.53.2 現場),結果已回寫本文件,D8–D14 使用者已定案——見 §3、§5 作業 0;**本輪只規劃不實作**,實作授權另行取得。
> 來源:使用者 2026-10-15 要求重新審視 51～54 輪,移除不符目標、多餘、過度設計、難以使用的內容,讓 PRTG 資料順利加入專案並達到輔助判斷的目的,整體可靠性達企業級。審查過程與逐項裁定見 [ROUNDS-51-54-REVIEW.md](archive/ROUNDS-51-54-REVIEW.md);原版 54 輪 PLAN 封存為 [FEEDBACK-54-PLAN-v1.md](archive/FEEDBACK-54-PLAN-v1.md)。
> 起點:`feature/prtg-feedback-54` HEAD(`0985be90`),測試基線 7,978 通過／1 失敗(已修)／10 可選略過(`b77a72c7`,Windows)。本輪延續此分支,不另開。
> 原 27 項 E01–E10 TODO、AC01–AC22、U53、Q1–Q6、R54、C54、coverage inventory 與 user-matrix **全部廢止**,不再追蹤;以本文件 §6 的目標導向驗收取代。

## 1. 目標與不變契約

**北極星**:以 NetIQ 主機清單為準,用 IP 對應 PRTG 裝置(找不到才用 hostname);取得 CPU／記憶體／磁碟／ping 數值;依**過往趨勢提前發現未來可能的問題**;每筆風險有憑有據、不誤報;PRTG 風險可單獨成立,也可搭配 NetIQ;設計規模 3,000 主機／15,000 sensor／180 日;SQLite 與 SQL Server 皆支援。

**非目標**(審查時據此直接刪):即時告警(資料最新也是前一日)、處理當下事故、鏡像整台 PRTG、NetIQ 清單外的主機、證明「比原生 PRTG 早幾分鐘」。

**企業級可靠的定義**(依序):資料正確(不丟、不重、對應正確、單位方向正確)→ 故障可恢復(斷線／重啟／來源異常後自動續跑)→ 狀態可觀測(卡在哪、為什麼)→ 操作簡單(少設定、預設合理)→ 規模可擴展(程式審查＋小型精確驗證,沿用 README「開發測試與驗收準則」)。

**不變的既有契約**(本輪不動):
- 主機身分來自主機主檔,PRTG 對不到就跳過、不建主機。
- `PrtgScopeDevices.Compute` 是取數範圍唯一出口;感測器／狀態變更／快照只處理範圍內;不對整台 PRTG 查。
- 寫入優先序 `ok` 覆蓋 `sampled`;可用列＝`ok` 或 `sampled` 且 coverage≥75。
- 風險只升不降;PRTG 看不到事件層證據,不壓低既有等級。ACK 與長期 Down 不降風險。
- 同日配對是弱佐證,不升風險。
- 停用／縮小範圍在安全點停止 PRTG,不取消 NetIQ。
- 快照 journal(checksum、writer lease)、`PrtgTablePager` 三道停止條件、資源守門、人工對應與 IP 排除。
- 共享配額 table 2／秒、historic 5／60 秒、在途 4,與退避。

**本輪推翻的舊契約**(明寫,避免兩份文件各說各話):
- 52 輪 9/30「排除 PRTG-only」→ 推翻:PRTG 證據足夠時可單獨成立風險(§5 C2)。
- 53 輪「數值必須經 trusted profile／raw proof／qualification 才可信」→ 推翻:可信＝對應有效＋品質可用＋單位可判(§5 B1)。
- 53 輪「CPU／記憶體預設提示、試算授權後轉正式」→ 推翻:規則啟用即正式(§5 B6)。
- 52／53 輪 PRTG 專屬即時郵件 → 推翻:PRTG 風險只進日／週摘要(§5 B2)。
- 53 輪兩小時資源壓力規則 → 移除:即時狀態不是趨勢。

## 2. 事實核對摘要

完整核對表見審查報告 §1、§4。對本輪契約有直接影響的事實:

| 事實 | 影響 |
|---|---|
| `PrtgHostMapper` 只比 IP,無 hostname 比對;NetIQ 主機有 `HostName`,PRTG device 有 `host`(可能是 DNS 名稱)與裝置名稱 | C1 新增 |
| 衝突清單／指派／排除 IP／批次 UI 已有(`prtg-admin.js` 鏡像狀態頁籤) | 保留,不重做 |
| 數值→finding 要過 `PrtgResourcePeriodConsumer` 四道閘 + 每顆 sensor 的 trusted profile | B1 主體 |
| 八條規則:down／flapping／warning／silent(狀態型)、三條兩小時壓力、`disk_free_trend`;CPU／記憶體預設 Hint | B6 移除壓力規則;C3 補趨勢 |
| 父紀錄閘三層;`lf_top_issues.record_id` 非空外鍵到 `lf_daily_records` | C2 載體必須是 `DailyAnalysisRecord` |
| 53／54 輪 store 走 `EfJsonBlobStore`(lf_blobs),以字元上限防爆 | B5 |
| `Directory.Build.targets` 每次建置跑 PowerShell;唯一 runtime 消費是全量回填 verified 閘 | B3 |
| `scripts/round53-user-matrix`、`round54`、`prtg-workload` 不在 CI、產品不讀;`Tests.csproj` 編進 `QualificationCoverage.cs` | B4 |
| PRTG 測試 213 檔 84K 行,25 處以上讀前端原始碼做字串斷言 | B7 |
| 三個專案皆 `net8.0-windows`;本機 Mac 無法建置 | 實作與驗證在 Windows |

**作業 0 探測事實(2026-10-16,現場 PRTG 24.1.92、程式 1.0.53.2、SQLite)**:

| 事實 | 影響 |
|---|---|
| 磁碟就緒度 43 顆全部 0／28 日,原因一律「可信採樣的 AnalysisTimeZoneId 缺失或無效」(`PrtgDiskAssessmentService` 的 trusted sampling 閘);小範圍資料流卻成功寫入並讀回 24 筆 `ok` CPU 數值 | 數值路徑現場可用,卡點正是 B1 要拆的閘——B1 方向確認 |
| `lastcheck_raw`／`datetime_raw`／raw history 的 OLE 數值換算後**恰等於 UTC**(10/15 00:00 +08 的列 raw 為 10/14 16:00);顯示字串是 zh-TW 上午／下午格式,探測端以 Invariant 解析失敗,正式路徑靠 `CurrentCulture` 才解得開 | 時間基準改以 raw(UTC)為主(D9),刪 `PrtgSourceTimeZoneId`;51 輪「raw 不可靠、差 7 小時」的註解是時區基準誤判 |
| sensor／channel 表**不回傳** `unit`、`scaling`、`primary` 欄;snapshot 無 `primarychannel` 欄;`getobjectproperty primarychannel` 四類皆回 0;raw history 可由各頻道數值互相驗算(disk 頻道 0＝頻道 1÷頻道 2＝90.0%;memory 頻道 0＝24.55%＝available÷total) | 單位對照只能依 type,且四類語意已可定案(D10);「以 unit 欄為準」那條路不存在 |
| Traffic 的 raw history 取值失敗(`raw-channel-source-shape-or-transport-invalid`)、`primarychannel` 回 `invalid_property_value` | Traffic 不在四類規則內,從對照表與白名單移除(D10) |
| device `host` 欄 6,604 台中 6,596 台是 IPv4、2 台 DNS 名稱、5 台打壞;裝置**名稱**格式固定如 `U(O-stkomsdb1)216.45.101`、`W(VM-GFYQ-AIS002)_216.11.10` | C1 hostname 候選改取裝置名稱括號內字串(D11);`host` 欄幾乎無 hostname 命中空間 |
| 站台對照:範圍 20 台(對應 18、衝突 2),鏡像卻有 42,998 顆 sensor、42,872 顆在範圍外(首次基準／縮小保護擋下自動清除);就緒度卡片以 9,782 顆全鏡像磁碟 sensor 當母體 | 一次性人工「確認清除」(作業 0 後續);就緒度母體改為範圍內已對應(D8) |
| 資源守門比對到的 3 台裝置(PRTG core、Sentinel ×2)不在取數範圍,要人工按「自動偵測並填入」 | C6 自動偵測(D12) |
| 逐裝置 messages 含下層 sensor 訊息(9d-5 ✓,平均 253 ms／台);`filter_objid` 分批取值精確相符 | 狀態型規則資料路徑不動 |
| sensor 表單次查詢固定耗時 7–8 秒,與 objid 數(1 或 100)無關;全站 sensor 全表約 1 分鐘 | 作業 D 容量估算用這個常數 |
| 探測步驟 4 相依性查 800 顆即逾時、且 100% 為預設父物件相依;步驟 7 Type×IPv4 幾乎全 100%;步驟 8 分頁語意結論已固化為 pager 保險絲 | 探測輸出瘦身併入 B1(D13) |
| 小範圍資料流印「對應日期 2026-09-22」——`GetLatestHostMapWithDate` 回看 30 日內最近一筆 `lf_prtg_host_map`,表示 9/22 之後對應表沒再寫入,即 PRTG 夜間取數或同步 24 天沒成功 | 現場狀態問題,非規劃問題;列為作業 0 後續查項,B1 完成後就緒度仍會是 0 直到取數恢復 |

## 3. 決策(2026-10-15 使用者定案)

| 編號 | 決策 |
|---|---|
| D1 | B1 砍 trusted sampling 整鏈;可信條件改為對應有效＋品質可用＋單位可判。 |
| D2 | PRTG 獨立風險載體:無 NetIQ 紀錄時建立 `LogSource=Prtg` 的 `DailyAnalysisRecord`;NetIQ 涵蓋類統計以 `LogSource` 排除。 |
| D3 | 驗收腳本目錄直接刪除(git 歷史保留),不搬 archive。 |
| D4 | 趨勢規則四類:磁碟耗盡(已有)、記憶體攀升、CPU 長期惡化、ping 延遲惡化;ping 反覆中斷由既有跨日升級(`PrtgCrossDay` N≥3)承接,不另立規則。ping 數值納入取數白名單。 |
| D5 | 實作與驗證在使用者的 Windows 機器;Mac 端只做規劃、規格與審查。 |
| D6 | 環境探測由使用者先執行(§4 作業 0),結果決定單位對照表與 hostname 退路的實際形狀;探測不阻擋移除類批次開工。 |
| D7 | GitHub CI(`windows-latest`,build＋test)是每段 push 後的第二道驗證通道;本機 Mac 不建置。 |

**2026-10-16 依作業 0 結果追加**(規劃者依探測事實提案,使用者 2026-10-16 全部接受定案;與 D1–D7 不衝突):

| 編號 | 決策 |
|---|---|
| D8 | 探測頁「磁碟值型資料準備度」整張重做為「數值資料就緒度」:四類共用、母體＝取數範圍內已對應 sensor、依不足原因分組計數＋精簡表格;每列「驗證這顆」「到指定主機補值」與整張「磁碟語意驗證」卡(批次驗證、規則試算、人工覆核)刪除。歸 B1-2。 |
| D9 | PRTG 時間基準改以 `*_raw`(OLE,現場證實為 UTC)為主,轉服務主機本地時間落盤;顯示字串只做交叉檢查與帳號時區偵測。**取消** `PrtgSourceTimeZoneId`——本輪新增設定鍵歸零。 |
| D10 | 單位對照只依 sensor type,不讀 `unit` 欄;CPU／記憶體／磁碟／Ping 四類語意定案(見 B1 對照表);Traffic 自對照表與預設白名單移除;`SNMP Linux Meminfo`、`WMI Free Disk Space (Multi Disk)` 現場未驗,先標「單位未知」排除,待實際資料再補對照。 |
| D11 | C1 hostname 候選字串＝裝置名稱括號內字串(含「去掉開頭單一字母＋`-`」變體)＋ `host` 欄非 IP 值;不以 `host` 欄為主。 |
| D12 | 新增小作業 C6:資源守門目標在覆寫清單為空時由夜間快照自動偵測並存入,人工填寫優先。 |
| D13 | 「完整環境探測」文字輸出瘦身:刪步驟 4(相依性)、步驟 7(Type×IPv4),步驟 8(分頁語意)縮為一行結論;併入 B1 的 probe 階段。 |
| D14 | 作業 0 後續三件使用者操作(清除範圍外鏡像、查執行紀錄為何 9/22 起無對應、到 PRTG 修 5 台壞 IP)不寫程式,列在 §5 作業 0 由使用者完成;未對應清單截圖仍待提供,C1 命中空間估算待補。 |

## 4. 作業總覽

| 作業 | 主題 | 性質 | 相依 | 規模(估) |
|---|---|---|---|---|
| 0 | 環境探測(使用者執行)——**已執行 2026-10-16**,剩三件後續操作 | 取事實 | 無;與 A／B 並行 | 使用者 30 分 |
| C3 | 三類趨勢規則共用引擎(含 B1 移入的覆寫語意 `type=分類[:語意]`、Ping 進／Traffic 出預設白名單) | 新增 | B1、作業 0 | 大 |
| C4 | finding 證據欄位與「資料不足」狀態 | 補強 | C3 | 中 |
| B5 | 收斂 lf_blobs JSON store | 重構 | B1、B2、B6、C2 | 中 |
| D | 容量設計審查(§4.12 方式) | 審查 | C3、B5 | 中 |
| E | 文件瘦身與收尾 | 文件 | 全部 | 中 |

**已完成**(逐條比對程式碼後自本表刪除,紀錄見 §7):A、B1(含 D8 就緒度卡、B1-4 殘留清除)、B2、B6、B3、B4、C1、C2、C5、C6、B7。B1 唯一未做的「`PrtgSensorTypeCategoryOverrides` 擴成 `type=分類[:語意]`」與 C3 共用同一個評估入口,移入 C3 一起做(C3 列已註記)。

**建議順序**:0 → C3 → C4 → B5 → D → E。先砍再加:C2／C3 要接的 consumer 會被 B1 簡化,先加會做兩次。

**委派與驗收方式**:
- 實作端在 Windows(待定:使用者機器上的 Claude Code 或 agy);整輪只用一種,中途切換註明起點。Mac 端(本規劃者)不寫程式,只寫階段規格與獨立審查 diff。
- 每階段驗收固定三件:`dotnet build LogForesight.sln -v q` 0 警告 0 錯誤;該階段列出的定向測試全綠;移除類另加「舊符號零命中」grep。每個作業結束跑一次全量,記入 §7;push 後 CI(windows-latest)必須綠,CI 紅不得進下一段。
- 每段只動該段白名單內的檔案;NetIQ／本機分析既有測試每段都要全綠——PRTG 導正不得讓 NetIQ 路徑退化。
- 移除類階段規格必附「依賴方白名單」:每個被刪符號的 production 呼叫端與處置(改呼叫替代／一併刪／保留)。白名單外出現新替身或相容多載視為未完成。
- **委派期間規劃者不動 repo**;突變驗證只用檔案備份還原,未提交檔案不得 `git checkout`。

## 5. 各作業契約

### 作業 0:環境探測(使用者執行,結果回傳後更新 §5 暫定值)

**不需新寫探測程式**:現行程式已具備本輪需要的輸出;步驟 9 的「相容性證據探測」在 B1 才刪,探測先用它取事實。用 `feature/prtg-feedback-54` 的乾淨候選(`faeb7bca` 或本分支最新)建置部署;若現場仍是舊版(1.0.53.x),步驟 9 可能缺 Ping／Traffic 兩類或 `unit` 欄位,屆時先更新部署再探測。

操作:「系統管理 > PRTG 維護 > 環境探測」先確認連線設定已儲存,依序按「驗證小範圍資料流」→「完整環境探測」,完成後按「下載探測結果」(文字)與「下載去識別相容性證據 (JSON)」。文字輸出含主機名稱、IP、PRTG 訊息,分享前遮蔽;JSON 已去識別。另請截一張鏡像狀態頁籤「未對應」清單前 20 筆的 device 名稱欄(可遮蔽成前三字),供 C1 判斷 hostname 命中空間。

| 要回答的問題 | 看哪一段 | 決定什麼 |
|---|---|---|
| CPU／記憶體／磁碟／Ping／Traffic 各一顆的頻道、`unit`、數值長相(記憶體是可用 % 還是已用 %?磁碟是剩餘 %?ping 是 ms?) | 相容性 JSON `targets[]` 的 `channels`(`unit`／`unit_type`／`scaling_type`)、`snapshot`、`identity_preserving_raw_history` | B1 單位對照表、C3 方向(暫定轉定案) |
| sensor table 的 `unit` 欄全站有沒有值 | 文字輸出步驟 3 type 分布的 unit 樣本 | B1「單位可判」以 unit 欄還是 type 對照為主 |
| device 的 `host` 欄有幾台是 DNS 名稱、幾台無法判定 | 步驟 6 IP 覆蓋概要 | C1 hostname 退路的範圍 |
| 目前對應 ok／unmatched／conflict 各幾台;未對應 device 名稱與 NetIQ 主機名的相似度 | 站台對照 [S1]、未對應清單截圖 | C1 比對字串取 `host` 欄還是裝置名稱 |
| PRTG 帳號 Date Format 與時區 | 相容性 JSON `message_time.date_format`／`source_timezone`、探測標頭 | B1 是否需要 `PrtgSourceTimeZoneId` 預設值 |
| 逐裝置 messages 是否含子 sensor | 文字輸出 9d-5 結論 | 狀態型規則資料路徑是否要改(✓ 就不動) |
| 小範圍資料流是否寫入讀回 ≥1 筆 `ok` | 小範圍資料流輸出 | historicdata 路徑在現場可用 |

探測結果**不授予**任何資格,只回答上表。沒有結果前,C1／C3 的數值與對照表一律標「暫定」;探測不阻擋 A／B 系列開工。

**2026-10-16 探測回答**(原始輸出見使用者提供的文字與相容性 JSON;事實摘要在 §2 第二表):

| 問題 | 回答 | 落地 |
|---|---|---|
| 四類頻道、單位、數值長相 | CPU 頻道 0「Total」已用 %;記憶體頻道 0「Percent Available Memory」**可用 %**(頻道 1 available bytes、頻道 2 total bytes 可驗算);磁碟頻道 0 **剩餘 %**(頻道 1 free bytes ÷ 頻道 2 total bytes＝90.0% 吻合);Ping 頻道 0「Ping Time」ms、頻道 3「Packet Loss」%;Traffic raw history 取值失敗 | B1 對照表定案(D10) |
| `unit` 欄全站有沒有值 | channel 表不回 `unit`／`scaling`／`primary`,snapshot 無 `primarychannel` 欄;type 分布的 unit 樣本只來自 sensor 表 `lastvalue` 的單位字尾 | 單位可判只依 type(D10) |
| `host` 欄 DNS 名稱幾台、無法判定幾台 | 2 台 DNS、5 台打壞(objid 130033、130039、130045、153861、153866) | C1 候選改裝置名稱(D11);壞 IP 由使用者到 PRTG 修 |
| 對應 ok／unmatched／conflict;未對應名稱相似度 | 範圍 20 台:ok 18、conflict 2;未對應清單截圖**未提供** | C1 命中空間估算待補 |
| 帳號 Date Format 與時區 | JSON `source_timezone`／`source_locale` 皆 unknown;顯示字串為 zh-TW 上午／下午;raw 換算等於 UTC,與 +08 顯示差 8 小時 | 時間基準改 raw(D9),不需設定鍵 |
| 逐裝置 messages 是否含子 sensor | ✓(9d-5,三台皆含下層感測器訊息) | 狀態型規則資料路徑不動 |
| 小範圍資料流 ≥1 筆 `ok` | ✓ 24 筆 `ok`(CPU,00:00＝0.75) | historicdata 路徑現場可用 |

**作業 0 後續(使用者操作,不寫程式)**:
1. 維護頁「監看範圍外資料」預覽後按「確認清除」,把 42,872 顆範圍外 sensor 鏡像清掉(守門保留集合與首次基準保護仍生效;清完後就緒度母體與站台對照的數字才有意義)。
2. 查「執行紀錄」與鏡像狀態頁:為何 `lf_prtg_host_map` 最後一筆是 2026-09-22——夜間取數沒跑、跑了失敗、還是對應步驟被擋。此事不解,B1 做完就緒度仍是 0／28。
3. 到 PRTG 修正 5 台壞 IP 的裝置 host 欄;提供鏡像狀態頁「未對應」清單前 20 筆裝置名稱(可遮蔽)供 C1 估命中空間。

### 作業 A:止血與基線

- 廢止原 27 項 TODO 與全部驗收矩陣(已在本文件頁首宣告);`README.md` 頁首「PRTG 整合進度」一段改指本文件。
- 記錄起點 commit、Windows 基線測試數、移除前的 PRTG 程式行數／測試檔數(審查報告 §2),供 E 對照。
- 驗收:`docs/` 主目錄只有本 PLAN 一份活動規劃;`grep -rn "E0[1-9]-T\|AC0[1-9]\|U53-\|R54-\|C54-" docs/*.md README.md CLAUDE.md` 零命中(archive 除外)。

### 作業 B1:拆 trusted sampling 鏈,簡化數值可信條件

**目標行為**:一顆 sensor 的小時數值要被趨勢／狀態規則採信,只需同時滿足:(1) 該日有 `ok` 對應(或人工對應)的主機;(2) 該小時列為可用列(`ok` 或 `sampled` 且 coverage≥75);(3) 單位可判(§B1 單位對照)。不需要任何綁定、proof、資格作業、profile。

**單位對照**(2026-10-16 定案,D10):只依 sensor `type` 查內建對照 → 得「量測語意」與「取值頻道」;PRTG 的 sensor／channel 表不回 `unit`／`scaling`／`primary`,不存在「以 unit 欄為準」的路徑。對照外的 type → 該 sensor「單位未知」,所有數值規則跳過它並計入「排除:單位未知」。既有 `PrtgSensorTypeCategoryOverrides` 擴成可同時覆寫語意(`type=分類[:語意]`),不新增第二張設定表。

| sensor type | 語意 | 取值頻道(historicdata 欄名關鍵字) | 現場驗證 |
|---|---|---|---|
| `SNMP CPU Load` | `cpu-percent-used` | `Total` | ✓ 頻道 0,4% |
| `SNMP Memory` | `memory-percent-free`(規則端轉已用 %) | `Percent Available Memory` | ✓ 頻道 0,24.55%＝available÷total |
| `SNMP Disk Free` | `disk-percent-free` | 第一個顯示為 % 的頻道(頻道 0) | ✓ 90.0%＝free bytes÷total bytes |
| `Ping` | `ping-ms` | `Ping Time` | ✓ 頻道 0;`Packet Loss` 不取 |
| `WMI Free Disk Space (Multi Disk)`(18 顆)、`SNMP Linux Meminfo`(797 顆) | 暫「單位未知」 | — | 未驗;Linux Meminfo 待實際資料補對照,Multi Disk 多碟合一不適合單值趨勢 |
| `SNMP Traffic 32bit／64bit` | 移除 | — | raw history 取值失敗,且不在四類規則內;自預設白名單移除 |

**頻道選取**:`PrtgFetchService` 現行「取第一組 `value_raw`」在四類現場都剛好是對的頻道,但沒有核對;B1 改為「依對照表的欄名關鍵字選欄,選不到才退第一組 `value_raw`」,退路筆數在執行輸出計數(與既有「原始日期數值推得」同一段)。欄名關鍵字比對不分大小寫、容許前後綴(PRTG 可能帶 drive letter 或語系翻譯)。

**數值合理性檢查**(取代 proof 的最後一道防線):百分比語意的值必須在 0–100、ping 毫秒 ≥0,否則該小時列標 `unknown` 並計入「排除:值不合理」;同一顆 sensor 連續 7 日(暫定)每小時值完全相同視為「固定值」,趨勢規則不評估它(PRTG 端卡住或 sensor 失效的常見形狀)。檢查在寫入後、評估前做,不改寫原始 `avg_value`。

**時間基準**(2026-10-16 定案,D9):現場證實 `datetime_raw`／`lastcheck_raw`／raw history 的 OLE 數值是 **UTC**(10/15 00:00 +08 的列 raw 為 10/14 16:00),51 輪「raw 與字串差 7 小時、不可靠」是把 UTC 當成本地時間的誤判。B1 改為:
- `historicdata` 與 `messages` 的時段起點以 `*_raw` 為主:`DateTime.FromOADate` → 視為 UTC → 轉服務主機本地時間落盤。既有「依文化解析字串、失敗退 raw」的順序反過來;字串不再是主來源,月日對調的風險整個消失。
- 顯示字串只做兩件事:(1) 交叉檢查——字串解析成功且與 raw 換算的本地時間差 >30 分鐘時,計數並在執行輸出印一行「PRTG 帳號時區與服務主機差 N 小時」;(2) 字串解析失敗(非 zh-TW 服務主機碰到上午／下午格式)不影響落盤,只計數。
- 取值請求的 `sdate`／`edate` 仍以服務主機本地日送出;PRTG 以帳號時區解讀,兩者不同時回來的列會跨日,既有「寫入前再次限制資料日期」把範圍外的列丟掉,丟掉筆數計入上面那一行警告。處置是把 PRTG 帳號時區設成與服務主機相同,寫進 HelpContent,不加設定鍵。
- **取消** `PrtgSourceTimeZoneId`;本輪新增設定鍵歸零。移除 policy 的時區／文化欄位照舊。
- B1-1 第一步先用現場一列 hourly 驗算(raw＋8h 是否等於字串起點)釘住這個事實,再動解析順序;若現場帳號時區不是 +08,上面交叉檢查那一行就是驗收案例。

**移除的機制與依賴方處置**(階段規格須逐一列 production 呼叫端):

| 移除 | 主要 production 呼叫端 | 處置 |
|---|---|---|
| `PrtgTrustedSamplingProfileStore`／`Resolver`／`BindingStore`／`StrategyStateStore`／`ProbeService`／`ProfileRefreshHostedService`／`ProfileRefreshStateStore`／`ProfilesController`／`ProfileTransportCapacity(Pilot)` | `PrtgResourcePeriodConsumer`(閘)、`PrtgSnapshotHostedService`(profile 核對)、`PrtgTrustedSnapshotParser`、`EfPrtgStore` 常數引用、DI 註冊、`prtg-admin.js` 「Trusted profile 進度」頁籤 | consumer 改用 §B1 三條件;快照服務移除 profile／准入核對,保留配額與退避;parser 保留「取第一個 value_raw」解析,移除 proof／primarychannel 核對;刪頁籤 |
| `PrtgQualificationJobStateStore`／`QualificationJobHostedService`／`QualificationJobsController`／`PrtgHistoricAdmissionStore`／`PrtgResourceQualification` | `PrtgSilentPresenceFormalConsumer.IsCurrent`、`PrtgTrustedSamplingBindingStore` | silent 改簡化判定(見下);其餘一併刪 |
| `PrtgChannelDiscoveryJobStore`／`ChannelDiscoveryHostedService` | `ProfilesController` | 一併刪 |
| `PrtgAuthorityRepairStore`／`PrtgAuthorityRepairController`／RawAuthorityContext／authority fingerprint／`RawAuthorityRevision`／message-time fingerprint | `PrtgResourcePeriodConsumer` 前後快照比對、`PrtgSensorTimelineCollector`、`PrtgFetchService`、`PrtgMessageDateTimeParser`、probe | 時間解析保留「依帳號 Date Format 與 source 時區解析、失敗計數出聲」;fingerprint／revision／authority 全刪 |
| `PrtgCapacityRuntimeAdmission`／`PrtgCapacityAdmissionPlan`／`PrtgSnapshotCapacityPilot`／request-shape fingerprint(含 build MVID)／五份新鮮樣本／25% 餘裕准入 | `PrtgSnapshotHostedService`、`PrtgMonitoringController`、`SettingsController` 容量試測、`PrtgQualificationJobsController` | 快照只依策略間隔、配額、退避採樣;刪容量試測按鈕與設定鍵 |
| `PrtgDiskSemanticEvidenceStore`／`PrtgDiskVerificationService`／`DiskVerificationResultStore`／`PrtgDiskTimeBasisInvalidator`／人工語意確認 | `PrtgDiskAssessmentService` readiness、`disk_free_trend` 啟用條件、探測頁「驗證昨天語意」卡 | readiness 的「語意已驗證」條件改為「單位可判」;刪語意驗證卡與人工確認;規則啟用條件改為 §C3 |
| `PrtgCompatibilityProbe`(2,746 行)、`PrtgStorageEnvironmentProbe`、`scripts/Verify-PrtgProbeEvidence.ps1`、本機診斷 JSON 端點 | `PrtgProbeRunner`、`PrtgProbeService`、`PrtgClientFactory` | 保留原「完整環境探測」文字輸出與下載;安全 JSON 整組刪。**同時瘦身文字輸出**(D13):刪步驟 4 相依性(800 顆即逾時、100% 是預設父物件相依,無資訊量)、刪步驟 7 Type×IPv4(幾乎全 100%);步驟 8 分頁語意縮為一行「devices／sensors 遵守 start、超界夾末頁(pager 保險絲已處理);messages 依時間排序、超界回空」;步驟 9 改為「四類各一顆:頻道名稱、頻道 0 raw 值、對照表語意、單位判定結果」四行,不再印 JSON。9d-5 與站台對照保留 |
| 探測頁「磁碟值型資料準備度」卡(`PrtgDiskAssessmentService` readiness 查詢、每列「驗證這顆」「到指定主機補值」、`prtg-readiness-*` 區塊)與「磁碟語意驗證」卡(`prtg-disk-verification-*`、批次驗證、同一感測器規則試算、人工覆核表單) | `PrtgDiskReadinessQuery`、`PrtgDiskVerificationService`(上列已刪)、`prtg-admin.js` 對應區段、HelpContent 兩版「磁碟值型資料準備度」段 | 重做為「數值資料就緒度」(D8,見下段);舊卡整組刪 |
| `PrtgMonitoringPolicy.Ready` 的 CoreSystemId／SourceGeneration／ValidFrom／EndpointHint／時區／文化必填;來源變更「延續／新來源／無法確認」三選一 | `PrtgDailyPipeline.pilotReady`、`PrtgMonitoringController.Put`、試點範圍表單、`PrtgFormalEligibility`／`preview` 端點 | `Ready`＝PRTG 啟用且有連線;試點主機／sensor 清單保留為**可選**縮圈(空＝全部 ok 對應主機);改 URL 只重算對應,數值歷史不變;刪 Core 身分、時區、文化欄位(時區見上段);作用範圍預覽(52 輪 A04)保留,資格改用三條件 |
| 校準匯出的 `FormalEvaluationScope`／`MagnitudeSemanticsVersion`／trusted timeline 欄位 | `CalibrationService` | 匯出改以簡化後的 timeline 與規則重算;欄位名改回中性,`FormatVersion` 遞增 |

**silent 簡化**:availability 類 sensor 全 Up 且其他未暫停 sensor 全 Unknown(以鏡像狀態判),不要求完整原生 inventory 與 timeline;`PrtgSilentPresenceFormalConsumer`／`PrtgSilentPresenceHostedService`／`PrtgSilentDeviceProofRevalidator` 刪,回到 `PrtgRuleEvaluator` 的 silent 判定。

**設定鍵**:上述機制消費的 `SystemSettings.Prtg*` 鍵一併刪(紅線:有設定無行為);階段規格列出要刪的鍵名清單。

**資料**:刪除機制的 lf_blobs key 前綴(階段規格列出)由一次性 `SchemaUpgrader` 步驧刪除——**不可逆**,升級步驧前先寫入「請先備份資料庫」的啟動期提示並在 HelpContent 與版本說明明寫;SQLite 與 SQL Server 兩後端各驗一次。`lf_prtg_observations` 停止寫入,表保留一輪(BACKLOG 記下輪 drop)。

**頁籤結果**:`Prtg.cshtml` 由 8 個頁籤縮為 6 個:連線、擷取參數、鏡像狀態(含衝突處理與狀態時間軸同步進度)、環境探測(含就緒度)、歷史回填、使用效果。「Trusted profile 進度」刪;「狀態涵蓋進度」併入鏡像狀態。

**「數值資料就緒度」卡**(D8,取代「磁碟值型資料準備度」＋「磁碟語意驗證」兩張卡;行為契約如下,視覺實作前依全域規則先問是否套用 `ui-ux-pro-max`):
- 母體＝**取數範圍內、最新對應為 `ok`(含人工)、未暫停、type 在對照表內**的 sensor;四類(CPU／記憶體／磁碟／Ping)共用一張,不再只算磁碟。全鏡像顆數、未對應顆數、白名單顆數不再出現在這張卡——那些是鏡像狀態頁籤的事。
- 上半:一行母體總數,之後**依不足原因分組計數**(就緒／有效日不足／單位未知／coverage 不足／值不合理／固定值／對應無效,與 C4 同一個列舉),每組可點開展開該組的列;分母為零印「範圍內無可評估 sensor」並指回鏡像狀態。
- 下半:精簡表格(主機／sensor／類別／有效日 X／28／最新有效日／原因),每頁 100 列,分頁;**不**逐顆放按鈕、不逐顆一張卡、不展開 bitmask。
- 卡頂只有兩顆鈕:「重新整理」與「前往歷史回填」(後者只在存在「有效日不足」且最早有效日距今 <28 日時顯示;回填頁本身不動)。
- 唯讀、不呼叫 PRTG、不啟動探測;每日管線評估 C3 規則前算一次同樣的分組並寫進執行輸出,卡片與執行輸出的數字必須相同(同一個查詢)。

**階段切分**(每段獨立 build＋定向測試):B1-1 時間基準改 raw(先驗算現場一列)＋頻道選取改對照表,consumer 的閘直接換成三條件(不留 feature flag——留旗標就是「有設定無行為」),舊閘的呼叫一併移除,舊類別暫時保留讓編譯通過 → B1-2 刪 controllers／hosted services／JS 頁籤／DI,就緒度卡重做,探測輸出瘦身 → B1-3 刪 stores、設定鍵、blob 清理 → B1-4 刪對應測試(與 B7 合併計)。

**驗收**(全部必要):
- 固定小集合(2 主機、各 1 顆 CPU／記憶體／磁碟 sensor、3 日 `ok` 列)跑每日管線,三顆都進入規則評估母體;把其中一顆 coverage 改 60 → 排除且執行輸出記「品質不足 1」;把一顆 type 改成對照外 → 排除且記「單位未知 1」。分母為零(範圍內無 sensor)→ 執行輸出寫「無可評估 sensor」,不顯示 0 命中為正常。
- 刪除符號在 Core／Web／Tests／scripts 零命中;無新增同名或相容替身;Prtg* 設定鍵每一個都有消費端(反射測試既有)。
- 快照服務在無 profile 情況下按策略間隔採樣,配額與退避行為測試照舊全綠。
- 升級既有 SQLite／SQL Server 測試庫:刪除的 blob key 清空,`lf_prtg_values`／對應表內容不變(前後 hash 比對)。
- 時間基準:固定一列 historicdata JSON(`datetime_raw`＝46309.6670、字串「2026/10/15 上午 12:00:00 - 上午 01:00:00」)在服務主機 +08 下落盤為 10/15 00:00;把服務主機文化換成 en-US 重跑,落盤時間不變、字串解析失敗計數 1;把字串改成 +07 的寫法 → 落盤仍依 raw、時區差警告計數 1。
- 頻道選取:記憶體 historicdata 三個頻道欄順序打亂 → 仍取 `Percent Available Memory`;欄名全對不上 → 退第一組並計數 1。
- 就緒度卡:固定小集合四類各 1 顆＋1 顆範圍外＋1 顆對照外 → 母體 4、分組計數相加等於母體、範圍外那顆不出現、對照外那顆在「單位未知」;執行輸出同一組數字;分母為零案例印指定文字。探測文字輸出不含「相依性」「Type × IPv4」字樣,步驟 8 恰一行。

**B1 實作修正(2026-10-16,實作時查碼確認;取代上文相衝突的敘述)**:
1. **狀態型規則改回 messages 路徑**:`down`／`warning`／`flapping` 讀 `lf_prtg_state_changes`(逐裝置 messages,作業 0 9d-5 ✓)交 `PrtgRuleEvaluator`,即第 46～51 輪現場用過的路徑。53 輪 sensor timeline(`PrtgSensorTimelineCollector`／`Consumer`／`Store`、`PrtgCoveredRuleEvaluator`、`PrtgTimelineProgressController`、「狀態涵蓋進度」頁籤)整組刪除,不併入鏡像狀態。原因:timeline 證據以來源世代、資源世代、訊息時間指紋與帳號 Date Format 為鍵,B1 拆掉這些授權後等於重寫一套;作業 0 的提問本來就以 messages 為前提。
2. **頻道選取不改**:historicdata.json 一列不帶頻道名稱(多頻道是重複的 `value`／`value_raw` 鍵),「依欄名關鍵字選欄」做不到。保留「第一組 `value_raw`＝主頻道」(現場四類 raw history 驗算皆為頻道 0);選錯頻道由合理性檢查擋下(例如選到磁碟總容量 bytes,整顆 sensor 會變「值不合理」而不是靜默進趨勢)。
3. **小時列 `datetime_raw` 是時段結束**:R43 現場同一列 raw＝9/10 16:00 UTC,顯示字串為 +08 的 23:00～00:00,結束時刻相符。起點＝raw 轉本地時間減一個聚合間隔(`avg=3600`)。
4. **快照容量准入一併在 B1-1 解除**:保守策略下快照是唯一數值來源,`PrtgCapacityRuntimeAdmission` 與共享配額的准入計畫驗證不拆,B1 做完數值仍進不來。快照只依策略間隔、共享配額(table 2／秒、在途 4)與退避;每輪以一個採樣間隔為完成期限。
5. **silent**:鏡像狀態中 availability 類 sensor 全部 Up、其餘未暫停 sensor 全部 Unknown／無狀態才成立;只評最新一天。
6. **數值評估服務**:`PrtgValueAssessment`(三條件＋值不合理＋固定值,原因列舉 `PrtgValueReason` 供就緒度卡、規則頁、主機詳情、執行輸出共用);每日管線的 `disk_free_trend` 改由它挑候選與算每日代表值,C3 擴成四類。

### 作業 B2:拆 PRTG 即時郵件

**目標行為**:PRTG finding 只透過既有每日／每週摘要寄出,與 NetIQ 問題同列;不觸發高風險即時信。NetIQ 既有高風險即時信、per-recipient outbox、SMTP 接受狀態與重試**不動**。

**移除與依賴方**:`PrtgUrgentFacts`／`PrtgUrgentFactsForAcceptedIntent`、formal mail claim／fence(`PrtgFormalMailClaimStore`、`PrtgResourceFormalDeliveryFence`、`AuthorizeFormalStart`／`CompleteFormalStartClaims`)、`TryReserveRenderedMailContributions` 等 reservation、`FilterCurrentPrtgLanes`;`PrtgOperationsController` 的 retry 不再呼叫 `NotifyAfterRunAsync`(保留補追加重播);`PrtgSupplementHostedService`／`PrtgResourcePressureModeReplayHostedService` 對 `NotifyAfterRunAsync` 的呼叫移除;`MailNotifyState` 與 `EfAnalysisRecordStore` 對 fence 的引用移除;`RetentionPruner` 對 claim 的清理移除。

**判準**:`QualifiesForUrgentRisk` 改為「把 `RiskBasis` 中所有 `prtg:*` 依據拿掉後,剩餘依據推得的等級仍為 High」才寄即時信;只剩 PRTG 依據或拿掉後降為中／低 → 不寄,進摘要。`LogSource=Prtg` 的主機日(C2)一律不寄即時信。

**驗收**:同一主機日 (a) 只有 PRTG availability down 升 High → 不寄即時信、進日摘要;(b) NetIQ 高風險事件 → 即時信照寄(既有測試);(c) 兩者皆有 → 即時信內容只含 NetIQ 問題段落,PRTG 段在摘要。刪除符號零命中;`MailNotificationServiceTests` 既有 NetIQ 案例全綠。

### 作業 B6:拆兩小時壓力規則與提示→正式授權

- 刪規則代碀 `resource_cpu_sustained_pressure`／`resource_memory_sustained_pressure`／`resource_disk_pressure`、`PrtgResourcePressureEvaluator`／`AuthorizationService`／`ModeStore`／`ModeReplayHostedService`／`FormalEpisodeStore`／`PrtgResourcePeriodReadiness` 的兩小時窗口部分、trial／mode／replay API 與主機詳情的模式控制。`PrtgResourcePeriodConsumer` 保留為 C3 趨勢引擎的 consumer 骨架(每日主機日批次評估、finding 映射、融合至主機日),移除 episode／mode／hint。
- `KnownIssueSeed` 版本遞增;移除三條內建規則;`RuleValidator.CheckPrtgFields` 白名單同步;既有 DB 中這三條規則的列由 seed 匯入流程標記為「已停用且不再評估」並在規則頁顯示原因(不靜默刪使用者資料)。
- 既有 `lf_top_issues` 中這三個代碼的歷史 finding 保留可讀,問題排行白話說明回「規則已移除」。
- 驗收:規則白名單＝down／flapping／warning／silent／disk_free_trend(+C3 新增);舊代碼零命中;規則頁載入含舊代碼的 DB 不報錯並顯示停用原因;主機詳情無模式控制;`Hint`／`FormalRisk` 決策模式概念零命中。

### 作業 B3:拆 build identity 閘與建置腳本依賴

- `Directory.Build.targets` 移除 `GenerateLfBuildIdentityStamp` 與 manifest 後置步驟;`AssemblyInformationalVersion` 回歸 MSBuild 原生(`SourceRevisionId` 由 CI／本機 git 供應,無 git 時為純版本號)。刪 `scripts/Write-BuildIdentityStamp.ps1`、`Verify-BuildIdentity.ps1`、`scripts/build-identity/`、`LogForesightBuildIdentity.IsReadyForLatestAcceptance`。
- 依賴方:`PrtgBackfillService` 全量回填 verified 閘(刪閘,保留原有啟動閘門:未啟用／連線不齊／互斥／無對應);`PrtgProbeService` 標頭改印 InformationalVersion;`PrtgAcceptanceController`(B4 刪)。
- 驗收:乾淨 clone 在無 pwsh 的環境 `dotnet build` 成功;探測輸出標頭仍含版本與 git revision(CI 建置時);全量回填在 verified 不成立時不再被擋,其餘閘門測試全綠。

### 作業 B4:刪驗收基建目錄與 JSON

- 刪 `scripts/round53-user-matrix/`、`scripts/round54/`、`scripts/prtg-workload/`、`scripts/tests/`、`docs/FEEDBACK-53-ACCEPTANCE.json`、`LogForesight.Tests/Fixtures/prtg-round53-formal-cases.json`;`LogForesight.Tests.csproj` 移除對 scripts 的 Compile Include;`.github/workflows/ci.yml` 移除腳本契約測試步驤;刪 `PrtgAcceptanceController`、`PrtgAcceptanceComparison`、事故查證表單與「使用效果」頁籤中的四基準／證據包區塊(保留 finding→案件→交辦→靜音計數)。
- 依賴方:`PrtgQualificationCoverageTests`、`PrtgRollbackReplyGraphFixtureTests`、讀 formal-cases.json 的四個測試檔(B7 一併刪);`prtg-admin.js` 使用效果區段中呼叫 acceptance API 的函式。
- 驗收:CI 綠;repo 內 `scripts/` 只剩仍被建置或文件引用的腳本(列清單);`grep -rn "acceptance" LogForesight.Web/wwwroot/js/pages/prtg-admin.js` 零命中。

### 作業 C1:對應補 hostname 退路

**目標行為**:`PrtgHostMapper.MapForDate` 對每台 device 的判定順序:人工對應 → IP 排除 → **IP 比對**(既有)→ IP 無法命中任何主機(含 device 無 IP、IP unmatched)時 → **hostname 比對** → 仍無則 unmatched。

**hostname 比對**(2026-10-16 定案,D11):現場 6,604 台裝置的 `host` 欄只有 2 台是 DNS 名稱,拿它比對幾乎無命中空間;裝置**名稱**格式固定為 `<代號>(<主機名>)<IP 片段>`,如 `U(O-stkomsdb1)216.45.101`、`W(VM-GFYQ-AIS002)_216.11.10`、`U(L-TP-Sentinel-SRVS05)216.7.117`。候選字串依序:(a) 裝置名稱第一對括號內的字串;(b) 該字串去掉「開頭單一字母＋`-`」的變體(`O-stkomsdb1`→`stkomsdb1`、`L-TP-Sentinel-SRVS05`→`TP-Sentinel-SRVS05`;`VM-` 兩個字母不去,`vm-idc-eosm02` 本身就是主機名);(c) `host` 欄非 IP 時的值;無括號的裝置名稱整串當 (a)。每個候選與主機主檔 `HostName` 都正規化為短主機名(去 scheme／port／域名尾、小寫、trim)後比對;命中集合取三個候選的聯集。括號規則寫成一個可測的純函式,現場名稱格式變了只改它。恰一台活躍主機命中 → `ok`,Note 標「hostname」;多台 → `conflict`(不填主機);同一短名多台 device → `conflict`。hostname 命中的 device 仍受既有「同 IP 已有人工指定」規則約束(以其解析 IP 分組,若有)。

**不變**:IP 命中結果不因 hostname 改變;hostname 只在 IP 路徑完全無結果時啟用;衝突清單與指派 UI 不改,新增 `conflictKind=hostname-multi`／`hostname-host-taken` 的標籤文字。

**同主機兩台 device**:hostname 命中的主機若當日已有 IP 命中(或人工對應)的 device → 該 device 標 `conflict`(`hostname-host-taken`),不填主機——同一台主機掛兩台 PRTG device 多半是舊裝置未清,猜了會把舊裝置的數值混進來;管理者在衝突清單可指派或排除。

**驗收**:固定夾具 8 台 device:IP 命中、IP 衝突、無 IP＋括號名稱命中、無 IP＋去單字母前綴後命中、無 IP＋名稱多主機、名稱與 IP 指不同主機(IP 優先)、名稱大小寫／帶域名差異命中、`VM-` 開頭不被去前綴;各得預期狀態與 Note;執行摘要「hostname 命中 N」獨立計數,N＝0 時仍顯示 0。括號解析純函式對現場三種名稱格式與無括號、多對括號、空括號各一案。既有 `PrtgHostMapperTests` 全綠。

### 作業 C2:PRTG 獨立風險(`LogSource=Prtg` 主機日)

**目標行為**:每日管線對有**至少一筆未抑制** PRTG finding 但當日無 NetIQ 分析紀錄的主機,建立一筆 `DailyAnalysisRecord`:`LogSource=Prtg`、事件統計全零、`TopIssues`＝PRTG finding、風險依 PRTG 規則推導、`RiskBasis=prtg:*`;只有被抑制 finding 的主機不建紀錄(抑制的語意是不吵,沒必要為它造一筆主機日)。之後若 NetIQ 分析同日晚到,**同一筆紀錄原地更新**(record id 不變:填入事件統計、`LogSource` 改 Netiq、PRTG finding／人工處置／案件關聯／處理狀態全部保留),不刪再建——`lf_top_issues`、案件成員、郵件連結都掛在 record id 上;重跑冪等。

**SQL 欄位**:`LogSource` 目前只存在紀錄 JSON 內,SQL 端的統計查詢過濾不到。新增 `lf_daily_records.log_source`(smallint,雙 provider 冪等 DDL,既有列回填 0＝Unknown,寫入時與 JSON 同交易同步),涵蓋率等排除查詞用此欄過濾並建索引 `(log_source, record_date)`(暫定)。這是本輪唯一的 schema 新增(B5 的 identity 表另計)。

**消費端矩陣**(階段規格逐一列出並寫驗收):

| 消費端 | 行為 |
|---|---|
| 儀表板／紀錄清單／詳情 | 顯示為 PRTG 來源主機日;來源徽章「PRTG」;事件區空、PRTG 區有內容 |
| 問題排行、案件、交辦、處理狀態、靜音 | 照常(吃 `lf_top_issues`) |
| 日／週摘要郵件 | 照常列入;來源標示 PRTG;摘要頂部「已分析主機 N 台」只算 NetIQ／本機,PRTG 來源主機日另列「PRTG 訊號主機 M 台」 |
| AI 判讀 | 可判讀;prompt 只有 PRTG 段 |
| **NetIQ 涵蓋率、未回報主機、重跑選日、日誌趨勢基線、殘留判定** | **排除 `LogSource=Prtg`**——這些統計定義是「NetIQ 分析過的日」 |
| 低風險整形、保留期精簡 | 照常 |

**父紀錄閘處置**:`CanSupplementWithPrtg` 改為「`LogSource` 為 Netiq 或 Prtg 且未精簡」;`AttachPrtgFindings` 查無紀錄時改走建立路徑而非回 false;`PrtgSupplementReplay`／`PrtgSupplementHostedService`(晚到補追加)保留並改成「NetIQ 晚到時取代 PRTG 紀錄」的執行者;`FormalEvidencePending` 狀態移除。

**驗收**:四主機日矩陣 H1 只有 NetIQ、H2 只有 PRTG、H3 兩者、H4 皆無 → 問題主機日 3、NetIQ 分析主機日 2、PRTG 來源主機日 1;H2 的 NetIQ 晚到後 → 同一 record id、NetIQ 分析主機日 3、問題數不變、PRTG finding 與案件成員各一份;五次重跑不重複。H5 只有**被抑制** PRTG finding → 不建紀錄。涵蓋率、未回報、重跑選日、日誌趨勢基線、殘留判定五個消費端在 H2 存在時數字不變(與 H2 不存在時相同),且 SQL 端以 `log_source` 過濾(`ToQueryString` 兩 provider)。受限角色只見 H2 時計數與下鑽一致。NetIQ 管線既有測試全綠。

### 作業 C3:三類趨勢規則共用引擎

**引擎**(抽自 `disk_free_trend` 的評估,改為通用):輸入＝一顆 sensor 的「每日代表值」序列(當日可用小時的平均;當日可用小時 <12 的日不算有效日)、觀察窗(暫定 35 日)、最少有效日(暫定 28)、方向(越高越糟／越低越糟)、門檻、預測期(暫定 7 日);輸出＝就緒／不足原因、穩健斜率(暫定 Theil–Sen)、下降(上升)日比例、預估達門檻日、是否命中。**資料不足一律回「不足」,不回未命中**;四類規則共用同一份,不各寫一套。

**規則**(全部預設停用,門檻可在規則頁編輯,數值暫定待作業 0 與實際資料校準):

| 代碼 | 語意 | 命中(暫定) |
|---|---|---|
| `disk_free_trend`(既有) | 磁碟剩餘 % 下降 | 既有:低水位 20%、≥0.5 pt/日、下降日 ≥70%、預估 ≤30 日 |
| `memory_used_trend` | 記憶體已用 %(可用 % 先轉向)持續攀升 | 35 日窗 ≥28 有效日;斜率 ≥0.3 pt/日;上升日 ≥70%;預估 7 日內 ≥90% |
| `cpu_load_trend` | CPU 已用 % 長期上升且已偏高 | 同窗;斜率 ≥0.3 pt/日;上升日 ≥70%;近 7 日平均 ≥70% |
| `ping_latency_trend` | ping 回應毫秒持續惡化 | 同窗;近 7 日中位數 ≥ 前 28 日中位數 ×1.5 且 ≥50 ms;上升日 ≥60% |

- **解除與再發**(每規則都要):命中後若最新有效日的值已回到門檻安全側、且近 7 日(暫定)斜率不再惡化 → 該 sensor 當日不再命中;既有簽章去重與處理狀態機制處理「同一問題仍在」與「已處理後再發」,趨勢規則不另造事故概念。管理者清完磁碟後隔天就不會再被報。
- **慢性 vs 突發**:趨勢規則只抓慢性惡化;一夜塞滿的突發由 PRTG 自己的閾值告警經狀態型 `warning`／`down` 承接。這句寫進規則說明與 HelpContent,避免使用者以為趨勢規則會抓所有磁碟滿。
- 記憶體規則吃 B1 對照的 `memory-percent-free`,在引擎輸入前轉成已用 %(100 − 值);轉向只在這一處做,測試釘住(§驗收「方向寫反」案)。`SNMP Linux Meminfo` 在補對照前不進此規則。
- ping 的 type(`Ping`)加入預設數值白名單、Traffic 兩型自白名單移除(快照目標數因此約增加「有 ok 對應的主機數」顆、減少 traffic 顆數,仍在 15,000 設計內,D 作業核);ping 反覆中斷不另立規則(既有 down／flapping ＋ `PrtgCrossDay` 連續／累計升級)。
- 規則平台 `prtg`、`ThresholdKind` 沿用 `disk_free_trend` 的專用門檻結構;`RuleValidator`／規則 DTO／編輯頁／seed／匯入差異一路打通;seed 版本遞增(B6 的移除與 C3 的新增若在同一次發佈,只遞增一次,升級橫幅只出現一次)。
- 啟用條件(後端強制):至少一顆對應有效、單位可判且達最少有效日的 sensor;否則拒絕啟用並指回探測頁 readiness。
- 評估時點:沿用 `disk_free_trend`——只讀評估前已落盤的完整日,不等當趟取值;每顆每日最多一筆 finding;`EventKey=prtg:{代碀}:{objid}`;跨日由 `PrtgCrossDay` 統一處理。
- 預覽試算:沿用既有 draft trial 分頁機制,四規則皆可試算、不寫業務資料。

**驗收**(每規則至少 5 正例、每類反例各 1):恰門檻命中、門檻前不命中;單日暴衝不命中;低位平坦不命中;28 有效日差一天 → 「不足」且原因「有效日 27／28」;coverage 不足日不算有效日;方向寫反(記憶體可用 % 未轉向)→ 測試釘住;同資料重算結果位元相同;分母為零(無 sensor)→ 「無可評估」;正式評估與試算對同一輸入結果相同。

### 作業 C4:finding 證據欄位與「資料不足」狀態

- 每筆趨勢 finding 的 `Detail` 固定格式:「{裝置}／{sensor}:{規則白話};近 {N} 日有效 {M} 日;目前 {值}{單位},每日 {斜率};預估 {日期} 達 {門檻};資料截至 {日}」;狀態型 finding 保留既有格式。另在簽章帶結構化欄位(規則、窗口起訖、關鍵數值、來源 objid)供詳情頁與郵件使用,不靠解析文字。
- readiness／規則頁／主機詳情三處對同一 sensor 顯示同一「不足原因」(有效日不足／單位未知／coverage 不足／值不合理／固定值／對應無效),用同一個列舉。主機詳情對**非 Maintain** 使用者顯示原因與「資料累積中 X／28 日」,不顯示設定入口;Maintain 另有指向 readiness 頁的連結。
- 驗收:同一輸入在詳情、摘要郵件、AI prompt 的證據數字一致;「資料不足」不顯示為綠燈,也不升風險。

### 作業 C5:有用／誤報標記

- 核對既有問題處理狀態列舉能否表達「誤報」;能 → 直接用,不加欄位;不能 → 新增一個狀態值「誤報」,行為同「已處理」但統計分開。可取消標記。
- 效果:標記後同簽章 30 日內(暫定)再命中不進摘要、排行顯示「已標誤報」;30 日後再命中視為新問題重新出現。這是「不吵」不是「不算」:finding 仍寫入主機日供事後校準。
- 使用效果面板新增「標記誤報 N／已處理 M」,並**按規則代碼分列**——哪條規則誤報多,就是下一輪調門檻的依據;分母寫明是「已標記的 PRTG 問題」。
- 驗收:標記後摘要郵件與排行不再列該簽章;取消標記後恢復;30 日邊界前後各一案;計數分母為零時顯示「尚無標記」。

### 作業 C6:資源守門目標自動偵測(D12)

- **目標行為**:守門覆寫清單為空時,夜間快照開跑前以既有「直接查 PRTG」偵測(PRTG 位址比對＋Sentinel 位址比對＋corehealth)跑一次,結果存入覆寫清單並註記「自動偵測 {日期}」;之後每趟只在清單為空或偵測到的裝置不在範圍時重跑。人工填寫過的清單(註記非自動)**不覆寫**;設定頁「自動偵測並填入」鈕保留為手動入口。範圍補抓(PRTG-SPEC §3c)照舊把這些裝置帶進鏡像。
- 有界:偵測沿用探測 [S3] 的查法(裝置全表一次、sensor 以 `filter_objid`),共用配額,15 秒逾時,逾時就這趟不守門並在執行輸出出聲,不阻擋取數。
- **不變**:守門的判定與退避邏輯不動;覆寫清單仍是唯一的守門目標來源,自動偵測只是替使用者填它。
- 驗收:清單為空＋PRTG 位址命中 → 清單被填、註記自動、當晚守門監看到 cpu／memory 感測器;清單非空且無自動註記 → 不動;偵測逾時 → 清單仍空、執行輸出一行警告、取數照跑;重跑三次清單內容相同。

### 作業 B5:收斂 lf_blobs JSON store

- B1／B2／B6／C2 完成後盤點仍在使用的 PRTG blob key:`PrtgResourceIdentityStore`(sensor↔主機身分)、`prtg_sync_status`、`prtg_scope_revision`、監看政策、快照 checkpoint 相關。逐一判定:多列關聯資料(identity)→ 改正式表 `lf_prtg_resource_identity`(雙 provider DDL);單例狀態(sync status、scope revision、policy)→ 保留 blob。`ReadBoundedWithVersion` 字元上限保留為一般防護。
- 驗收:PRTG 相關 blob key 前綴清單寫進 DB-SPEC 且每個有讀寫端;identity 表遷移前後資料筆數相同;SQL Server `ToQueryString` 翻譯測試。

### 作業 B7:測試瘦身

- 刪:讀 `prtg-admin.js`／`Prtg.cshtml` 原始碜做字串斷言的測試(改為保留 `FrontendConsistencyUiTests` 守門的 innerHTML／路徑規則);測 fixture JSON 結構、腳本、B1–B6 已刪機制的測試。
- 保留:行為測試(mapper、pager、journal、daily pipeline、rule evaluator、trend engine、mail、authorization、store contract)。
- 驗收:全量綠;記錄新基線(總數、PRTG 測試檔數與行數)對照審查報告 §2。

### 作業 D:容量設計審查(README 準則)

逐環節查碼並以小集合驗:`lf_prtg_values` 索引 `(sensor_objid, period_start)` 覆蓋 C3 的「每顆 35 日」查詞;C3 逐 sensor 分頁(每頁 ≤100 顆)且每顆查詞點數有界;快照每批 ≤100 顆、在途 ≤3,ping 納入、traffic 移出白名單後的目標數估算——**現場常數**:sensor 表單次查詢固定 7–8 秒、與 objid 數無關,15,000 顆＝150 批,在途 3 約需 6–7 分鐘一輪,必須放得進快照策略間隔(保守策略現行間隔多少、放不下時策略怎麼降批,D 要給數字);`DailyAnalysisRecord` 建立路徑無 N+1;`log_source` 索引覆蓋排除查詞;保留期清理 `BatchedPrune`;hostname 比對不在迴圈內做 DNS。順手修:`PrtgDailyPipeline` 直接 `new PrtgFetchService`、`PrtgHostMapper` 無 DI 註冊 → 改走 DI,讓測試替身與正式路徑一致。輸出一張「已排除的程式風險／殘餘環境限制」表,不宣稱量測正式容量。

### 作業 E:文件瘦身與收尾

- `PRTG-SPEC.md` 重寫為現況(預估從 1,347 行縮到 ≤500):定位、資料表、擷取、對應(含 hostname)、回填、探測、設定、API、狀態規則、趨勢規則、跨來源佐證、資源守門、匯出。掃「原本／後來／第 N 輪」敘事字眼。
- `DB-SPEC`／`DETECTION-SPEC`／`WEB-SPEC`／`BACKLOG`／**兩版 HelpContent**(刪 trusted profile／綁定／授權步驤,補「≤4 步啟用」「趨勢規則 28 日後才有」「慢性 vs 突發」「升級前備份」)同步;`CLAUDE.md` 測試基線與「不要做」條目更新(刪 trusted sampling 相關、補「數值可信＝三條件,不再加閘」)。
- 版本說明(release note)明寫:升級不可逆(刪 blob key)、先備份;三條壓力規則停用;PRTG 不再即時寄信;設定鍵刪除清單。
- `FEEDBACK-52-PLAN.md`、`FEEDBACK-52-ROUND2-PLAN.md`、`FEEDBACK-53-PLAN.md` 與本文件 `git mv` 進 archive 並補索引。
- HelpContent 補「PRTG 帳號時區請與服務主機相同;不同時執行輸出會有警告與丟列計數」;`PRTG-SPEC` §時間解析段改寫為 raw(UTC)為主,刪「datetime_raw 只當退路」與 7 小時敘述。
- 依 `project-closeout`:體檢、併 dev、刪分支。

## 6. 本輪驗收(取代原 27 項)

全部必要,AND 通過:

1. 新站台從零到第一個 PRTG 風險的操作 ≤4 步:連線→同步結構與對應(背景作業;衝突可事後在清單處理)→啟用規則→等每日排程。無 Core 身分、時區、文化、profile、綁定、授權、守門手動偵測任何一項;本輪新增設定鍵為零。**時程明寫**:狀態型 finding(down／warning)隔日第一次排程就有;趨勢型最早 28 個有效日後;readiness 頁與主機詳情全程顯示 X／28。
2. 一顆 CPU sensor 從取數到趨勢 finding 的路徑可在小集合上端到端重現,中間沒有「等待／證據不足」以外的第三種卡點;每個「不足」有原因。
3. 四主機日矩陣(C2)與五次重跑冪等。
4. 四類趨勢規則各 5 正例、各類反例;同資料重算結果相同。
5. PRTG 風險不觸發即時信;日／週摘要含 PRTG 段。
6. hostname 退路六案例。
7. 刪除清單(B1–B6)符號零命中;Prtg* 設定鍵每個有消費端;blob key 每個有讀寫端。
8. 全量測試綠(Windows),建置 0 警告,無 pwsh 可建置。
9. 容量審查表產出;`PRTG-SPEC` ≤500 行且無敘事字眼。
10. 就緒度卡以取數範圍內已對應 sensor 為母體、四類共用、分組計數與執行輸出一致;探測頁無逐顆按鈕;時間基準三案例(B1 驗收)通過。

## 7. 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| 規劃 | Claude(Fable,Mac) | 本文件與審查報告 | 待使用者定案 | — |
| A | Claude(Opus,Windows) | 52／52-ROUND2／53 三份規劃 `git mv` 進 archive 並補索引;README 頁首與準則段、CLAUDE.md 測試基線、BACKLOG 舊路徑改指本文件 | 舊 TODO 代號 grep 除本文件外零命中;`docs/` 主目錄只剩本 PLAN(`FEEDBACK-53-ACCEPTANCE.json` 由 B4 刪) | 純文件,未建置 |
| B1-1 | Claude(Opus,Windows) | 取值時間改 raw(UTC)為主、刪訊息時間授權閘;新增 `PrtgValueAssessment`(三條件＋值不合理＋固定值);每日管線評估段改回 messages＋`PrtgRuleEvaluator`、磁碟趨勢走新評估、移除 manifest／fence／資源期間 consumer 呼叫;silent 改「availability Up＋其餘 Unknown」;快照服務拆容量准入、trusted 解析與 timeline 迴圈;共享配額拆准入計畫驗證;workflow 的 PRTG 腿以「該日評估完成」為就緒 | `dotnet build` 0 錯;PRTG＋HostDayWorkflow 定向 2,793 筆:33 失敗中 26 筆屬將刪機制(測試檔一併刪除)、2 筆准入 fence 測試刪除、5 筆在 HEAD 同樣失敗(既有,非本段造成);新增 `PrtgValueAssessmentTests` 9 筆全綠;`PrtgFetchServiceTests`／`PrtgRuleEvaluatorTests` 全綠 | 實作修正 6 條記於 §5 B1;既有失敗 `SchemaUpgraderPrtgTests` 表名清單、`PrtgDiskCandidatePagingTests.HostMapDataRevision` 列入 B7 查;受影響的舊類別(timeline、資源期間 consumer 等)本段只停止呼叫,B1-2／B1-3 刪 |
| B1-2／B1-3／B6／B2 | Claude(Opus,Windows) | 刪 trusted sampling／容量准入／磁碟語意驗證／資源壓力／正式郵件 claim 全鏈(stores、hosted services、controllers、JS 頁籤與模組、DI、約 150 個測試檔);探測輸出瘦身(刪步驟 4 相依性、步驟 7 Type×IPv4,分頁語意縮成一行);監看政策只剩可選 HostIds／SensorIds 縮圈;B6 三條資源規則退役(seed v10、`PrtgRuleCatalog.RetiredRuleCodes`、匯入時停用內建列、規則頁「已移除」標記);B2 即時通知先剔除 PRTG 再判是否仍 High,PRTG 只進摘要;升級時清除退役 blob(`SchemaUpgrader.RemoveRetiredPrtgBlobs`) | `dotnet build` 0 錯;整體 `dotnet test` **6,215 通過／10 略過／0 失敗**(本段新基線,刪測試後總數 6,225) | 保留 `MailContributionReservation` 與 `MailContextFingerprint`(NetIQ 郵件仍用);預覽不套政策 sensor 縮圈;順手修:`PrtgScopePurgeStore` 三處交易補執行策略、兩個超過 30 字元的索引更名(`IX_lf_prtg_sc_purge_sensor`／`IX_lf_prtg_val_purge_sensor`,只存在本輪分支)、`TryAttachAiResult` 對已不建立的 manifest 解參考(AI 補寫全數失敗)、同一份 PRTG finding 重跑回傳值、恢復等待解除後還原 PRTG 腿、監看範圍 PUT 交易內重核可見權限、校準覆蓋天數把空對應日算進去、摘要信截斷後「其餘 N 個問題」不再出現、PRTG-only 主機日不進即時通知;校準匯出 `MagnitudeSemanticsVersion` 3、`FormalEvaluationScope=state-change-messages`;清除退役 blob 不可逆,升級說明在 E 補 |
| D8 | Claude＋agy(flash-high／medium,Windows) | 就緒度後端 `PrtgValueReadiness`(母體＝已對應主機上四類 sensor,沿用 `PrtgValueAssessment` 判定)、每日執行輸出一行、`GET api/prtg/value-readiness`(可見主機、排除案件授與、原因篩選、每頁 100);環境探測頁籤新卡片(分組按鈕、精簡表格、重新整理／前往歷史回填) | 定向 63＋68 通過;本機預覽登入後卡片 API 200、空母體狀態正確 | 規格「母體 4 且對照外歸單位未知」自相矛盾,改依類別定母體;有資料的畫面未實測(本機無 PRTG 資料) |
| B1-4／B6 尾差／CI | Claude＋agy(flash-high,Windows) | 刪三個死檔、`LogIssueSignature`／`PrtgFinding` 12 個 trusted 欄位、案件證據世代欄位、`lf_prtg_values` trusted 欄位映射;退役規則說明回「規則已移除」、規則編輯頁移除退役選項;`NuGet.Config` 補 `*`(CI 自 9 月起還原失敗) | 定向 2,165＋368 通過 | 既有 DB 欄位保留不 DROP |
| B1 收尾(workflow) | Claude(Opus,Windows) | `HostDayWorkflowState` 刪 whole-evidence／readiness 19 欄位與 7 方法;`PrtgManifest` 刪除;登錄簿改「該日評估完成」旗標,分析路徑與每日管線同判準 | 定向 279 通過 | 修正實際缺陷:晚到 NetIQ 分析會把已評估日的 PRTG 腿記成 manifest 缺失等待;指紋保留空位不讓既有報告失效 |
| B3＋B4 | Claude＋agy(flash-medium,Windows) | 刪 `Directory.Build.targets`、建置身分腳本與 `LogForesightBuildIdentity`;全量回填拿掉建置身分閘;探測標頭改印 InformationalVersion;刪 `scripts/` 全部、`FEEDBACK-53-ACCEPTANCE.json`、兩個驗收 fixture;CI 移除 pwsh 步驟 | 建置輸出無 powershell／build-identity;定向 303 通過 | `scripts/` 底下未受版控的舊執行產物(約 890 MB)留在使用者磁碟,未刪 |
| CI | Claude(Opus,Windows) | `NuGet.Config` 補 `*`(只列 OpenCC 讓乾淨主機套件全部 NU1100,dev 自 9 月起紅燈);傳輸流程測試改用固定容量替身(CI 資料碟低於 25% 保留門檻) | `ac25990` 起 CI 綠 | — |
| C2 | Claude(Opus,Windows) | `AnalysisLogSource.Prtg`、`log_source` 抽出欄與索引(回填器 v3);PRTG 獨立主機日建立(至少一筆未抑制 finding、同主機日無紀錄);日誌分析晚到原地更新同一 record id 並併回 PRTG finding;缺日、重跑、首次回望、週檢、趨勢歷史、執行監看、排程預覽、儀表板分析主機日排除 PRTG 獨立主機日;NetIQ 嘗試紀錄來源合法值補 3 | 新測試 8(含 SQL Server 翻譯、突變有抓);定向 716 通過 | 摘要郵件沒有「已分析主機 N 台」字樣,該項不適用;消費端盤點由唯讀子代理完成 |
| C1 | Claude＋agy(flash-high,Windows) | `PrtgHostnameCandidates` 純函式＋`PrtgHostMapper` 在無 IP／IP 查無主機時以名稱比對;多主機、同主機多裝置、主機已被對應各標 conflict;摘要 hostname=N;衝突清單兩種新標籤 | 定向 174 通過(候選 27 案、8 台裝置夾具) | Claude 把重複的衝突列組裝抽成函式 |
| C3-1 | Claude＋agy(flash-high,Windows) | `PrtgTrendEngine`(記憶體／CPU／Ping 判定與暫定門檻);磁碟趨勢改用共用斜率與下降比例 | 新測試 64;定向 116 通過;反轉方向突變被 7 筆抓到 | — |
| C3-2a | Claude＋agy(flash-high,Windows) | 三條趨勢規則接上規則常數、目錄(`IsValueTrend`／`TrendSensorCategory`)、`KnownIssueRule.PrtgTrendThresholds`、驗證、seed v11(預設停用)、匯入比對;跨日／派工／建案品質改認四條數值趨勢;每日管線在磁碟趨勢後逐條評估;`TrendFinding` 為記憶體轉向唯一點 | 定向 850 通過;移除轉向的突變讓 2 筆轉紅 | — |
| C6 | Claude＋agy(flash-high,Windows) | `PrtgResourceGuardAutoDetect`:清單為空或自動結果有 sensor 已不在鏡像時,作業範圍建立前直接查 PRTG 偵測(15 秒),寫回清單與 blob `prtg_resource_guard_autodetect`;人工清單不覆寫;設定頁預覽回傳並顯示「自動偵測 {日期}」 | 新測試 7;定向 288 通過;取消人工保護的突變 2 筆轉紅 | 註記只在按「預覽」後顯示(設定讀取 DTO 不帶,未新增欄位) |
| C5 | Claude＋agy(flash-high,Windows) | `PrtgFalsePositiveWindow` 單一判定(同主機同簽章最近一筆明確標記為誤報且 ≤30 日);狀態判定套用並標 `InheritedFalsePositive`、標記查詢往前多撈 30 日;摘要排除;排行「已標誤報」;使用效果按規則代碼分列 | 定向 345 通過;停用繼承、窗口 +1 兩個突變 3 筆轉紅 | Claude 收緊排行語意:每台主機的最近一次出現都在窗內才標(agy 版只要任一台就標),補「部分主機標記為 false」測試;`ResolvedOccurrence` 新參數拿掉預設值 |
| B7 | Claude(Opus,Windows) | 刪 4 個純原始碼字串斷言檔(PrtgAdminPage／Effectiveness／SelectedBackfill／ValueReadiness UiTests,約 1,300 行);3 個混合檔只刪字串斷言方法,控制器與服務行為測試保留 | 受影響三檔＋FrontendConsistency 定向 33 通過 | fixture JSON 結構測試已在 B4 隨 fixture 刪除,無殘留 |
| C3-2b | Claude＋agy(flash-high,Windows) | 規則 DTO／儲存／複製／seed 差異帶趨勢門檻;門檻驗證三處;後端啟用守門(至少一顆就緒且語意相符);草稿試算共用分頁骨架、判一列改委派;規則頁三選項、門檻欄、分類鎖定、列表門檻、試算說明 | 定向 577 通過 | Claude 補 Core `EvaluateTrend`／`TrendSemantic` 讓試算不再複製引擎呼叫與記憶體轉向;拿掉啟用守門的 fallback 門檻;JS 暫定值合併為一份;原三筆拒絕測試只走到相依缺漏分支,補 27 日真實路徑拒絕測試(突變有抓) |

## 8. 規劃複檢(2026-10-15)

- 與既有設計衝突:C2 推翻 52 輪 NetIQ 主體契約,已在 §1 明寫;`LogSource=Prtg` 會進入所有讀 `DailyAnalysisRecord` 的消費端,C2 矩陣列出必須排除的五類統計——遺漏任一類會讓 NetIQ 涵蓋率被 PRTG 日灌水,列為 C2 驗收。
- 批次衝突:B1 與 B6 都動 `PrtgResourcePeriodConsumer`,順序 B1→B6 且 B1-1 先以三條件閘替換、B6 再刪 episode／mode,避免兩次重寫;C3 在 B6 後接手該 consumer。B2 與 C2 都動 `MailNotificationService`——B2 先改 `QualifiesForUrgentRisk`,C2 只確認 `LogSource=Prtg` 主機日走摘要路徑。
- 四個坑:「什麼算一個」——有效日＝可用小時 ≥12 的日、hostname 命中＝恰一台活躍主機;分母為零各處皆明寫顯示方式。破壞性判準——B1 blob 清理只刪列出的 key 前綴,`lf_prtg_observations` 不 drop;B6 舊規則列不刪只停用。單向閘門——C3 規則啟用的後端條件可因資料累積自然解除,無永久旗標。移除類——每個批次都有依賴方表,階段規格須逐一展開為呼叫端清單。
- 升級路徑:既有 DB 升級後舊 finding(三條壓力規則)可讀;刪除的設定鍵在 blob JSON 中多出的屬性由既有「未知欄位忽略」行為吞掉,須在 B1-3 驗收確認。
- 複檢後新增事項:C2 矩陣補「低風險整形、保留期精簡照常」;B2 補「兩者皆有」案例。

### 第二次複檢(2026-10-15,四角度)

**使用者(尖銳但有理)**:「裝了一個月沒反應」→ §6-1 補時程;「砍了 proof 你怎麼知道記憶體是可用還是已用」→ B1 補數值合理性檢查與固定值排除,單位對照由作業 0 定案;「我昨天剛清磁碟你隔天還報」→ C3 補解除條件;「一夜塞滿你抓不到」→ C3 明寫慢性 vs 突發;「標了誤報之後呢」→ C5 補 30 日效果、取消、按規則分列;「我不是 Maintain 看得到原因嗎」→ C4 補主機詳情非 Maintain 顯示。

**程式面**:B1-1 原寫 feature flag 失效舊閘,違反「有設定無行為」紅線 → 改直接替換;`AnalysisTimeZoneId` 其實屬 trusted sampling(要刪)→ B1 時區改為沿用 51 輪行為＋一個可選 `PrtgSourceTimeZoneId`;`LogSource` 只在 JSON、SQL 統計過濾不到 → C2 新增 `log_source` 欄;C2 NetIQ 晚到改「同 record id 原地更新」保住 FK 與案件連結;B2 判準改成「拿掉 prtg 依據後仍 High」才寄;C1 補「主機已有 IP 命中 device」的衝突型;`PrtgFormalEligibility`／校準匯出是 B1 漏列的依賴方,補進表。

**管理者**:升級刪 blob 不可逆 → B1 補啟動期備份提示、E 補版本說明;頁籤由 8 縮 6 明寫;版本可追溯由 InformationalVersion 承接(B3);CI windows-latest 列為第二驗證通道(D7)。

**上帝視角**:C2 動 `HostDayPostProcessor` 是 NetIQ 共用路徑 → 每段都要 NetIQ 測試全綠(§4);ping 進白名單讓快照目標數增加 → D 核;B6 與 C3 兩次 seed 遞增合併為一次;HelpContent 兩版與 CLAUDE.md「不要做」條目在 E 同步,否則文件會教使用者去做已刪的步驤。本輪新增的 schema／設定只有三項:`log_source` 欄、identity 表(B5)、`PrtgSourceTimeZoneId`,其餘全是刪除。無其他新增事項。(10-16 更正:`PrtgSourceTimeZoneId` 取消,見第三次複檢。)

### 第三次複檢(2026-10-16,作業 0 結果回寫)

**為什麼改**:探測把三個「暫定」變成事實——(1) 數值路徑現場可用、卡點就是 trusted sampling 閘,B1 不需保留任何過渡;(2) PRTG 不回 `unit`,單位只能依 type,而四類頻道都能用 raw history 互相驗算出語意,所以對照表可以直接定案而不是等資料;(3) raw 時間是 UTC,原本要加的 `PrtgSourceTimeZoneId` 變成多餘——用 raw 落盤比用字串更穩,還能自動偵測帳號時區差。

**取捨**:
- 就緒度卡重做(D8)而不是只刪按鈕:只刪按鈕留下的仍是「一顆一張卡、全鏡像當母體」,使用者看到 9,782 顆裡 0 顆就緒,得到的結論是「壞了」而不是「資料累積中」。四類共用一張、按原因分組,才對得上 §6-1「全程顯示 X／28」。這是本輪唯一的 UI 重做,其餘都是刪。
- 時間基準改 raw(D9)動到 51 輪的正式路徑,風險在「現場帳號時區其實不是 +08」——所以 B1-1 第一步是驗算現場一列再改,驗收加 en-US 文化與 +07 字串兩個反例。不改的代價是:en-US 服務主機解析 zh-TW 上午／下午必失敗、退 raw 當本地時間、整份基線靜默平移 8 小時——那是比改更大的風險。
- Traffic 移除(D10)是因為現場 raw 取值失敗且四類規則用不到;不是永久排除,BACKLOG 記「traffic 進規則前先補 historicdata 形狀探測」。
- C6(D12)是新增,違反「先砍再加」的傾向,但不做的話 §6-1 的 ≤4 步要加一步「到設定頁按自動偵測」,而且 3 台守門裝置不在範圍就是現場現況。規模小、有界、有退路。
- 範圍外 42,872 顆鏡像不寫程式自動清(D14):PRTG-SPEC §3c 的首次基準與縮小保護是刻意設計,現場這批是基準建立前留下的;人工確認清一次就收斂,寫自動清反而要再開一個「什麼情況可以自動」的判定。

**仍待使用者**(D8–D14 已於 2026-10-16 定案):作業 0 後續三件操作;未對應清單截圖。9/22 起對應表未寫入這件事如果是取數排程壞了,要先修現場再進 B1,否則 B1 驗收的「就緒度數字」在現場看不到變化。
