# docs/archive — 歷程資料夾索引

> ⚠️ **非必要不要讀取這個資料夾。** 這裡只有「當時怎麼決策、怎麼實作」的過程記錄，
> 沒有現行事實。要知道系統現在怎麼運作，一律讀 `docs/` 主目錄的現行文件
> （`WEB-SPEC.md`／`DETECTION-SPEC.md`／`RULES-SPEC.md`／`DB-SPEC.md`／
> `LINUX-RULES.md`／`NETIQ-API-REFERENCE.md`／`DESIGN-SYSTEM.md`／`BACKLOG.md`）。
>
> 這裡的檔案動輒上千行，整批掃描會大量消耗 token 且讀到過時敘述。需要查證某個
> 決策的來龍去脈時，用下表定位**單一檔案**再開。

## 索引

| 檔案 | 內容一行摘要 |
|---|---|
| `HISTORY.md` | 2026-07-20～07-28 期間 10 份規劃案的逐字彙整（初版擴充規劃、AI 角色、NetIQ 主機清單／Web 設定、SQL 儲存後端、規模化 2000 台、Web 回饋一二輪、共用標準、維運強化）。 |
| `WEB-SCHEDULER-PLAN.md` | 排程 Web 化與風險 log 暫存（Phase 1~5），批次 console 專案退場的出處。 |
| `FEEDBACK-3-PLAN.md` | 使用者實測回饋 8 項（批次／NetIQ 2 項＋Web 6 項）。 |
| `FEEDBACK-4-PLAN.md` | 問題案件化與查詢視角擴充、詢問 AI 現場取數、處理人員工作頁。 |
| `FEEDBACK-5-PLAN.md` | 介面細節收斂、批次群組維護、設定頁頁籤、規則庫初始化缺口。 |
| `FEEDBACK-6-PLAN.md` | 排程 Web 化落地與 console 退役（五項回饋）。 |
| `FEEDBACK-7-PLAN.md` | 排程 UI、網段範例、AI 單一開關、立即執行失敗修復、console 專案退場。 |
| `FEEDBACK-8-PLAN.md` | 等待動畫統一、執行進度條、處理狀態「觀察 N 天」等七項。 |
| `FEEDBACK-9-PLAN.md` | 十項回饋＋文件整理＋appsettings 精簡＋NetIQ 連線預設（§1~§13）。 |
| `FEEDBACK-10-PLAN.md` | 十二項回饋，含案件授與端到端與越權缺口修補。 |
| `FEEDBACK-11-PLAN.md` | 八項回饋；負責人成為第二條授權路徑、主視角定為問題事件。 |
| `FEEDBACK-12-PLAN.md` | Linux 取數與 SSH 關聯全面落地，Linux 主機與 Windows 走同一條管線。 |
| `FEEDBACK-13-PLAN.md` | 23 項審查清單＋3 項補充（批次 A~G）。 |
| `FEEDBACK-14-PLAN.md` | P0~P3 六項＋UI 六項（批次 A／B／C／E）；批次 D 通知管道暫緩。 |
| `FEEDBACK-15-PLAN.md` | 規則管理 R1~R4、告警 A1~A4＋操作說明書／登入錯誤訊息／郵件通知。 |
| `FEEDBACK-16-PLAN.md` | 外部審視九項發現＋使用者六項（批次 A~F），含郵件三項行為修正。 |
| `FEEDBACK-17-PLAN.md` | 外部審視發現＋使用者八項（批次 A~I）。 |
| `FEEDBACK-18-PLAN.md` | 批次 A~H；狀態顯示文字與上報信的實作偏離記於文末兩節。 |
| `FEEDBACK-19-PLAN.md` | 問題主視角一次到位（批次 A~I）：問題聚合走 SQL、機房級基準線與首見、PriorityScore、郵件問題優先。 |
| `FEEDBACK-20-PLAN.md` | 外部審查七項＋使用者回饋十九項（含終檢輪）：問題主視角收尾、首見日浮水印閘門、OpenAI／Azure provider、只補跑失敗或未執行旗標。 |
| `FEEDBACK-21-PLAN.md` | 外部審查四項（補跑旗標／雲端 provider 申報／首見日增量／小項）＋使用者回饋十一項：需補跑判定單點化、回望天數 30、風險類型卡改問題類型數、Linux 問題可指派＋負責人自動交辦、NetIQ 權限異動待辦。 |
| `FEEDBACK-22-PLAN.md` | 使用者回饋四項：SQLite 預設路徑移入 `Db\` 子資料夾（不搬移舊檔）、移除權限異動每主機日筆數上限與彙總列、明細改為保留換行＋通用 key/value 拆欄、品牌名稱與副標的字級與間距。 |
| `FEEDBACK-23-PLAN.md` | 權限異動待辦列表白話化：異動對象不再錯置事件來源名、帳號顯示短名（DN 取 CN 值）、異動說明改「操作者→動作→對象」白話句、DTO 補 EventId。 |
| `FEEDBACK-24-PLAN.md` | 權限異動解析修正與降噪：NetIQ 攤平單行訊息的「行內多對＋區段感知」解析、欄位對應設定（自訂欄名→語意角色）、例行同步成對合併（限對稱模式、特權群組除外）、存量列重剖回填。 |
| `FEEDBACK-25-PLAN.md` | IIS 子 Application 路徑前綴機制（LF_BASE／appUrl／appPath／cookie 範圍化與舊 cookie 清理）＋品牌版面整組置中與文字視覺貼齊圖示。 |
| `FEEDBACK-26-PLAN.md` | 品牌副標題對齊（共用 partial＋字距貼齊）／權限異動說明（彙總句首、涵蓋區間、4670 物件權限變更分流與重剖）／報表甜甜圈修正／保留期下限 90／問題查詢（欄位換行、期間快捷共用與「昨日」、分類取最近一天與 Linux 白話說明）。 |
| `FEEDBACK-27-PLAN.md` | AI token 用量統計／佇列進度回報／權限異動彙總列／報表效能／說明書雙版本（aiFile 機制）。 |
| `FEEDBACK-28-PLAN.md` | 19 項回饋：保留鍵合併（RawEventRetentionDays）與預設拉長／回望上限動態化／控制項高度基準統一撤像素補丁／四頁期間快捷統一／「權限異動檢核」改名與 raw_text 全文／儀表板 KPI 收編／log 每日歸檔分流 error／說明書 11 章 AI 版補齊／報表比對前期兩級提示。 |
| `FEEDBACK-29-PLAN.md` | 登入可診斷性（cookie Secure 跟隨連線含 X-Forwarded-Proto／serverAdmin 補 web.log／PasswordHash 格式 fail fast／登入頁 module 失敗防呆）＋儀表板「未處理問題」KPI 與下鑽同口徑（補套可見嚴重度、去重改大小寫不敏感）＋郵件摘要與群組未處理數口徑統一。 |
| `FEEDBACK-30-PLAN.md` | 登入失敗誤判分辨（結構化明細／殘留憑證跨日確認制／關聯與趨勢下游修正／密碼噴灑偵測）＋NetIQ 探索改良（提早停頁、背景工作化、網段分割、掃描粒度）＋規則補強（seed v4→v5、ModifiedBy 分流、既有規則降噪）。 |
| `FEEDBACK-31-PLAN.md` | 規則更新後舊日重新分析：立即執行改四模式下拉（依處理狀態分級重跑）＋紀錄層 DeleteDays＋RerunDateFinder＋兩條路徑逐日就地取代（來源無資料保留原結果）＋修 OnlyMissingOrFailed 從未生效／JSON enum 綁定／先刪後分析／趨勢基準自污染等真 bug。 |
| `FEEDBACK-32-PLAN.md` | 報告檔依主機＋年月分子目錄（年月由檔名日期前綴推導，舊檔不搬）＋清理後移除空目錄；報告保留期拆成獨立設定 `ReportRetentionDays`（預設 1095，刻意不與 `RetentionDays` 做大小約束）；NetIQ 掃描分段平行化（併發 1~3 由掃描列每次自選，每段各自一份 `ScanState`＋client pool）＋修預算用盡時「未掃描網段」用 `Skip(已完成數)` 會指錯段、分段中斷時涵蓋警告遺失等真 bug。 |
| `FEEDBACK-34-PLAN.md` | 排程記憶體 22GB 有界化（本機回補分塊掃描 14 天/塊、權限異動去重改逐主機日查 DB 移除整窗鍵快照、Sentinel 取數分頁串流分組、RawText 截斷 8000 字＋寫入每 500 筆分批、Host/User 單筆查找去複製）；使用者名稱顯示規則（AD 分頁正則設定，套在顯示層短名化之後，含登入失敗明細擴接；入庫原文不動）；立即執行兩個天數欄位合併為單一回望天數（缺的補、有的按模式重跑，本機與 NetIQ 皆適用，`rerunDays` 全鏈移除）。委派 agy 兩度卡住改自做；終檢抓到共用 IP 桶子誤刪、本機留空被多夾、前端 JS 驗 .NET 正則語法誤擋；換模型體檢確認終檢手改無新問題。 |
| `FEEDBACK-33-PLAN.md` | 報告全文改存資料庫（`lf_reports`），移除 `export\` 檔案輸出的全部實作（sink／reader／pruner）。三個寫入端改綁真實主機，結構性修掉多主機同日同風險同類別報告互相覆蓋的檔名碰撞；讀取改走「主機×日期×種類」自然鍵（三種報告同一條路徑、舊參照不必改寫）；`ReportFileMigrator` 一次性遷入舊檔（紀錄驅動歸戶，舊檔保留）；`ReportRetentionDays` 預設 1095→180 且上限收斂 ≤ `RetentionDays`（推翻三十二輪「不互相約束」——檔案補償路徑已消失），設定頁加空間告知（實測份數＋MB）；Web 補體檢／權限異動報告入口與下載 txt（共用 `report-view.js`）。體檢輪抓到 upsert 鍵與讀取語意不一致（未登記列認領）＋終檢補認領查詢排序防唯一索引衝突。 |
| `UX-AUDIT-2026-08-05.md` | 四角色實際登入的全面 UX 體檢報告（只提問題，不含實作）。 |
| `SCALE-ISSUE-FIRST-PLAN.md` | 規模化（2000~6000 台）與問題主視角的規劃案。 |
| `SCALE-REVIEW-2026-08-06.md` | 上述規模化改版的體檢報告（14 項問題）。 |
| `SCALE-FIX-PLAN-2026-08-06.md` | 針對該體檢 14 項問題的修復規劃。 |
| `NETIQ-DISCOVERY-PLAN-2026-08-06.md` | NetIQ 主機探索成本改善＋開發環境 console 編碼修復。 |
| `SCALE-3000-PLAN.md` | 三千台規模化：主機清單快取、兩層保留期、處理狀態與報表／儀表板 SQL 下推、年度同期比較；含實測數字、驗收所得與委派紀錄。 |
| `PERMISSION-CHANGES-PLAN.md` | 權限異動待辦頁改版（2026-08-20）：JSONL＋blob 正規化為 lf_permission_changes 真表（確認狀態同列）、舊資料遷移、帳號擷取分區段修正、分頁篩選排序、批次核准、前端表格化；含終檢與體檢的完整發現記錄。 |

## 給 AI 助理的提醒

預設只讀 `docs/` 主目錄的現行文件。只有在使用者明確問到「當初為什麼這樣決定」、
或需要查證某項歷史決策時，才依上表開**單一**檔案，不要整個資料夾掃過去。
這裡的敘述停留在寫作當下，與現行程式碼可能已不一致——衝突時以現行文件與程式碼為準。
- `FEEDBACK-35-PLAN.md`：回饋第三十五輪——取數／AI 分析拆成兩個獨立排程（ai_pending 欄位為事實佇列、強制重新分析、完整性閘門）、使用者名稱顯示規則全站化（含指派下拉置頂契約）、執行總表「全部」＋伺服器端分頁、記憶體第二輪有界化（取數欄位過濾／GC 設定）、儀表板報表整包快取（版本戳＋TTL）；換模型體檢修十個真 bug（AI 補跑輕量投影、快取鍵漏稽核維度、NeedsBackfill 與 AI 排程打架等）。
- `PRTG-1-PLAN.md`：PRTG 整合第 1 輪（鏡像層）——五張 `lf_prtg_*` 表、PRTG 設定頁籤與加密 apitoken、`PrtgClient`、環境探測 probe（sensor type 分布／IP 覆蓋率，用來決定後續分析層怎麼設計）、每日擷取器（掛進夜間批次第三條並行路徑）、以 IP 對應 NetIQ 主機主檔、歷史回填、保留期。修訂了原計畫書六項前提（`prtg.*` schema→`lf_prtg_*` 前綴、UTC→本地時間、沿用的 AI 管線範圍、一 IP 多主機、移除分割區、NetIQ 維運事件覆蓋）。終檢抓到擋路級的前端 import 遺漏與四個會讓資料靜默流失／批次卡死的問題。分析層（弱訊號、訊號合成、敘述化）不在本輪，見 BACKLOG。
- `PRTG-2-PLAN.md`：PRTG 整合第 2 輪——認證方式二選一（API token／帳號密碼，帳密走 PRTG 的 passhash 流程，密碼只在換取雜湊時出現一次）、`PrtgClientFactory` 收斂四個 client 建立點並清掉三份重複的憑證解密、環境探測預設收合。範圍刻意只做到「拿得到測試資料」——舊版 PRTG 沒有 API token 功能會讓整個模組不能用，其餘 UI 重構（獨立維護頁、回填搬排程頁、主機頁面對應整合與手動對應）遞延，見 BACKLOG。終檢抓到兩個高嚴重度：密碼錯誤會讓每個 sensor 各打一次 getpasshash（足以觸發 PRTG 帳號鎖定）、切換認證模式時隱藏欄位的殘值會靜默清空或覆寫另一組憑證。
- `FEEDBACK-36-PLAN.md`：回饋第 36 輪——SQLite 慢查詢與 PRTG 探測解析。慢查詢根因是複合的：`lf_daily_records.risk_level` 無索引、`event_id IN + 日期範圍` 只有以 record_date 為前導的索引（等於掃整段期間）、6GB 檔配上零 PRAGMA 調校、以及 `Pooling=False`（規避 EF user-function 釋放 bug）造成每查詢冷 cache。補兩個等值前導索引（雙後端皆建）＋ Sqlite 專屬 PRAGMA 攔截器＋為唯一沒有查詢層快取的 `ActionableOccurrences` 補上短 TTL 快取。探測面修好「unit 樣本恆為無」（PRTG sensors 表根本沒有 unit 欄，改由 lastvalue 推導）並新增 Type×IPv4 交叉統計，供下一輪縮圈規劃使用；探測與回填按鈕的錯誤不再被空 catch 吞掉。
- `PRTG-4-PLAN.md`：PRTG 整合第 4 輪——**取數縮圈＋分析層第一步**。實機探測顯示 42,393 個 sensor 逐一抓 hourly 數值一晚跑不完，改為**觸發式取數**：只對「NetIQ 判定高／中風險 ∪ PRTG 規則命中」的主機、經 `ok` 對應的 device、且 type 命中白名單的 sensor 取數，以輪詢與分析並行（NetIQ pipeline 沒有單一主機完成的掛載點，硬插回呼要動並行迴圈本體，故改為定期查已落地的分析結果）＋收尾掃描補 AI 事後上調風險的主機。同輪完成 UI 重構三項（獨立維護頁 `/admin/prtg`、回填與總開關搬排程頁、主機頁整合＋人工對應新表）、規則第一階（四條狀態變更型規則，finding 以 `EventId=0`＋`EventKey=prtg:{code}:{objid}` 進 `lf_top_issues` 全鏈）、以及跨後端資料搬運。收尾體檢抓到六項正確性缺陷，其中兩項高嚴重度：`PrtgUsername` 未提供時被清空（設定頁搬家後同一模式的第二次犯，第一次只修了發現的三個欄位而未全面盤點）、finding 追加與分析並行導致當日紀錄尚未寫入而幾乎全數丟棄（批次F 價值歸零）。
- `PRTG-3-PLAN.md`：PRTG 整合第 3 輪——認證方式新增第三種 `passhash`（使用者自行提供 username＋passhash，系統不保存密碼、不呼叫 `getpasshash.htm`）。`PrtgClient` 把原本單一的 `_isPasswordMode` 拆成 `_usesUsernameAuth`（組 URL 與帳號必填）與 `_needsPasshashExchange`（是否去換 passhash），新模式靠建構時預填 passhash 快取達成，組 URL 與遮蔽零新增分支。終檢抓到一個高嚴重度：規劃階段誤判「passhash 模式不需要憑證黏住」，實際上資料請求的 401 一樣會累加 PRTG 帳號鎖定計數——補上資料請求層的黏住（401 黏、403 不黏、token 不黏）。
- `FEEDBACK-37-PLAN.md`：回饋第 37 輪——校準數值匯出、PRTG 設定收斂與進度顯示、技術債清理。新增 `/admin/calibration` 判斷四個校準項（值型基線／規則門檻／觸發式量級／殘留判定）的資料量是否達標並一次匯出自描述 JSON（含最低門檻逐日重評得到的 magnitude 分佈與分位數，殘留資料集只有統計形狀不含帳號）；PRTG 六個擷取參數集中到維護頁並開專屬更新端點（整包請求的 PRTG 欄位一律可空、跨欄位檢查用 effective 值）；夜間擷取第三條進度軌與回填兩級進度。技術債：diag/ 傾印上界、最近對應日期聚合（含回填器同型遺漏）、規則對照表單一來源、4740 帳號與跨日關聯 IP 改讀結構化欄位（顯示字串只列前 5 個，第 6 個以後永遠比對不到）。委派 agy 十五段；換模型體檢抓到跨日數整欄恆 0（不匹配行尾的替換靜默失敗）、候選歷史 N+1、門檻第五份手抄等十四項。
- `FEEDBACK-38-PLAN.md`：回饋第 38 輪——PRTG 維護頁重組、衝突對應操作、排程狀態卡與執行紀錄結構化、資源守門。維護頁拆四頁籤（連線／擷取參數／鏡像狀態／環境探測）並支援網址 hash 直達，校準匯出與資料搬運併入環境探測頁籤、舊路由轉址。衝突清單改分頁並依型別分岔指派（同 IP 多裝置要挑裝置、IP 對多主機要挑主機，過去混在同一個下拉導致使用者選不對）；新增 IP 排除清單（新表 `lf_prtg_ip_excludes`），並補一條「同 IP 已有人工指定時其餘裝置一律略過」——少了它，管理者挑完一台之後剩下那台會因分組只剩一個而自動對到同一主機，選擇被繞過。排程狀態卡補本機軌完工訊號、三軌完工改為保留數字並標記完成（清空會讓軌憑空消失）、不定進度帶軌別。執行紀錄新增九個分路結構化欄位並分欄呈現。**錯誤歸屬修正**：`BatchRunRecorder` 的 NLog target 原本收全行程 Warn，夜間批次會把前景慢 SQL、互動 AI 逾時、AI 排程失敗全記成自己的問題（使用者據此誤判「本機一直有錯」）——改以 `ScopeContext` 只收自己非同步流程內的事件，慢 SQL 照記但不計入警告數。本機分析改走 AI 分析排程，取數執行內不再有同步 AI 呼叫。新增資源守門：從 PRTG 讀 NetIQ 與 PRTG 主機的 CPU／記憶體即時值，連續超標即暫停取數，累計暫停有上限以免門檻設錯造成整晚空轉。終檢抓到兩類斷點：**規劃定案漏抄進規格**（兩批次共三條，逐段驗收無訊號，靠 44 條定案逐條比對抓出）、**跨段產出鏈斷裂**（`prtgTriggeredHosts` 後端寫入但前端渲染未接參數，前後兩段各自都綠）。
- `FEEDBACK-39-PLAN.md`：回饋第 39 輪——排程作業頁重排（三張狀態卡＋四頁籤）、PRTG finding 進日風險與 AI 敘述、AI 與取數並行、取數範圍設定、排程流程梳理。執行總表錨點改昨天；PRTG 結構同步三階段各自回報進度、messages 帶 `filter_drel`；新增帶日期的 finding 登錄簿，finding 在兩條寫入路徑當場追加並排在案件掛接之前，以 `prtg-findings-ready` 訊號讓 AI 排程不再等整趟取數；追加時單向上調日風險（`PrtgRuleCatalog` 的 `ElevatesDayRisk` 過去是死值）；取數範圍三選一＋估算端點，`all-mapped`＋空白名單設定層與執行層兩道閘門；phase 集中到 `RunPhases`、PRTG 路徑抽成 `PrtgDailyPipeline`、三軌進度收成值型別。**終檢抓到**登錄簿缺日期維度（回補多天會把昨天的 finding 掛到每一天）、`all-mapped` 第二道防線漏做、窗口誤報、`filter_drel` 邊界差一；**體檢（換模型）抓到** AI 回寫無條件覆蓋會蓋掉 PRTG 上調、兩條追加路徑對同一主機日的並行競態、「下一個窗口補跑」文案錯誤。agy 額度於第一批後耗盡，其餘由 Claude 自做。
- `FEEDBACK-40-PLAN.md`：回饋第 40 輪——PRTG 位址比對、同步入口、AI 跟隨取數、狀態卡版面、資源守門搬家。**根因是位址正規化**：原本只做 trim 加小寫，實機的「IP 加 port」與 DNS 名稱因此永遠比不到，主機對應一直是空的，連帶讓資源守門的自動偵測與觸發式取數全部落空。改為兩層——純語法層（去 scheme／路徑／port、去前導零、非 IP 回 null，取代 `NormalizeIp` 的實作，二十個呼叫點不動）與解析層（名稱走 DNS、實例內快取含失敗結果）。守門的比對鍵定為三段優先序（IP、DNS 解析出的 IP、名稱字面），第三段是既有能力，語意收緊時差點弄丟。新增手動「同步結構與對應」背景工作補齊鏡像並重算今天的對應，與取數執行雙向互斥（取數中拒絕啟動；同步中取數先等再跳過自己的結構同步，新 phase `prtg-wait-sync`）。AI 移除獨立啟用開關，改為取數一發佈當日 finding 就立刻開跑，窗口只管背景消化積壓。`all-mapped` 取數改單輪收工（候選與分析無關，原本空等到分析結束）。狀態卡三卡等高（grid stretch＋flex column＋按鈕列貼底，不寫死 min-height），第三張卡從「PRTG 歷史回填」擴為「PRTG」並承接每日擷取軌。資源守門搬到設定頁新頁籤（它節制 NetIQ 與 PRTG 兩路），前端抽成 `prtg-guard.js` 讓實作只有一份。**終檢抓到的最嚴重問題是抽模組時多切三行且漏帶 import**——模組載入即擲 ReferenceError，設定頁與維護頁兩頁的 JS 全都不執行，而語法檢查與字串比對測試都看不到，因此新增 `JsModuleImportTests` 讓同型問題在建置時就紅。另外靠 Claude 自做的定案逐條比對，抓到整個批次 B2（對應重算的觸發點）漏做，而文件已先寫了「主機新增或改 IP 會重算」——兩個終檢代理都沒抓到這條，因為代理看 diff，看不到「規劃有寫、diff 沒有」。換模型體檢（Fable 5.1）另抓到 19 條：最重的是等待手動同步逾時後仍跳過結構同步（上限加了、退路沒接上）、守門 import 測試只認 `fn(` 抓不到 `api.` 這種原始事故的形狀、`RemapWarning` 後端加了欄位前端沒接。
- `FEEDBACK-41-PLAN.md`：回饋第 41 輪——PRTG 位址解析對裝置側亂值做 DNS、阻塞式逾時、啟用開關併入取數範圍、排程頁按鈕互斥。**根因**：守門偵測對每一台 PRTG 裝置的 host 送 DNS，PRTG 上一台佔位裝置的 host 字面是 `10.2xx.x.x`，解析器 `Task.Run + Wait(2000)` 逾時後執行緒不回收、例外在 lambda 內擲出讓偵錯器中斷。改法：`PrtgAddress.IsDnsCandidate` 只讓像主機名稱的值進 DNS（壞 IPv4 判定經三次收斂，最後定為「第一段全數字且每段只含數字或 x」），DNS 改 `GetHostAddressesAsync` 一秒可取消、介面維持同步；守門偵測裝置側 DNS 延後到字面比對之後並以整趟預算 20 次保險，裝置依 objid 排序讓預算命中確定；主機對應（§4 定案）不動。探測「IP 覆蓋概要」改與主機對應同一份判定分四桶，壞值列 objid。第二段回饋：`PrtgEnabled` 入口從排程頁 switch 併進維護頁「擷取參數」四選一下拉（關閉／三種範圍），舊 switch 與 `PUT prtg-enabled` 端點移除；排程頁 PRTG 卡狀態指路、未啟用時同步／回填閘住；立即執行在「連線已設定但未啟用」時提示（沒設 PRTG 的站台不問）；三張卡動作鈕改「執行中只留停止」。**逐條回檢**抓到三處規劃有寫、diff 沒有（WEB-SPEC 立即執行提示、`off` 為第一選項的斷言、第四個測試替身）；**體檢（Opus low 三代理掃、Fable 取捨）**修 11 條，最重是同一顆按鈕的 `disabled` 有三個寫入點只改一處、總開關變更不再留稽核、探測把 IPv6 列為壞值；**終檢**抓到體檢修正本身的規則既誤擋 `163.com` 又漏擋 `192.168.1.100x`、結尾點只在判定層剝。教訓：同一判定的寫入點要 grep 全檔而不是改看到的那個、判定規則收緊要拿真網域與真佔位值兩組反例一起測、sed 的 `
` 在替換端是真換行。
