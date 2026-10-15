# 第 54 輪規劃：PRTG 剩餘實作與完整驗收

> 狀態（2026-10-15）：實作完整性已核對，目前逐項驗收；原27項現以乾淨候選b2b82708重新完成E01-T01／T02，仍開25項；§8.30記本次證據，歷史證據保留。使用者已授權逐段完成並驗證後commit／push；只做必要小範圍驗證，沿用有效證據，不執行大規模大量資料測試，不得已須先提出方案討論。本輪未宣告整體驗收通過。
> 來源：2026-10-13 使用者明確要求將第53輪尚未實作項目移入下一輪，先收尾已實作部分。此為範圍移交，不代表原目標、容量、實用性或驗收已通過。
> 本輪起點：`dev` 的 `a69fedd7`；工作分支 `feature/prtg-feedback-54`。第53輪乾淨驗證基準為 `638c58434f0bc01fe77e62365addeafd75680fb0`，至本輪起點僅README、PLAN與驗收JSON差異，正式程式未變。
> 原始需求與契約：[第53輪 PLAN](FEEDBACK-53-PLAN.md) §§3、6、13、15–17；最新責任邊界為 §§5.7、5.9。

> 早期分段紀錄（後續已由§8.13～8.23補齊，不是目前實作待辦）：urgent逐收件者重查改以結構scope與各issue實際authority隔離，Date Format-only變更不取消同父有效NetIQ／raw lane；保留原SMTP／workflow key演算法，不遷移或重置既有接受紀錄。主代理正常checkout定向驗證16/16、零skip（49秒），其中6項mail混合lane／legacy接受投影／部分SMTP重試，其餘10項為時間設定及runtime ownership；TRX `work/round54/root-timebasis-owner-mail-r1/root-timebasis-owner-mail-r1.trx`。當時跨次Notify去重、summary／digest raw重驗及replay／qualification隔離尚待補齊；後續實作完成不連帶表示B2完整驗收通過。
>
> 小範圍驗證偏差：D1子代理曾以整類filter誤跑既有501／5,001／30,000 identity案例；`work/round54/d1-bounded-consumer-r1/d1-bounded-consumer-r1.trx`六例舊SQL形狀／query-count斷言失敗，不列PASS、不重跑大量案例。已改逐方法核資料量；後續新增tiny helper邊界4/4通過，但helper測試不代替正常consumer完整驗收。33M極限文件依使用者定案僅查官方文件，禁止建立極限payload。

## 1. 定位與不變契約

PRTG 輔助 NetIQ 已合格的每日主機日；成功零事件父紀錄也可加入獨立 PRTG 風險理由，缺父、父失敗或證據不合格只顯示等待。兩來源取較高級、不相加；同日弱佐證不升級。CPU／memory 預設壓力提示，Maintain 同版試算後才可啟用正式風險；磁碟低水位及可信趨勢可成正式理由。

固定條件不因跨輪調整：連續兩完成小時、每小時四個 physical slot、午夜歸後一小時的主機日；可信磁碟28日準備度、預估7日內降至20%剩餘門檻；初始化72小時、資格作業720小時且每顆一次attempt；table每秒2次、在途4、historic每60秒5次；單HTTP≤30秒、接受取消後≤5秒禁止新請求並退出可取消等待。

隔離負載為3,000 hosts／15,000 sensors／180日／64,800,000 hourly，slot與hourly不可混算。15分鐘輪≤10分鐘／sample age p95≤30分鐘；5分鐘輪≤3分鐘／age≤10分鐘；時間餘裕≥25%，資料卷25%空間另計。NetIQ三對六輪p95增量≤10%、3,000主機日warm≤10分鐘且每日封存窗口75%內；兩路由各模式每對100成功、共1200成功，相對p95≤10%與絕對≤2秒；PRTG額外常駐≤1GiB、三輪高水位增長≤5%、停寫兩間隔後四間隔清積壓、耐久接受丟失與重複sample/finding/member均為0。

AC01–AC22、U53-01–10、Q1–Q6及八規則族固定222正反例全部保留；不得縮分母、刪失敗、以零延遲或控制時鐘冒充容量結果。探測只取得來源事實，不在正式環境驗收。UC01–UC07為使用者確認提醒，不列開發驗收、完成率或阻塞；未確認不得宣稱現場全面通過或真實事故增益。

## 2. 本輪實作核對結果

本節D54-01～04為原始缺口識別；執行拆分依§4，磁碟核驗獨立為C批次但仍回掛D54-01。§6權限調整回掛E05-T02／E08-T03，不另製造重複TODO。

下列與後面的「已實作、剩驗收」分開計數。私人工作樹有候選不等於目前分支已交付。

原 D54-01～04 的程式／工具接線缺口已逐 consumer 補齊；時間基準、完成分析日、修復介面及真實維護登入 fixture 均已接線並完成必要小範圍核對，實作待辦移除，分段證據見§8.13～8.23。這些實作交付不等於§3父驗收通過；未執行的驗收與資源限制不得刪除或改稱功能未寫。

### 2.1 已補齊的時間授權接線

D54-01 的 fetch／timeline 原始接線缺口已由 `PrtgMessageDateTimeParser.TryParse/BuildFingerprint`、`PrtgFetchService` 的保存日期授權及 `PrtgSensorTimelineCollector` 的逐頁／游標 fingerprint 核對補齊；這些已完成實作從待辦移除。六格式、DST／錯格式／舊空指紋拒絕的有效分段證據保留，完整操作驗收仍由§3承接。

磁碟正常核驗已由 `PrtgDiskVerificationService.ProbeAndSaveAsync` 接入 `TryGetSourceContract`、`AnalysisDayWindow`、有界持久樣本及正常來源 HTTP，已有正常 Verified、DST／缺 authority／漂移反例；原「無法產生 Verified」實作待辦刪除。時間設定、完成分析日 UI 與修復屏障已接線；正常保存、過期拒絕、明確重讀及修補的瀏覽器證據見§8.22，不重開已完成的後端接線。

### 2.2 已補齊的生成工具實作（D54-03）

`scripts/prtg-workload/storage-generator/Program.cs` 的 `InitializeOwnedDatabase`、`HasInitializationMarker`、`AuditOwnedStructure`、`ValidateOwnedDatabase`、`AuditOwnedRowRange`、`AdoptLegacyRun`、`ReadLastProgressLine` 與 `FinishOwnedToolUpgrade` 已實作初始化／採納／升級中斷恢復、完整已提交前綴與 mapping 核對及有界逐行 log 讀取；原對應實作待辦刪除。既有小範圍證據與兩次獲授權的固定結構 seed 記錄保留；後續只能重用 `work/e1-generator-bounded-second-20261014`，不再次建立固定結構。完整64.8M／容量／恢復驗收仍保留 E07-T01/T04，且不得開啟或修改受保護41,140,900筆副本。

完整資格矩陣必須由可驗證的 listener PID／start identity／assembly／exact command tokens／runtime DataRoot與provider綁定實際服務，核對 descriptor hash及reparse祖先。runtime 正常端點、獨立校驗及 Start／Resume／Check 工具已實作並交付；numeric loopback或可編輯JSON宣告仍不足以授權 public settings／排程寫入。D54-02實作待辦移除，依使用者限制未跑完整來源的驗收仍保留 E07-T04／E08-T04，不冒稱功能缺漏或容量通過。

交付來源以目前 repository 的實際程式與已提交版本為準，不再把舊私人候選目錄列為必要實作輸入。任何小型資格／契約驗證都不替代15,000顆完整容量；新缺口仍須明列並映回原ID。

### 2.3 已補齊的有界讀取接線（D54-04）

原列 `PrtgResourcePressureModeStore.Find`、binding Save／RecordQualification、qualification ResumeExisting 及 disk semantic／verification snapshot 的有界接線已存在；不再列為完全未實作。identity／host marker、silent presence、probe local facts、formal-mail claim 的完整 physical bounded reader、CAS／逐筆隔離已有程式及小型驗證；候選提交狀態依分段紀錄。

formal episode／pressure hint／timeline progress／strategy 的直接 mutation 已逐一接入原交易完整有界讀寫及輸出上限，實作待辦移除，見§8.14。identity／qualification／binding 的剩餘 mutation 已直接 attach 完整 physical bounded snapshot，保留原 Version／UpdatedAt CAS 及同交易 readset，實作待辦移除；小型 NUL／Unicode／過期 snapshot、重複 identity mutation 與正式資格提交證據見§8.18。mail reservation 的有界交易及正常 SMTP consumer 接線已完成，見§8.13。profile-refresh state 的 physical bounded reader、SavePage lease／版本雙token追蹤列、earliest頁面完整性核對已接線。

本輪繼續逐呼叫端補齊有界projection／snapshot，保留同一transaction readset、CAS與來源authority；小型字元／超限／損壞／未知extra JSON與設定漂移反例核對等待／拒絕及零正式副作用。依使用者指示不建立33,554,432字元極限文件，雙provider邊界依官方文件及小範圍反例核對。容量export另量測，不把正式mail拒絕等同匯出吞吐通過。對應E03、E06、E07-T03、E08-T05、E10-T02。

## 3. 尚未完成的驗收與交付 TODO

2026-10-15依實際程式重新核對：已完成的功能實作已從§2待辦刪除；本表保留的是尚未完成的驗收／交付，不把「已有程式但未驗」誤報成「功能未寫」，也不因已有程式而刪除未執行的驗收。E02、E08-T01、E10-T01既有完成項不重開。有效分段證據可在確認相同程式／設定／環境及母體後沿用。細部使用者劇本仍以 [coverage inventory](../scripts/round53-user-matrix/coverage-inventory.json) 逐子句追蹤；原22項AC、10項U53、6項Q、7種受控來源情境、222案與53條承接缺口保留，不以27個父項掩蓋子句。

- [x] **E01-T01【承接驗收】** 收尾修正後已對乾淨`b2b82708`重新publish、獨立核組件及執行中probe／assets一致（§8.30）；歷史乾淨 `cfa062e9` 已重新建置／publish，source commit、來源指紋、manifest、Web/Core 組件與執行中 probe 完全一致（§8.24）。後續若修改正式程式／依賴／設定／成品輸入，須重新核對受影響成品；純驗收紀錄不觸發重建。
- [x] **E01-T02【承接驗收】** 乾淨`b2b82708`的錯提交、偽造manifest／MVID及混版皆於啟動前拒絕（§8.30）；未改的preview／舊尾碼契約沿用有效證據；歷史乾淨 `cfa062e9` 成品的錯提交／偽造MVID／混版啟動拒絕已實測；未知身分／已載入組件變更後的預覽皆拒絕且零來源請求。舊尾碼拒絕沿用未改動的build identity契約證據，舊成品雜湊未變（§8.25）。
- [ ] **E03-T01【承接驗收】** 操作明確設定→probe→proof/profile→正式採樣→期間引擎完整鏈，核對 CPU／memory／disk／ping 樣本來源、channel、unit、scale、方向與各端點時基；available-memory 25.0834% 轉 used 74.9166%，disk 90.0887% 保留剩餘方向。
- [ ] **E03-T02【承接驗收】** 補齊錯時區、缺單位／尺度／主通道、世代變更、陳舊 proof、DST 重複／缺小時、跨午夜與無時區歷史反例；不合格輸入保持等待、零正式副作用，維持兩完成小時、28 日與 7 日20%契約。
- [ ] **E04-T01【承接驗收】** 逐欄核對已補強的 build／設定修訂／時區 authority／scope／readiness／guard／traffic 安全診斷／NetIQ 回執與父資格／AI 小探測輸出及其正式消費端；每個 unknown 有原因與具體缺項，不把已保存可核對資料永遠標未知。
- [ ] **E04-T02【承接驗收】** 完整操作目前表單與已保存來源的匯出→離線核對，涵蓋取消、截斷、逾時、無設定、版本不符及防偽／敏感資料反例；維持 64 KiB、depth32、取消≤5秒、單 HTTP≤30秒與整輪截止。
- [ ] **E05-T01【承接驗收】** 補完未保存／缺資料／陳舊守門狀態、原因與設定入口的操作驗證；守門失聯不得視為安全，守門資料不得產生業務 finding／案件／通知。
- [ ] **E05-T02【承接驗收】** 核對範圍縮小、鏡像差額、同步未完成與保留資料不誤刪；補齊 readonly／case-only 修改拒絕、過期寫入及其餘完整角色操作。已通過的 18成功／2衝突及保存→採樣→guard 子集合保留。
- [ ] **E05-T03【承接驗收】** 取消→續跑的單sensor精確集合已補證（§8.31）；其餘夾末頁、messages 完整前導／期間涵蓋及完整操作集合仍待核對；抽樣相依資料不得授權完整拓撲。全量預覽／保存／实际請求集合核對合併由 E07、E08 執行。
- [ ] **E06-T01【承接驗收】** 在完整共同負載中核對成本預覽與實際 transport、quota 等待、property/profile 核驗刷新、歷史初始化及 guard 的種類／數量／版本／scope 一致，並量測資格到期尖峰與日常額度。
- [ ] **E06-T02【承接驗收】** 完成正常、10%逾時＋10%unknown、全來源拒絕及回填＋profile刷新＋NetIQ＋AI＋查詢情境；核對 15分鐘輪≤10分鐘／age p95≤30分鐘、5分鐘輪≤3分鐘／age≤10分鐘與≥25%餘裕。
- [ ] **E06-T03【承接驗收】** 核對 table 2/秒、在途4、historic 5/60、72小時初始化期限及公平有限等待；來源失敗不阻 NetIQ，停止後不開新請求，未知成本／超窗口拒絕擴量。既有 fallback/admission 定向通過不代替共同全負載。
- [ ] **E07-T01【承接驗收】** 確認完整生成與隔離卷空間預檢，實跑 3,000主機／15,000 sensors／180日／64,800,000 hourly 的固定四 slot 資料，核對 DB／WAL／工作與備份副本／匯出／保留峰值及全卷25%空間；工具安全測例不代替此證據。
- [ ] **E07-T02【承接驗收】** 完成三對六輪 NetIQ基準／合併矩陣與共1200成功路由請求；核對 NetIQ p95增量≤10%、3,000主機日warm≤10分鐘、日窗口75%內、路由相對p95≤10%與絕對≤2秒。
- [ ] **E07-T03【承接驗收】** 量測 PRTG額外常駐≤1GiB、三輪高水位留存增長≤5%、真實I/O、鎖等待及查詢計畫；停止寫入兩間隔後四間隔內清積壓，耐久接受丟失及重複 sample/finding/member 皆為0。
- [ ] **E07-T04【承接驗收】** 在完整隔離副本逐項驗備份還原、180日保留、升級／回退、雙程序、提交未知、損壞journal與容量滿後續跑；精確核對列數／版本，舊新 RunId writer 不混寫。
- [ ] **E07-T05【承接驗收】** 補齊 SQL Server 原定查詢計畫與相容／恢復門檻，分開記錄 provider、硬體與資料密度限制；既有每sensor一筆的四組測試不能裁定180日全量吞吐通過。
- [ ] **E08-T02【承接驗收】** 完成初次設定→核驗／等待→NetIQ父紀錄→PRTG期間風險→AI→報告／案件／交辦回覆→通知／恢復／再發全程；成功零事件可合法補強、不合格父不發布、弱佐證不升級、兩來源取較高級不相加。
- [ ] **E08-T03【承接驗收】** 逐 AC01–AC22／U53-01–10／Q1–Q6 核對並補齊六角色真實操作、直接URL及修改API、子路徑、受限證據閱讀與交辦回覆；既有匯出授權及部分頁面拒絕不代替完整角色流程。
- [ ] **E08-T04【承接驗收】** 完成兩管理者過期寫入、對應修改／合併／刪除／回退、取消／續跑與精確持久集合；在完整3,000／15,000母體核對預覽、保存、讀回與實際來源請求一致。
- [ ] **E08-T05【承接驗收】** 補齊三來源進度、晚到NetIQ／晚AI、AI故障、PRTG失敗／停用、local-only、NetIQ無PRTG、重啟與DB失敗／滿journal恢復；真實瀏覽器結果與耐久ID／版本／副作用一致；統計通知已SMTP接受但generic mail workflow仍等待的缺漏已修正，沿用第53輪99＋20案與隔離實收子項證據；其餘工作流組合仍須驗收，不新增formal PRTG接受facts。
- [ ] **E08-T06【承接驗收】** 補完摘要及高風險通知的SMTP五分鐘、部分成功、提交未知、重試去重與隔離實收；ACK→可信恢復→再發保持案件、人工回覆與通知識別一致。
- [ ] **E09-T01【承接驗收】** 各規則族以受限角色實際找出正確資源、原因、證據與下一步，完成風險／未知／排除及校準的瀏覽器查證；已通過的222案consumer重播不重列待實作。
- [ ] **E09-T02【承接驗收】** 核對最終報表與操作結果的四基準、時間／分母／未知／排除及提前量一致，缺時間不算提前量、不得使用未來資料；保留合併未優於聯集的既有結果，不為宣告增益改真值。
- [ ] **E10-T02【承接驗收】** 逐原需求、第52輪有效子條款、AC／U53／Q與E01–E09核對真實呼叫端、程式／脚本／文件及證據，完成可執行全範圍程式審查，記實際模型／角色与限制；代理範圍已知缺陷與必要未測清零。
- [ ] **E10-T03【承接驗收】** 在最終凍結同版完成受影響驗證及完整全量回歸，記命令／母體／SHA／環境／結果；歷次失敗結果保留；最近全量為b77a72c7的7,978通過／1失敗／10可選略過，共7,989案，唯一失敗在638c5843已有修正後定向通過證據。正式程式未變的有效結果可沿用，最終變更須有完整有效回歸覆蓋，不冒稱最終提交曾單次全量全綠。
- [ ] **E10-T04【承接交付】** E01、E03–E09全部必要條件通過後，同步PLAN／AC／現況規格／Help／交付限制與證據可取得性；依使用者既定逐段commit／push授權在本輪feature分支提交，確認遠端SHA；使用者已於收尾授權必要條件通過後整合dev並刪分支，目前尚未符合。功能實作已獲授權，尚未完成的相依繼續補齊。

## 3.1 全部原Phase的承接對照

| 原Phase | 本輪已有實作及仍需核對的界線 | 本輪責任 |
|---|---|---|
| N1／A0 來源與版本 | build identity、來源探測及拒絕混版已有分段證據；正式來源完整核對未完成 | E01、E04；D54-01 |
| N2 範圍與守門 | scope／guard／mapping及不誤刪防線已有實作；完整角色與衝突操作未全驗 | E05、E08-T03/T04 |
| N3／A1 可信取數 | pager、raw/profile／proof、readiness、文字時間與正常磁碟核驗已有實作；DateFormat-only變更的背景raw工作隔離已依§8.18～8.20補齊，完整來源／角色操作仍待驗 | D54-01、E03、E04、E05-T03 |
| N4／B 風險與主機日整合 | 合格父紀錄、期間規則、補充／fusion與案件通知已有分段實作；全部正常操作鏈未驗 | E08-T02/T05/T06；E09 |
| N5／C 使用者與管理者 | 設定／probe／維護／證據及授權頁已有實作；六角色完整動線未驗 | E05、E08、E09-T01 |
| N6／D 持續負載及恢復 | quota／admission／journal／SQL與容量工具已有實作；64.8M生成及全混合負載未完成 | D54-02/D54-03、E06、E07 |
| N7 實用性與四基準 | 固定222案及局部報表重播已有證據；完整角色找證據與最終四基準核對未完成 | E09 |
| N8 全分支審查與交付 | 本輪做已實作部分審查與bug修正；原全目標閉環並未成立 | E10；不得將移交當作整輪PASS |

原 R01–R18、M0–M7、AC／U53／Q 的細部條款仍由第53輪原始ID追溯，以上不代替逐條門檻；新缺口必須回掛原E／TODO，避免只以Phase總名勾選。原來已驗子項及仍未驗子項分開登錄。

## 4. 本輪批次、相依與客觀驗收

全部工作保留在第54輪。27個原TODO是責任索引，不是27個等量工作，也不把53條覆蓋缺口另加成完成率分母。27／53是承接基線而非新增缺口的上限；R54-01～08各有多個必要檢查，不混成等量完成率。功能實作與尚未完成的驗收分開追蹤；已完成實作不再留作「待寫功能」，父驗收仍須全部必要子條款通過。同一有效證據可跨ID引用，不重複執行或計功。A至F已有分段程式／工具與局部證據，G／H未完成；實際缺口依§2及逐段紀錄。

| 批次 | 內容與原ID | 相依／下一個使用端 | 完成界線 |
|---|---|---|---|
| A 規劃一致性與資源預檢 | 基準、AC／U53／Q對照、證據有效性；E06成本及E07空間預檢前移 | 起點；供B～H使用 | 清單一致；功能開工後、全量生成前取得空間／engine／成本預檢，不以工具存在當容量足夠 |
| B 訊息時間授權 | D54-01訊息部分；E03-T01/T02、E04-T01/T02、E05-T03 | A；輸出明確格式與fingerprint，供collector／timeline／正式讀側／probe | 六格式、DST、跨日、漂移、舊授權及續跑正反例；raw OA/channel既有授權不誤失效 |
| C 磁碟核驗接線 | D54-01磁碟部分；E03-T01/T02、E04-T01、E08-T02 | A及保存的端點時間authority；共用契約若由B修改須同版整合；供profile／期間引擎 | 正常UI/API入口可完成Verified；缺契約、歧義、漂移不保存正式資格 |
| D 有界讀取 | D54-04；E03、E06、E07-T03、E08-T05、E10-T02相關子句 | A；供資格／replay／mail／data-transfer | 全部正式consumer使用有界讀取；大小、CAS及authority邊界精確驗證 |
| E 容量工具與資格準備 | E1＝D54-03；E2＝D54-02；E07-T01/T04、E08-T04 | E1安全修正先完成；E2正常全量準備依完整receipt及B／C／D受影響契約 | 小資料故障反例通過，再完成固定全量副本與正常HTTP資格流程；合成輸出不當資格PASS |
| F 完整操作與角色 | E03～E05、E08、E09功能子項及§6權限 | B／C／D受影響輸出；供G全量操作與H回查 | 六角色、兩session、子路徑、風險／案件／回覆／通知／恢復及四基準同版一致 |
| G 共同負載與恢復 | E06-T01～T03、E07-T01～T05、E05-T03／E08-T04全母體子項 | A預檢、D、E及F相關功能完成 | 固定64.8M資料、三對六輪、1200成功路由、兩後端原定門檻與恢復實證 |
| H 最終整合與交付 | E01-T01/T02、E10-T02～T04 | B～G必要子項全部完成；E01版本拒絕反例可提前準備 | 凍結成品、全部需求及子條款回查、有效全量回歸、文件／遠端SHA一致 |

### 4.1 B／C：可信時間與磁碟資格

- 原始缺口已補齊：`PrtgFetchService.FetchStateChangesRangeAsync`及`PrtgSensorTimelineCollector`單顆／批次均使用保存的嚴格文字日期授權，逐頁保存／游標恢復核對 fingerprint。有效分段證據依§8沿用。
- 原始缺口已補齊：`PrtgDiskVerificationService.ProbeAndSaveAsync`提供`PrtgDiskSemanticSourceContract`，使用分析時區相鄰日期邊界；缺契約仍回傳`source-contract-unconfirmed`。正常入口與 DST／漂移反例見§8，完整操作驗收保留。
- 輸入：保存的來源／分析時區、帳號Date Format、來源樣本及世代。輸出：具版本的訊息時間證據或磁碟核驗結果；下一端是正式profile、期間規則與probe，而非只停在測試helper。
- 驗收：明確正常值與歧義反例並存，核對API、持久版本、讀側判定及零非法finding／案件／通知；CPU／memory、NetIQ父資格及既有28日raw證據不得被文字日期設定誤傷。取消期限依§4.5。

### 4.2 D：所有正式消費路徑的有界讀取

- 原始缺口已補齊：`PrtgResourcePressureModeStore.Find()`經完整有界快照取得選中資料；§2.3列出的binding、qualification、disk map、replay、mail及data-transfer依實際呼叫端核對，分段證據見§8。
- 輸入／輸出須保留相同transaction readset、blob version、CAS與來源authority；沒有有效資料時明確等待／拒絕，正常選中資料不得被任意截斷後當完整授權。
- 驗收涵蓋正常大小、cap邊界、超量但選中entry有效、未知extra JSON、設定漂移、並行保存；精確核對拒絕原因與正式副作用為0。262,144字元 mode／33,554,432字元 disk snapshot的既定字元限制不提高；編碼位元組與程序記憶體另量測；export吞吐另由G量測。

### 4.3 E：先安全續跑，再正常全量準備

- E1以自有最小資料在DB marker、manifest、checkpoint各寫入界線注入中斷，核對可重入恢復、已提交前綴、mapping竄改與log有界讀取。legacy adoption現已使用`ReadLastProgressLine`逐行有界讀取，分次寫入的恢復接線及有效證據見§2.2與§8；不再列為待實作。
- 完整64,800,000筆目標仍屬G批次未驗收項。41,140,900筆是移交文件記錄的暫停水位，該既有副本依使用者限制禁止開啟或修改；本輪授權不包含續跑。後續若確有必要，須先討論資源與執行方案；目前僅重用已獲授權的小型資料，不再次seed固定結構，不修改checkpoint或縮分母。
- E2從完整receipt與catalog核對進入public settings／pilot／binding／qualification入口；服務ownership須綁PID、start identity、assembly、exact command tokens、runtime DataRoot／provider、descriptor hash及reparse祖先。
- 驗收foreign listener、舊revision、pilot拒絕／過期、部分binding／job、720h／每顆一次attempt；合成contract與50顆功能證據不代替15,000顆實跑。E1、E2各在相依完整且驗證後提交，完整負載結果由G裁定。

### 4.4 F／G／H：功能、量測與結案責任

- F由代理在隔離Web按固定case操作，六角色與直接URL／修改API皆核對；使用兩個session測過期寫入。完整鏈包含成功零事件父、父不合格等待、弱佐證不升級、兩來源取高不相加、ACK／回覆／可信恢復／再發及耐久識別。
- F對8族222案沿用或補驗正式consumer精確結果，瀏覽器依原矩陣逐族至少一個正例及必要風險／未知／排除情境核對資源ID、期間、原因、證據、狀態與下一步入口；四基準TP／FP／TN／FN、unknown／excluded及提前量可重算。沒有未來資料，缺時間不計提前量；保留combined未勝union結果。只量測操作步驟，不要求必然減少或新增效益。
- G沿用§1與27項TODO全部固定門檻。先封存硬體、provider、資料密度、來源延遲模型、暖機、記憶體取樣／GC、p95樣本與窗口口徑，再跑混合負載、來源故障、資格刷新、quota、公平等待及停止恢復。小型SQL Server相容結果不代替原定全量／恢復；engine不適用時安排適合的隔離engine，不要求改到正式機驗。
- G核對DB／WAL／工作及備份副本／匯出／保留峰值與全卷25%空間；備份還原、180日保留、升級／回退、雙程序、提交未知、滿journal及續跑核精確集合／版本，耐久接受丟失與重複為0。
- H先逐原需求及全部子條款直接檢查consumer與交付物完整性，有漏做先補回對應批次，再正式整輪驗收；不把清單一致性當功能PASS。既有相同程式／設定／環境有效證據可沿用，變更或原檔不可取得才補驗，並記清有效範圍。

### 4.5 統一驗收格式與代理責任

每個必要子條款以「原ID／批次、前置資料與角色、實際入口及操作、精確預期、失敗反例、命令／版本／環境、實際結果及可取得證據」記錄。結果分待實作、待驗、通過、失敗、資源未就緒；除通過外皆未完成，不得以使用者確認或主觀評語取代必要證據。跨批次同一證據只留一份並引用，父TODO須所有必要子項通過。

| 項目 | 代理負責的客觀判定 | 不作開發通過條件 |
|---|---|---|
| 人工磁碟核驗、交辦回覆 | 代理扮演測試角色操作正常入口，核對持久狀態／版本及副作用 | 等現場人員按鈕或回覆 |
| 易用性／文件 | 依操作說明走固定案例，找出正確資源、原因、證據與下一步，核API／DB | 新操作者主觀理解、偏好或滿意度 |
| 通知／AI | 隔離SMTP讀MIME／收件人／時間；受控AI成功、故障、取消與重試 | 等真實收件者已讀或正式模型服務可用 |
| 來源、部署與效益 | 版本manifest、既有去識別／明示合成來源、固定真值與四基準重算 | 等新現場樣本、真實事故發生、保證合併必勝聯集 |
| 容量與恢復 | 自有隔離DB／實際I/O／完整母體及真實單調耗時 | 正式機故障測試或使用者口頭確認效能 |

取消自後端接受起立即禁止新請求／批次，可取消HTTP與等待≤5秒退出；正常單HTTP≤30秒是另一門檻。不可安全中斷的DB原子提交在封存有限期限內到安全點停止，不混算成可取消等待。通知意圖自合格父與必要證據耐久提交後≤15分鐘可見；SMTP恢復可用、重試具資格且排程啟用起≤5分鐘首次接受，拒收期間保留重試狀態，提交未知不保證外部郵件恰好一次。

UC01～UC07僅限制正式站台／真實效益宣告，不是開發TODO或必要輸入；來源未知仍由產品資格守門處理。coverage inventory的AC22／Q2／Q6及U53-04皆套用本節，不得重新要求獨立使用者效益證據。全部Q的journey關聯須與user-matrix完全相同；A2逐Q、R54／C54／§4.10、維護角色能力與證據ID檢查及漏項反例已有腳本接線，實際角色操作與驗收狀態仍獨立核對。

每段相依完整、檢查整體影響並適當驗證後commit／push至本輪feature分支，確認遠端SHA。使用者最新授權允許收尾條件通過後整合dev並刪本輪分支；未通過前保留feature，不force push。不用AGY；若後續授權委派，subagent僅gpt-6-luna low／medium／high，主代理獨立核對。記實際審查角色與模型，不冒稱跨模型背書。

### 4.6 八項再次審查補強契約（全部本輪處理）

機器可讀逐項清單為coverage inventory的`round54ReviewAdditions`；下列R54 ID與該表一對一，任何必要檢查未完成均不得以父TODO或既有測試通過代替。實作前／後都核對真正操作端，非僅檢查symbol存在。

| ID／階段 | 原TODO | 定案及精確驗收 |
|---|---|---|
| R54-01／F2 | E05-T02、E08-T04 | 確認清除須綁定已閱預覽的scope及相關資料版本；對應或範圍變更拒絕並重新預覽。清除期間與同步、回填、另一清除取得有效互斥或等價一致性保護。驗預覽後改對應、雙管理者、檢查後啟動作業及中途故障；記精確已刪／剩餘集合、耐久進度及重試結果，不將部分成功稱完成。 |
| R54-02／C1 | E03-T01/T02、E08-T02 | 後端依保存分析時區提供已完成日期，UI明示日期／時區；單顆、批次、資格預檢、預覽、證據有效性與人工確認同一契約。跨瀏覽器／伺服器／分析時區、午夜、DST23／25小時日核精確UTC區間；歧義點拒絕但不得默默漏掉合法完成日。 |
| R54-03／F1 | E04-T01、E08-T02、E09-T01 | 分開缺authority／設定、樣本不足、可人工覆核、不匹配、設定過期、可重試錯誤。Maintain連到正確修復入口；非Maintain顯示原因與需管理者處理事項，不導向必然403。修復後正常入口重驗，維護／排程／詳情／報告同版一致；只有有效比對點可人工確認。 |
| R54-04／F2 | E05-T02、E08-T03 | 分開全域影響數量、可見明細數與明細受限狀態；確認明示刪除是全域而非畫面名稱集合。先授權過濾再取可見前20筆。驗隱藏名稱／ID、超過20筆、孤立sensor及零可見主機；零可見不得顯示成零影響。 |
| R54-05／B2 | E03-T02、E08-T05/T06 | 建立日期格式／culture／raw、source、analysis時區／來源世代／無關scope變更對各fingerprint及游標的影響矩陣。升級或變更後舊不合格訊息不新增正式風險／案件／通知；保留歷史事實、人工回覆、合法NetIQ與不相關raw證據。已存在finding／案件／回覆／待寄郵件，經重啟、重播、設定回復及版次回退逐consumer核對；空舊authority不追認。 |
| R54-06／D1 | E03-T02、E07-T03、E08-T05 | mode為262,144字元、disk snapshot為33,554,432字元，位元組／記憶體另計。SQLite／SQL Server均驗ASCII、中文、補充字元、cap邊界及超限／損壞。依證據相依範圍隔離：單主機mode只阻其相依項；共用disk blob無法安全讀取時，所有依賴它的磁碟資格等待，獨立NetIQ／證據／通知繼續。不得部分信任損壞JSON；不新增儲存拆分或遷移。修復保留CAS及不相關資料，詳§4.9。 |
| R54-07／A2 | E01-T01、E08-T03、E10-T02 | 保留原六角色，新增§4.8四種真實登入維護情境與一個合成元件防禦測例，不新增正式角色或擴張權限；救援帳號走真正serverAdmin登入，不用普通使用者改名冒充。凍結本輪commit／Web／Core／靜態資源／fixture／環境；第53輪證據只依未變子範圍沿用。A2補validator、fixture、recorder的新增角色／R54欄位支援，未實作前不得稱新矩陣已可執行。 |
| R54-08／A2 | E10-T02/T03/T04 | 依§4.7逐階段列原ID、相依、精確預期、反例與可取得證據；27個父TODO須全部必要子項通過。27／53非上限，新增缺口明列，不藏在完整流程泛稱。每階段及整輪驗收前先查真實consumer完整性，漏做先補；只沿用有效證據，變更才補受影響驗證。 |

清除契約補充：預覽失敗／逾期時停用舊確認，重新預覽不自動提交。伺服器驗當前權限、preview身分與scope，不信任客戶端傳回的候選集合。接受後不得把新加入的候選自動納入舊確認；分批中斷以同作業識別查明已完成部分，不假設整批原子回滾。實作方式由F2依既有作業／版本機制定案，驗收必須涵蓋有效所有權與安全點，不能只有讀一次IsRunning。

日期契約補充：C1同時調整目前browser-local yesterday、DateTime.Today的資格／手動確認有效期，以及probe的UTC區間≤24小時限制；使用分析時區兩個相鄰日期邊界定義完成日，25小時合法日不得當作超長惡意請求，仍保留有限HTTP／作業上限。跨午夜後不能用另一個日期悄悄替換已開啟表單；過期以明確狀態要求重新取得日期。

遷移契約補充：B2區分「歷史曾發生」與「目前可正式發布」。未寄通知在發送端核驗當前授權；已寄不重寄或假裝撤回。重新獲得合法證據後依原去重及案件識別續作，保留人工處理；設定改回舊值不自動信任舊空指紋。所有舊writer停止後才執行升退版；不在混版writer下重用新authority。影響矩陣包含collector／cursor、期間consumer、案件、待寄／重試、報告、匯出、歷史讀側，不擴大失效到獨立NetIQ或未變raw樣本。

字元契約補充：既有常數與有界查詢以字元為名，不直接改成相同數值的byte cap；補充字元在資料庫與.NET表示可能不同，兩provider須得到相同有效／拒絕判定且不截斷後誤授權。D1須定義超限的安全維護讀取／修復方式，不能要求使用者手改DB，也不能由一般讀側靜默刪掉未知欄位。

### 4.7 可獨立驗收的子階段與相依

每個階段完成完整相依、程式／文件及正反驗證後才提交；允許同一有效證據跨ID引用。A資源預檢應及早執行，不能讓大型容量作業先開始。功能實作已依2026-10-14使用者指示授權。

| 階段 | 原ID與輸入／相依 | 交付／完成界線 |
|---|---|---|
| A1 | E06-T01、E07-T01；本輪基準 | 隔離engine／成本／空間預檢及證據可用性，未足不得全量啟動 |
| A2 | E01-T01、E08-T03、E10-T02～04；R54-07/08 | 角色／R54／C54／Q與補強ID檢查、fixture／recorder、即時授權及回退啟動前接線；validator與原生回覆差額工具已有分段交付，完整角色／最終回退驗收仍待完成 |
| B1 | D54-01、E03／E04／E05-T03；A | 格式、parser、fingerprint、兩條取數路徑及probe接線 |
| B2 | R54-05；B1 | 舊資料／游標／案件／mail／報告／匯出升級生命週期及恢復矩陣；依A2工具驗回退啟動前隔離 |
| C1 | D54-01磁碟、R54-02；A及B受影響時間契約 | 正常Verified及完整日期鏈、DST／漂移／缺契約反例 |
| D1 | D54-04、R54-06；A | 有界consumer、CAS、雙provider字元邊界、隔離失敗與修復 |
| E1／E2 | 沿§4.3；A1、B／C／D受影響契約 | 先生成／續跑安全，再完整副本與正常HTTP資格；各自可驗收提交 |
| F1 | E03、E04、E05-T01、E08-T02、E09-T01；B/C/D、R54-03 | 設定→等待／修復→核驗→採樣→正常consumer，固定資源及版本一致 |
| F2 | E05-T02/T03、E08-T03/T04；A2、B/C/D、R54-01/04 | 原六角色及維護子矩陣、直接API、全域清除、父子所有權、每批即時撤權、刪除／進度一致性與安全重試；全母體集合另在G驗 |
| F3 | E08-T02/T06；F1/F2 | 父資格→風險→案件／人工回覆→SMTP、ACK／恢復／再發，耐久識別與去重 |
| F4 | E08-T05、E08-T04恢復部分；F3 | 來源失敗／停用、晚AI／NetIQ、取消、重啟、DB／journal失敗可見且可恢復 |
| F5 | E09-T01/T02；F1/F3 | 每規則族實際找證據，222案有效consumer證據、四基準及as-of精確重算 |
| G1 | E07-T01及T04備份；A1、E1/E2、D1 | 完整母體／receipt、空間／WAL峰值與可還原副本 |
| G2 | E06-T01～03、E07-T02/T03、E05-T03/E08-T04全母體；G1、F相關功能 | 三對六輪／1200路由、quota／新鮮度／記憶體及集合一致 |
| G3 | E07-T04、E07-T01保留、T03恢復；G1/G2 | 完整副本故障、升退版（含F2清除狀態與啟動前防護）、雙provider提交未知／即時撤權、180日保留、精確丟失／重複／剩餘集合 |
| G4 | E07-T05及其他原定SQL Server條款；D1、G1及相關工作流 | SQL Server查詢計畫、容量／相容／恢復原定門檻；小型翻譯測試不替代 |
| H1 | E01-T01/T02、E10-T02；全部階段 | 最終成品、原需求與所有新增子項實作完整性及程式審查；回查最終版回退目標／清除狀態相容性 |
| H2 | E10-T03/T04；H1與有效回歸 | 最終同版有效全量、Help／規格／清單同步、遠端SHA一致 |

### 4.8 維護權限子矩陣與證據格式

保留主矩陣原六角色，另以`user-matrix.json.maintenanceRoleMatrix`定義四種真實登入情境及一個合成元件防禦測例，不將新增角色偽裝成原六角色已覆蓋。M54-rescue走真正救援登入；M54-scoped僅為合成授權元件測例，不是正常登入可建立的帳號，不計瀏覽器驗收；M54-global同時Maintain＋ViewAll；M54-readall只有ViewAll無Maintain；M54-nomaintain為受限且無Maintain。M54-readall與M54-nomaintain的preview／confirm直接API均拒絕；M54-rescue與M54-global各依§6讀取明細，清除集合不因可見範圍改變。

serverAdmin可見必要維護統計限定為監看裝置數、範圍外數值／狀態變更筆數、受影響裝置數、孤立sensor數、基準數量／時間、安全阻擋原因；不含裝置名稱、host／sensor／device ID、IP或來源原文。回應應另說明明細受限；錯誤與確認提示不得繞過此界線。ViewAll本身不授予Maintain。

每份角色證據記登入路徑、能力及可見集合、凍結commit／資源雜湊、操作與直接API、全域／可見計數、preview身分、精確持久集合及副作用。未provisioned角色與未接入validator／recorder一律待實作，文件清單通過不等於可執行或瀏覽器通過。

### 4.9 五項契約修正（本次定案，優先於前文概括敘述）

C54-01～05以coverage inventory的`round54ContractCorrections`追蹤，分別回掛既有R54，不重開原27個TODO，也不把新增檢查當已通過。以下保留具體行為契約；實作現況依§2與§8，不再以開工前的規劃措辭判斷是否已有程式。

**C54-01／A2／R54-07：真實角色與合成防禦分開。** `RoleCapabilityMap`的一般Maintain來自Admin且含ViewAll，角色聯集不扣除能力；serverAdmin無業務可見主機。因此四種真實情境為M54-rescue／global／readall／nomaintain。M54-scoped留在`syntheticDefenseCases`，只驗授權元件在合成能力／可見集合下不外洩，不建帳號、不注入claims冒充正常瀏覽器結果，不新增正式角色、不擴張權限模型。A2的validator／recorder後續須分開兩種證據類型，原六角色不變。

**C54-02／D1／R54-06：隔離按證據相依範圍。** mode為主機獨立文件，損壞只阻依賴該文件的操作；disk semantic與verification result各為多主機共用文件，無法解析、超限或快照不可信時，所有依賴該份文件的磁碟資格明確等待。不得跳過壞entry後把其他entry冒稱已驗可信。獨立NetIQ、其他有效證據與不依賴該文件的通知／重播繼續；組合通知須排除未合格理由，不得把缺證據說成健康。驗精確相依集合、等待原因與其他成功集合。保留既有CAS／快照規則及有界修復契約，不為達成單主機隔離另建儲存拆分、分表或資料遷移。

**C54-03／F2／R54-01：三個清除入口同受保護。** 管理頁確認、`PrtgDailyPipeline`與`PrtgStructureSyncRunner`經`PrtgScopePurge.RunAfterStructureSync`的自動清除，共用有效執行所有權、一致性與耐久部分進度。人工綁preview，自動綁成功同步scope／來源世代，保留CheckScope、CheckShrink及守門保留集合；不要求自動流程提供人工preview，也不因共用而放寬自動清除。驗自動中人工確認、人工中自動觸發、兩條自動入口競爭、自動部分失敗／重啟／續跑。同步成功與清除部分失敗分開呈現；記已刪、剩餘與作業身分，基準只在對應完整清除成功後更新。

**C54-04／A2準備、B2驗收／R54-05：設定恢復不等於程式回退。** A2在升級前封存確切回退目標commit／Web／Core／assets與配套DB、key、設定備份、schema及authority版本；本輪起點a69fedd7可作候選，但必須取得對應可核對成品，不能只寫latest。同庫降版只有在針對該目標證明相容後才允許，否則拒絕並採成套備份還原；停止writer不足以證明相容，不直接換DLL就宣稱回退成功。升級後產生的案件／人工回覆／通知回執／待辦作業須先封存差額與精確清單，不默默遺失；可安全相容重放才重放，否則標明未恢復差額並阻止宣稱無損回退。外部已接受郵件不因還原回執倒退就盲目重寄，未完成去重核對前暫停相關派送。驗目標拒絕／合法還原、資料與key一致、人工內容、待辦及去重；不要求新增通用跨版本遷移框架。設定值恢復仍受B2當前authority重驗規則約束。

**C54-05／F2／R54-01：預覽與作業生命週期。** 人工preview自伺服器建立起有效5分鐘；確認須綁操作者、當前權限版本、來源世代、對應／保留集合及候選資料邊界。以上改變或到期即拒絕且零新增刪除，重新預覽不能自動提交。使用同一已接受確認識別重送，回傳同一作業／結果，不另開刪除；首次接受必須在期限內，重送已接受識別只是查回原作業，仍須核對身分與讀取權。作業開始後不納入新候選資料，與清除相依的寫入／scope變更須由一致性機制攔截或令作業在安全點停止。

人工作業接受前撤回Maintain：拒絕且零刪除；接受後每個新批次及resume前重驗，撤權後只讓已開始的不可安全中斷原子提交到安全點，禁止新批次，保留已完成計數與原因。resume須目前有Maintain且原接受scope／世代仍有效；否則重新預覽建立新作業並關聯前次部分結果，不能沿舊確認擴大集合。自動作業由成功同步及服務作業所有權授權，不依附人工帳號；其scope／世代變動同樣安全停止。驗5分鐘邊界、重送／逾時未知、撤權／恢復權限、scope變更、原作業續跑與新作業建立，精確核對刪除及剩餘集合。

### 4.10 清除與回退的四項執行補強（納入既有C54）

以下ID為既有契約的必要子項，機器清單為`round54ExecutionRefinements`；不新增正式角色、不擴張權限、不新增通用工作流框架。四項已有實作及分段證據，完整所有權／耐久／撤權／回退驗收仍須符合本節，不能只以方法存在或文字提醒驗收。

**C54-03-O／父子作業所有權（F2實作驗收、G3整合）。** 自動清除是在`PrtgDailyPipeline`、`PrtgStructureSyncRunner`或回填內同步尚未結束時執行。合法子步驟必須繼承可核對父作業身分，不能直接套用`ScopePurgeConflict()`的「同步／取數／回填中就拒絕」，也不能等待父作業先完成。只有該父作業可委託自己的清除；外部競爭作業仍受互斥。父作業取消或失去所有權時，子作業在安全點停止且不得繼續開新批次。驗夜間同步→清除、回填內同步→清除正常成立；人工／自動競爭拒絕，父取消及重啟後無自我等待、無殘留所有權。保留C54-03的自動縮小／範圍保護。

**C54-05-A／每批次即時撤權（A2準備、F2實作驗收、G3跨程序整合）。** 保存操作者識別，批次與resume前透過既有權限規則重新判定當前帳號啟用狀態、群組與能力，不把原HTTP principal／claims或`ICurrentUser`快照當當前授權。`ActiveUserMiddleware`只在HTTP入口刷新，`PermissionVersionStamp.Current`有行程快取；雙程序驗收須證明看到持久化權限異動，不假設快取跨程序同步。Core接收Web權限單一來源的判定，不複製角色規則。驗停用帳號、移出Admin、停用授權群組，操作者不再送刷新HTTP請求仍能在下一新批前停止；已開始的不可安全中斷提交依C54-05到安全點完成。serverAdmin另驗設定撤用／重啟後的舊作業resume拒絕，不以普通帳號停用代替。保留停止原因與精確部分結果，不引入新正式角色。

**C54-03-T／刪除與進度提交一致性（F2實作驗收、G3雙provider完整副本）。** 每批已提交刪除及耐久進度／結果必須原子一致，或有可證明等價的恢復協定；現有`BatchedPrune`記憶體累計不能作耐久證據。分別注入提交前中斷、刪除已提交但回應／進度觀測遺失、結果送達前重啟；同識別重送回同一作業，已刪計數不重複、不遺漏，不加入新資料。與保留期／其他合法清理併行時，精確區分原接受集合、本作業刪除、其他作業已刪與剩餘集合；不能只用剩餘筆數倒推本作業功勞。驗SQLite及SQL Server各交易／提交未知路徑，保留守門資料與完整成功後才更新基準的契約。

**C54-04-B／回退啟動前防護（A2交付工具、B2時間授權驗收、G3／H最終回查）。** A2規劃並於功能輪交付具啟動前相容檢查的可執行回退控制工具，由它負責拒絕不相容的本輪受控回退路徑；不能假設舊二進位理解新authority，也不以操作文件提醒當拒絕證據。明列工具入口、輸入manifest、可接受目標及拒絕結果，不承諾能阻止繞過工具的任意外部手動啟動。還原Web或背景worker啟動前先隔離排程、來源寫入及通知派送；完整版本／DB／key／設定、備份後差額、待辦與通知去重核對通過才解除，任一步失敗維持隔離。不得先啟動再手動暫停。以帶待寄通知、待恢復工作及人工回覆的備份驗核對前零對外副作用，成功才受控恢復。B2驗時間授權；F2後新增清除狀態由G3及H1/H2對最終成品補驗回退相容性，舊B2證據不自動涵蓋後續schema／作業變更。不新增通用跨版本遷移框架。

相依順序：A2準備現行授權與回退工具→B2驗時間授權回退；F2完成父子所有權、批次撤權與提交一致性→G3以雙provider／完整副本補驗→H1完整性核對、H2有效最終回歸與交付。相同狀態有效證據可引用；F2改到持久作業／schema時，須重新判定回退證據有效範圍，不能把早期B2結果直接當最終版通過。

### 4.11 收尾發現四項缺陷的正式回掛與驗收

這四項是既有安全／授權契約的實作缺陷，不是新增功能或四個新的父TODO。工具三項已在`03e16f30`修正，授權歧義已在`9f2e3824`修正；本節只補正式規劃追蹤。修正與小範圍驗證狀態依§§8.28–8.29，不能重新列為待實作，也不能因此勾選其完整父驗收。原27項、角色及權限模型、容量門檻均不變。

| 缺陷／既有批次與相依 | 必須保留的行為契約及下一使用端 | 客觀驗收與可沿用證據 |
|---|---|---|
| 回退副本完整性結果未檢查；A2／B2→G3→H，C54-04-B，E07-T04／E10-T02 | reconcile完成後的備份須完整讀取`quick_check`結果，只有單列ok可繼續形成回執；錯誤列或檢查失敗不得生成成功回執、解除隔離或授權launch。拒絕不等於已回滾先前合法提交的reconcile資料，不能宣稱來源與目的地都未變。 | 沿用§8.28舊行為失敗、修後錯誤結果拒絕且無receipt、合法小型還原／release證據；核對後續release／launch仍依有效回執守門。小型結果不替代完整恢復或SQL Server驗收。 |
| Windows junction／reparse防護不足；A2／B2→G3→H，C54-04-B，E07-T04／E10-T02 | 受控回退工具使用原始路徑檢查既有祖先的reparse屬性，必須在resolve消除別名前拒絕；prepare／verify／launch／release／reconcile及相關檔案讀取不得繞過。合法尚不存在的restore末節可建立，其他既有路徑與身分檢查照常；不承諾阻止工具外手動啟動。 | 沿用§8.28真實Windows junction經CLI launch拒絕與合法新路徑證據。其餘入口先查是否共用相同已驗防護及呼叫順序；只有獨立路徑或缺證據才補單一自有臨時目錄反例，不逐入口機械重跑或啟動正式服務。 |
| descriptor只在起始時核hash；E2→F／G→H，D54-02，E08-T04／E10-T02 | runtime ownership查詢及每個業務API請求前，重核descriptor原路徑／祖先與當前bytes SHA，必須符合preflight固定值；漂移、消失或reparse即停止後續請求，不重新pin漂移檔。既有PID／啟動身分／組件／DataRoot／provider核對仍必要。 | 沿用§8.28小檔相符／漂移拒絕及兩處請求前接線證據；若不能證明拒絕發生在送出前，僅補有請求計數的最小受控驗證。已完成的API副作用不得被說成隨後漂移而自動回滾；完整資格母體仍待驗。 |
| shared authority重複／大小寫別名被最後值覆蓋；D1→資格／正式發布→F／H，R54-06／C54-02，E03-T02／E08-T06／E10-T02 | settings／policy完整有界原文在取得正式權威前先拒絕歧義；snapshot、policy讀寫、binding保存／資格發布、job resume／發布、profile讀寫及正式delivery fence須一致。保留CAS／相依隔離、合法缺值預設與未知擴充欄位；不新增角色或重設已接受郵件／案件識別。 | 沿用§8.29 authority12案及formal claim3案、零非法binding／qualification／claim副作用。先逐呼叫端確認同一防護已覆蓋；間接使用或診斷輸出不僅因名稱相同就重驗，真正繞過正式防護者才列必要缺口。不得把case claim證據當成完整SMTP操作驗收。 |

驗收順序：先核四項修正的實際入口及既有證據適用範圍，再補必要的小型接線缺口，最後回掛原父驗收與H。程式、依賴、設定及環境未變的證據直接引用，不因本次規劃文字修改重建或重跑；E01-T01／T02因上一輪Core修正重開的最終候選核對仍保留。大資料、極限payload與受保護副本限制完全不變。清單於`round54TodoReview.items`的既有ID補本節引用，不新增測試工具schema或改完成率分母。

## 5. 再次比對與開工條件

初次盤點：原§5.8共28子項，其中E10-T01已完成、27項未勾。移交當時承接27個原ID，未新增驗收門檻或放寬原契約；2026-10-14另定案的§6權限修正與§4.5清單工具補強已回掛本輪批次。2026-10-13再次比對原§5.8與本文件§3：27個未勾ID集合完全相等、無重複，E10-T01不重開；N1–N8均在§3.1承接，四組實作缺口均回掛原E項。第53輪已補原生狀態碼、冷啟動、耐久ID、郵件工作流及訊息解析版本／舊證據隔離修正；不可將私人候選整份覆蓋回舊版。收尾後核對最終程式基準與新增修正；建立下一輪最終基準、可沿用證據／需重驗差異與範圍後才開工。

第53輪已實作部分已完成收尾；本輪以a69fedd7為起點承接剩餘工作。本次規劃及清單更新不宣告功能完成、驗收通過或PRTG完整實用。功能開工前核對工作區與基準差異，先完成A剩餘資源／成本預檢；既有artifacts／docs/env未追蹤資料不納入提交。

## 6. 已實作的權限分離契約：全域清除與明細閱讀

2026-10-14使用者依審查建議授權更新規劃：保留Maintain執行既有全域PRTG維護的能力；清除預覽中的裝置名稱等業務明細另依既有可見範圍過濾。serverAdmin維持Maintain／ViewAudit、不新增ViewAll，只顯示必要數量及不含業務明細的阻擋原因。目前一般Maintain由Admin取得且同時具ViewAll；不為測試建立受限Maintain正式角色。授權元件仍防禦性地依可見集合過濾；同時具有Maintain與ViewAll者可讀完整預覽。

`RoleCapabilityMap.ForServerAdmin()`維持無業務檢視權；`SettingsController.PreviewPrtgScopePurge()`已經由 `PrtgScopePurgeAuthorizationService`、可信全域集合及 `PreviewOutOfScopeCandidateDetails` 先依可見範圍過濾再取明細，區分全域影響數、可見數及明細受限。此實作待辦刪除，保留以下既定契約與尚未完成的全角色／跨程序驗收；不新增角色或擴張權限。

- 範圍：F批次，回掛E05-T02／E08-T03；包括preview DTO、畫面、直接API及錯誤／阻擋原因，沿用既有授權單一來源，不在Core複製角色規則。
- 輸入／輸出：可信全域清除集合與登入者權限，輸出必要全域維護數量、可見明細及安全原因；全域清除集合與既有安全閘門不因明細過濾改變。不可用先取全域前20筆再遮罩冒充完整可見前20筆。
- 驗收：serverAdmin、具ViewAll的Maintain、具ViewAll但無Maintain、受限且無Maintain四種真實登入情境，實際API／UI核對拒絕或精確可見集合；受限Maintain僅於元件合成測例核對過濾，不冒稱真實角色操作；以不可見裝置名稱／識別作反例，確認欄位、提示與直接回應不外洩。依§4.6的預覽版本、互斥及部分恢復契約執行；相同scope下不同閱讀權的清除候選集合一致；權限不足、範圍不可信或過期狀態仍拒絕操作。清除僅在自有隔離副本驗證。

## 7. 開工前規劃更新紀錄（歷史）

以下保留規劃定案與當時的文件驗證，不代表目前仍未實作。2026-10-15重新核對後的實作缺口以§2為準；已交付及小型驗證以§8為準，尚未完成的驗收以§3為準。

本次修改本PLAN、`scripts/round53-user-matrix/coverage-inventory.json`、`user-matrix.json`及同目錄README，保留原27個未勾ID、22 AC／10 U53／6 Q、7來源情境、8族222案及53條承接缺口，另列8項R54補強、5項C54契約修正、4種真實維護登入情境及1個合成元件防禦測例；不把措辭修正記為功能通過。同步Q1／Q2對照與代理操作責任，保留既有未通過旗標。全部既知缺口及§6調整在本輪處理，不另移待辦或下一輪。尚未執行功能測試、容量或恢復驗收。

前次文件驗證（不涵蓋本次新增R54／維護矩陣）：`Test-UserMatrixManifest.ps1`、`Test-UserMatrixCoverageInventory.ps1`均通過；另逐集合核對全部Q的journey與主矩陣一致、原27個TODO順序及集合不變、53條缺口與未驗收旗標保留。`git diff --check`通過。以上僅為規劃／清單一致性結果，不是A的容量預檢或B～H驗收。

前次八項補強文件驗證（當時5種維護情境分類已由本次C54-01修正）：兩支既有Manifest／CoverageInventory檢查通過；另以獨立結構核對確認R54-01～08共8項／24條必要檢查、5種維護情境、原TODO／AC／journey引用及全部Q對照一致，原27個TODO不變、53條承接缺口保留，新增項皆無通過證據。第54輪版本要求、未實作／未建立角色旗標與字元單位一致，git diff --check通過。此為本次文件審核，未實作或執行新增角色、清除、時區、容量及恢復功能。

本次C54-01～05文件驗證：既有Manifest／CoverageInventory檢查通過；補充核對4種真實登入＋1個合成元件測例分離、不建立帳號／不擴張權限旗標、5項C54至R54與階段對照、全部Q關聯及原27個TODO／53條承接缺口不變。R54及C54均待實作、無通過證據；git diff --check通過。本次僅修改PLAN、兩份JSON與README，未修改功能、fixture或驗證腳本。

本次規劃更新範圍：4項§4.10子契約全部回掛既有C54／R54，同步PLAN、coverage inventory、user matrix即時授權驗收中繼資料與README；正式角色、權限模型及功能程式均未修改。

本次§4.10文件驗證：Manifest及CoverageInventory既有檢查通過；另逐項核對4個補強ID／12條必要檢查、C54反向引用、R54及階段對照、角色即時授權中繼資料一致。原27個TODO、53條承接缺口、六個基本角色與四個真實維護情境／一個合成測例保持；所有新增項均待實作且無通過證據。git diff --check通過，差異僅四份規劃／清單文件。

## 8. 本輪實作紀錄（2026-10-14開始）

### D54-01訊息時間探測與續傳（2026-10-15）

- probe schema 1.1 保存已設定的 Date Format／時區／語系、修訂與 fingerprint，並於既有 classic messages 小樣本查詢核對解析相容性；明示遠端帳號設定未讀回，不當成 qualification 或原始時間基準證明。舊 1.0 證據可讀且明示訊息時間未驗證。
- timeline 每頁請求前重新核對保存授權；授權漂移停止下一頁並清除未提交 cursor／pending prefix，保留既有歷史正式涵蓋。tiny page seam 僅供內部測試，正式頁上限不變。
- 主代理隔離 checkout 獨立測試 5/5＋2/2、零略過；TRX：work/round54-mode-validation/work/d54-probe-root/d54-probe-root.trx 與 work/d54-probe-context-root/d54-probe-context-root.trx。Node 匯出契約與 PowerShell 有界證據 verifier 全部通過。完整來源、容量及最終 UI／成品回歸仍未通過，不勾父驗收。
- 依使用者最新限制，不執行 33,554,432 字元文件極限測試；SQLite incremental BLOB、SQL Server DATALENGTH 與 .NET UTF-16 字元語意以官方文件及 tiny 16/17 單位案例為依據，不宣稱正式上限已實測。

### F2耐久範圍清除與正常入口（2026-10-15）

- 預覽凍結保留／候選 sensor 與原資料水位，同一預覽冪等接受；父子作業保存真實所有權、自動作業不得經人工續跑路由接手，每批前讀取持久帳號／群組／設定授權。小批刪除、游標、精確本作業計數與耐久進度同交易提交；完成才更新基準，安全暫停／停止可讀取部分結果。範圍 writer 共用交易序列鎖與版本 fence。
- UI 以操作者隔離 sessionStorage 的已嘗試預覽／作業 ID 保存未知結果，明確操作才繼續；維護摘要不回傳不可見裝置名稱。鏡像重新整理現在同步重新預覽，避免舊數量與過期權杖留在畫面。
- 主代理此前隔離整合 61/61 中 F2 store/controller/protection 43項通過，零略過；本段沿用該凍結後端。在小型自有 SQLite 網站另以1筆數值＋1筆狀態走正常 login→preview→同識別重送接受→resume→complete→durable GET，結果本作業1/1、剩餘0/0、revision3及 countsReconciled=true，work/round54/browser-small-purge-result.json。
- 真瀏覽器於 /browser-fixture 子路徑核 Admin 非空預覽／取消不提交、零資料停用、serverAdmin 無明細摘要、Manager 無維護入口；額外同一小資料1/1核鏡像刷新從0→1/1，按鈕啟用且 console 無 error。此次 browser frozen backend 與其後單一 static JS 更新各有 fixture manifest／SHA，非最終同HEAD成品；未宣稱完整角色旅程、雙provider／跨程序 G3或父驗收完成。保留所有自有小型資料供重用。

### D1有界資源身分與主機版本標記讀取（2026-10-15）

- 交易內 resource identity 改讀完整有界實體文字，主機 authority／observation 版本標記沿既有批次查詢讀取，拒絕 NUL 後隱藏內容，不以 SQLite length／substring 的合法前綴當完整文件。批次 metadata 保留原 UpdatedAt 供交易內更新的並發檢查；既有 UTF-8 位元組上限不放寬。
- 主代理隔離小型整合8/8、零略過，其中本段5項核 transaction identity、兩個 host markers、原正常重用／8KiB拒絕及 tiny Unicode/NUL/batch metadata；TRX：work/round54-mode-validation/work/round54/mail-identity-root-r5/mail-identity-root-r5.trx。通知其餘3項另段處理，未將未完成通知相容修正納入此提交。其他 reader／writer 遷移仍待核對，不勾 D1 父驗收。
- 文件依據已重新核實：[SQLite length](https://www.sqlite.org/lang_corefunc.html#length) 以 code point 計數且止於 NUL，[incremental BLOB I/O](https://www.sqlite.org/c3ref/blob_open.html) 可讀 TEXT/BLOB；[SQL Server DATALENGTH](https://learn.microsoft.com/en-us/sql/t-sql/functions/datalength-transact-sql?view=sql-server-ver16) 回傳實體位元組，[.NET string 編碼](https://learn.microsoft.com/en-us/dotnet/standard/base-types/character-encoding-introduction) 的 Length 計 UTF-16 char 單位。以文件支持字元模型及 provider adapter；不做正式上限極限負載、不以文件當成程式已實測全容量。

實作起點為`a7f2f2868c76d1af84148e40ad054d03732b1d57`，沿用feature/prtg-feedback-54。使用者授權直到全部完成、逐段驗證後commit／push；前文只規劃／待另行指示均為文件輪邊界，不撤銷本次開工。原起點a69fedd7及全部原TODO／子契約仍保留。主代理負責獨立完整性核對與驗收；委派僅gpt-6-luna low／medium／high，不用AGY。換模型額外背書依UC07，現有可執行審查仍必做。

### A1第一段：只讀容量預檢工具

- 交付：`scripts/round54/Measure-CapacityPreflight.ps1`。讀取固定母體manifest及checkpoint、source DB／sidecar檔案尺寸，報告卷空間、RAM／CPU及線性多副本空間情境；不開啟SQLite、不修改source、拒絕輸出覆寫與來源內寫報告。下一端為E1續跑及G1容量準備。
- 實測：本機25,612,460,032 bytes RAM、8 logical processors；C卷總998,092,824,576 bytes，可用512,202,895,360。既有source checkpoint宣告41,140,900筆，DB46,870,904,832 bytes，線性全量估73,825,186,934 bytes。工作／備份／匯出3份加1份全量WAL的保守情境與25%全卷保留後，差額約-59.6GB；此是資源預警，並非量測到的WAL峰值或容量失敗裁定。必須依實測峰值／錯開副本或合適隔離資源解決，不縮固定母體或保留門檻。
- 來源持久列數／所有權及prefix尚待E1正式audit，工具明列false，不能把checkpoint當完整receipt。報告位於work/round54/a1-capacity-preflight.json。
- SQL只讀事實：現有MSSQLLocalDB為Express Edition 13.0.4001.0／EngineEdition4；不支援本輪完整密度目標，LocalDB小型相容結果不當G4容量PASS。完整SQL須取得適合的自有隔離engine。
- A1仍待source cost及全卷峰值預檢，不勾E06／E07父TODO。本段僅可交付只讀預檢工具及其定向驗證。

### A2第一段：驗收清單與逐檢查證據接線

- 執行者：gpt-6-luna medium；主代理直接檢查diff、initializer／recorder真實消費端並獨立重跑。修改僅scripts/round53-user-matrix相關腳本與README，沒有建立新帳號或變更權限。
- 交付：validator逐R54／C54／4個refinement、固定相依與Q journey對照，核四真實＋一合成角色；initializer輸出全部pending欄位，recorder逐check ID記錄到獨立contract JSONL，不更新aggregate acceptance。
- 主代理驗證：Manifest、CoverageInventory、7個清單否定案例通過；新增owned縮小fixture實際執行initializer→recorder，驗R54／C54／refinement／M54-global／M54-scoped及舊U53路徑。缺／錯CheckId、unknown ID及來源commit不符皆拒絕且不改ledger。此fixture使用明示fake build verifier，只證明記錄接線，不是實際build或browser驗收。
- 首次獨立fixture找出C54單數required接線及PowerShell單字串索引缺陷，修正後相同fixture通過；原失敗不改称PASS。真實登入、serverAdmin及完整瀏覽器／即時撤權流程仍由F2驗；回退工具另段處理。A2父階段尚未全部完成。

### D1第一段：正式mode讀取不再繞過有界快照

- 主代理實作：`PrtgResourcePressureModeStore.Find`走同次有界snapshot，驗所有host/grant/revocation/replay識別；`PrtgResourceProfileQualification.IsCurrentFormalPressure`對該主機超限／JSON損壞返回不合格，避免相依理由授權且保留獨立NetIQ。真實使用端為正式郵件、supplement replay及詳情資格，沒有新增未使用介面。
- 根因反例在f25cadd3隔離checkout：3案中2失敗／1通過，原Find可從超限及混主機文件挑有效grant；修後相同反例加正式資格、mode撤權／formal mail回歸40/40通過，零略過，3分34秒。紅／綠TRX位於主代理projectless work/round54-mode-validation/work/d1-red及d1-green；4個階段檔案逐位元組與實際checkout相同。
- 字元cap正向、cap+1否定、選中grant有效但文件混主機、壞host不影響另一合法host、正常正式pressure→文件超限／損壞後拒絕且NetIQ父理由保留皆已核對。現行PRTG規格同步此事實。
- D1其他binding／qualification／共享disk／data-transfer等讀取及SQL補充字元與修復流程尚待處理；本段不勾D1父階段或E03／E07／E08 TODO，未宣稱完整共享證據隔離。

### D1第二段：binding mutation 內的授權文件有界讀取

- 主代理實作並驗證：Save／RecordQualification 對 settings 及 policy 使用同一原交易內的前綴＋長度 projection，2 Mi 字元上限，保留來源／身分／binding CAS；超限拒絕且不新增 binding version、channel generation 或 profile。使用端為現有 binding 保存及正式 raw qualification，無新增權限或資料模型。
- 隔離 checkout 定向執行 BindingStore 與 RawByteCap 共 12/12 通過，零略過，2分10秒；含 settings／policy × 保存／資格提交四個超限反例及既有正常保存、CAS、qualification、raw byte cap。TRX 位於 work/round54-mode-validation/work/d1-binding/d1-binding-bounded.trx；兩個程式／測試檔與本輪 checkout SHA256 相同。
- 此為 SQLite 小型功能驗證，SQL 補充字元邊界、共享 disk 快照及其他 D1 項目仍未完成，不提升父 TODO 或整輪 acceptance。

### D1第三段：qualification 續跑授權讀取

- 主代理實作 ResumeExisting 在原 overview mutation 交易內以 2 Mi 字元前綴加長度讀 settings／policy，保留原來源、scope、版本及頁面 CAS。超限拒絕，不推進 wave／deadline／overview version 或 page version；無新增資格或自動續跑。
- 獨立隔離 checkout 執行 PrtgQualificationJobStateStoreTests，11/11 通過、零略過，27秒；含兩份授權文件超限及既有正常續跑、取消、到期、初始化水位與 proof attempt。TRX 為 work/round54-mode-validation/work/d1-resume/d1-resume-bounded.trx。此為小型 SQLite 功能證據，未驗 SQL 字元邊界或大量資料。

### B1第一段及D1第四段：訊息timeline授權與磁碟共用證據消費

- B1執行者 gpt-6-luna high，主代理直接核對設定/API/表單/JS→parser→single/batch collector→metadata projection→daily/calibration/silent/replay/mail 的呼叫相依；六格式與官方帳號文件核對。message fingerprint／單調版次獨立於 raw OA，缺設定不發 messages，改格式清訊息派生證據與游標、保留 raw／identity／disk；舊 timeline 無指紋拒絕。UI僅靜態接線／module syntax驗證，未宣稱瀏覽器或現場格式驗收。
- D1主代理實作：兩份disk共享文件公開讀取走完整有界typed snapshot；正式mail/replay核完整paired snapshot及兩份版本，損壞只拒絕其相依channel資格，不能挑有效entry；data-transfer先核metadata、最後核版本，拒絕不完整匯出。正常／混入null record／壞JSON反例使用兩個host與兩顆sensor，拒絕後零寫入。維護修復及全部通知組合的精確隔離尚待下段驗證。
- 主代理隔離checkout整合驗證182/182通過，零略過，6分鐘，TRX work/round54-mode-validation/work/b1-d1/b1-d1-integrated.trx。另前段disk store/consumer 29/29通過，22秒；B1委派首次151/152中游標fixture缺指紋失敗，修正後單案1/1通過，再由主代理全範圍定向重驗182案。保留原失敗，不將未執行容量測試改稱PASS。
- 最後補齊公開Get/GetAll/GetRecent拒絕反例後，同一兩host fixture新binary重驗5/5通過、零略過、29秒，TRX work/round54-mode-validation/work/d1-disk-accessors/d1-disk-accessors.trx。JS首次CommonJS syntax命令因ES module模式錯誤未通過；改用node --input-type=module --check後通過，此仍僅語法檢查。
- 仍待：FetchStateChangesRangeAsync、正式probe/離線鏈與finding→signature→DB→pending/replay/mail fingerprint；current timeline gate不能防止舊finding被新timeline追認，B2尚未完成。主代理另確認resource period consumer的mode損壞可能中斷整批，依C54-02待補逐host等待；共享disk相關等待/維護修復及SQL字元相容也未完成。原父TODO維持未勾。

### A2回退第一段：SQLite配套隔離還原與啟動前防護

- 委派 gpt-6-luna high，主代理審查實際layout、startup integrations及process/listener相依。首次候選錯放DB/key且只驗fake binary，已修成產品data/Db及data/keys，核所有schedule/local-analysis、mail、PRTG、AI、AD與sentinel停用。主代理再修actual command substring比對為完整Windows argv token並核真正listener PID；拒絕時不啟動，啟動後身分不符停止該owned PID。
- 交付 scripts/round54/rollback 的 prepare→verify→launch→stop；固定operator-reviewed full commit及authority SHA，核Web/Core/assets/config/DB/key/schema與封存差額精確清單。seal/hash只偵測相對pinned bytes變更，不是作者身分或簽章。拒絕同庫直接降版；差額保持held、不盲目重寄。
- 主代理獨立13/13 unit通過（4.11秒，無skip），再以已核Verify-BuildIdentity的真實舊版638c58434f0bc01fe77e62365addeafd75680fb0執行完整CLI，/login 200，815,104-byte自有SQLite少量case/reply/task/mail/sentinel逐列及blob完全不變、external TCP trap=0、source DB/key與release tree SHA完全不變；PID15376已停止並查無process。報告work/round54/rollback-real-root.json、命令log同目錄。沒有使用正式DB/key或完整母體。
- 仍待：正式release/bundle consumer接線、差額reconcile與SMTP去重解除hold；SQL Server相容回退未知即拒絕，尚未做其正式還原驗收。此段不宣稱無損回退、全A2/B2/G/H或整輪PASS。

### D1第五段：資源期間引擎逐host mode隔離

- 主代理修正兩個正常consumer：期間引擎的host快照讀取及authorization snapshot讀取遇超限／壞JSON，不再擲出中斷整批；該host以負版本拒絕正式發布，回明確formal-mode-document-unavailable等待，不將缺mode當健康／正常關閉。historical authorization同用此防護；合法其他host繼續。
- 小型SQLite：兩host各一sensor、各兩個可信hour；先確認兩者正式finding，再損壞其中一個mode，精確核另一host保留唯一formal finding、壞hostInsufficient／版本-1、壞文件零寫入。ASCII超限與壞JSON兩反例＋既有trial/restart/one-use/rule變動回歸共10/10通過、零略過、47秒，TRX work/round54-mode-validation/work/d1-mode-consumer/d1-mode-consumer-stable-fixture.trx。
- 原fixture建立第二資源時意外改ValidFrom／settings revision，先導致兩個否定案例在正式baseline前失敗；修成共享既有來源契約後重驗通過，沒有放寬production authority。首次fixture還有以finding.HostId存取不存在欄位的compile錯誤，已改核SensorObjid。全部失敗log保留；此段不替代完整mail組合、disk共用等待、維護修復或SQL邊界驗收。

### D1第六段：mode維護寫入與未知欄位

- mode停用、trial保存／移除及consume維持原交易，先由DB投影cap+1字元檢查再讀完整row；補掉停用及繼承Update的無界讀取。此文件採262,144字元限制，UTF-8 bytes不再誤當相同字元上限；其他原有byte-bound呼叫保持原契約。
- JSON document、grant、revocation及replay job以extension data保留未知欄位，維護已知欄位不靜默丟棄未認識內容。小型SQLite測例核90,000中文字（字元合格但UTF-8超262,144 bytes）的正式mode停用、未知document／grant欄位保留，以及超限停用／trial保存拒絕且content/version不變；既有atomic revocation回歸合計20/20通過、零skip、22秒，TRX work/round54-mode-validation/work/d1-character-mutation/d1-character-mutation.trx。
- 尚未替代SQL Server實際邊界核對、超限／損壞文件管理修復入口及完整shared snapshot維護驗收。

### D1第七段：完整有界文字與共用authority readset

- 有界blob讀取改以UTF-16單元定義字元契約。SQLite使用既有Microsoft.Data.Sqlite 8.0的readonly SqliteBlob：先取得物理size，依資料庫UTF-8／UTF-16編碼確認有限byte budget，再分段解碼至cap+1；原context／transaction及blob version保留。SQL Server以NVARCHAR DATALENGTH的2×char budget先拒絕超限，再回完整合格內容。NUL、壞encoding或不完整文件均拒絕，不能信任截斷JSON前綴；既有byte-bound呼叫另保留原UTF-8限制。
- 官方依據：[SQLite length／substr](https://www.sqlite.org/lang_corefunc.html)按codepoint計數，length(TEXT)停在NUL並需讀取整個文字；[.NET文字編碼](https://learn.microsoft.com/en-us/dotnet/standard/base-types/character-encoding-introduction)的String.Length是UTF-16單元；[SQL LEN](https://learn.microsoft.com/en-us/sql/t-sql/functions/len-transact-sql?view=sql-server-ver16)忽略尾空白、[SC SUBSTRING](https://learn.microsoft.com/en-us/sql/t-sql/functions/substring-transact-sql?view=sql-server-ver16)以surrogate pair計數，故不能直接假設兩provider相同。[Microsoft SQLite streaming](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/blob-io)與[SQLite readonly incremental I/O](https://www.sqlite.org/c3ref/blob_open.html)支援保留目前儲存結構的有界讀取，沒有升級套件或新增儲存拆分。
- 主代理tiny16／17字元的ASCII、尾空白、中文、supplementary及NUL反例：SQLite7/7，SQL Server重用自有LocalDB7/7，content/version不變；work/round54/sql-character-parity/provider-small-physical-result.json。共用settings／policy新增原read transaction兩份完整有界snapshot及versions，bad JSON／超限回shared-authority-document-unavailable；與mode定向回歸17/17（11秒），binding原交易／qualification續跑22/22（77秒）。TRX於work/round54-mode-validation/work/d1-physical-*。
- 本段未替代其餘直接批次projection的完整性接線、超限／壞文件維護修復及最終同版consumer驗收；共用snapshot正常mail／replay接線由B2整段驗證。

### E1第一段：初始化、升級與前綴安全恢復

- 已補初始化marker／checkpoint中斷恢復、legacy adoption完整結構與deterministic prefix稽核、owned tool upgrade intent與四個提交邊界恢復，以及有界4096字元log scanner。初始值表非空或自增序號已使用即拒絕；沒有checkpoint時須先由原工具恢復，不能跨版直接生成。結構marker已提交時保持既有結構及時間戳。
- 使用者授權第二次固定結構後，保留並重用work/e1-generator-bounded-second-20261014；主代理最後獨立12步驟通過，包括初始跨版拒絕、原工具checkpoint恢復、四升級中斷、非零初始序號拒絕、一批100列停止、值／結構／foreign marker竄改拒絕，再新程序完成205列。結構三表SHA不變、synthetic fixture還原完全相同persistent bytes、新增structure seed=0；work/round54/e1-root-reused-report.json及e1-root-reused-final-console.log。
- 測試合成fixture曾刪值但遺留AUTOINCREMENT序號，導致後續prefix恢復失敗；已明確記錄fixture-only sequence reset，正式工具僅拒絕而不修資料。另修測試WAL完整view還原、volatile SHM比較及result檔名；歷次失敗保留。主代理以隔離現行Core建置generator成功0警告／0錯誤；兼容凍結Core的恢復驗證不作正式release來源身份證據。
- 仍待最終乾淨同版交付核對、正常E2資格準備及全量G驗收；未開啟既有41.14M DB、未全掃／續跑、未生成64.8M，各capacity／fullWorkload／wholeRound旗標維持false。

### 開發驗證限制更新：只執行分段小範圍（2026-10-14最新指示）

使用者明確要求開發期間不要大規模大量資料測試，優先分段小範圍驗正確性；不得已必須先討論可行方案。本條約束所有階段、委派與舊驗收引用。持續完成各項程式／腳本／正常consumer接線，使用少量主機／sensor／列、可控批次、故障注入與精確預期，並逐段commit／push。

使用者再次定案：不做33,554,432字元磁碟共用文件的極限測試，改查官方或技術文件確認語意、配合縮小功能驗證及程式檢查。常數不調小／不提高；文獻型依據與小資料證據分別記錄，不宣稱曾做該極限實測或資源峰值量測。後續不得因舊清單提到cap而自動啟動巨大payload測試。

完整64,800,000生成、既有41.14M前綴全掃／續跑、3,000／15,000共同負載與高水位恢復、原三對六輪／1200路由壓測以及其他大量資料操作，現階段不執行。G等既定門檻保留但標為待討論執行方案／未驗，不刪母體、不降低門檻、不把小fixtures或工具能力宣稱容量／整輪PASS。若某段必要驗證無法縮小，先列最小資料量、耗時／空間、隔離與清理方式、替代方案及仍無法證明的範圍，再討論；未取得定案不啟動該大量驗證，其他可做工作繼續。

本次限制不改功能契約或取消實作授權；整體完成裁定須分清全部實作完整性與仍未執行的大量驗收。當前已執行的D1 40案及A2縮小fixture屬分段功能驗證；A1僅讀metadata／檔案尺寸，沒有打開既有巨大SQLite或續跑。

### D1第八段：有界更新版本與交易前置防護

- 有界更新的完整列讀取綁定預讀 version；版本改變時走既有並發重試，避免 SQL Server ReadCommitted 下檢查舊版後讀取另一份超限文件。新增交易內、所有文件讀取前的 beforeRead hook，供 F2 範圍鎖統一順序使用；沒有新增權限。
- 主代理隔離 checkout 定向 physical read 8/8 通過（5 秒、零略過），含 tiny 16/17 字元及前置防護改寫超限後拒絕、原交易回滾、內容及 version 不變。TRX：work/round54-mode-validation/work/d1-bounded-fence/bounded-fence.trx。本段尚不代表 F2 完整工作流驗收。
- 依使用者最新指示，33,554,432 字元極限內容不建立、不測量；字元／provider 邊界依官方文件與小型反例核對。其餘直接批次 projection、維護修復及同版交付仍待完成，父階段維持未勾。

### C1後端接線：正常磁碟語意核驗

- 正常 controller→service→來源／主頻道／historicdata→結果與語意證據使用保存的來源 API、raw OA 與分析時區及精確 profile 合約；canonical 資源指紋一致。單日 DST 23／25 小時區間不依本機時區；歧義端點／樣本、authority 缺少／漂移保持等待或失效。比較樣本在 SQL 依品質、時間及 ID 排序只投影最多 5 筆時間／數值，不宣稱整日涵蓋。
- 主代理檢查完整呼叫鏈並在隔離 checkout 獨立執行 Probe／VerificationService 60/60 通過、零略過（59 秒）；work/round54-mode-validation/work/c1-root/c1-root.trx。含正常入口取得 Verified、DST、malformed policy、來源漂移及人工證據失效反例。測試使用獨立 request budget，未證正式 shared historic SQL admission；production guard 未變。
- 後端回應已完成分析日期／時區供 F1 UI 使用；UI 尚未完成瀏覽器核驗，不勾 C1／E03／E08 父驗收或整輪完成。

### B2接線段：已保存訊息時間指紋到正式消費端

- range 取數依保存的 strict 格式／fingerprint，請求與頁提交核當前 authority；未配置格式只等待文字 messages，保留獨立數值。daily／歷史回填／snapshot queue 接入保存政策。finding→signature→SQL TopIssue→pending/replay/mail 保留原 fingerprint，nullable 新欄不回填舊授權。
- 正式通知逐 issue 排除過期訊息／raw 證據，保留同主機合法 NetIQ、有效 message 及有效 raw disk trend；送達事實只記各實際收件者都收到的 issue。paired shared authority 及版本在重查前後核對；explicit-null policy 當不可用，保持歷史與其他 lane。人工歷史案件／回覆不改寫，缺訊息指紋的舊證據標示 legacy-message-time-unverified。
- 主代理讀取實際 diff／producer／persisted projection／consumer，隔離 checkout 獨立 42/42、零略過（1分50秒），含真 SQL TopIssue readback、受控 SMTP 混合 lane、range authority drift、replay、case history 及 5 個 tiny null authority 反例。work/round54-mode-validation/work/b2-root/b2-root.trx；補 daily provider 後 Web build 0 errors，16 個既有 nullable／captured-field warnings。
- 此段取代前文 B1 紀錄的 range／finding→SQL／replay／mail 待接線項；正式 Date Format 樣本 probe／離線證據與 tiny 多頁 cursor lifecycle 仍在本輪補齊，A2差額／通知去重回退、報告／匯出最終生命週期矩陣及整輪驗收仍未通過。未把 account format 設定當成已從遠端讀回的事實。

### D1第九段：批次完整有界讀取與正式派送閘

- 新批次讀取以每頁最多 200 個 key 查 metadata；SQLite 於同一 read transaction 逐文件使用原生實體大小與嚴格 decoding，SQL Server 以 DATALENGTH 決定是否回完整 NVARCHAR。缺少、超限、NUL、非法 encoding 均不冒充截斷後的合法 JSON。binding GetMany 已接通，維持原每份 byte 限制；不改為每個 key 一次 SQL 查詢。
- 正式派送的 mode 讀取改完整字元限制，避免中文被誤套等值 byte cap；相依 authority blob 同樣使用完整有界讀取。settings／policy 上限與 shared authority 的 2 Mi 字元一致，排除原 policy 64 KiB 閘可能拒絕正常 15,000 sensor scope 的矛盾。
- 主代理隔離 checkout 小範圍 31/31（68秒）與正式郵件開始／撤銷／未知SMTP重試四例 4/4（48秒），零略過。首輪四例因 fixture 在 Accept 後才安裝訊息 fingerprint 而失敗，改為先安裝已保存 authority；未放寬正式檢查。TRX：work/round54-mode-validation/work/d1-batch-root/d1-batch-root.trx、work/d1-formal-fence-r2/d1-formal-fence-r2.trx。
- 重用自有 LocalDB，以每筆最多 17 UTF-16 units 驗 7 個實體讀取及 3 份 batch 文件／missing key，正常／中文 supplementary／NUL 拒絕、內容不變均通過；work/round54/sql-character-parity/provider-small-batch-result.json。前兩次在5秒連線預算的 post-login 階段逾時，保留 log；確認既有 LocalDB 運行後把此測試連線預算設為30秒，未重啟服務或建立新DB。未建立33M文件或做容量壓測。
- 技術依據：SQLite [length](https://www.sqlite.org/lang_corefunc.html) 按codepoint且在NUL前終止，TEXT計長需載入；[incremental blob IO](https://www.sqlite.org/c3ref/blob_open.html)可唯讀TEXT。Microsoft [.NET UTF-16](https://learn.microsoft.com/en-us/dotnet/standard/base-types/character-encoding-introduction)與[SQL DATALENGTH](https://learn.microsoft.com/en-us/sql/t-sql/functions/datalength-transact-sql?view=sql-server-ver16)支持目前單位核對。官方規則與小反例沒有被記為33M極限實測。
- 其他直接批次 projection、維護修復、全生命週期 consumer 與整輪驗收仍待完成。

### D1第十段：通知／重播重用完整磁碟 readset

- 每個通知／重播 operation 延遲擷取完整 semantic＋verification pair一次，逐sensor重用已解析文件並重查兩份 durable version；任一版本漂移拒絕該readset，下一operation才重新擷取。無法解析任一entry則整份pair不可用，不能跳過壞entry，也不能為每個issue重載兩份多主機共用文件。
- 主代理隔離checkout整合61/61、零略過（3分19秒），其中readset／mixed SMTP／replay18項及F2 store/controller/protection43項。新tiny單sensor反例核重用正常pair、version變動即拒、fresh pair可重新核對、另一entry為null則全部拒且內容不變；work/round54-mode-validation/work/f2-d1-root/f2-d1-root.trx。F2另待UI／browser及提交完整相依，本段不宣稱F2父驗收完成。
- 主代理開發時Lazy declaration先放錯其他mail closure，造成main Web編譯CS0103；已移到urgent Recheck／CurrentResource共同範圍，隔離整合通過。失敗輸出保留，不列為成功證據。

### A2原生回覆差額與郵件隔離接線（2026-10-15）

- 回退差額只接受同 schema、完整原生 Reply 相依圖；逐列前像 CAS、actor、父子關係、逐日與歷程一致性皆核對，保留 .NET 七位小數時間精度。單一回覆以外、未知差額、實體刪除與新清除工作表維持拒絕或 held。正式同步門檻仍為 5,000；內部測試門檻 2 只用三筆逐日列驗 pending intent 與真正消費端。
- 主代理核對原生 SQLite before/after、恢復 DB 與 after-backup 六筆精確回讀、四份啟動保護設定、actor 竄改及一 tick 時間反例；pending intent 真正消費後為三筆逐日列與三筆歷程，來源快照雜湊未改。證據 `work/round54/a2-root/root-native-result.json`。實際 CLI 拒絕以共用 Refusal 型別回傳 structured refused-closed／exit 2。
- 新增郵件貢獻帳本對舊版不透明時，完整保留前後原始 JSON 並持續 hold 郵件放行；資料回復另行裁定。主代理獨立 Python 小型套件 32/32 通過（零略過，32 秒）。這些證據不代表 SQL Server 回復、完整容量、正式資料無損回退或整輪驗收通過，A2／G／H 父項維持未勾。

### E2執行中 ownership 正常端點（2026-10-15）

- 新增既有 Maintain 能力下、只接受直接 loopback 的 runtime ownership 查詢；回傳實際 PID／啟動 UTC ticks、DataRoot、SQLite DB 路徑與已載入 Web／Core MVID／路徑，沒有連線字串或憑證。未配置 descriptor hash、轉送來源、非檔案 SQLite 或無實體 assembly 均拒絕；descriptor hash 只作比對 pin，不自行等同來源資格。
- 主代理兩次獨立定向檢查均為 4/4（包含於 `root-timebasis-owner-mail-r1` 與隔離 `root-authority-consumers-r5`），並以自有約 1.5MiB fixture 的正常登入 HTTP 核對回應與真實行程／已載入 DLL／資料路徑。HTTP receipt：`work/round54/browser-timebasis-normal-bootstrap-result.json`。fixture 已停止並保留；未啟動完整容量流程。
- 資格準備 launcher／獨立磁碟校驗工具仍屬下一接線段；本端點完成不代表完整 E2 準備、來源資格或同 HEAD 整輪驗收通過。

### D1正式消費端與實作待辦核對（2026-10-15）

- `PrtgSilentPresenceSnapshotStore` 的保存／批次讀取改用完整物理有界讀取，保留64KiB／512 sensors／50 keys限制及單筆失敗隔離；過期清理只取key後刪除。`PrtgProbeLocalFactsCollector` 的policy／binding／profile／identity／strategy全部走既有bounded reader，保留原有樣本／UTF8上限與profile逐筆排除。
- `PrtgFormalMailClaimStore` 在原交易內讀取有界完整shard，從snapshot attach並保留Version／UpdatedAt並發原值；不先載入另一份未核版本全文。prune retry重新有界核對同keyset，只認列精確匹配的已提交結果。額外重現並修正保留期bug：仍保留的smtp-accepted claim必須繼續保有其fence，直到引用claim本身到期，否則清理後ValidateShard會失敗。
- 主代理獨立小型結果：local facts 6案通過（`root-authority-consumers-r5`子集）；silent批次NUL／過期清理2/2（`silent-bounded-consumer-r6`），原生HTTP來源至正式finding正向通過（`formal-claim-silent-r8`子集）；claim NUL／小cap／Local不一致3案通過，保留期正向修正後1/1（`claim-retention-r9`）。全部零略過，fixture最多兩sensor／單shard／單claim+fence；NUL以byte[] CAST寫入並核實實際bytes，避免provider字串截尾形成假反例。編譯錯誤與修正前失敗receipt保留，不冒稱曾整批全綠。
- 依使用者新指令逐PLAN對照實際producer／consumer，已完成的原始實作待辦從§2刪除並修正§4、§6的陳舊現況；尚缺的背景raw工作隔離、郵件原子receipt／正文範圍、其餘D1consumer及中斷候選仍明列。§3保留未執行的驗收／交付，不把小型程式驗證當成G／H通過。

### 實作與驗收清單分離核對（2026-10-15）

- R54八項、C54五項與四項執行補強共17項，分別記錄 `implementationStatus`、實際 `sourceReferences`、`remainingImplementation` 與 `remainingAcceptance`；原必要檢查、ID、AC／journey／Q關係不刪除。已實作項的實作待辦留空，仍未通過的驗收維持 `not-accepted`，不再一律誤寫 `planned-not-implemented`。
- validator核對檔案與精確source locator、禁止有已知程式缺口卻標實作完成，且逐字保留原驗收檢查。主代理獨立CoverageInventory及A2 baseline＋12個否定案例通過；22 AC／10 journeys／6 Q／7來源情境／222案／53承接缺口及原27責任索引不變。這是清單一致性證據，不是17項功能全通過。
- 四種維護情境的真登入fixture接線另行補齊；仍保留原六角色與一個合成防禦測例，未實際登入前不宣稱browser accepted。

### 8.8 回退郵件 reservation 相容防護（2026-10-15）

回退工具將新的 `MailContributionReservations` 與兩份 contribution ledger 一同視為舊版不可理解的耐久證據。完整原文保留於還原及備份，資料可獨立釋放，郵件保持 held，避免未知 SMTP 結果被舊版重送。主代理只跑自有小型 `test_nonempty_new_contribution_ledgers_preserve_raw_state_and_hold_only_mail`，1/1 通過；不代表 SQL Server／全量恢復驗收。

### 8.9 Profile-refresh 有界狀態接線（2026-10-15）

`PrtgTrustedSamplingProfileRefreshStateStore` 的 notice／policy／SavePage／EarliestNextAttempt 均採完整 physical bounded reader；SavePage 在 Serializable 交易以相同 bounded snapshot 附加 EF 追蹤列，核對 Version／UpdatedAt 及既存 local state，不再有界預讀後無條件全讀。沿用原頁面／notice／overview 上限及 lease；policy 對齊共用 2Mi UTF-16 契約。主代理在隔離 checkout 的 `root-interrupted-candidates-r13` 六個小型 profile-refresh 案例通過（1sensor正常往返、真實 NUL頁面拒絕、lease過期／owner/version與缺頁），不執行既有150頁×100 sensors案例。raw notice 因 Date Format-only取消的 D54-01 接線仍未完成，不因此移除該待辦。

### 8.10 匯出／校準 consumer 有界讀取（2026-10-15）

Web診斷匯出 policy 與 Core timeline export、CalibrationService timeline/policy 讀取已改用完整 physical bounded snapshot，沿用現有交易／版本／取消檢查與字元上限，拒絕 NUL 隱藏尾端，不把有效前綴當完整證據。校準按日建立的 bounded timeline 副本補傳 `MessageTimeFingerprint`，避免合法新格式證據被清空。主代理小型 export 正／反例2/2通過（`root-mail-export-calibration-r14`），校準最終正／反例3/3通過（`root-calibration-authority-r15`）；r14先前校準正例失敗保留，已由r15驗證修正。各例1sensor／少量state，無極限payload；這些已完成實作從§2.3刪除，匯出吞吐及完整校準操作驗收仍保留。

### 8.11 Mode replay progress 有界 mutation（2026-10-15）

`PrtgResourcePressureModeReplayProgressStore` 的 Update／SetNextJobOffset 與 host cursor Set 均在原交易中先讀完整有界文件；維持既有 progress 128Ki、cursor 512 UTF-16字元與輸出上限。舊文件若有 NUL／損壞不會被直接覆寫。主代理檢查實際 store 與正式 consumer，隔離 checkout 的 `root-bounded-diagnostics-replay-manual-r17` 三個小型 replay progress／cursor測例通過（正常往返及兩種 physical NUL，原 bytes／Version 不變）。不代表 replay 的 raw/source 語意相依隔離已完成；該 D54-01 待辦保留。

### 8.12 快照診斷文件的有界讀寫（2026-10-15）

`PrtgSnapshotDiagnosticsStore` 的 ReadHours／skip cache初始化／Record mutation 改用完整 physical bounded reader；診斷文件上限4Mi UTF-16字元，單原因4096字元，14日cutoff連同當日最多15個日曆日／360個不同wall hour，拒絕重複小時、JSON null、壞文件及寫入超限。這是 best-effort診斷的資源上限，既有 SnapshotHostedService 仍攔截診斷失敗，不將其當業務成功或取消獨立採樣。主代理 `r17` 既有四個小型sample正例與 physical NUL反例通過；`root-bounded-state-mail-r19` 以明確 InvalidDataException 斷言重驗NUL拒寫、原Version及完整原文不變。未建立4Mi上限payload。此實作從§2.3待辦移除。


### 8.13 郵件正文貢獻與 SMTP 未知結果（2026-10-15）

summary／urgent／daily／weekly 逐收件者重驗目前可見及有效證據，同一 rendered projection 決定正文、截斷範圍、接受帳本及 formal start claim。超過正文上限的問題保持待送，下次正常 Notify／digest 續送，不能提前標記整個父紀錄完成。保留既有 context key 及 legacy 接受紀錄。

原 Serializable 交易先保留精確 lane／收件者／host-day／父決策／issue JSON 貢獻，接受、legacy held 及 reservation 合計最多12,000 key，mail state 完整 physical bounded read／mutation 上限4Mi UTF-16字元。只有尚未嘗試的 prepared reservation 可在5分鐘後釋放；attempted／unknown 不會因時間經過自動重寄。SMTP 成功才轉接受，明確單收件者拒收釋放，IO／逾時／取消保持未知；重建服務仍持有原 intent。

正式 sender 只在已知單收件者 envelope 且收到明確4xx／5xx回覆時產生限定拒收證據，不把任意一般例外冒稱明確拒收。官方 .NET 8 [SmtpClient](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Net.Mail/src/System/Net/Mail/SmtpClient.cs)、[SmtpCommands](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Net.Mail/src/System/Net/Mail/SmtpCommands.cs) 與 [RFC5321 §4.2.5](https://www.rfc-editor.org/rfc/rfc5321#section-4.2.5) 支持：同步 Send 等候 final DATA 回覆，QUIT 不讀取後續回覆。未知結果不釋放 reservation。

主代理核對 store→sender→outcome→workflow／formal 接線。小範圍證據：r19 mixed lane、實際 Notify receiver 及三種正文截斷；root-smtp-definite-refusal-r23 真實 loopback SMTP 三例通過；root-smtp-unknown-raw-rollback-r24 direct sender 遺失ACK／取消及 raw overflow 四例通過；root-smtp-unknown-r25 正常 mail service 遺失ACK後重建服務不重送一例通過；root-mail-reservations-digest-r26 原子保留／拒收及11個小型 digest 例共13例通過。各例少量主機／問題／數值，無極限文件。r20～r22初次451分類失敗及r24單收件者案例錯用雙收件者 fixture 的失敗保留，不列成功。

此段從§2刪除實作待辦；R54-05 replay／qualification 等 raw 語意相依及跨consumer完整驗收仍保留，不因此勾選 B2／F3／G／H 父項。


### 8.14 Raw 單調版本與嚴格有界 authority 後端（2026-10-15）

Policy 三個正式 writer 以同一交易舊 raw/source tuple 管理 RawAuthorityRevision，只有 raw/source 時基變更遞增；DateFormat／message revision／選取範圍不改此版本，負值與 overflow 拒寫。revision=0 保留舊 fingerprint，正值接入 SourceAuthority／Strategy／AuthorityContext，改後改回不能恢復舊 proof。時間授權儲存與既有受影響磁碟證據失效同交易，未知欄位保留。這是後端相依交付，時間設定頁與各 raw worker 接線另段核對。

Mode／共用 disk authority 在 typed deserialize 前拒絕重複 JSON key、已知欄位大小寫別名及非 canonical sensor key；不部分信任壞 entry。共用文件有界讀寫保留未知欄位。修復 store／Maintain API 提供固定預算、16 edits／每 edit 4096字元、CAS／刪除文字 SHA 防護；語法修復使相依 authority 失效。serverAdmin 只能必要統計，明細需既有 Maintain＋ViewAll，不新增角色。修復後舊／未重新取得的 probe 不能人工確認復活；正常 probe 才解除屏障。瀏覽器與完整角色操作仍待驗。

Episode／hint 上限各2Mi、timeline／strategy 各8Ki UTF-16字元，正常 producer／直接 mutation 及 CAS 使用完整 physical bounded read，同交易核對並檢查輸出，壞文件不覆寫。上限由既有欄位／128項結構與 JSON escaping 計算；不建立上限 payload。Diagnostic export 在 SQL 分頁前先完整有界驗共用 authority 的 JSON 不歧義，不保留完整 typed dictionary。

主代理分段證據：r28 strict九例／repair六例／四種state六例共21例通過；該輪另有一個replay fixture等待案例失敗，另段修正，不算本段成功；r5／r6舊結果保留；r16 resolver拒絕 ABA 舊 profile，r17正常人工確認；r19四種 state六例；r24 overflow連同已讀磁碟證據版本／原文同交易回滾；r21共用 export valid／重複key／別名／physical NUL 四例。舊失敗結果保留，雙provider正式上限依官方文件與已留存tiny字元反例核對，未做33M極限測試。完整角色、容量及最終成品驗收不因此通過。


### 8.15 Replay 提交前 raw 防護與長期作業 scope（2026-10-15）

Mode replay 將當次 raw SourceAuthority fingerprint 及 settings revision 帶至 reconciliation batch；EfAnalysisRecordStore 在原 Serializable attach 交易、任何 finding／manifest／reconcile 副作用之前，完整有界重讀 settings／policy，核對 enabled／Ready、settings revision、主機／sensor選取及 raw fingerprint。raw/source A→B→A、設定及 sensor移除競爭均不寫 finding／manifest、不完成進度；DateFormat-only 可完成 finding→報告→案件及 replay progress。正式 case／mail start 保留 parent、mode grant、identity、scope、settings 及實際 profile resolver 的 RawAuthorityRevision context，只移除多餘的長期整份 PolicyRevision 相等要求，仍要求目前 revision 非空。

Snapshot operation 與兩個 backfill operation 改用 ReadRawSemantic（結構scope＋raw/source tuple／單調版本＋host/sensor membership）。原 Read／ReadStructural 演算法不改，避免改動既有 mail context／接受 key；原預覽、持久 queue 及明確請求 CAS 保留。PrtgOperationScope 的 settings 比對保留，混合回填的 message path 仍以獨立目前 message fingerprint 驗證。

主代理實查 producer→batch→交易→實際後續consumer；root-raw-replay-scope-r29 八例全通過、零略過：正常日期格式變更 replay一例、raw ABA／settings／sensor範圍反例三例、formal start日期格式／raw ABA兩例及兩個小型scope測例。每個 replay fixture 單host／sensor共28筆值。r27／r28失敗保留：小型fixture未採用Upsert實際HostId造成後續報告找不到主機，已修正且r29確認完成，不以放寬進度斷言掩蓋。qualification／refresh／discovery／capacity相依候選與正常來源完整操作仍另段驗，不勾R54-05父驗收。


### 8.16 重啟後保留未知 SMTP 狀態（2026-10-15）

補查舊 protocol consumer 發現：durable reservation 已禁止未知結果重送，但 urgent 初始化會把既有 failed-or-unknown／sending-result-unknown 改成 pending，最後顯示 not-sent。已保留未知／已接受／已撤回狀態，只有真正新收件者才初始化pending。舊 protocol fixture 補齊保存的 message authority、相同 finding／timeline fingerprint 及實際 digest，再按本輪契約核對不重送；沒有移除或跳過該反例。主代理 root-smtp-scope-identity-r31 的兩項實際 loopback SMTP 重啟案例通過（正常 mail service CPU 與 message protocol），保留 pending及精確原intent，recipient仍failed-or-unknown、只一份DATA。r31另兩個快照fixture因缺保存日期／raw時基而未到HTTP，已補為有效設定，root-qualification-attach-snapshot-r32確認來源／scope HTTP變動時均正確取消且queue保留；不把r31整套宣稱全過。


### 8.17 回退差額輸入格式拒絕（2026-10-15）

capture-delta 的唯讀 quick_check 若遇非SQLite bytes，現在關閉連線並轉為共用 Refusal，CLI一致回傳 refused-closed JSON及exit2，不產生差額輸出、不改輸入。主代理先以21-byte反例重現原exit1 traceback，再驗非SQLite、錯SQLite application identity及正常CLI差額→消費→release gate三例全過。prepare／verify／launch仍只允許已pin的SQLite配套隔離副本；SQL Server未知相容性持續拒絕，不啟動或連接其provider。這不是SQL Server恢復、全容量或G3/G4通過的證據，原條款與驗收待辦保留。

### 8.18 資格作業、容量與有界 mutation 的相依交付（2026-10-15）

Qualification、profile refresh notice、channel discovery 與 capacity admission 長期工作改用含 RawAuthorityRevision 的 raw context；保存／發布／重綁的當次請求仍核對目前 settings／policy CAS。資格結果在同一 Serializable 交易重讀完整有界 settings、policy、canonical host/sensor scope，拒絕 raw ABA、來源／範圍變更及已停用來源後才可更新 binding proof／identity／job page。正式 profile publisher 同樣比較完整 raw context，移除多餘的歷史 BindingPolicyRevision 相等要求。舊 capacity plan 缺 raw context 不能沿用。

Identity／binding／qualification／queue／timeline／profile mutation 直接 attach 完整 physical bounded snapshot，保留原 Version／UpdatedAt concurrency token，不在有界檢查後重新讀取無界 Content。新資格頁持久化 qualifiedIdentityEpoch／qualifiedChannelGeneration，讓正常 HTTP 準備工具識別資格提交造成的合法 channel rotation；ReadStateVersion 是 overview 實體版本，逐頁前後同時核對它及 lease version／wave，避免 lease 未變但提交中的 proof 頁被混讀。舊缺欄位頁面不能追認新證據。

正常 monitoring PUT 及獨立 time-basis PUT 明確保存 raw／analysis zone／reference；來源啟用時拒絕時間變更，保存設定及受影響 disk 失效同交易。操作者輸入只標 saved-operator-input-unverified，不當作遠端帳號或 native 資格證明。binding 輸入需與目前保存時間基準完全一致。

主代理小型分段證據：r30 capacity concurrency／retry／job store18例及 time-basis／binding6例通過；r31 identity五例通過；r32 attach snapshot兩例及資格三例通過；r33資格／capacity／refresh／discovery共11例通過；r34 physical Unicode／NUL四例及 permission／disabled／bounded input三例通過；r36真實 profile publisher 日期格式正例／raw ABA負例兩例通過。均使用單／少量 host、sensor 及值；UTF-8 byte[] CAST AS TEXT 建立 physical NUL，避免字串參數截斷造成假陽性。r27／r30舊失敗、r35 publisher 額外 policy revision 阻擋的重現保留，不宣稱整套失敗回合全過。後續補查期間／磁碟／資料流／資格作業提示四處接線仍另段列明，正式來源、全角色、完整容量及父驗收未通過。

### 8.19 E2 正常 HTTP 資格準備工具（2026-10-15）

qualification-preparation 提供來源與磁碟預檢、live runtime owner 確認、正常 settings／source policy／pilot／binding／qualification job 的 Start／Resume／Check 及 durable receipt。固定720h／每顆一次 attempt，沒有縮小完整母體的正式 CLI 開關；foreign listener、reparse、PID／ticks／MVID／DataRoot／descriptor 不符在寫入前拒絕。未知 start 結果只以正常 read-only current／page 與精確 job／version／wave 對帳。

主代理核對腳本的真正 public API consumer、原 sealed manifest 及 receipt CAS。Resume／Check 不重寫 manifest 或政策；DateFormat-only 可續查，raw／source／scope／settings 漂移拒絕。資格帶來的 channel rotation 必須由已提交後端的 qualifiedIdentityEpoch／qualifiedChannelGeneration／binding proof 逐 sensor 證明；逐頁以 ReadStateVersion 防止同 lease 的混合快照。舊缺少 proof／read-state 欄位不得默認有效。

最終 Python 24/24 與 PowerShell contract checks 再次通過，結果在 work/round54/root-e2-final-contracts.log；涵蓋 partial proof、合法 rotation、偽造 channel／binding、raw context 漂移、缺舊欄位及同 lease read-state 競爭等小型反例。這是工具實作／契約驗證，不是15,000 sensor、64.8M資料、正常全量來源的正式驗收；未啟動完整 CLI、未再 seed 固定結構、未開受保護資料庫。E2／G父驗收與 native／capacity／retention／formal acceptance 旗標仍保持未通過。

### 8.20 再核對四處 raw-only 消費端（2026-10-15）

期間評估的 selected sensor fingerprint 與最終 closed-day authority recheck、磁碟評估 metadata snapshot、資料流探測 operation scope、資格作業狀態提示，均改用 raw context／RawAuthorityRevision。保留 settings、來源、host/sensor membership、identity、strategy、profile、evidence／verification、permission 及交易 CAS。環境探測仍包含訊息；DailyPipeline、workflow recovery／manifest 與 silent-presence 的混合 message/resource 終局防護保留，不能因 raw 可續行而追認過期訊息。

SelectedSensorFingerprint 材料變更會一次性使舊持久 closure/readiness 選取指紋不符，需重新評估，不能追認空值或舊 hash。Disk snapshot 缺有效 raw context 即拒絕。資料流測試只透過 internal client factory seam 隔離自有 SQL quota coordinator，正式預設工廠／HTTP／operation checkpoint／fetch／DB 路徑不變，沒有新增正式 API 旁路。

主代理獨立查碼與驗證：r37資格作業提示4例通過；r42期間consumer25值正例／raw ABA負例、disk metadata正反例及selected fingerprint共5例通過；r43資料流真實 loopback HTTP五例全過，日期格式變更寫入單筆值，raw ABA／sensor移除／來源世代／相關settings變更均取消且零值寫入。每例最多1host／1sensor／25值。r37/r38測試缺白名單／SQL coordinator、r39臨時HttpListener埠註冊拒絕與首窗口fixture不足、r40/r42自製TCP回應連線失敗均保留，沒有宣稱失敗回合全過；最終使用標準HttpListener及有界埠重選，負例另明確斷言Cancelled，避免一般傳輸失敗冒充authority守門通過。

本段移除D54-01實作待辦及R54-05 remainingImplementation；長期raw工作與當次寫入CAS分離。正式來源、所有角色／跨程序完整操作及G/H父驗收仍未通過。
### 8.21 四種真實維護登入 fixture 與證據矩陣（2026-10-15）

四種維護情境已映射既有帳號／授權路徑，六個正式角色不變。M54-rescue 使用隔離 Ad provider 的真正 serverAdmin password-required 分支；每次執行隨機密碼以 child environment 傳入PBKDF2 hash，manifest只記hash fingerprint，不保存明文／hash，不改正式部署憑證。M54-global／readall／nomaintain使用既有source角色帳號；M54-scoped仍是合成元件測例，不建立帳號。

主代理實跑 Test-UserMatrixApiExportAuthority：六個baseline正常登入與能力核對、真serverAdmin錯密碼401／正常登入200、Maintain=true／ViewAll=false／IsServerAdmin=true共7份觀察通過。report為work/round54/root-role-api-current-r2.log；普通clone 49dae34df1094270bbf41cf3e26faa64、rescue clone 7a0862176067478583aec5c5b209d07e 均依精確owner停止；來源DB/key SHA未變。來源為cd062561的dirty-source fingerprint、配對Web/Core及明確靜態資源SHA；這是本段API角色驗證，不能當最終clean同版凍結或全瀏覽器矩陣通過。

Manifest／CoverageInventory、A2所有正反契約、Round54 recorder／initializer evidence fixture與source-pin拒絕再次通過。已修正A2「有缺口卻標完成」反例不再依賴已完成R54-05的舊狀態，改明確注入合成未完成項後驗validator拒絕，保留原門檻。R54-07／C54-01實作待辦移除；maintenanceRoleMatrix標implemented-pending-browser-acceptance，所有acceptance／browser／native／capacity／whole-round旗標仍未通過。

### 8.22 時間基準、完成分析日與資格修復介面（2026-10-15）

時間設定頁經既有Maintain路由，另核對ViewAll，來源未Ready或仍啟用時先鎖定編輯並指向正常維護入口。保存使用目前policy revision；失敗／未知結果清掉舊revision並鎖定，明確重讀後才可再編輯，確認勾選會重設。修復面板依伺服器canReadDetails顯示入口；救援帳號只有統計，不引向必然403的明細／時間設定。單顆與批次磁碟操作採後端completedAnalysisDay／analysisTimeZoneId／UTC邊界，不再從瀏覽器昨天推算。

主代理使用owned clone正常登入、公開settings／estimate／source-preview／monitoring路徑準備1host／3sensor範圍；沒有授予來源資格。瀏覽器已確認正常時間保存、另一舊頁過期拒絕、失敗後鎖定、明確重讀保存值與取消舊勾選；再驗新增source-not-ready前置鎖定。原生window.confirm造成瀏覽器控制工具逾時，確認DB仍版本1未送出後停止重送；使用者取消視窗後續驗。新介面改用專案既有confirmAction，確認期間鎖住目標，取消保留未保存值、確認才重讀；沒有移除影響確認。

同一小型79字元mode損壞文件經正常UI讀為InvalidJson，明確頁內確認一字元修補後讀回WithinLimit、版本1→2、78字元，未知FixtureNote保留，修補按鈕停用且內容唯讀。正常consumer與後端CAS／失效屏障證據沿用未變有效結果，不重跑。Node四個CRLF／混合換行／補充字元patch對位測例通過；本段Web/Core配對build成功。兩次Web clone 704f8a3e9ceb42c8a5e9a36b0dd92fa7／c9d1b3b4d45d4633bf5fa55825ffe404 均依owner停止，全部自建瀏覽器分頁已關閉，資料保留；未做大量資料測試。這是新增介面的必要分段證據，非完整瀏覽器矩陣／最終同HEAD凍結或全輪驗收。

### 8.23 完整性核對及過期待辦清理（2026-10-15）

主代理依§2、§4.6～4.10逐consumer核對，所有R54八項、C54五項與四個執行補強均有實作接線；remainingImplementation清空，原remainingAcceptance逐條保留。再追shared disk正式路徑：DI／daily／resource qualification／assessment使用semantic及verification store完整strict snapshot；時間變更同交易失效；diagnostic export在分頁前驗共用JSON；data-transfer經相同store。C54-02原盤點待辦已完成，不再留作功能未寫。

C54-04／04-B依原§4.9／4.10交付明列目標的可執行回退控制工具，非通用跨provider／版本遷移框架。validate_bundle及verify_restore_root在讀DB與launch前要求Sqlite及空ConnectionString，launch強制隔離SQLite／writer／mail設定；未知SQL Server相容性拒絕，不建立其連線。移除把「SQL Server完整備份還原尚未驗收」混列為本工具必須新增任意provider遷移的實作待辦；G3/G4雙provider完整恢復條款原封保留、仍未通過，不宣稱SQL Server已可還原。實作sourceReferences改指向可追蹤程式／測試，移除未提交生成smoke-report JSON的必要相依，避免新checkout因缺私人產物失敗。

保留27個原父驗收TODO及53條承接基線，未勾未跑項目；清掉的只有已完成或分類錯誤的實作待辦。來源／容量／完整角色矩陣、跨程序與雙provider恢復仍未驗收；此處只確認實作完整性，不執行整輪G/H或宣稱正式交付PASS。主代理為目前主模型，委派只用gpt-6-luna high／medium／low，產物均由主代理獨立查碼及選必要小型測例；本段不冒稱不同於全部實作者的換模型體檢。

使用者新增測試原則：每次測試前先判斷必要性。只有新改動、已知失敗或必要證據缺口才執行最小範圍；相同程式／依賴／設定／環境已有有效結果就沿用，不因換HEAD、換模型或文件修改重跑。新介面的必要正反操作已完成；後續只核對文件／清單與Git提交，不再重跑已通過後端或展開完整矩陣。

### 8.24 E01-T01：乾淨成品及實際載入組件核對（2026-10-15）

測試必要性：先前 publish 為 modified tree，缺乏乾淨目前程式的成品證據；只補一次建置、離線身分核對與執行中本機診斷，不重跑後端測例、不生成數值或裝置。起點 `cfa062e90bc835189d703c83b0aeec34f66fc5b6` tracked 工作樹乾淨。

- `dotnet publish LogForesight.Web/LogForesight.Web.csproj -c Release --no-restore -o work/round54/acceptance-cfa062e9/publish` exit0；既有 nullable／captured-field warnings保留，未列為零警告。
- `Verify-BuildIdentity.ps1 -Directory work/round54/acceptance-cfa062e9/publish`：PASS。兩組件版本同為 `1.0.53.2+cfa062e90bc835189d703c83b0aeec34f66fc5b6`，clean source fingerprint `3474dfc02d9b2384a8c640d3a5ccb29cf9804c6a2220b236ca4cfa65b661e912`。
- Web SHA256 `86d74fefaf8e5cd4f03dc1ebd1f245f67a5a7201f7d187306688fab0f22930f0`、MVID `6bdac346-e2b0-4643-aecd-b5f47fe7c516`；Core SHA256 `01b51eef376971e47aa1d9d3eafdace6f94988039ebd138b5ee6e32dfebbdbf9`、MVID `a2ecf001-761f-4b32-adb3-d826c6b465e9`。
- 自有1.1MiB隔離站 `4f36b9ed6fc24ad896117a4702de4e5c`，正常維護登入後 GET `/browser-fixture/api/admin/settings/prtg-probe/local-evidence`。回應 build_identity=verified／clean；程式逐欄斷言 publish manifest 與實際 probe 的兩個 DLL SHA/MVID完全相等。runtime assets來源及副本SHA同為 `ba5f1ab5da0c9041faf95f4b8ebfd6980faa618671443ca8d5eb72f07d2b9722`；來源DB/key前後SHA不變。
- 原始證據在 `work/round54/acceptance-cfa062e9/`；publish manifest SHA256 `04c02a69cbe28658ae5323b3de1c3664610d8a540e4415dd97487f10b19e1165`，local-evidence.json SHA256 `dc057f64e94c0717d5da199dcaf52e96dab0e6ffe4eb6db0132e4f538531fd56`。`Verify-PrtgProbeEvidence.ps1`以目前source revision核對：正確為INCOMPLETE，沒有把成品身分通過誤當來源／風險／容量通過。

E01-T01對此正式程式成品完成；E01-T02及其他來源、操作、容量、回復、完整回歸條款仍開。後續修改任何正式成品輸入就重開受影響成品核對；只改PLAN／驗收紀錄／不進成品的測試不為換HEAD重建。§3.1 N3陳舊的背景raw「仍有缺口」同步更正為已補齊，保留尚未通過的完整操作驗收。

### 8.25 E01-T02及E03／E04必要接縫（2026-10-15）

測試必要性：E01要求目前交付組件的真實啟動防護；E03正式binding／probe／profile publisher／raw consumer在本輪改過，舊joined證據未完整覆蓋目前接縫。只執行以下有限案例，不重跑舊139／152／51、222規則母體或全套回歸。

- `Test-UserMatrixBuildFence.ps1`輸入§8.24已凍結的runtime app、同版assets、既有1.1MiB來源fixture及先前f8c97版Core，結果PASS：錯誤ExpectedSourceCommit與真正混版皆在配置run前拒絕，偽造MVID在process啟動前拒絕，ProductProcessesStarted=false；沒有啟動全量回填。log：`work/round54/acceptance-cfa062e9/build-fence.log`。原publish及舊版Core只讀使用；原始DLL沒有以新檔覆寫。
- `PrtgFullBackfillPreviewServiceTests.TryStartFull_RejectsUnknownSourceIdentityBeforeClaimingRunOrOpeningPrtg`及`TryStartFull_RejectsPreviewWhenEitherLoadedAssemblyIdentityChanges`，同一新建測試binary定向2/2、0略過，9秒；各例2主機、3個歷史目標，沒有來源請求、沒有取得run執行權。TRX：`work/round54/acceptance-e01-refusals/build-refusals.trx`。沿用未改動的`LogForesightBuildIdentityTests.Assess_RejectsOldInformationalVersionSuffixEvenWhenBothAssembliesMatch`與第53輪既有build identity基線；`git diff 638c5843`確認該測試、Assess實作、full-preview測試檔無變更。這不把第53輪全量舊結果說成本次全量執行。
- E03目前正式鏈：`PrtgE03ProductionProbeJoinedAcceptanceTests.ProductionBindingProbeQualificationProfilesAndExactSamplesReachPeriodConsumer`及`PrtgDiskVerificationServiceTests.NormalServiceRunUsesSavedContractAndPersistsVerifiedEvidence`合計2/2、0略過、10秒。前者3 sensors×2完成小時×4 slots，最後只保存6個hourly rows；後者保存6筆值並核正常保存及source漂移拒絕。TRX `work/round54/acceptance-e03-current/joined-current.trx`，SHA256 `733f4e7ce0fe3d4888a1dfee2356a9ddafba7abd615b06c441aab5371a13546f`。CPU25.0834%、memory available25.0834%→used74.9166%、disk free90.0887%精確值及profile／proof接線通過；Ping來源父身分／raw時基未確認時仍partial、零正式授權。E03-T01／T02整項仍開，不把Ping未知當來源合格。
- E04當前clean frozen瀏覽器在`/browser-fixture/admin/prtg#probe`正常下載「本機診斷JSON（不連PRTG／AI）」；下載事件工具等待逾時，但實際5846-byte檔已落地，未重按。檔`C:/Users/eldar/Downloads/prtg-local-diagnostics-2026-10-15T06-21-18-309Z.json`，SHA256 `1c7c72b4277e6e6de6c2089fbeb13fb55e70d5af712a068e44123ff5061d74a0`，由離線verifier核目前commit為INCOMPLETE；source-not-configured、五類missing、saved bindings=0、qualified=0、AI not-probed，且不把本機檔當探測成功。完整saved-source探測／取消／版本漂移操作尚未因此通過；E04-T01／T02保留。

唯讀盤點提出NetIQ摘要日疑點後，主代理追`AnalysisOrchestrator`→`NetiqPipelineService`→`NetiqSourceAttempt`→`RunMonitorService`核對：NetIQ依伺服器主機日D−1，與PRTG分析時區相互獨立。原`DateTime.Today.AddDays(-1)`符合正式消費端，撤回缺陷推論，不改程式也不新增測試。既有證據只有PLAN摘要而未找到原始receipt者，僅列歷史線索，不能擴大認列目前未覆蓋條款。

### 8.26 原27項驗收逐項裁定與最小剩餘範圍（2026-10-15）

本次逐項核對的是原27個ID，沒有縮小驗收契約。已完成2項、仍開25項；§3的checkbox與`coverage-inventory.json.round54TodoReview`同步。主代理親查目前程式、建置／HTTP／TRX／下載原始產物；兩個gpt-6-luna high僅做唯讀條款盤點，沒有實作或執行測試。NetIQ日期疑點已由主代理否定並撤回。這不是不同於全部實作者的整輪換模型體檢，也沒有宣告G/H或整輪PASS。

證據沿用方式：§8中有原始receipt且相關程式／依賴／設定／環境仍覆蓋該子條款者保留；較早來源或只有第53輪PLAN摘要而未找到原始receipt者列歷史線索，不擴大為目前整項驗收。原始53個gap及R54/C54各子條款仍保留，父項勾選不連帶授權來源／容量／角色動線。

| 原TODO | 可用證據及本次裁定 | 仍缺的客觀門檻／下一步 |
|---|---|---|
| E01-T01 | 完成；§8.24 clean publish＋實際loaded probe的SHA/MVID及assets完全一致。 | 只有正式成品輸入改變才重開；文件提交不重建。 |
| E01-T02 | 完成；§8.25三種實體啟動拒絕＋未知身分／換DLL預覽兩反例，舊尾碼契約未改沿用。 | 相同拒絕行為不再重跑；不得覆寫舊成品。 |
| E03-T01 | §8.25目前joined probe/profile/數值/期間引擎與正常disk核驗2案通過；精確轉換值已核對。 | Ping實際來源父身分／raw時基仍partial；完整明示時間設定→四類來源→正式consumer操作不能用合成Ping未知替代。 |
| E03-T02 | 保留先前parser／profile的缺值、單位、scale、primary、世代／stale、DST與時間authority拒絕子項；§8.14～8.20補raw ABA及正式consumer隔離。 | 尚欠逐子句完整證據對照，尤其無時區歷史／跨午夜與28日、7日20%同鏈引用；不得因51/152案摘要直接勾整項，亦不重跑其全母體。 |
| E04-T01 | §8.24～8.25 clean local export；本節目前saved-source完整probe及取消後證據，build/settings/本機摘要有實際輸出。 | saved時區／scope／guard／traffic／NetIQ父資格／AI全部欄位與正式消費端仍需逐欄對照，unknown必須按其來源證據判定，不把operator input當native proof。 |
| E04-T02 | 舊64KiB/depth32/白名單verifier與目前本機瀏覽器下載保留；本節saved-source API→offline及取消→瀏覽器下載→offline均通過。 | 目前表單未保存值、來源逾時、truncation／revision漂移與下載端的完整組合仍未全驗；每次只補缺失操作，不重跑整套109契約。 |
| E05-T01 | 目前頁面實見停用、0/28天、profile未確認、白名單與設定入口；舊guard與readiness反例保留為相應子項。 | 守門失聯及未保存／缺資料／stale操作仍須精確核業務finding/case/notification為0；畫面原因不能代替持久副作用核對。 |
| E05-T02 | 原18成功/2衝突及保存→取數→guard子集合保留；§8 F2 store/controller/protection43案及1值/1狀態正常HTTP清除證據。 | 目前scope縮小＋sync未完成＋保留集合、過期寫入與readonly/case-only完整拒絕操作；跨角色部分回掛E08-T03，不重建20-device歷史fixture。 |
| E05-T03 | pager末頁／前導／取消與§8 timeline多頁authority漂移證據保留；抽樣不授權完整拓撲。 | 缺取消後cursor／剩餘集合的全部相依核對；全量預覽/保存/實際請求集合仍由E07/E08原母體承擔，不能拿小頁替代。 |
| E06-T01 | §8.18 r30/r33 admission／qualification／refresh／discovery及§8.19 E2有界契約子項保留。 | 完整共同負載的transport種類/數量/版本/scope、資格到期尖峰與日常額度未量測；暫不啟動全負載。 |
| E06-T02 | §8.20 r43五案loopback raw/scope漂移接線已通過。 | 正常、10%逾時＋10%unknown、全來源拒絕及合併負載的輪次/p95/25%餘裕需原負載母體，不能由1sensor推算。 |
| E06-T03 | 現有quota/admission／有限等待及取消子項保留；本次不為舊2/s、4在途、5/60契約重跑計時集合。 | 72h初始化期限與共同工作公平性／停止後零新請求／NetIQ獨立續行需連續操作證據；未知成本仍拒擴量。 |
| E07-T01 | §8 E1固定結構已獲授權且保留重用，205值/12步驟恢復；預檢報告只讀檔案metadata。 | 64.8M及DB/WAL/backup/export/保留峰值＋全卷25%未驗。既有預估多副本空間差約-59.6GB，先定案隔離資源方案；不開受保護41.1M副本、不再seed固定結構。 |
| E07-T02 | 舊400成功HTTP只是歷史子項，1200分母receipt不是完成證據。 | 三對六輪/1200成功路由請求與各p95/warm/window門檻需要原量測，依no-bulk指示不執行。 |
| E07-T03 | generator batch尺寸／高水位安全工具的既有小型證據保留。 | 額外RAM≤1GiB、三輪留存≤5%、I/O/lock/query plan與停寫清積壓、零丟失重複均需真實完整負載；小型工具自測不代替。 |
| E07-T04 | §8 generator205列跨程序恢復及SQLite rollback CLI／原生Reply差額／held mail子項保留。 | 完整副本180日保留、升退版、雙程序、unknown commit／journal壞/滿續跑與精確集合尚未驗；SQLite受限回復不是SQLServer完整復原。 |
| E07-T05 | tiny SQLServer Unicode/physical reader與Express完整shape拒絕邊界保留；未建立新SQL資料庫。 | 需合格自有隔離SQL engine的原定query plan、密度、容量及恢復證據；LocalDB Express及每sensor一筆均不能裁定通過。 |
| E08-T02 | 舊Daily/父/AI/mail consumer及§8.15目前raw replay/scope 8案保留。 | 初設→NetIQ父→PRTG風險→AI→報告/案件/交辦回覆→通知/恢復/再發完整操作鏈，含零事件/弱佐證/不合格父，不是只補fixture資料即完成。 |
| E08-T03 | §8.21六角色＋真serverAdmin登入API觀察、§8.22時間/repair瀏覽器子項保留；既有六角色/權限模型不擴張。 | AC01–22/U53/Q逐角色實際UI、直接URL、修改API、子路徑、受限證據和交辦回覆完整矩陣仍開；API觀察不冒稱全browser通過。 |
| E08-T04 | §8.22兩頁時間設定stale409及F2預覽/owner/CAS局部證據保留。 | mapping/merge/delete/rollback兩管理者完整操作及3,000/15,000預覽→保存→讀回→來源集合未驗；不縮母體。 |
| E08-T05 | 已修統計郵件generic workflow；§8.13/8.16目前reservation/未知SMTP重建不重送子項保留，原99+20維持分開歷史母體。 | 三來源進度、late NetIQ/AI、AI/PRTG故障/停用、local-only與DB/journal故障/重啟其他組合仍需真實操作與durable IDs核對。 |
| E08-T06 | §8.13 r23～r26及§8.16實際SMTP拒收/unknown/重建/精確reservation子項保留。 | 五分鐘摘要/urgent、部分成功與隔離實收，以及ACK→可信恢復→再發的人工作業/案件/通知識別連續操作尚未完整。 |
| E09-T01 | 固定222規則consumer重播是歷史子項，無新增ID或改真值；本輪raw/formal consumer變更另有定向證據。 | 每個規則族仍缺受限角色實際找資源/原因/證據/下一步及unknown/排除/校準瀏覽器核對；不得為湊完成重播222案。 |
| E09-T02 | 舊四基準固定truth報表、時間/分母/unknown/排除斷言及「合併未優於聯集」裁定保留。 | 最終報表與真正操作結果、as-of/缺時間/禁止future資料的完整對照尚缺，舊226案例不是本次最終操作PASS。 |
| E10-T02 | 本次27項全列、實作呼叫端核對、兩個新E03接縫及E01成品拒絕；修正§3.1 N3陳舊缺口文句。 | 原需求/52輪子條款/AC/U/Q與E01–09整體可執行審查及必要未測清零尚未成立；沒有新增已確認的production缺陷。 |
| E10-T03 | 保留b77a72c7全量7,978/1/10與638修正後定向結果、Round54各分段receipt；這次只新增4個必要小型.NET案例。 | 最終受影響程式的完整有效回歸覆蓋尚未齊，不啟動未審過資料量的全套，也不冒稱最終曾單次全綠。 |
| E10-T04 | 本次分段提交/遠端SHA已核對，PLAN/清單有明確狀態及限制。 | E01已完成；E03–09必要條件仍開，因此整輪正式交付仍待其相依，不合併/不推預設分支。 |

本次補驗E04的原始結果：沿正常public settings API保存自有`127.0.0.1:3510`來源與合成token，PRTG/schedule/mail均維持停用；完整probe唯讀在17.01秒結束，僅讀取stub固定小型回應，evidence11793 bytes、設定修訂`d7d6feffa290416c8bf28383a6498ccd`，verifier正確INCOMPLETE。之後同一owned來源切`prtg-hold-503`，確認isRunning後cancel至cancelled=true/isRunning=false花165ms，沒有source qualification；clean目前瀏覽器顯示「已停止」及相同durable操作ID/版本，下載取消證據5886 bytes，verifier仍INCOMPLETE。來源請求log最後只有該次status請求，沒有取消後新請求。原始receipt在`work/round54/acceptance-cfa062e9/{saved-source-status,saved-source-evidence,cancelled-result,cancelled-evidence}.json`；實際瀏覽器下載為`C:/Users/eldar/Downloads/prtg-compatibility-evidence-2026-10-15T06-28-30-545Z.json`。這些是新接縫，不是重新跑整套verifier或完整角色矩陣。

測試決策結束條件：已補上述必要缺口，未重跑相同程式的有效集合；沒有新production缺陷就不為製造修正而改碼。其餘25項的下一步已具體列明；大母體項仍須先討論合適資源與可行方案，小範圍操作項仍維持原契約待驗，不被改稱不可能或排除。工具本身沒有受保護路徑denylist，故任何generator/capacity/highwater命令均不得指向既定受保護副本；本次沒有呼叫這些資料工具。測試站保留小資料供後續重用，收尾停止owned Web/來源行程。

清單驗證只針對本次新增狀態：validator逐一核原27個ID唯一且完整、已接受項須有證據/範圍/重開條件、pending須有明確缺口，並與PLAN checkbox一致。正常清單通過；兩個純JSON反例「重複ID」與「未勾PLAN卻標accepted」均依預期拒絕，零DB/HTTP/產品測試，receipt `work/round54/acceptance-todo-review/result.json`。既有22AC/10U/6Q/222case/53gap分母及aggregate flags不變。owned fixture `4f36b9ed6fc24ad896117a4702de4e5c` 已停止，來源DB/key最後再次hash核對不變，保留clone供重用。


### 8.27 收尾體檢範圍與文件同步（2026-10-15）

本次從原始起點 `a69fedd7` 回查至 `d4304a9d`，共48個提交、220個變更檔案，包含後期修正，不以最後一個commit取代整輪範圍。遠端dev仍是起點，feature遠端與該基準一致；未追蹤工作證據、環境及大型資料均保留未提交。使用者最新授權為「沒問題才整合dev並刪分支」，已取代先前禁止自行整合的授權界線，但不等於取消下列驗收條件。

| Phase／批次 | 提交及實際接線核對 | 收尾判定 |
|---|---|---|
| A／A2 | 規劃30807f82～a7f2f286、preflight f25cadd3、清單71eea7d8、角色df66dcf5、最終逐項d4304a9d；validator／launcher／recorder相互銜接 | 工具與清單已接線；全角色操作待驗 |
| B | 時間格式、raw分離、finding／mail持久化從7e49c4a3、13e592c4至38c4b902、f8c97b84、cfa062e9；對照§8.13～23的修正串 | 功能及局部證據存在，完整跨來源／通知操作待驗 |
| C | 620ee7f4及後續disk/readset修正，正常核驗服務傳source contract並重核世代 | 正常入口小型Verified證據存在；完整來源資格待驗 |
| D | 45fb7c14、0dec7085、f23a2e53及efbd4f28～24a57854、7feabc96、dd7babbc、86be5386與後續bounded修正；讀取到mutation／consumer均核對 | 共享authority歧義欄位另發現修正缺口，不能只憑舊通過紀錄收尾 |
| E | 38e9ca74生成恢復、03b65d1d runtime ownership、cd062561正常HTTP準備 | 工具已交付；descriptor漂移防護另修，完整母體未驗 |
| F／回退 | 8ee07fd3 purge、7a80b185啟動防護、49f7d878差額、0dac20c7郵件hold、24850cf3拒絕、df66dcf5角色及cfa062e9介面 | purge交易／撤權接線存在；回退完整性及Windows reparse另修，完整操作仍開 |
| G | f25cadd3僅資源預檢工具；沒有可宣告共同負載／恢復通過的提交 | 未完成，不能用工具存在或小樣本推算 |
| H | e9aa0569／aa304fa4成品子項、d4304a9d逐項核對 | 父驗收仍受E03～09相依阻擋，尚不可正式交付 |

主代理直接覆核交易柵欄、授權、訊息解析／持久化、修復與schema／現況文件；三個 `gpt-6-luna high` 分查Core、Web／mail及工具，其摘要只作線索。收尾發現回退quick_check回傳未檢查、Windows junction防線、descriptor逐次漂移及shared authority重複欄位四項，另段修正並記前後反例。清除設定競爭疑點未成立：RunBatch對scope相依blob在交易內鎖定，允許舊批先提交、設定後提交、下批停止，符合§4.9安全點契約；本人進度仍須當前讀取權，不以撤Maintain為由擴張權限。

文件整理沿用現有PLAN／規格，不另建重複報告或搬入archive。PRTG-SPEC與WEB-SPEC回收「已接線卻仍待實作」的日期／磁碟／owned入口描述；角色工具README移除過期planning-only／validator未實作說法；DB-SPEC補清除表、索引、交易及訊息時間欄位，保留完整容量／恢復未驗限制。修正§4.3舊續跑指示，受保護大型副本禁止開啟或修改。11份本輪變更Markdown本地連結核對無缺檔（archive不讀），diff whitespace核對通過；文件修改不重跑產品測試。

skill評估：沿用project-closeout、project-doc-hygiene、subagent-verification-policy及delegated-implementation；現有規則已要求重用有效證據、主代理獨立覆核、反例及實際consumer驗證，本次無需新增流程或改寫共用skill。不可為了完成整合移除25項必要驗收。歷史主代理實際模型型號無完整可信紀錄，且Luna也參與過實作；本次不冒稱「不同於全部實作者」的獨立換模型背書。修正由Luna high實作、主代理查碼及核驗的範圍另記，技能要求的整輪獨立體檢資格尚未成立。


### 8.28 收尾工具缺陷修正

Luna high實作、主代理逐行核對實際入口後確認：回退after-backup的`quick_check`須fetch全部結果且僅單列ok接受；Windows reparse用lstat原生attribute檢查所有祖先，launch／release／reconcile在resolve前拒絕junction；不存在的新restore末節仍可正常準備。資格工具在runtime ownership與業務API請求前各自重核descriptor原路徑及SHA，不沿用開工時hash充當當前檔案。保留既有工具及scope，不新增通用防護框架。

必要驗證僅針對新缺口：主代理以舊quick_check語句行為代回tiny reconcile consumer，確認原本錯誤結果未拒絕（1個預期失敗）；同測例在修正後拒絕且未產生receipt。另驗合法小型差額還原／release、實際Windows junction經CLI launch入口拒絕，3/3通過、零skip；receipt `work/round54/closeout-review/tools-consumers.log`。descriptor小檔hash相符／漂移拒絕通過，`descriptor.log`；兩個真正HTTP呼叫之前的接線直接查碼。代理另有不存在restore路徑正例及靜態接線通過，不將其重跑以增加數量。無Web啟動、無全量source preflight、無固定結構seed、無大型payload；不替代完整恢復或資格矩陣驗收。


### 8.29 共享授權歧義修正與最終候選狀態

`system_settings`／monitoring policy的完整有界字串先經既有`StrictAuthorityJsonDocument`檢查，再反序列化。snapshot、binding保存／資格發布、job resume／持久頁授權、policy讀寫、資源綁定及profile發布／撤回、正式delivery fence一併核同一條件，重複欄位或大小寫別名不再以最後值取得正式權威。保留既有交易／CAS、缺值預設、合法未知擴充欄位與權限模型，沒有新增儲存或通用workflow。Luna high實作；主代理直接追consumer與diff、讀原始TRX/log，沒有為主代理背書重跑相同集合。

收尾實際修改Core及工具後，E01-T01／T02原`cfa062e9`證據只代表該歷史成品，依既定reopenWhen重開最終候選核對；不是推翻原測試或新發現兩個bug。§3與coverage inventory同步為27項待驗（原25項加2項候選成品），不在未達整輪收尾條件時為文件更新額外publish或重跑舊成品測試。舊有效證據保留其未變範圍；最終成品仍待凍結後核對。E10-T04相依未完成，故目前不整合dev、不刪feature、不宣告整輪PASS。使用者可決定後續驗收資源或明確變更交付範圍；本次未自行降級容量／恢復／角色條款。


本段原始驗證：authority 12/12、formal claim正常及settings／policy別名拒絕3/3，零skip。主代理已讀TRX實際case/outcome，保存至`work/round54/closeout-review/authority.trx`（SHA256 `7A7DC33A6CB9D873F5426B838D45F61940E29AC35242D8C710C164966B049840`）與`fence.trx`（`E723FFD56B0A19E0691BC4E2D049F6D0A253665622677962819D7E2D3F7C41DC`），同目錄有build/test原始log。使用Debug定向建置，既有nullable／analyzer warnings保留，不宣稱零警告或Release最終成品驗收；測試資料為少量設定／binding／父紀錄，resume沿用101個sensor識別的兩頁metadata，不寫大量數值。修前9案失敗由實作者工具輸出確認，沒有另存原始檔，不為補檔重跑。新增及修改測例直接檢查未產生binding／qualification／formal claim與保留未知欄位，不僅檢查helper回傳。清單validator在候選重開後通過（`inventory.log`，AcceptanceClaimed=false）；未執行其他整類／全量測試。

本次架構核對結論限定於已查範圍：修正沿既有Core持久層、Web能力規則及隔離工具，沒有新增正式角色、權限或儲存重構；四個確認缺陷已修，兩個未成立的清除疑點撤回。G/H、完整操作矩陣及最終有效回歸仍未通過，不能保證沒有其他bug或稱全面完美整合。文件與工具修正已分段提交`cb975a6f`、`03e16f30`並確認遠端SHA；本段Core與相依清單再以完整提交推送feature，dev維持`a69fedd7`，分支保留。


### 8.30 更新後規劃續驗：候選成品、descriptor派送與未保存表單

本次正式程式未修改，凍結候選為乾淨`b2b82708f6fe9811f3cf95f4759f68aa074fc52e`。E01因§8.29實際Core變更而重開，故只做一次必要Release publish（`dotnet publish LogForesight.Web/LogForesight.Web.csproj -c Release --no-restore -o work/round54/acceptance-b2b82708/publish`），既有nullable警告保留。`Verify-BuildIdentity.ps1`通過；兩組件來源指紋`6671f59d81f15ae128d5dfcfc3806897f016093da89670cec99c124d99cb738e`，Web SHA `028c33a4de50d678dfb63e3fb47f0fd755d46d75c64e1586740950fa0202541e`／MVID `f7b2b0a4-df5c-4054-bfad-7216b4b8d673`，Core SHA `fa283ce167ff87f37a58554454a6c3c3c938b722602b841f11e1789e87811f9f`／MVID `e176b9ed-e31f-4d51-a88f-74adbb0da572`。

owned小型fixture `7226860943fb42f9b20c198390f3bf2c`正常登入後讀公開local-evidence，逐項assert執行中source commit、clean狀態、Web／Core hash及MVID與publish完全一致；來源DB／key hash未變，靜態assets來源及拷貝一致。`build-runtime-result.json`記確切結果。只執行當前成品的`Test-UserMatrixBuildFence.ps1`，錯提交／偽造manifest／實際混版皆在分配run或啟動process前拒絕，`build-fence.log`顯示ProductProcessesStarted=false。未知來源／loaded assembly改變及舊suffix的preview防護程式與測試沒有改動，沿§8.25有效子集，不重跑。E01兩項對此候選完成；將來正式成品輸入變更才重開，文件及測試紀錄不觸發重建。

§4.11 descriptor原有hash小檔／靜態證據不重跑；新增`test_descriptor_dispatch.ps1`直接取正式腳本的Invoke-Api與Assert-NoReparse函式，使用計數HttpMessageHandler及固定owner替身，專驗ownership核對後、業務派送前的接線。正常值送1次；在owner核對返回前令descriptor漂移，拒絕且新增請求0，PASS（`descriptor-dispatch.log`）。沒有開DB／listener、沒有全量preflight／metadata，這不宣稱runtime owner本身或完整資格流程通過。

E04-T02／AC07–08補未保存表單操作：維護帳號在瀏覽器PRTG連線表單填自有loopback與合成token、不保存，切至探測按完整探測，顯示「尚未設定PRTG連線位址，無法執行探測」。公開API核對保存URL仍空、PRTG停用且probe沒有StartedAt/isRunning；source request log尚未建立。透過同頁本機診斷按鈕實際下載5846bytes，沒有未保存token，SHA `596B22CDE36A0653DF4BA0997EF7B4EABEBEF75D650F3D862F0549267D561B80`；正常offline verifier使用當前commit／settings revision回INCOMPLETE（exit2）而非來源就緒。`unsaved-form-result.json`、`unsaved-local-download.json`、`unsaved-offline.log`保存原檔與核對。首次offline命令參數名稱誤寫未執行verifier，修正為實際Path／ExpectedRevision後執行一次，沒有重送下載或探測。E04-T02其餘timeout／truncation／revision操作缺口仍保留；§8.26已驗取消／保存來源證據不重跑。


### 8.31 E05-T03：取消後續跑精確集合

Luna high只在`PrtgSensorTimelineCollectorTests.cs`新增`CallerCancellationDuringBootstrapDiscardsPartialPageAndResumeReplaysExactMessages`，主代理查完整diff與原始TRX，沒有重跑已通過測例。1sensor、3則messages、pageSize2；第一頁收到2筆後，第二頁請求受控阻塞並caller取消。契約是bootstrap未完成不持久化局部頁：States／Coverage／PendingStates／PendingEventKeys／游標／邊界hash均空、watermark null，已用請求預算2。resume從start=0重新讀未保存第一頁，再讀start=2，累計預算4；精確3則事件＋目前Down錨點、3個右開period，沒有漏失或重複。

定向單方法最終1/1、0skip，TRX `work/round54/acceptance-b2b82708/timeline-cancel/timeline-cancel-final.trx` SHA `6E43DD28C281F70319EAD8F3B2CF67E58B0E96A998B427CD000E3CB9D3821B71`。先前兩次fixture錯誤結果保留：先誤用非狀態raw碼5，改為正式Down碼608；再修正Through右開不產生額外末段period的斷言。沒有據此修改產品程式，不把修測例誤稱修bug；編譯等待不重開第二個runner。此子項完成，完整前導、其餘分頁操作及全母體拓撲仍在原E05-T03／E07／E08待驗。


### 8.32 E04-T02：逾時匯出及離線交接反例

同一owned fixture在正常瀏覽器保存loopback來源及合成憑證，PRTG、排程、AI及通知維持停用；公開settings把來源timeout設5秒，受控來源`prtg-hold-503`只在第一個請求延遲8秒。瀏覽器啟動一次正常環境探測，5.297秒後isRunning=false／success=false／cancelled=false，畫面明示未通過並可下載JSON。下載5966bytes，SHA `6121F9DAF72E3DC13C1143A5BB958D97ABD88DF56C1794DC9154BB753C15B7D1`；無測試憑證，offline核對當前build／settings回INCOMPLETE。原始status、下載、result、offline log及畫面分別在`work/round54/acceptance-b2b82708/timeout-*`，沒有重送同一probe或跑成功來源／取消舊測例。

未找到既有verifier契約測試的原始執行收據，故只對同一下載的最小副本補必要負例，未重跑109項整套：錯expected commit、錯version、不同settings revision、檔案截斷、重複JSON property均拒絕；注入revision不回顯sentinel、depth超32、65537-byte檔案亦拒絕。前端既有小型JS契約一次通過，確認running禁下載、完整/truncated狀態、超五類／偽正式宣告／malformed及ASCII／UTF8 64KiB邊界，最大反例約66KiB，不是33M文件或負載測試。舊cfa與b2的verifier／下載JS／頁面逐檔比對均未變，既有實際取消165ms／保存來源回執保留其適用範圍。

必要反例發現新缺陷：前端拒絕`evidence_ready=true`，offline原忽略該欄位，回exit2 INCOMPLETE；不能把這個結果說成格式合法或測試通過。已在正式verifier補「欄位若存在必須false」，偽造宣告現在exit1 `InvalidEvidenceReadyClaim`；舊文件未帶欄位仍可讀，沒有新增formal授權。修後只跑原失敗及尚未跑的4案，原5案位於未改檢查路徑沿用；合法實際下載與缺readiness欄位的舊shape各核exit2一次。原始檔、各case輸出及result見`offline-negatives/`，修前偽造案僅保留於工具回傳，沒有為補檔重跑；既有契約測試檔新增同一拒絕案例供後續回歸。

檢查30秒／整輪期限時另確認一般探測前段尚沿用最高600秒設定、僅後段CompatibilityProbe有期限，依原E04-T02契約補正並另段驗收。此處暫不勾E04-T02；不能以本次5秒設定單案推論所有設定均受30秒上限。正式成品若因後續期限修正再變更，E01依既定reopenWhen處理，不重驗未受影響的browser及verifier結果。
