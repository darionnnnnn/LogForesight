# 第 54 輪規劃：PRTG 剩餘實作與完整驗收

> 狀態：逐ID移交複核完成；本文件只規劃下一輪，不授權現在接續開發下一輪功能。
> 來源：2026-10-13 使用者明確要求將第53輪尚未實作項目移入下一輪，先收尾已實作部分。此為範圍移交，不代表原目標、容量、實用性或驗收已通過。
> 盤點基準：`feature/prtg-feedback-53`／`adb656067d48bd94aabf5ac8e0cf86a7d668060e`，另納入本輪收尾審查已辨識的接線缺口；下一輪實作起點須另核對第53輪最終提交。
> 原始需求與契約：[第53輪 PLAN](FEEDBACK-53-PLAN.md) §§3、6、13、15–17；最新責任邊界為 §§5.7、5.9。

## 1. 定位與不變契約

PRTG 輔助 NetIQ 已合格的每日主機日；成功零事件父紀錄也可加入獨立 PRTG 風險理由，缺父、父失敗或證據不合格只顯示等待。兩來源取較高級、不相加；同日弱佐證不升級。CPU／memory 預設壓力提示，Maintain 同版試算後才可啟用正式風險；磁碟低水位及可信趨勢可成正式理由。

固定條件不因跨輪調整：連續兩完成小時、每小時四個 physical slot、午夜歸後一小時的主機日；可信磁碟28日準備度、預估7日內降至20%剩餘門檻；初始化72小時、資格作業720小時且每顆一次attempt；table每秒2次、在途4、historic每60秒5次；單HTTP≤30秒、接受取消後≤5秒禁止新請求並退出可取消等待。

隔離負載為3,000 hosts／15,000 sensors／180日／64,800,000 hourly，slot與hourly不可混算。15分鐘輪≤10分鐘／sample age p95≤30分鐘；5分鐘輪≤3分鐘／age≤10分鐘；時間餘裕≥25%，資料卷25%空間另計。NetIQ三對六輪p95增量≤10%、3,000主機日warm≤10分鐘且每日封存窗口75%內；兩路由各模式每對100成功、共1200成功，相對p95≤10%與絕對≤2秒；PRTG額外常駐≤1GiB、三輪高水位增長≤5%、停寫兩間隔後四間隔清積壓、耐久接受丟失與重複sample/finding/member均為0。

AC01–AC22、U53-01–10、Q1–Q6及八規則族固定222正反例全部保留；不得縮分母、刪失敗、以零延遲或控制時鐘冒充容量結果。探測只取得來源事實，不在正式環境驗收。UC01–UC07為使用者確認提醒，不列開發驗收、完成率或阻塞；未確認不得宣稱現場全面通過或真實事故增益。

## 2. 尚未整合完成的實作

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

既有SQL有界讀取機制尚未覆蓋所有正式consumer：`PrtgResourcePressureModeStore.Find`仍讀完整blob，供`PrtgResourceProfileQualification.IsCurrentFormalPressure`使用；binding Save／RecordQualification與qualification ResumeExisting仍整份選取settings／policy；disk semantic／verification map的replay、mail與data-transfer路徑也有直接Get／GetAll。已存在的256KiB mode及32MiB disk snapshot上限不能因consumer未接線而繞過。

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
- [ ] **E10-T03【承接驗收】** 在最終凍結同版完成受影響驗證及完整全量回歸，記命令／母體／SHA／環境／結果；舊全量7851通過／35失敗／10可選略過須保留原結果；本輪收尾修正後結果見第53輪收尾紀錄，不以定向測試改稱全量全綠。
- [ ] **E10-T04【承接交付】** E01、E03–E09全部必要條件通過後，同步PLAN／AC／現況規格／Help／交付限制與證據可取得性；提交與推送須有本次有效授權，交付時確認遠端SHA。本輪僅完成移交與收尾；下一輪功能開工須依後續使用者指示，原逐段commit/push規則保留。

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

## 4. 批次順序與裁定

1. D54-01：時間授權接線完整後，先跑最小歧義／漂移反例，再走production collector與正式讀側，補受影響probe／UI。
2. D54-03先完成生成／續跑工具的故障注入與安全修正；D54-02完成HTTP契約否定矩陣與owned identity後，才在完整資料副本執行production全量準備。D54-04的正式consumer有界讀取須先完成，再進E06／E07共同負載。
3. 按E01→E03／E04→E05補同版來源與角色鏈；E06／E07成本與空間預檢前移，固定全量資料、I/O、三對六輪及復原不得延後到上線。
4. E08完整工作流與六角色、E09四基準與證據查找，最後E10原需求／子句／全部phase回查及最終同版全量回歸。

每段完成相依改動後由主代理獨立驗證再commit／push至使用者指定的當輪分支，確認remote SHA；不用AGY，subagent只使用gpt-6-luna low／medium／high。本輪審查模型與過去實作者可能相同，記錄實際身分，不宣稱跨模型獨立背書。全量資格／效能使用真實單調耗時；不可把50顆功能性測試或start-only作全量完成。

完整回填目前已停止，checkpoint記錄41,140,900筆；重新執行前驗owned manifest、checkpoint、pending batch及凍結tool identity，再以原root與原64,800,000目標續跑，不縮資料、不直接改checkpoint。此數字為暫停水位，不是完整生成或容量PASS。

## 5. 再次比對與開工條件

初次盤點：原§5.8共28子項，其中E10-T01已完成、27項未勾。本文件承接27個原ID，未新增驗收門檻或放寬原契約。2026-10-13再次比對原§5.8與本文件§3：27個未勾ID集合完全相等、無重複，E10-T01不重開；N1–N8均在§3.1承接，四組實作缺口均回掛原E項。第53輪收尾後核對最終程式基準與新增修正；建立下一輪最終基準、可沿用證據／需重驗差異與範圍後才開工。

本文件當前未宣告實作完成、驗收通過或已證明PRTG完整實用；第53輪已實作部分的收尾審查與修正仍由第53輪負責。

## 6. 接續前須釐清的既有權限語意

既有全域PRTG清除入口依Maintain授權，而serverAdmin可Maintain但無ViewAll。這是本輪起點即有的管理權限設計，尚無證據判為本輪引入漏洞；下一輪先定義全域維護可否讀取清除預覽中的主機資訊，再決定是否加可見範圍限制。此項為設計待討論，不自動新增驗收門檻，也不在本輪擅改既有管理權限。
