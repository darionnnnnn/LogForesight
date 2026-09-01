# PRTG 第 4 輪規劃：縮圈＋UI 重構＋PRTG 規則第一階
> 狀態：規劃中（待使用者確認後實作）
> 基準：dev@ee596bf（3202 綠）
> 來源：R36 探測結果評估＋使用者定案（2026-09-01）＋BACKLOG「PRTG UI 重構（下一輪候選）」提前納入

## 背景與定案脈絡

實機探測（PRTG 24.1，6,277 device／42,393 sensor）確認：Type × IPv4 覆蓋幾乎全為 100%，
IP 不是篩選維度；分析型 type 合計約 79%，白名單單獨縮不了圈。主機主檔（NetIQ 匯入）
約 3,000~4,000 台。

使用者定案：
1. 主機以 NetIQ 匯入的主檔為準做 IP 對應；**數值不必每台每天抓**。
2. 取數優先序：**有單獨 PRTG 規則邏輯者依規則；否則 NetIQ 查到問題的主機再查 PRTG 加強**。
3. 本輪同時納入先前規劃的 UI 重構三項（獨立維護頁／回填搬排程頁／主機頁整合）
   與排程設定加入 PRTG。

### 核心架構：兩層訊號、兩種取數成本

| 層 | 資料源 | 成本 | 用途 |
|---|---|---|---|
| 第一層：狀態變更 | `lf_prtg_state_changes`（夜間**單次** messages 呼叫，已全量入庫） | 便宜，維持全量 | **PRTG 獨立規則**（down／flapping／持續 warning／沉默）的判定依據 |
| 第二層：hourly 數值 | `historicdata` 逐 sensor 呼叫 | 昂貴，必須縮圈 | 觸發式加強：只對「問題主機」抓 |

**觸發主機集合 ＝ NetIQ 判定高/中風險的主機 ∪ PRTG 第一層規則命中的主機**——
這正是定案 2 的落地：有 PRTG 自己的規則邏輯（第一層）就依規則，NetIQ 出問題的也加強。
值型規則（磁碟趨勢預測、基線偏移等 L2~L5）仍需 4~8 週數值基線，本輪明確不做。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 |
|---|---|---|---|
| A | `PrtgSensorTypeWhitelist` 設定＋category 自動填＋鏡像量級顯示 | 中 | 無 |
| B | 數值擷取改觸發式（移到分析後、三重過濾） | 中大 | A |
| C | 歷史回填套同一套過濾 | 小 | A、B |
| D | UI 重構：獨立維護頁 `/admin/prtg`＋設定頁瘦身＋排程作業頁 PRTG 區塊（含回填搬家） | 中大 | 無（可與 A~C 並行） |
| E | 主機頁整合：PRTG 對應篩選／明細／手動對應（含人工對應持久層） | 中大 | 無 |
| F | PRTG 規則第一階（狀態變更型）＋finding 掛接＋規則頁 prtg 平台 | 大 | B（命中主機回饋觸發集合） |
| G | 鏡像資料匯出／匯入（正式機 → 開發機）＋值型規則資料取得流程文件 | 中 | 無 |

建議順序：A → B → C → D → E → F → G，一段一驗；A~C 完成先併一次 dev（縮圈可先實測），
D~G 完成再併第二次。

## 批次A：type 白名單與語意分類

### 現況與核對結果
- 階段 4 對「全部未暫停 sensor」逐一抓 hourly（`PrtgFetchService.cs:123-125`）。
- `lf_prtg_sensors.category`／`category_source` 恆為 null；每日結構同步絕不覆蓋（PRTG-SPEC §2）。
- 探測 unit 樣本確認主力 type 單位乾淨；`SNMP System Uptime` 為文字型數值，不可收。

### 定案
- 新設定 `PrtgSensorTypeWhitelist`（`SystemSettings`，逐行一個 type，不分大小寫、去空白）。
  **預設值**（8 種，不含 Ping——想收再加）：`SNMP Traffic 64bit`、`SNMP Traffic 32bit`、
  `SNMP Disk Free`、`SNMP CPU Load`、`SNMP Memory`、`SNMP Linux Meminfo`、
  `Windows Network Card`、`WMI Free Disk Space (Multi Disk)`。
- 消費端：批次B/C 的 sensor 選取過濾＋鏡像狀態量級顯示（紅線要求）。
- category 自動分類：type → 語意分類（traffic／disk／cpu／memory），`category_source='auto'`，
  **只填 null 絕不覆蓋**；白名單外的 type 留 null。掛在每日結構同步之後。
- 鏡像狀態新增「白名單命中 sensor 數」「其中位於已對應（ok）device 上的數量」。
- 編輯入口放批次D 的獨立維護頁（D 完成前暫掛設定頁 PRTG 頁籤，D 搬家）。

### 測試／驗收
白名單解析、預設值、分類只填 null、量級計數。

## 批次B：觸發式數值擷取

### 現況與核對結果
- PRTG 每日擷取是夜間批次的並行第三路徑（PRTG-SPEC §3），主機對應在擷取後才跑（§4）——
  並行時序拿不到當晚分析結果與當日 map。
- 分析錨定昨天，PRTG hourly 也抓昨天——觸發式選取天然同日。

### 定案（使用者定案：與 NetIQ 同步進行，不等全部分析完）
- 夜間路徑重排：PRTG 並行路徑保留「結構同步 → 主機對應 → 狀態變更（→ 批次F 規則評估）」，
  前置作業完成後**數值擷取以佇列消化器與 NetIQ 分析並行**：
  - **逐主機觸發**：NetIQ／本機分析每完成一台主機且昨日判定高/中風險，即把該主機入佇列；
    批次F 落地後，PRTG 第一層規則評估（只依賴狀態變更，不等 NetIQ）命中的主機也入佇列。
  - 消化器並行執行：主機出列 → 當日 host map（ok）反查 device → 白名單且未暫停的 sensor
    → 逐 sensor 抓 hourly。對 PRTG 的併發仍由 `PrtgFetchConcurrency` semaphore 統一節流。
  - 去重：同一主機同晚只入列一次（NetIQ 與 PRTG 規則同時命中不重抓）。
  - 前置作業（host map）未完成前入列的主機先暫存，map 就緒後開始消化——不因時序漏抓。
  - 收尾：分析全部結束且佇列清空，數值擷取階段才結束；統計併入既有執行輸出。
- 失敗語意不變：單主機/單 sensor 失敗只影響自己，吞例外記 log，僅取消穿透。
- 無觸發主機 → 0 次 historicdata 呼叫；執行輸出寫明「觸發主機 X 台（NetIQ Y＋PRTG 規則 Z）、
  對應 device N 台、目標 sensor M 個」。
- 寫入／去重／分頁／失敗隔離／時間解析計數沿用不動。

### 測試／驗收
選取邏輯各過濾維度、佇列去重、map 就緒前入列不漏抓、短路、與分析並行的收尾時序。

## 批次C：歷史回填套同一套過濾

### 定案
- 回填 sensor 選取：**回填區間內曾出現高/中風險日的主機** ∩ ok 對應 device ∩ 白名單。
  逐日用「該日」風險主機與「該日或最近一日」的 host map（只讀既有 map，不重建歷史對應；
  仍對不到就跳過）。
- 狀態變更回填維持全量（單次呼叫，便宜）。冪等、逐日獨立失敗、與探測互斥不變。

## 批次D：UI 重構——獨立維護頁與排程整合

### 現況與核對結果
- PRTG 全部功能擠在設定頁 PRTG 頁籤（連線／鏡像狀態／回填／探測）；NetIQ 有獨立頁
  `Views/Pages/Netiq.cshtml`（側欄「系統管理」）。
- 排程作業頁（`Runs.cshtml`）已有「排程設定」（取數排程＋AI 分析排程，R35 拆分）。
- BACKLOG「PRTG UI 重構」三項方向已定案，本批做前兩項＋排程整合，第三項在批次E。

### 定案
- 新增獨立維護頁 **`/admin/prtg`**（側欄「系統管理」、緊鄰 NetIQ；權限 Maintain，
  頁籤結構參考 NetIQ 頁）：**連線設定**（含認證三選一與測試連線）／**鏡像狀態**
  （計數、最新時間、對應摘要、白名單量級）／**環境探測**。前端沿用既有 `settings.js`
  的 PRTG 模組拆出為 `pages/prtg-admin.js`（既有渲染/輪詢邏輯搬移為主，不重寫）。
- 設定頁 PRTG 頁籤**瘦身為純參數**：白名單、併發、回填天數、保留天數、逾時、忽略 SSL。
  連線與操作類全部移走；既有儲存驗證邏輯不變。
- 排程作業頁「排程設定」新增 **PRTG 區塊**：`PrtgEnabled` 開關搬到這裡呈現
  （語意＝「夜間批次含 PRTG 擷取」，不另設獨立時間窗——擷取沿用夜間批次窗口是既有定案）、
  觸發式取數的說明文字、**歷史回填**操作與狀態（自設定頁搬家，含確認框與輪詢輸出）。
- 路徑一律走 `appUrl()`／`~/`（WEB-SPEC §8.1a 紅線）；全站用詞照 §8.6a。

### 測試／驗收
授權（Maintain）、頁面路由、API 不變（僅前端搬家）、設定頁儲存回歸。

## 批次E：主機頁整合 PRTG 對應（含手動對應）

### 現況與核對結果
- `lf_prtg_host_map` 按日重算（(map_date, device_objid) 複合鍵），無人工對應概念。
- BACKLOG 定案：手動對應必須優先於自動對應且不被覆蓋（同 category 契約精神）。

### 定案
- **人工對應持久層**：新表 `lf_prtg_manual_map`（`device_objid` PK、`host_id`、
  `created_by`／`created_at`／`note`；長期有效，不按日）。建表走 EnsureCreated＋
  SchemaUpgrader 冪等 DDL 雙軌（DB-SPEC 規範：`lf_` 前綴、識別字 ≤30、雙後端相容）。
- **優先序**：每日自動對應執行時，凡 `lf_prtg_manual_map` 有列的 device，當日 map 直接
  寫入人工指定結果（status=ok、note 標示 manual），不進自動判定；刪除人工對應即恢復自動。
- 主機清單加「PRTG 對應」篩選（有對應／衝突／未對應）；主機明細新增 PRTG 區塊
  （對應 device、其白名單 sensor 清單與最新數值時間）；未對應／衝突清單（維護頁鏡像狀態內）
  提供「指派給主機」的手動對應入口；操作記稽核。
- 對應作業仍**只讀主機主檔絕不寫回**（PRTG-SPEC §4 既有紅線）。

### 測試／驗收
人工對應優先、刪除後恢復自動、篩選計數、schema 冪等、稽核。

## 批次F：PRTG 規則第一階（狀態變更型）

### 現況與核對結果
- 狀態變更已全量入庫（夜間單次呼叫），是唯一「不用加取數成本」就能做規則的資料。
- finding 掛接方向 BACKLOG 已定調：映射成 `LogIssueSignature`（`EventId=0`＋`EventKey`，
  同 Linux 規則模式），沿用 `lf_top_issues` 與處理狀態／郵件／排行全鏈，
  不另建獨立 finding 表與 UI。
- 規則維護頁現有 windows／linux 平台架構（RULES-SPEC）。

### 定案
- **規則型態（第一階，僅狀態變更型）**，判定窗口＝昨日，逐主機（經 host map 歸屬）評估：
  | 規則 | 語意 | 預設門檻（保守起步） |
  |---|---|---|
  | sensor 持續 Down | 白名單 sensor 進入 Down 且直到日終未恢復 | 持續 ≥ 60 分鐘 |
  | flapping | 同一 sensor 一日內 Down↔Up 反覆 | ≥ 5 次往返 |
  | 持續 Warning | 同一 sensor Warning 狀態累計 | ≥ 4 小時 |
  | 沉默 device | 對應主機的全部 sensor 皆 Unknown/無訊息 | 整日 |
- 命中產出 PRTG finding → `EventKey`＝規則代碼＋sensor 識別（如 `prtg:down:objid`），
  進當日該主機的 `lf_top_issues`（`EventId=0`），白話說明由規則自帶模板（不呼叫 AI）；
  處理狀態、排行、郵件、儀表板全鏈自動生效。
- **命中主機回饋批次B 的觸發集合**（聯集），完成「PRTG 規則邏輯驅動加強取數」的閉環。
- 規則維護頁新增 **prtg 平台**：清單顯示上述規則（啟用開關＋門檻參數可調），
  不做「PRTG+NetIQ」合併平台（既有定案）。抑制機制沿用既有範圍支援矩陣的 Host 範圍。
- 評估掛在夜間批次 PRTG 並行路徑內「狀態變更同步完成後」立即執行（**不依賴 NetIQ 分析**），
  命中主機直接入批次B 的取數佇列——與 NetIQ 分析同步進行。

### 測試／驗收
四型規則判定（含邊界：跨午夜、單次 Down 未達門檻）、EventKey 簽章穩定性、
全鏈掛接（top_issues 寫入、處理狀態可標記）、門檻設定消費端、觸發集合聯集。

## 批次G：鏡像資料匯出／匯入與值型規則資料流程文件

### 背景（使用者定案）
值型規則（磁碟趨勢等）的設計需要**真實累積數值**，而數值累積在正式機（SQL Server）、
規則開發在開發機（SQLite）——兩後端無法直接搬 DB 檔。需要正式的跨後端資料通道與文件，
不是「等資料夠了自然有規則」。

### 定案
- **匯出**（PRTG 維護頁，Maintain 權限）：選日期區間與資料類別
  （values／state_changes／devices+sensors 結構／host_map），產出單一 JSON 壓縮檔下載。
  格式為自描述（含 schema 版本與筆數），數值不做聚合轉換——原樣帶走。
- **匯入**（PRTG 維護頁，開發機用）：上傳匯出檔，沿用既有自然鍵冪等 upsert
  （values 的 `(sensor_objid, period_start)`、state_changes 的 `(sensor_objid, changed_at)`、
  結構表 objid）——重複匯入不產生重複資料。匯入寫稽核。
- 大小控制：逐批序列化與寫入（沿用 500 筆一批的既有紀律），不整份載入記憶體；
  預估量級寫進文件（觸發式縮圈後 values 每日筆數 ≈ 觸發主機 × sensor × 24）。
- **文件**：PRTG-SPEC 新增「值型規則的資料取得流程」一節——累積前提（觸發式取數
  自然累積問題主機的數值；必要時對特定主機跑歷史回填補基線）、匯出→匯入步驟、
  資料品質旗標（quality）在分析時的處理原則。

### 測試／驗收
匯出格式 round-trip（匯出→匯入→比對筆數與抽樣值）、冪等重匯、批次寫入、權限與稽核。

## 明確不做（本輪定案）
- 值型規則（磁碟趨勢預測、基線偏移、L2~L5 特徵/合成/敘述化）**本體**：資料經批次G 通道
  累積與搬運後另輪設計——本輪只建資料通道與文件。
- Ping 不入預設白名單。
- 常態全量數值鏡像、`PrtgFetchConcurrency` 上限調整。
- PRTG 擷取獨立排程時間窗（沿用夜間批次窗口，只在排程頁呈現開關與說明）。
- 多台 PRTG core server、finding 獨立表與獨立 UI（方向已定不做）。
