# 第 54 輪規劃：PRTG 剩餘實作與完整驗收

> 狀態：2026-10-14使用者已授權全部實作及逐段驗證後commit／push；實作中。同日最新限制：開發期間只做分段小範圍驗證，不執行大規模大量資料測試；不得已須先提出方案討論。本輪未宣告整體驗收通過。
> 來源：2026-10-13 使用者明確要求將第53輪尚未實作項目移入下一輪，先收尾已實作部分。此為範圍移交，不代表原目標、容量、實用性或驗收已通過。
> 本輪起點：`dev` 的 `a69fedd7`；工作分支 `feature/prtg-feedback-54`。第53輪乾淨驗證基準為 `638c58434f0bc01fe77e62365addeafd75680fb0`，至本輪起點僅README、PLAN與驗收JSON差異，正式程式未變。
> 原始需求與契約：[第53輪 PLAN](FEEDBACK-53-PLAN.md) §§3、6、13、15–17；最新責任邊界為 §§5.7、5.9。

## 1. 定位與不變契約

PRTG 輔助 NetIQ 已合格的每日主機日；成功零事件父紀錄也可加入獨立 PRTG 風險理由，缺父、父失敗或證據不合格只顯示等待。兩來源取較高級、不相加；同日弱佐證不升級。CPU／memory 預設壓力提示，Maintain 同版試算後才可啟用正式風險；磁碟低水位及可信趨勢可成正式理由。

固定條件不因跨輪調整：連續兩完成小時、每小時四個 physical slot、午夜歸後一小時的主機日；可信磁碟28日準備度、預估7日內降至20%剩餘門檻；初始化72小時、資格作業720小時且每顆一次attempt；table每秒2次、在途4、historic每60秒5次；單HTTP≤30秒、接受取消後≤5秒禁止新請求並退出可取消等待。

隔離負載為3,000 hosts／15,000 sensors／180日／64,800,000 hourly，slot與hourly不可混算。15分鐘輪≤10分鐘／sample age p95≤30分鐘；5分鐘輪≤3分鐘／age≤10分鐘；時間餘裕≥25%，資料卷25%空間另計。NetIQ三對六輪p95增量≤10%、3,000主機日warm≤10分鐘且每日封存窗口75%內；兩路由各模式每對100成功、共1200成功，相對p95≤10%與絕對≤2秒；PRTG額外常駐≤1GiB、三輪高水位增長≤5%、停寫兩間隔後四間隔清積壓、耐久接受丟失與重複sample/finding/member均為0。

AC01–AC22、U53-01–10、Q1–Q6及八規則族固定222正反例全部保留；不得縮分母、刪失敗、以零延遲或控制時鐘冒充容量結果。探測只取得來源事實，不在正式環境驗收。UC01–UC07為使用者確認提醒，不列開發驗收、完成率或阻塞；未確認不得宣稱現場全面通過或真實事故增益。

## 2. 尚未整合完成的實作

本節D54-01～04為原始缺口識別；執行拆分依§4，磁碟核驗獨立為C批次但仍回掛D54-01。§6權限調整回掛E05-T02／E08-T03，不另製造重複TODO。

下列與後面的「已實作、剩驗收」分開計數。私人工作樹有候選不等於目前分支已交付。

| ID／批次 | 現況、改動與下一個使用端 | 相依與客觀完成條件 |
|---|---|---|
| D54-01／訊息日期格式與時間授權 | 主線仍以保存culture解析messages文字日期，未記錄PRTG API帳號独立Date Format。新增明確日期格式、嚴格parser及獨立message-time fingerprint；串接設定→fetch／timeline→正式consumer→探測／離線核對。 | 保留raw OA/channel的既有授權與28日證據；空／core-default未解決時只讓文字訊息規則等待。六格式dmy/mdy/ymd×12/24時、DST gap/fold、跨午夜、錯格式、日期-only、offset文字、policy漂移／已提交頁與游標續跑皆需正反例；舊空fingerprint不能被讀側追認。對應E03-T01/T02、E04-T01/T02、E05-T03。 |
| D54-02／全量資格準備的可重播交付 | 已提交版本尚無完整全量副本的正常HTTP準備入口。私人候選有嚴格64.8M receipt／catalog預檢、公共settings／pilot／binding／qualification routes與獨立contract launcher，尚未完成拒絕矩陣或整合。 | 不增加production測試旁路；只有owned完整副本可進production入口，listener／ancestor／exact args核對不得只比substring。驗foreign listener、舊revision、pilot拒絕／過期、部分binding／job、持久720h／1attempt；合成contract輸出不得作容量或正式資格PASS。對應E06、E07、E08-T04。 |

### 2.1 時間授權的明確接線缺口

D54-01 必須同時處理 `PrtgFetchService.FetchStateChangesRangeAsync` 的環境預設文字日期解析，以及 `PrtgSensorTimelineCollector` 單顆／批次的 saved culture/timezone 解析；兩者均缺獨立帳號 Date Format。包括逐頁保存、游標續跑、設定漂移後停止及正式讀側拒絕舊授權。

磁碟人工語意驗證亦有未接通的 production consumer：`PrtgDiskVerificationService.ProbeAndSaveAsync` 未向 `PrtgDiskSemanticProbe` 提供必要的 `PrtgDiskSemanticSourceContract`，因此現有入口保持 `source-contract-unconfirmed`，不能產生 Verified；持久點另仍使用本機時區轉 UTC。下一輪須將保存的端點時間授權與分析時区接入，嚴格轉換範圍／點資料，再驗正常入口能完成核驗、缺 authority／DST 歧義／設定漂移不保存正式資格。不得以略過 contract 檢查修正。對應 E03-T01/T02、E04-T01、E08-T02。

### 2.2 容量工具尚缺的完整安全交付（D54-03）

生成工具的 Initialize／legacy adoption 尚缺跨 DB marker、manifest、checkpoint 的可重入中斷協定；resume 目前沒有完整核對已提交前綴及來源 mapping，legacy adoption 亦仍一次讀入 log。下一輪先以最小資料故障注入驗各寫入界線、已提交列／mapping竄改與大log，再補原子 DB marker、可重入意圖／恢復及有界讀取；不可只驗 checkpoint 後尾部。以上是來源審查確認的工具缺口，尚無實際損毀事故證據；不得修改既有41,140,900筆副本來做反例。對應 E07-T01/T04。

完整資格矩陣還須由可驗證的 listener PID／start identity／assembly／exact command tokens／runtime DataRoot與provider綁定實際服務，重新核對 descriptor hash及reparse祖先。numeric loopback或可編輯JSON宣告不足以授權 public settings／排程寫入；本輪先封住不具可驗證 ownership 的入口，下一輪 D54-02 才完成可用的 owned 正常準備與否定矩陣。

本機候選：外層工作區的 `work/e03-message-time-candidate-20261013`（訊息時間）及本輪checkout下 `work/logforesight-e07-capacity-current/scripts/prtg-workload/qualification-preparation`（HTTP資格準備）。這些不是已提交交付物；其他主機沒有候選時，仍須依本PLAN從正式分支完成，不依賴私人檔案。下一輪須先核實目前commit／檔案雜湊，不能整份覆蓋主線已完成的冷啟動或其他修正。50顆正常資格流程的局部驗證不替代15,000顆完整容量。新審查若確認其他尚未實作子句，加入本節並映回原ID，不藏在泛稱「等待探測」。

### 2.3 尚未完成的有界讀取接線（D54-04）

既有SQL有界讀取機制尚未覆蓋所有正式consumer：`PrtgResourcePressureModeStore.Find`仍讀完整blob，供`PrtgResourceProfileQualification.IsCurrentFormalPressure`使用；binding Save／RecordQualification與qualification ResumeExisting仍整份選取settings／policy；disk semantic／verification map的replay、mail與data-transfer路徑也有直接Get／GetAll。已存在的262,144字元 mode及33,554,432字元 disk snapshot上限不能因consumer未接線而繞過。

下一輪逐呼叫端使用有界projection／snapshot，保留同一transaction readset、CAS與來源authority；以正常大小、cap邊界、超量但選中entry仍有效、未知extra JSON及設定漂移反例驗等待／拒絕與零正式副作用。容量export另量測，不把正式mail拒絕等同匯出吞吐通過。對應E03、E06、E07-T03、E08-T05、E10-T02。此為尚缺的hardening接線，不宣稱已發生現場OOM或資料毀損。

## 3. 承接的驗收與交付 TODO

本表是原§5.8的逐ID移交，**不表示27項程式都尚未實作**。已完成的E02與E08-T01不重開；E10-T01已勾選的文件同步保留原完成紀錄。較廣原驗收尚未通過，既有分段證據可在確認相同程式／設定／環境及母體後沿用。細部使用者劇本仍以 [coverage inventory](../scripts/round53-user-matrix/coverage-inventory.json) 逐子句追蹤；2026-10-13結構核對包含22項AC、10項U53、6項Q、7種受控來源情境及222案，53個明列覆蓋缺口由下列原E項承接，不能用27個TODO的粗粒度摘要掩蓋子句。

- [ ] **E01-T01【承接驗收】** 以最終乾淨提交重新建置／publish，核對 source commit、來源指紋、manifest、Web/Core 組件與 probe；既有 modified-tree 分段證據不代替最終成品。
- [ ] **E01-T02【承接驗收】** 在最終交付上核對舊尾碼、混版 DLL、未核實來源、陳舊預覽 token 的拒絕與提示；確認舊檔不自動覆寫、未知識別不授權全量啟動。
- [ ] **E03-T01【承接驗收】** 操作明確設定→probe→proof/profile→正式採樣→期間引擎完整鏈，核對 CPU／memory／disk／ping 樣本來源、channel、unit、scale、方向與各端點時基；available-memory 25.0834% 轉 used 74.9166%，disk 90.0887% 保留剩餘方向。
- [ ] **E03-T02【承接驗收】** 補齊錯時區、缺單位／尺度／主通道、世代變更、陳舊 proof、DST 重複／缺小時、跨午夜與無時區歷史反例；不合格輸入保持等待、零正式副作用，維持兩完成小時、28 日與 7 日20%契約。
- [ ] **E04-T01【承接驗收】** 逐欄核對已補強的 build／設定修訂／時區 authority／scope／readiness／guard／traffic 安全診斷／NetIQ 回執與父資格／AI 小探測輸出及其正式消費端；每個 unknown 有原因與具體缺項，不把已保存可核對資料永遠標未知。
- [ ] **E04-T02【承接驗收】** 完整操作目前表單與已保存來源的匯出→離線核對，涵蓋取消、截斷、逾時、無設定、版本不符及防偽／敏感資料反例；維持 64 KiB、depth32、取消≤5秒、單 HTTP≤30秒與整輪截止。
- [ ] **E05-T01【承接驗收】** 補完未保存／缺資料／陳舊守門狀態、原因與設定入口的操作驗證；守門失聯不得視為安全，守門資料不得產生業務 finding／案件／通知。
- [ ] **E05-T02【承接驗收】** 核對範圍縮小、鏡像差額、同步未完成與保留資料不誤刪；補齊 readonly／case-only 修改拒絕、過期寫入及其餘完整角色操作。已通過的 18成功／2衝突及保存→採樣→guard 子集合保留。
- [ ] **E05-T03【承接驗收】** 補齊夾末頁、messages 完整前導／期間涵蓋、取消與剩餘集合核對；抽樣相依資料不得授權完整拓撲。全量預覽／保存／实际請求集合核對合併由 E07、E08 執行。
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
- [ ] **E10-T04【承接交付】** E01、E03–E09全部必要條件通過後，同步PLAN／AC／現況規格／Help／交付限制與證據可取得性；依使用者既定逐段commit／push授權在本輪feature分支提交，確認遠端SHA；不自行合併或推dev／master。本次規劃更新不代表功能開工授權。

## 3.1 全部原Phase的承接對照

| 原Phase | 本輪已有實作及仍需核對的界線 | 下一輪責任 |
|---|---|---|
| N1／A0 來源與版本 | build identity、來源探測及拒絕混版已有分段證據；正式來源完整核對未完成 | E01、E04；D54-01 |
| N2 範圍與守門 | scope／guard／mapping及不誤刪防線已有實作；完整角色與衝突操作未全驗 | E05、E08-T03/T04 |
| N3／A1 可信取數 | pager、raw/profile／proof、readiness與期間consumer已有實作；文字時間及磁碟人工核驗接線有缺口 | D54-01、E03、E04、E05-T03 |
| N4／B 風險與主機日整合 | 合格父紀錄、期間規則、補充／fusion與案件通知已有分段實作；全部正常操作鏈未驗 | E08-T02/T05/T06；E09 |
| N5／C 使用者與管理者 | 設定／probe／維護／證據及授權頁已有實作；六角色完整動線未驗 | E05、E08、E09-T01 |
| N6／D 持續負載及恢復 | quota／admission／journal／SQL與容量工具已有實作；64.8M生成及全混合負載未完成 | D54-02/D54-03、E06、E07 |
| N7 實用性與四基準 | 固定222案及局部報表重播已有證據；完整角色找證據與最終四基準核對未完成 | E09 |
| N8 全分支審查與交付 | 本輪做已實作部分審查與bug修正；原全目標閉環並未成立 | E10；不得將移交當作整輪PASS |

原 R01–R18、M0–M7、AC／U53／Q 的細部條款仍由第53輪原始ID追溯，以上不代替逐條門檻；新缺口必須回掛原E／TODO，避免只以Phase總名勾選。原來已驗子項及仍未驗子項分開登錄。

## 4. 本輪批次、相依與客觀驗收

全部工作保留在第54輪。27個原TODO是責任索引，不是27個等量工作，也不把53條覆蓋缺口另加成完成率分母。27／53是承接基線而非新增缺口的上限；本次另加入R54-01～08，每項包含多個必要檢查，不與原缺口混成等量完成率。每個TODO須由對應批次的全部必要子條款通過才可勾選；同一證據可以被多個ID引用，不重複執行或計功。B～H目前均待實作／驗收，A本次只完成文件與清單整理，未完成資源預檢。

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

- 已核對：`PrtgFetchService.FetchStateChangesRangeAsync`仍有環境預設日期解析；`PrtgSensorTimelineCollector`單顆與批次依saved culture解析。兩條路徑及逐頁保存／游標恢復一起納入B，不能只更換其中一個parser。
- 已核對：`PrtgDiskVerificationService.ProbeAndSaveAsync`未提供`PrtgDiskSemanticSourceContract`，且以Local轉UTC；`PrtgDiskSemanticProbe`缺契約時回傳`source-contract-unconfirmed`。C修正真實入口的接線與嚴格時區轉換，不取消守門。
- 輸入：保存的來源／分析時區、帳號Date Format、來源樣本及世代。輸出：具版本的訊息時間證據或磁碟核驗結果；下一端是正式profile、期間規則與probe，而非只停在測試helper。
- 驗收：明確正常值與歧義反例並存，核對API、持久版本、讀側判定及零非法finding／案件／通知；CPU／memory、NetIQ父資格及既有28日raw證據不得被文字日期設定誤傷。取消期限依§4.5。

### 4.2 D：所有正式消費路徑的有界讀取

- 已核對`PrtgResourcePressureModeStore.Find()`仍經`Get()`讀完整文件。§2.3列出的binding、qualification、disk map、replay、mail及data-transfer逐呼叫端核對，不能以新增一個有界方法宣稱完成。
- 輸入／輸出須保留相同transaction readset、blob version、CAS與來源authority；沒有有效資料時明確等待／拒絕，正常選中資料不得被任意截斷後當完整授權。
- 驗收涵蓋正常大小、cap邊界、超量但選中entry有效、未知extra JSON、設定漂移、並行保存；精確核對拒絕原因與正式副作用為0。262,144字元 mode／33,554,432字元 disk snapshot的既定字元限制不提高；編碼位元組與程序記憶體另量測；export吞吐另由G量測。

### 4.3 E：先安全續跑，再正常全量準備

- E1以自有最小資料在DB marker、manifest、checkpoint各寫入界線注入中斷，核對可重入恢復、已提交前綴、mapping竄改與大型log有界讀取。已核對legacy adoption仍`File.ReadAllText`，且marker／manifest／checkpoint分次寫入；不宣稱已有現場毀損。
- E1完成後才能續跑既有完整目標。41,140,900筆是移交文件記錄的暫停水位，本次未重開資料庫核實；先驗manifest、checkpoint、pending batch與tool identity，再以原root續至64,800,000，不修改checkpoint或縮分母。故障反例不得使用該既有副本。
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

UC01～UC07僅限制正式站台／真實效益宣告，不是開發TODO或必要輸入；來源未知仍由產品資格守門處理。coverage inventory的AC22／Q2／Q6及U53-04皆套用本節，不得重新要求獨立使用者效益證據。全部Q的journey關聯須與user-matrix完全相同；A2須補逐Q、R54／C54及§4.10補強條款、維護角色能力及證據ID檢查與漏項反例，本次只修清單、未修改驗證腳本或建立角色。

每段相依完整、檢查整體影響並適當驗證後commit／push至本輪feature分支，確認遠端SHA。不自行merge、force push或改推dev／master。不用AGY；若後續授權委派，subagent僅gpt-6-luna low／medium／high，主代理獨立核對。記實際審查角色與模型，不冒稱跨模型背書。

### 4.6 八項再次審查補強（全部本輪處理，未實作）

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
| A2 | E01-T01、E08-T03、E10-T02～04；R54-07/08 | 角色／R54／C54／Q與補強ID檢查、fixture／recorder、即時授權驗收及回退啟動前工具規格／接線與漏項反例；本次僅完成文件定義 |
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

C54-01～05以coverage inventory的`round54ContractCorrections`追蹤，分別回掛既有R54，不重開原27個TODO，也不把新增檢查當已通過。以下是具體行為契約；實作方式仍由對應階段決定。本次不修改程式、fixture或validator。

**C54-01／A2／R54-07：真實角色與合成防禦分開。** `RoleCapabilityMap`的一般Maintain來自Admin且含ViewAll，角色聯集不扣除能力；serverAdmin無業務可見主機。因此四種真實情境為M54-rescue／global／readall／nomaintain。M54-scoped留在`syntheticDefenseCases`，只驗授權元件在合成能力／可見集合下不外洩，不建帳號、不注入claims冒充正常瀏覽器結果，不新增正式角色、不擴張權限模型。A2的validator／recorder後續須分開兩種證據類型，原六角色不變。

**C54-02／D1／R54-06：隔離按證據相依範圍。** mode為主機獨立文件，損壞只阻依賴該文件的操作；disk semantic與verification result各為多主機共用文件，無法解析、超限或快照不可信時，所有依賴該份文件的磁碟資格明確等待。不得跳過壞entry後把其他entry冒稱已驗可信。獨立NetIQ、其他有效證據與不依賴該文件的通知／重播繼續；組合通知須排除未合格理由，不得把缺證據說成健康。驗精確相依集合、等待原因與其他成功集合。保留既有CAS／快照規則及有界修復契約，不為達成單主機隔離另建儲存拆分、分表或資料遷移。

**C54-03／F2／R54-01：三個清除入口同受保護。** 管理頁確認、`PrtgDailyPipeline`與`PrtgStructureSyncRunner`經`PrtgScopePurge.RunAfterStructureSync`的自動清除，共用有效執行所有權、一致性與耐久部分進度。人工綁preview，自動綁成功同步scope／來源世代，保留CheckScope、CheckShrink及守門保留集合；不要求自動流程提供人工preview，也不因共用而放寬自動清除。驗自動中人工確認、人工中自動觸發、兩條自動入口競爭、自動部分失敗／重啟／續跑。同步成功與清除部分失敗分開呈現；記已刪、剩餘與作業身分，基準只在對應完整清除成功後更新。

**C54-04／A2準備、B2驗收／R54-05：設定恢復不等於程式回退。** A2在升級前封存確切回退目標commit／Web／Core／assets與配套DB、key、設定備份、schema及authority版本；本輪起點a69fedd7可作候選，但必須取得對應可核對成品，不能只寫latest。同庫降版只有在針對該目標證明相容後才允許，否則拒絕並採成套備份還原；停止writer不足以證明相容，不直接換DLL就宣稱回退成功。升級後產生的案件／人工回覆／通知回執／待辦作業須先封存差額與精確清單，不默默遺失；可安全相容重放才重放，否則標明未恢復差額並阻止宣稱無損回退。外部已接受郵件不因還原回執倒退就盲目重寄，未完成去重核對前暫停相關派送。驗目標拒絕／合法還原、資料與key一致、人工內容、待辦及去重；不要求新增通用跨版本遷移框架。設定值恢復仍受B2當前authority重驗規則約束。

**C54-05／F2／R54-01：預覽與作業生命週期。** 人工preview自伺服器建立起有效5分鐘；確認須綁操作者、當前權限版本、來源世代、對應／保留集合及候選資料邊界。以上改變或到期即拒絕且零新增刪除，重新預覽不能自動提交。使用同一已接受確認識別重送，回傳同一作業／結果，不另開刪除；首次接受必須在期限內，重送已接受識別只是查回原作業，仍須核對身分與讀取權。作業開始後不納入新候選資料，與清除相依的寫入／scope變更須由一致性機制攔截或令作業在安全點停止。

人工作業接受前撤回Maintain：拒絕且零刪除；接受後每個新批次及resume前重驗，撤權後只讓已開始的不可安全中斷原子提交到安全點，禁止新批次，保留已完成計數與原因。resume須目前有Maintain且原接受scope／世代仍有效；否則重新預覽建立新作業並關聯前次部分結果，不能沿舊確認擴大集合。自動作業由成功同步及服務作業所有權授權，不依附人工帳號；其scope／世代變動同樣安全停止。驗5分鐘邊界、重送／逾時未知、撤權／恢復權限、scope變更、原作業續跑與新作業建立，精確核對刪除及剩餘集合。

### 4.10 清除與回退的四項執行補強（納入既有C54）

以下ID為既有契約的必要子項，機器清單為`round54ExecutionRefinements`；不新增正式角色、不擴張權限、不新增通用工作流框架。四項均待實作，前面概括的所有權／耐久／撤權／回退須符合本節，不能只以方法存在或文字提醒驗收。

**C54-03-O／父子作業所有權（F2實作驗收、G3整合）。** 自動清除是在`PrtgDailyPipeline`、`PrtgStructureSyncRunner`或回填內同步尚未結束時執行。合法子步驟必須繼承可核對父作業身分，不能直接套用`ScopePurgeConflict()`的「同步／取數／回填中就拒絕」，也不能等待父作業先完成。只有該父作業可委託自己的清除；外部競爭作業仍受互斥。父作業取消或失去所有權時，子作業在安全點停止且不得繼續開新批次。驗夜間同步→清除、回填內同步→清除正常成立；人工／自動競爭拒絕，父取消及重啟後無自我等待、無殘留所有權。保留C54-03的自動縮小／範圍保護。

**C54-05-A／每批次即時撤權（A2準備、F2實作驗收、G3跨程序整合）。** 保存操作者識別，批次與resume前透過既有權限規則重新判定當前帳號啟用狀態、群組與能力，不把原HTTP principal／claims或`ICurrentUser`快照當當前授權。`ActiveUserMiddleware`只在HTTP入口刷新，`PermissionVersionStamp.Current`有行程快取；雙程序驗收須證明看到持久化權限異動，不假設快取跨程序同步。Core接收Web權限單一來源的判定，不複製角色規則。驗停用帳號、移出Admin、停用授權群組，操作者不再送刷新HTTP請求仍能在下一新批前停止；已開始的不可安全中斷提交依C54-05到安全點完成。serverAdmin另驗設定撤用／重啟後的舊作業resume拒絕，不以普通帳號停用代替。保留停止原因與精確部分結果，不引入新正式角色。

**C54-03-T／刪除與進度提交一致性（F2實作驗收、G3雙provider完整副本）。** 每批已提交刪除及耐久進度／結果必須原子一致，或有可證明等價的恢復協定；現有`BatchedPrune`記憶體累計不能作耐久證據。分別注入提交前中斷、刪除已提交但回應／進度觀測遺失、結果送達前重啟；同識別重送回同一作業，已刪計數不重複、不遺漏，不加入新資料。與保留期／其他合法清理併行時，精確區分原接受集合、本作業刪除、其他作業已刪與剩餘集合；不能只用剩餘筆數倒推本作業功勞。驗SQLite及SQL Server各交易／提交未知路徑，保留守門資料與完整成功後才更新基準的契約。

**C54-04-B／回退啟動前防護（A2交付工具、B2時間授權驗收、G3／H最終回查）。** A2規劃並於功能輪交付具啟動前相容檢查的可執行回退控制工具，由它負責拒絕不相容的本輪受控回退路徑；不能假設舊二進位理解新authority，也不以操作文件提醒當拒絕證據。明列工具入口、輸入manifest、可接受目標及拒絕結果，不承諾能阻止繞過工具的任意外部手動啟動。還原Web或背景worker啟動前先隔離排程、來源寫入及通知派送；完整版本／DB／key／設定、備份後差額、待辦與通知去重核對通過才解除，任一步失敗維持隔離。不得先啟動再手動暫停。以帶待寄通知、待恢復工作及人工回覆的備份驗核對前零對外副作用，成功才受控恢復。B2驗時間授權；F2後新增清除狀態由G3及H1/H2對最終成品補驗回退相容性，舊B2證據不自動涵蓋後續schema／作業變更。不新增通用跨版本遷移框架。

相依順序：A2準備現行授權與回退工具→B2驗時間授權回退；F2完成父子所有權、批次撤權與提交一致性→G3以雙provider／完整副本補驗→H1完整性核對、H2有效最終回歸與交付。相同狀態有效證據可引用；F2改到持久作業／schema時，須重新判定回退證據有效範圍，不能把早期B2結果直接當最終版通過。

## 5. 再次比對與開工條件

初次盤點：原§5.8共28子項，其中E10-T01已完成、27項未勾。移交當時承接27個原ID，未新增驗收門檻或放寬原契約；2026-10-14另定案的§6權限修正與§4.5清單工具補強已回掛本輪批次。2026-10-13再次比對原§5.8與本文件§3：27個未勾ID集合完全相等、無重複，E10-T01不重開；N1–N8均在§3.1承接，四組實作缺口均回掛原E項。第53輪已補原生狀態碼、冷啟動、耐久ID、郵件工作流及訊息解析版本／舊證據隔離修正；不可將私人候選整份覆蓋回舊版。收尾後核對最終程式基準與新增修正；建立下一輪最終基準、可沿用證據／需重驗差異與範圍後才開工。

第53輪已實作部分已完成收尾；本輪以a69fedd7為起點承接剩餘工作。本次規劃及清單更新不宣告功能完成、驗收通過或PRTG完整實用。功能開工前核對工作區與基準差異，先完成A剩餘資源／成本預檢；既有artifacts／docs/env未追蹤資料不納入提交。

## 6. 已定案、待實作：全域清除的維護權與明細閱讀權

2026-10-14使用者依審查建議授權更新規劃：保留Maintain執行既有全域PRTG維護的能力；清除預覽中的裝置名稱等業務明細另依既有可見範圍過濾。serverAdmin維持Maintain／ViewAudit、不新增ViewAll，只顯示必要數量及不含業務明細的阻擋原因。目前一般Maintain由Admin取得且同時具ViewAll；不為測試建立受限Maintain正式角色。授權元件仍防禦性地依可見集合過濾；同時具有Maintain與ViewAll者可讀完整預覽。

已核對`RoleCapabilityMap.ForServerAdmin()`明定無業務檢視權，而`SettingsController.PreviewPrtgScopePurge()`直接傳回`TopDeviceNames`。這是既有設計邊界的修正，不宣稱本輪引入漏洞或已發生外洩；正式程式尚未修改，現況規格須待實作後同步。

- 範圍：F批次，回掛E05-T02／E08-T03；包括preview DTO、畫面、直接API及錯誤／阻擋原因，沿用既有授權單一來源，不在Core複製角色規則。
- 輸入／輸出：可信全域清除集合與登入者權限，輸出必要全域維護數量、可見明細及安全原因；全域清除集合與既有安全閘門不因明細過濾改變。不可用先取全域前20筆再遮罩冒充完整可見前20筆。
- 驗收：serverAdmin、具ViewAll的Maintain、具ViewAll但無Maintain、受限且無Maintain四種真實登入情境，實際API／UI核對拒絕或精確可見集合；受限Maintain僅於元件合成測例核對過濾，不冒稱真實角色操作；以不可見裝置名稱／識別作反例，確認欄位、提示與直接回應不外洩。依§4.6的預覽版本、互斥及部分恢復契約執行；相同scope下不同閱讀權的清除候選集合一致；權限不足、範圍不可信或過期狀態仍拒絕操作。清除僅在自有隔離副本驗證。

## 7. 本次規劃更新的範圍

本次修改本PLAN、`scripts/round53-user-matrix/coverage-inventory.json`、`user-matrix.json`及同目錄README，保留原27個未勾ID、22 AC／10 U53／6 Q、7來源情境、8族222案及53條承接缺口，另列8項R54補強、5項C54契約修正、4種真實維護登入情境及1個合成元件防禦測例；不把措辭修正記為功能通過。同步Q1／Q2對照與代理操作責任，保留既有未通過旗標。全部既知缺口及§6調整在本輪處理，不另移待辦或下一輪。尚未執行功能測試、容量或恢復驗收。

前次文件驗證（不涵蓋本次新增R54／維護矩陣）：`Test-UserMatrixManifest.ps1`、`Test-UserMatrixCoverageInventory.ps1`均通過；另逐集合核對全部Q的journey與主矩陣一致、原27個TODO順序及集合不變、53條缺口與未驗收旗標保留。`git diff --check`通過。以上僅為規劃／清單一致性結果，不是A的容量預檢或B～H驗收。

前次八項補強文件驗證（當時5種維護情境分類已由本次C54-01修正）：兩支既有Manifest／CoverageInventory檢查通過；另以獨立結構核對確認R54-01～08共8項／24條必要檢查、5種維護情境、原TODO／AC／journey引用及全部Q對照一致，原27個TODO不變、53條承接缺口保留，新增項皆無通過證據。第54輪版本要求、未實作／未建立角色旗標與字元單位一致，git diff --check通過。此為本次文件審核，未實作或執行新增角色、清除、時區、容量及恢復功能。

本次C54-01～05文件驗證：既有Manifest／CoverageInventory檢查通過；補充核對4種真實登入＋1個合成元件測例分離、不建立帳號／不擴張權限旗標、5項C54至R54與階段對照、全部Q關聯及原27個TODO／53條承接缺口不變。R54及C54均待實作、無通過證據；git diff --check通過。本次僅修改PLAN、兩份JSON與README，未修改功能、fixture或驗證腳本。

本次規劃更新範圍：4項§4.10子契約全部回掛既有C54／R54，同步PLAN、coverage inventory、user matrix即時授權驗收中繼資料與README；正式角色、權限模型及功能程式均未修改。

本次§4.10文件驗證：Manifest及CoverageInventory既有檢查通過；另逐項核對4個補強ID／12條必要檢查、C54反向引用、R54及階段對照、角色即時授權中繼資料一致。原27個TODO、53條承接缺口、六個基本角色與四個真實維護情境／一個合成測例保持；所有新增項均待實作且無通過證據。git diff --check通過，差異僅四份規劃／清單文件。

## 8. 本輪實作紀錄（2026-10-14開始）

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
