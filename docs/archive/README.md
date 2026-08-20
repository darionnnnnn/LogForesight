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
