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
- `PRTG-4-PLAN.md`：PRTG 整合第 4 輪——**取數縮圈＋分析層第一步**。實機探測顯示 42,393 個 sensor 逐一抓 hourly 數值一晚跑不完，改為**觸發式取數**：只對「NetIQ 判定高／中風險 ∪ PRTG 規則命中」的主機、經 `ok` 對應的 device、且 type 命中白名單的 sensor 取數，以輪詢與分析並行（NetIQ pipeline 沒有單一主機完成的掛載點，硬插回呼要動並行迴圈本體，故改為定期查已落地的分析結果）＋收尾掃描補 AI 事後上調風險的主機。同輪完成 UI 重構三項（獨立維護頁 `/admin/prtg`、回填與總開關搬排程頁、主機頁整合＋人工對應新表）、規則第一階（四條狀態變更型規則，finding 以 `EventId=0`＋`EventKey=prtg:{code}:{objid}` 進 `lf_top_issues` 全鏈）、以及跨後端資料搬運。收尾體檢抓到六項正確性缺陷，其中兩項高嚴重度：`PrtgUsername` 未提供時被清空（設定頁搬家後同一模式的第二次犯，第一次只修了發現的三個欄位而未全面盤點）、finding 追加與分析並行導致當日紀錄尚未寫入而幾乎全數丟棄（批次F 價值歸零）。
- `PRTG-3-PLAN.md`：PRTG 整合第 3 輪——認證方式新增第三種 `passhash`（使用者自行提供 username＋passhash，系統不保存密碼、不呼叫 `getpasshash.htm`）。`PrtgClient` 把原本單一的 `_isPasswordMode` 拆成 `_usesUsernameAuth`（組 URL 與帳號必填）與 `_needsPasshashExchange`（是否去換 passhash），新模式靠建構時預填 passhash 快取達成，組 URL 與遮蔽零新增分支。終檢抓到一個高嚴重度：規劃階段誤判「passhash 模式不需要憑證黏住」，實際上資料請求的 401 一樣會累加 PRTG 帳號鎖定計數——補上資料請求層的黏住（401 黏、403 不黏、token 不黏）。
