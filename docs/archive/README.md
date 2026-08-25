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
