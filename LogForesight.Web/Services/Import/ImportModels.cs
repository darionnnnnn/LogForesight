namespace LogForesight.Web.Services.Import;

/// <summary>
/// CSV 匯入類型。**§2a（回饋第十一輪）起只有 <see cref="Owners"/> 有實作**——
/// 前三種連同 Importer 與範本一併退役（替代動線見 docs/archive/FEEDBACK-11-PLAN.md §2a 對照表）。
/// 列舉值刻意保留：歷次匯入紀錄（`lf_import_logs`）存的是字串 Kind，
/// 拿掉列舉值會讓過去那些紀錄失去顯示名稱，而匯入紀錄是稽核性質的歷史事實。
/// </summary>
public enum ImportKind
{
    /// <summary>【已退役】使用者匯入。替代動線：使用者頁「一次新增多筆」</summary>
    Users,

    /// <summary>【已退役】主機匯入。替代：NetIQ 掃描匯入／批次自動登錄＋主機頁批次設定群組</summary>
    Hosts,

    /// <summary>【已退役】群組授權匯入。替代動線：群組頁「授權矩陣」</summary>
    GroupAccess,

    /// <summary>負責人指派（owners.csv）：host 對 owner 帳號，帳號不存在時自動建立</summary>
    Owners
}

/// <summary>
/// 預覽列的判定。原有的 <c>Remove</c>（全量取代語意下將被移除的既有授權）隨
/// group_access.csv 退役一併移除（§2a）——負責人匯入的取代是「這台主機的負責人整組換掉」，
/// 表達成 Update 而非逐筆 Remove。
/// </summary>
public enum ImportRowAction
{
    Add,
    Update,
    Unchanged,
    Error
}

/// <summary>預覽畫面的單列判定結果</summary>
public class ImportRowPlan
{
    public int LineNumber { get; init; }

    public ImportRowAction Action { get; set; }

    /// <summary>該列的識別（帳號／主機名稱／授權對應），顯示用</summary>
    public string Key { get; init; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Action=Error 時的原因；一律是可直接顯示的中文</summary>
    public string? Error { get; set; }

    /// <summary>異動前 → 異動後的欄位級對照（更新列可展開檢視）</summary>
    public List<ImportFieldChange> Changes { get; } = new();
}

public class ImportFieldChange
{
    public string Field { get; init; } = string.Empty;
    public string? Before { get; init; }
    public string? After { get; init; }
}

/// <summary>
/// 匯入計畫：預覽階段產出，套用階段執行。
/// 預覽與套用之間以 <see cref="Token"/> 綁定，避免「預覽 A 檔、套用 B 檔」。
/// </summary>
public class ImportPlan
{
    public string Token { get; init; } = Guid.NewGuid().ToString("N");

    public ImportKind Kind { get; init; }

    public string FileName { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public List<ImportRowPlan> Rows { get; init; } = new();

    /// <summary>將自動建立的使用者帳號（負責人匯入：owners.csv 引用不存在的帳號時自動建）</summary>
    public List<string> NewUsers { get; init; } = new();

    /// <summary>不擋下但需要提醒的事項（如套用後負責人會取得哪些權限）</summary>
    public List<string> Warnings { get; init; } = new();

    public int AddCount => Rows.Count(r => r.Action == ImportRowAction.Add);
    public int UpdateCount => Rows.Count(r => r.Action == ImportRowAction.Update);
    public int UnchangedCount => Rows.Count(r => r.Action == ImportRowAction.Unchanged);
    public int ErrorCount => Rows.Count(r => r.Action == ImportRowAction.Error);

    /// <summary>
    /// 有任何錯誤列就不允許套用（all-or-nothing）。
    /// 不做「跳過錯誤列繼續」——部分成功的匯入最難善後：
    /// 使用者無從得知哪些進去了、哪些沒有，只能逐筆人工比對。
    /// </summary>
    public bool CanApply => ErrorCount == 0;
}

/// <summary>
/// 套用結果。原有的 <c>Removed</c>／<c>CreatedGroups</c> 隨三種 CSV 退役一併移除（§2a）——
/// 唯一留下的負責人匯入既不移除資料也不建群組，留著會是「永遠為 0 的欄位」寫進稽核明細。
/// 歷史匯入紀錄（`lf_import_logs`）的對應欄位不動，舊資料照常顯示。
/// </summary>
public class ImportResult
{
    public int Added { get; set; }
    public int Updated { get; set; }

    /// <summary>本次自動建立的使用者帳號（負責人匯入）</summary>
    public List<string> CreatedUsers { get; } = new();
}
