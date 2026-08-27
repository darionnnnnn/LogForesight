using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 既有 NetIQ 權限異動列的重剖回填：舊版解析器在「攤平成單行」的訊息上會把欄位值切到行尾，
/// 也抓不到群組區段內的群組名，於是 DB 裡留下一批「對象是整段訊息尾巴」或「對象空白」的列。
/// 解析器修好之後，用同一份原始訊息（<c>alert_text</c>）重剖一次把欄位補回去。
///
/// 先天限制：<c>alert_text</c> 只保留原訊息前 500 字，被截掉的部分剖不回來——這種列維持原值，
/// 由顯示層的降級句兜底。這是**盡力回填**，不是保證每列都修好。
/// </summary>
public sealed class PermissionChangeReparser
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>每批處理的列數：一次抓一批改一批，避免大表一次載進記憶體</summary>
    internal const int BatchSize = 500;

    public const string StateBlobKey = "permission_change_reparse";

    /// <summary>
    /// 目前的重剖版本（見 <see cref="PermissionChangeReparseState.Version"/>）。
    /// 版本 2：擷取 4670 的物件類型／處理程序名稱、重算類別（物件權限變更自資料夾權限異動分出），
    /// 並允許把舊解析器留下的「整段訊息尾巴」對象洗掉。
    /// </summary>
    public const int CurrentVersion = 2;

    private readonly Func<LfDbContext> _contextFactory;
    private readonly PermissionChangeReparseStateStore _stateStore;

    public PermissionChangeReparser(Func<LfDbContext> contextFactory, PermissionChangeReparseStateStore stateStore)
    {
        _contextFactory = contextFactory;
        _stateStore = stateStore;
    }

    public bool IsCompleted
    {
        get
        {
            var state = _stateStore.Get();
            return state.Completed && state.Version >= CurrentVersion;
        }
    }

    /// <summary>
    /// 重剖回填（背景執行）。跑完即標記完成，之後不再掃描——新寫入的列走的是修好的解析器，
    /// 不需要再回填。
    /// </summary>
    /// <param name="mappings">自訂欄位對應（同寫入路徑）。訊息用非標準欄位名的站台，
    /// 沒有它就等於白跑一趟——而這是一次性工作，跑完不會自動重來。</param>
    public void Run(CancellationToken cancellationToken = default, PermissionFieldMappings? mappings = null)
    {
        if (IsCompleted) return;

        var updated = 0;
        long scanned = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var ctx = _contextFactory();

            // 只處理 NetIQ 來源的明細列：本機監控的欄位是程式自己組的、彙總列沒有原始訊息可剖
            var batch = ctx.PermissionChanges
                .Where(r => r.Source == PermissionChangeSources.Netiq
                            && r.Category != PermissionCategory.Summary
                            && r.Id > scanned)
                .OrderBy(r => r.Id)
                .Take(BatchSize)
                .ToList();

            if (batch.Count == 0) break;

            scanned = batch[^1].Id;

            foreach (var row in batch)
            {
                if (string.IsNullOrWhiteSpace(row.AlertText)) continue;

                // initialInitiatorAccount 傳 null：那個參數的語意是「事件自帶的操作者」，
                // 會蓋過訊息裡剖出來的值。回填時傳入 row 的舊值等於拿要修的髒值當標準答案，
                // 操作者欄位就永遠修不好。
                var details = PermissionChangeExtractor.Extract(
                    row.AlertText, row.ChangeType, row.EventId ?? 0, initialInitiatorAccount: null, mappings);

                var changed = false;

                // 只在重剖得出值時覆蓋：剖不出的欄位維持原狀，不要把既有資料洗成空的。
                // **例外是髒值形狀**：舊解析器把整段訊息尾巴（「- 控制代碼識別碼: 0x185c 處理程序: …」）
                // 寫進了對象欄，而這種訊息重剖出來的對象恰好是空字串——不放行「洗成空」的話，
                // 這批髒值會永遠留在畫面的說明句裡。只認形狀，不認特定來源。
                if (details.Target != row.Target &&
                    (!string.IsNullOrWhiteSpace(details.Target) || IsDirtyTarget(row.Target)))
                {
                    row.Target = details.Target;
                    changed = true;
                }
                if (details.ObjectType != row.ObjectType && !string.IsNullOrWhiteSpace(details.ObjectType))
                {
                    row.ObjectType = details.ObjectType;
                    changed = true;
                }
                if (details.ProcessName != row.ProcessName && !string.IsNullOrWhiteSpace(details.ProcessName))
                {
                    row.ProcessName = details.ProcessName;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(details.TargetAccount) && details.TargetAccount != row.TargetAccount)
                {
                    row.TargetAccount = details.TargetAccount;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(details.InitiatorAccount) && details.InitiatorAccount != row.InitiatorAccount)
                {
                    row.InitiatorAccount = details.InitiatorAccount;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(details.Before) && details.Before != row.BeforeValue)
                {
                    row.BeforeValue = details.Before;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(details.After) && details.After != row.AfterValue)
                {
                    row.AfterValue = details.After;
                    changed = true;
                }

                // 類別一律依現在的物件類型重算：4670 的 Token／登錄機碼物件要從「資料夾權限異動」
                // 分到「物件權限變更」，既有列沒有物件類型欄，只有重剖過才判得出來
                var category = PermissionCategory.Resolve(row.ChangeType, row.EventId, row.ObjectType);
                if (category != row.Category)
                {
                    row.Category = category;
                    changed = true;
                }

                // 特權判定一律依現在的對象重算：對象修對了它才成立（舊列的對象是訊息尾巴，
                // 永遠命中不了關鍵字），而舊列即使欄位沒變，旗標也可能是舊判定邏輯留下的錯值
                var privileged = PermissionCategory.IsPrivilegedTarget(row.Target, row.ChangeType);
                if (privileged != row.IsPrivilegedTarget)
                {
                    row.IsPrivilegedTarget = privileged;
                    changed = true;
                }

                if (changed) updated++;
            }

            ctx.SaveChanges();
        }

        if (cancellationToken.IsCancellationRequested) return;


        _stateStore.Update(s =>
        {
            s.Completed = true;
            s.Version = CurrentVersion;
            s.CompletedAt = DateTime.Now;
            s.UpdatedRows = updated;
        });

        if (updated > 0)
            Log.Info("[SQL] 權限異動重剖回填完成：更新 {Count} 列", updated);
    }

    /// <summary>
    /// 舊解析器留下的「對象是整段訊息尾巴」形狀：長度超過 40 字，且裡面帶著**至少兩組**
    /// 「鍵: 值」。這是唯一允許把既有欄位洗成空的路徑，判準要嚴——被誤判的對象清掉之後
    /// 救不回來（原訊息若也非標準格式就重剖不出值）。
    ///
    /// 為什麼是兩組而不是一組：合法的對象**可能**含一個冒號（群組名「專案: 財務季報」、
    /// 資料夾名「Dept: Finance」都是實務上會出現的命名），但不會連續出現兩組欄位形狀；
    /// 訊息尾巴則一定帶著「控制代碼識別碼: … 處理程序識別碼: … 處理程序名稱: …」好幾組。
    /// 磁碟機代號的「C:\」冒號後緊接反斜線，本來就不符合這個形狀。
    /// </summary>
    internal static bool IsDirtyTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length <= 40) return false;
        return DirtyTargetFieldRegex.Matches(target).Count >= 2;
    }

    private static readonly System.Text.RegularExpressions.Regex DirtyTargetFieldRegex =
        new(@"[\p{L}\p{N}][\p{L}\p{N} ]*[:：]\s+\S",
            System.Text.RegularExpressions.RegexOptions.Compiled);
}
