namespace LogForesight.Web.Services.Import;

public enum ImportKind
{
    Users,
    Hosts,
    GroupAccess,

    /// <summary>負責人指派（owners.csv）：host 對 owner 帳號，帳號不存在時自動建立</summary>
    Owners
}

public enum ImportRowAction
{
    Add,
    Update,
    Unchanged,

    /// <summary>全量取代語意下將被移除的既有資料（目前只有 GroupAccess 會出現）</summary>
    Remove,

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

    /// <summary>將自動建立的群組名稱（使用者群組或主機群組，依 Kind 而定）</summary>
    public List<string> NewGroups { get; init; } = new();

    /// <summary>將自動建立的使用者帳號（負責人匯入專用；owners.csv 引用不存在的帳號時自動建）</summary>
    public List<string> NewUsers { get; init; } = new();

    /// <summary>不擋下但需要提醒的事項（如負責人看不到自己負責的主機）</summary>
    public List<string> Warnings { get; init; } = new();

    public int AddCount => Rows.Count(r => r.Action == ImportRowAction.Add);
    public int UpdateCount => Rows.Count(r => r.Action == ImportRowAction.Update);
    public int UnchangedCount => Rows.Count(r => r.Action == ImportRowAction.Unchanged);
    public int RemoveCount => Rows.Count(r => r.Action == ImportRowAction.Remove);
    public int ErrorCount => Rows.Count(r => r.Action == ImportRowAction.Error);

    /// <summary>
    /// 有任何錯誤列就不允許套用（all-or-nothing）。
    /// 不做「跳過錯誤列繼續」——部分成功的匯入最難善後：
    /// 使用者無從得知哪些進去了、哪些沒有，只能逐筆人工比對。
    /// </summary>
    public bool CanApply => ErrorCount == 0;
}

/// <summary>套用結果</summary>
public class ImportResult
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public List<string> CreatedGroups { get; } = new();

    /// <summary>本次自動建立的使用者帳號（負責人匯入）</summary>
    public List<string> CreatedUsers { get; } = new();
}
