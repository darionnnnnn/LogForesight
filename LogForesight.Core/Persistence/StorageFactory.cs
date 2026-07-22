namespace LogForesight;

/// <summary>
/// 依設定選擇儲存後端（Strategy + Factory）。目前只有 Jsonl 一種實作；
/// 未來新增 DB 後端時，這裡是唯一需要新增 case 的地方，呼叫端（Program.cs／LogAnalysisService）不需修改。
/// </summary>
public static class StorageFactory
{
    /// <param name="ownerHost">
    /// 批次寫入端的「本機」識別。傳入時，缺日判定與趨勢基準等批次面讀寫只看這台主機自己的
    /// 紀錄（見 <see cref="JsonlAnalysisRecordStore"/> 的 ownerHost 說明）。Web 查詢端不傳，維持不分主機。
    /// </param>
    public static IAnalysisRecordStore CreateRecordStore(StorageSettings settings, string? filePath = null, HostKey? ownerHost = null)
    {
        switch (settings.Type)
        {
            case "Jsonl":
                return new JsonlAnalysisRecordStore(filePath, ownerHost);
            default:
                Console.WriteLine($"未知的 Storage.Type「{settings.Type}」，改用預設的 Jsonl。");
                return new JsonlAnalysisRecordStore(filePath, ownerHost);
        }
    }

    /// <summary>規則儲存後端，目前只有 rules.json 一種實作；未來新增 DB 後端時在這裡加一個 case 即可</summary>
    public static IKnownIssueRuleStore CreateRuleStore(StorageSettings settings, string? filePath = null)
    {
        switch (settings.Type)
        {
            case "Jsonl":
                return new JsonKnownIssueRuleStore(filePath);
            default:
                Console.WriteLine($"未知的 Storage.Type「{settings.Type}」，規則庫改用預設的 Jsonl。");
                return new JsonKnownIssueRuleStore(filePath);
        }
    }

    /// <summary>抑制設定的儲存後端，目前只有 suppressions.json 一種實作</summary>
    public static ISuppressionStore CreateSuppressionStore(StorageSettings settings, string? filePath = null)
    {
        switch (settings.Type)
        {
            case "Jsonl":
                return new JsonSuppressionStore(filePath);
            default:
                Console.WriteLine($"未知的 Storage.Type「{settings.Type}」，抑制設定改用預設的 Jsonl。");
                return new JsonSuppressionStore(filePath);
        }
    }

    // ── Web 自有資料的儲存後端（docs/WEB-SPEC.md §10.2）────────────────────────────
    // 檔案位於 {DataRoot}\webdata\，與批次的 history.txt／rules.json 同一個資料根目錄：
    // JSONL 後端下 Web 與批次讀寫同一份資料，這是「JSONL 即資料庫」決策的直接結果。
    // 未來 SQL 後端在此加 case，Web 的 Service 層完全不需修改。

    /// <summary>Web 使用者（webdata\users.json）</summary>
    public static IUserStore CreateUserStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonUserStore(WebDataPath(dataRoot, "users.json"));
        }
    }

    /// <summary>使用者群組（webdata\groups.json）</summary>
    public static IUserGroupStore CreateUserGroupStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonUserGroupStore(WebDataPath(dataRoot, "groups.json"));
        }
    }

    /// <summary>Web 的分析紀錄查詢（多條件篩選）。JSONL 後端與寫入端共用同一份 history.txt</summary>
    public static IAnalysisRecordQuery CreateRecordQuery(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonlAnalysisRecordStore(Path.Combine(dataRoot, "history.txt"));
        }
    }

    /// <summary>報告全文讀取（export\ 下的 txt）</summary>
    public static IReportReader CreateReportReader(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new FileReportReader(dataRoot);
        }
    }

    /// <summary>主機（webdata\hosts.json）——批次與 Web 共同寫入，職責見 IHostStore 註解</summary>
    public static IHostStore CreateHostStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonHostStore(WebDataPath(dataRoot, "hosts.json"));
        }
    }

    /// <summary>主機群組（webdata\host_groups.json）</summary>
    public static IHostGroupStore CreateHostGroupStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonHostGroupStore(WebDataPath(dataRoot, "host_groups.json"));
        }
    }

    /// <summary>群組授權對應（webdata\group_access.json）</summary>
    public static IGroupAccessStore CreateGroupAccessStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonGroupAccessStore(WebDataPath(dataRoot, "group_access.json"));
        }
    }

    /// <summary>操作稽核（webdata\audit.jsonl）</summary>
    public static IAuditLogStore CreateAuditLogStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonAuditLogStore(WebDataPath(dataRoot, "audit.jsonl"));
        }
    }

    /// <summary>內建規則的原廠種子鏡像（rule_seeds.json，供「回復預設」比對與還原）</summary>
    public static IRuleSeedStore CreateRuleSeedStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonRuleSeedStore(Path.Combine(dataRoot, "rule_seeds.json"));
        }
    }

    /// <summary>批次執行紀錄（rundata\runs.jsonl ＋ run_logs.jsonl）——批次寫、Web 讀</summary>
    public static IBatchRunStore CreateBatchRunStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonBatchRunStore(
                    Path.Combine(dataRoot, "rundata", "runs.jsonl"),
                    Path.Combine(dataRoot, "rundata", "run_logs.jsonl"));
        }
    }

    /// <summary>風險日處理狀態（webdata\handling.json ＋ handling_log.jsonl）</summary>
    public static IRecordHandlingStore CreateHandlingStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonRecordHandlingStore(
                    WebDataPath(dataRoot, "handling.json"),
                    WebDataPath(dataRoot, "handling_log.jsonl"));
        }
    }

    /// <summary>
    /// 權限異動（rundata\perm_changes.jsonl 由批次寫、webdata\perm_confirms.json 由 Web 寫）。
    /// 兩個檔案各有單一寫入者，見 JsonPermissionChangeStore 的類別註解。
    /// </summary>
    public static IPermissionChangeStore CreatePermissionChangeStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonPermissionChangeStore(
                    Path.Combine(dataRoot, "rundata", "perm_changes.jsonl"),
                    WebDataPath(dataRoot, "perm_confirms.json"));
        }
    }

    /// <summary>CSV 匯入紀錄（webdata\import_logs.jsonl）</summary>
    public static IImportLogStore CreateImportLogStore(StorageSettings settings, string dataRoot)
    {
        switch (settings.Type)
        {
            case "Jsonl":
            default:
                return new JsonImportLogStore(WebDataPath(dataRoot, "import_logs.jsonl"));
        }
    }

    private static string WebDataPath(string dataRoot, string fileName) =>
        Path.Combine(dataRoot, "webdata", fileName);
}
