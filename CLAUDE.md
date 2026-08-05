# CLAUDE.md — 專案入口地圖

LogForesight：分析 Windows Server（Linux 規則面就緒、取數未串）的 Event Log，
以確定性規則／趨勢／關聯層**提早發現硬體故障前兆與入侵跡象**；地端小模型只把結論翻成白話。
唯一的執行與查詢介面是 `LogForesight.Web`（ASP.NET Core MVC，.NET 8）。

這份檔案是**地圖不是百科**：先在這裡定位，再開對應文件。別把規劃內容寫回 README。

## 專案結構

- `LogForesight.Core/` — 分析邏輯類別庫。`Analysis/`（純規則/趨勢/關聯）、`Models/`、
  `Persistence/`（Sqlite/SqlServer 雙後端，`StorageBackend` 唯一路由點）、`Service/`
  （`AnalysisOrchestrator` 分析主流程單一入口、NetIQ pipeline、體檢）。
  沿用批次時期 `namespace LogForesight`（資料夾不對應命名空間）。
- `LogForesight.Web/` — 執行/查詢/維護介面。`Controllers/Api/`、`Services/`、`Auth/`、
  `Repositories/`、`wwwroot/js/`（原生 ES Modules：`core/` 共用、`pages/` 逐頁）、
  `Views/Pages/`（頁面殼，只回 View、資料由前端 fetch）。依 ASP.NET 慣例
  資料夾對應命名空間 `LogForesight.Web.*`。
- `LogForesight.Tests/` — xUnit 單元測試。

## 文件地圖：做什麼事讀哪一份

| 任務 | 讀哪裡 |
|---|---|
| 改 Web（頁面/API/授權/前端/設定） | `docs/WEB-SPEC.md`（§編號被程式碼註解大量引用，**勿拆檔**） |
| 改偵測邏輯/危險訊號清單/趨勢/關聯/AI 策略 | `docs/DETECTION-SPEC.md` |
| 改規則機制（語意邊界/seed/匯入/DB 映射） | `docs/RULES-SPEC.md`；Linux 規則面 `docs/LINUX-RULES.md` |
| 改資料庫欄位/索引/保留/Schema 升級 | `docs/DB-SPEC.md` |
| 改 NetIQ/Sentinel 取數 | `docs/NETIQ-API-REFERENCE.md` |
| 設計系統色票/字型/token | `docs/DESIGN-SYSTEM.md` |
| 查「已知但刻意未做」 | `docs/BACKLOG.md` |
| 追某個現況決策的來龍去脈 | `docs/archive/`（HISTORY.md ＋ 歷輪 FEEDBACK-N-PLAN，按需讀，勿全掃） |

## 慣例

- **分支流程**：自 `dev` 開 `feature/*`，完成後併 `dev` 給使用者實測、確認無誤才併 `master`。
  目前 `dev` 領先 `master`（架構重構＋UI v2 待實測），`feature/feedback-10` 待併 `dev`。
  不主動 commit/push，除非使用者要求。
- **測試**：`dotnet test`（根目錄）。改動需維持全綠——目前基線 **1356** 個測試。
  部署前驗證＝跑測試（規則合法性、遮蔽偵測、關聯層覆蓋皆為自動化測試，非手動 CLI）。
- **語言**：說明文字與註解用**台灣繁中**（專有名詞除外）。全站用詞規範見 WEB-SPEC §8.6a。
- **設定事實來源**：可調整項以 DB「系統管理 > 設定」（`SystemSettings`）為準；
  `appsettings.json` 只留啟動與安全前提（Storage/Jwt/Auth）。**新增設定必須有消費端**——
  「有設定無行為」是本專案紅線。
- **規劃案生命週期**：規劃寫 `docs/FEEDBACK-N-PLAN.md`，完成後移入 `docs/archive/`。

## 不要做

- 不要把偵測邏輯/規劃內容寫回 README（README 只留定位、結構、部署、操作）。
- 不要拆 WEB-SPEC（會斷開大量 §編號交叉引用）。
- 不要新增沒有消費端的設定欄位。
- 不要讓 AI 產出被當成 HTML 解析（前端一律 `textContent`／走 `markdown-lite` 唯一出口）。
