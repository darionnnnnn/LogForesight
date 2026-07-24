using System.DirectoryServices.AccountManagement;
using System.Security.AccessControl;
using System.Security.Principal;
using NLog;

namespace LogForesight;

/// <summary>單筆權限異動的明細（異動前/後對照），供人工逐項判斷是否為正常/授權的異動</summary>
public class PermissionChangeDetail
{
    public string Target { get; init; } = string.Empty;      // 資料夾路徑或群組名稱
    public string ChangeType { get; init; } = string.Empty;  // 成員新增/成員移除/擁有者變更/權限新增/權限移除/無法存取
    public string Before { get; init; } = string.Empty;
    public string After { get; init; } = string.Empty;
}

/// <summary>權限檢查結果：告警行（自動檢查用）＋異動明細（人工確認用），兩者一一對應</summary>
public class PermissionCheckResult
{
    public List<string> Alerts { get; } = new();
    public List<PermissionChangeDetail> Details { get; } = new();

    public void Add(string alert, PermissionChangeDetail detail)
    {
        Alerts.Add(alert);
        Details.Add(detail);
    }
}

/// <summary>
/// 權限/角色異動監控：直接讀取本機 Administrators 群組成員與指定資料夾的 ACL，
/// 與上次執行時存下的快照比對，任何新增或移除都告警（不判斷好壞，一律回報讓人工確認）。
/// 刻意不依賴 Windows Security 稽核記錄——稽核政策若沒配置好、或程式沒有系統管理員權限
/// （目前執行環境即是如此），Security log 就讀不到對應事件；直接讀 ACL/群組成員
/// 不需要額外的稽核原則設定，是更可靠的最後一道防線。
/// 與 Security log 事件規則（KnownIssueCatalog 的 4670/4907/4717 等）互為備援：
/// 有 Security log 權限時兩者都會觸發，形成雙重確認。
/// </summary>
public class PermissionMonitorService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IPermissionSnapshotStore _snapshotStore;
    private readonly List<string> _watchedFolders;

    public PermissionMonitorService(PermissionSettings settings, IPermissionSnapshotStore snapshotStore)
    {
        _snapshotStore = snapshotStore;

        // 執行檔自身目錄一律監控（防止程式本身被竄改），加上使用者在 appsettings.json 額外指定的資料夾
        var folders = new List<string> { AppContext.BaseDirectory.TrimEnd('\\') };
        folders.AddRange(settings.WatchedFolders
            .Select(Environment.ExpandEnvironmentVariables)
            .Select(f => f.TrimEnd('\\')));
        _watchedFolders = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> WatchedFolders => _watchedFolders;

    /// <summary>比對權限快照，回傳異動告警與逐項明細（皆空＝無異動或首次執行建立基準）</summary>
    public PermissionCheckResult Check()
    {
        Log.Info("開始權限檢查：監控 {FolderCount} 個資料夾", _watchedFolders.Count);
        var current = Capture();
        var result = new PermissionCheckResult();

        var previous = _snapshotStore.Load();
        if (previous == null)
        {
            Console.WriteLine("  尚無權限基準快照，本次建立基準（不產生異動告警）。");
            Log.Info("尚無權限基準快照，建立基準");
            _snapshotStore.Save(current);
            return result;
        }

        // Administrators 成員異動：兩次都讀取成功才比對，避免單次讀取失敗被誤判成「全部成員被移除」
        if (current.AdministratorsMembers != null && previous.AdministratorsMembers != null)
        {
            var added = current.AdministratorsMembers.Except(previous.AdministratorsMembers, StringComparer.OrdinalIgnoreCase).ToList();
            var removed = previous.AdministratorsMembers.Except(current.AdministratorsMembers, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var member in added)
            {
                result.Add($"【提權】本機 Administrators 群組新增成員：{member}",
                    new PermissionChangeDetail
                    {
                        Target = "本機 Administrators 群組",
                        ChangeType = "成員新增",
                        Before = "（不在群組中）",
                        After = member
                    });
            }
            foreach (var member in removed)
            {
                result.Add($"【權限變更】本機 Administrators 群組移除成員：{member}（可能為正常調整，也可能是入侵者提權得手後清除紀錄）",
                    new PermissionChangeDetail
                    {
                        Target = "本機 Administrators 群組",
                        ChangeType = "成員移除",
                        Before = member,
                        After = "（已移出群組）"
                    });
            }
        }
        else if (current.AdministratorsMembers == null)
        {
            Console.WriteLine("  本次無法讀取 Administrators 群組成員，跳過該項比對（不影響資料夾 ACL 檢查）。");
        }

        // 各監控資料夾的 ACL 異動：不判斷合理性，一律列出讓人工確認
        foreach (var path in _watchedFolders)
        {
            if (!previous.Folders.TryGetValue(path, out var before) || !current.Folders.TryGetValue(path, out var after))
            {
                continue; // 監控清單自上次執行後變動（新增/移除路徑），無基準可比對
            }

            if (before.Accessible && !after.Accessible)
            {
                result.Add($"【異常】資料夾變成無法存取：{path}（可能已被刪除，或權限被鎖死以阻擋存取／掩蓋內容）",
                    new PermissionChangeDetail
                    {
                        Target = path, ChangeType = "無法存取",
                        Before = "可正常存取", After = "無法存取（可能已刪除或權限被鎖死）"
                    });
                continue;
            }
            if (!before.Accessible && after.Accessible)
            {
                result.Add($"【狀態變更】資料夾恢復可存取：{path}",
                    new PermissionChangeDetail
                    {
                        Target = path, ChangeType = "恢復可存取",
                        Before = "無法存取", After = "可正常存取"
                    });
            }
            if (!before.Accessible || !after.Accessible)
            {
                continue; // 至少一次讀取失敗，ACL 內容無法比對
            }

            if (!string.Equals(before.Owner, after.Owner, StringComparison.OrdinalIgnoreCase))
            {
                result.Add($"【擁有者變更】{path} 的擁有者由「{before.Owner}」變更為「{after.Owner}」",
                    new PermissionChangeDetail
                    {
                        Target = path, ChangeType = "擁有者變更",
                        Before = before.Owner ?? "（未知）", After = after.Owner ?? "（未知）"
                    });
            }

            foreach (var rule in after.Rules.Except(before.Rules))
            {
                result.Add($"【權限新增】{path}：{rule}",
                    new PermissionChangeDetail
                    {
                        Target = path, ChangeType = "權限新增（ACL 規則）",
                        Before = "（無此規則）", After = rule
                    });
            }
            foreach (var rule in before.Rules.Except(after.Rules))
            {
                result.Add($"【權限移除】{path}：{rule}",
                    new PermissionChangeDetail
                    {
                        Target = path, ChangeType = "權限移除（ACL 規則）",
                        Before = rule, After = "（已移除）"
                    });
            }
        }

        _snapshotStore.Save(current);

        if (result.Alerts.Count > 0)
        {
            // 告警本身就是程式產生的短結構化字串（不是原始 ACL 傾印），數量也天然有限，完整記錄沒問題
            Log.Warn("權限檢查發現 {Count} 項異動：{Alerts}", result.Alerts.Count, string.Join(" | ", result.Alerts));
        }
        else
        {
            Log.Info("權限檢查完成，未發現異動");
        }

        return result;
    }

    private PermissionSnapshot Capture()
    {
        var folders = new Dictionary<string, FolderAclSnapshot>();
        foreach (var path in _watchedFolders)
        {
            folders[path] = CaptureFolder(path);
        }

        return new PermissionSnapshot
        {
            CapturedAt = DateTime.Now,
            AdministratorsMembers = CaptureAdministratorsMembers(),
            Folders = folders
        };
    }

    private static FolderAclSnapshot CaptureFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return new FolderAclSnapshot { Accessible = false };
            }

            var security = new DirectoryInfo(path).GetAccessControl();
            var owner = security.GetOwner(typeof(NTAccount))?.Value;
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(NTAccount))
                .Cast<FileSystemAccessRule>()
                .Select(r => $"{r.IdentityReference.Value}｜{r.AccessControlType}｜{r.FileSystemRights}｜{(r.IsInherited ? "繼承" : "明確設定")}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            return new FolderAclSnapshot { Accessible = true, Owner = owner, Rules = rules };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  無法讀取資料夾權限 {path}：{ex.Message}");
            Log.Warn(ex, "無法讀取資料夾權限：{Path}", path);
            return new FolderAclSnapshot { Accessible = false };
        }
    }

    /// <summary>回傳 null 代表讀取失敗（而非群組真的沒有成員），呼叫端須區分避免誤判</summary>
    private static List<string>? CaptureAdministratorsMembers()
    {
        try
        {
            using var context = new PrincipalContext(ContextType.Machine);
            using var group = GroupPrincipal.FindByIdentity(context, "Administrators");
            if (group == null)
            {
                return null;
            }

            return group.GetMembers(recursive: false)
                .Select(m => m.SamAccountName ?? m.Name ?? m.Sid?.Value ?? "(未知帳號)")
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  無法讀取本機 Administrators 群組成員：{ex.Message}");
            Log.Warn(ex, "無法讀取本機 Administrators 群組成員");
            return null;
        }
    }

}
