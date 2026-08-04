namespace LogForesight;

/// <summary>
/// 規則檔的完整內容：規則清單＋兩個版本號。SchemaVersion 是「檔案結構」的版本（供載入端判斷
/// 相容性）；SeedVersion 是「內建種子內容」的版本（對應 <see cref="KnownIssueSeed.Version"/>，
/// Web 規則維護頁的「內建規則升級」依此判斷是否有新內容可匯入）。
/// 兩者分開是因為升級節奏不同：改規則內容很常見，改檔案結構很少見。
///
/// **什麼時候該遞增 SchemaVersion**：只有當「舊版程式讀到新檔會做出錯誤的事」時才遞增，
/// 不是「只要加欄位就遞增」。單純新增可選欄位不必遞增——舊程式讀新檔時未知欄位會被忽略，
/// 既有規則照常運作。2026-07-28 的 Linux 規則欄位（Platform／ProgramPattern／EventNamePattern／
/// MessagePatterns，見 docs/LINUX-RULES.md §1.1）就是這種情況，刻意維持 SchemaVersion=1：
/// 遞增反而更糟——舊程式會因為「SchemaVersion 過新」整份拒絕載入而降級用內建種子，
/// 連使用者自訂的 Windows 規則都一起失效；不遞增則只有 Linux 規則被舊版的 RuleValidator
/// 判為不合格而跳過（帶警告），Windows 面完全不受影響，是比較好的降級行為。
/// </summary>
public class RuleFileContent
{
    /// <summary>此版本程式支援的規則檔結構版本上限。檔案宣告的 SchemaVersion 高於這個值時，
    /// 代表檔案是用更新版本的程式產生的，本程式不該嘗試解析，應提示使用者升級程式。</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int SeedVersion { get; set; }
    public List<KnownIssueRule> Rules { get; set; } = new();
}

/// <summary>規則檔載入結果：不存在與載入失敗是兩種不同情況，呼叫端（RuleBootstrapper）需要分開處理
/// ——不存在代表「初次部署，該寫種子」，載入失敗代表「檔案壞了，該降級用內建種子且不覆寫壞檔」。</summary>
public class RuleLoadOutcome
{
    public bool Success { get; init; }
    public RuleFileContent? Content { get; init; }
    public string? Error { get; init; }

    public static RuleLoadOutcome Ok(RuleFileContent content) => new() { Success = true, Content = content };
    public static RuleLoadOutcome Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// 規則儲存後端的抽象，唯一實作為 <see cref="KnownIssueRuleStore"/>（DB blob，key=rules）。
/// 保留這層介面是因為 KnownIssueCatalog／RuleBootstrapper 等消費端只依賴規則存取的語意，
/// 測試也透過這個介面注入假件。
/// </summary>
public interface IKnownIssueRuleStore
{
    /// <summary>供 console/log 顯示的位置描述</summary>
    string Location { get; }

    /// <summary>規則檔／規則表是否已存在。初次部署（不存在）與載入失敗（存在但壞掉）是兩種不同情況，
    /// 呼叫端需要能分開處理，所以獨立成一個唯讀屬性，不靠 Load() 的失敗訊息猜測原因。</summary>
    bool Exists { get; }

    /// <summary>讀取規則檔內容。整檔損毀（JSON 語法錯誤、SchemaVersion 過新）視為失敗，不覆寫原檔；
    /// 單條規則物件解析失敗會被跳過並記入警告，不影響其餘規則載入。</summary>
    RuleLoadOutcome Load();

    /// <summary>寫入規則檔內容（初次部署種子、或規則維護頁「內建規則升級」套用後）。實作應採原子寫入
    /// （寫暫存檔後改名），避免程式在寫入途中被中斷留下半個損毀的檔案。</summary>
    void Save(RuleFileContent content);
}
