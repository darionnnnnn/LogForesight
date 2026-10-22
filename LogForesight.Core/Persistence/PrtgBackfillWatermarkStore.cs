using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 全量歷史回填的完成水位（docs/PRTG-SPEC.md §5）：哪些日子已完整取回、當時該日的目標指紋。
/// 只在設定與監看範圍版本都相同時有效；任一不同代表目標可能變了，整份重算。
/// </summary>
public class PrtgBackfillWatermark
{
    public string SettingsRevision { get; set; } = "";
    public string ScopeRevision { get; set; } = "";

    /// <summary>已完整回填的資料日（yyyy-MM-dd）→ 完成當時該日的目標指紋。</summary>
    public Dictionary<string, string> CompletedDays { get; set; } = new(StringComparer.Ordinal);

    public DateTime UpdatedAtUtc { get; set; }
}

public class PrtgBackfillWatermarkStore : JsonBlobSingleton<PrtgBackfillWatermark>
{
    public const string BlobKey = "prtg_backfill_watermark";

    /// <summary>水位最後更新超過這麼久就不再採信（暫定）：中間可能有回填以外的寫入或清除。</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public PrtgBackfillWatermarkStore(EfJsonBlobStore blob) : base(blob) { }

    private static string Key(DateTime day) => day.Date.ToString("yyyy-MM-dd");

    /// <summary>
    /// 仍可略過的日子：版本相同、未過期、且該日目前的目標指紋與完成當時相同。
    /// 目標變了（新的高風險日、對應改變）的日子不算完成——那天要取的 sensor 已經不是同一批。
    /// </summary>
    public IReadOnlySet<DateTime> CompletedFor(string settingsRevision, string scopeRevision,
        IEnumerable<(DateTime Day, string Fingerprint)> planDays, DateTime nowUtc)
    {
        var mark = Get();
        if (!IsUsable(mark, settingsRevision, scopeRevision, nowUtc)) return new HashSet<DateTime>();
        return planDays
            .Where(day => mark.CompletedDays.TryGetValue(Key(day.Day), out var fingerprint) &&
                          string.Equals(fingerprint, day.Fingerprint, StringComparison.Ordinal))
            .Select(day => day.Day.Date)
            .ToHashSet();
    }

    /// <summary>新一趟開始時呼叫：沿用（版本相同且未過期）或從頭（其餘情況，含使用者選全部重做）。</summary>
    public void BeginRun(string settingsRevision, string scopeRevision, bool resume, DateTime nowUtc) =>
        Update(mark =>
        {
            if (resume && IsUsable(mark, settingsRevision, scopeRevision, nowUtc)) return;
            mark.SettingsRevision = settingsRevision;
            mark.ScopeRevision = scopeRevision;
            mark.CompletedDays = new Dictionary<string, string>(StringComparer.Ordinal);
            mark.UpdatedAtUtc = nowUtc;
        });

    /// <summary>某日完整回填（每個目標 sensor 都取到）後記下；版本已不同的水位不寫（另一趟已重設）。</summary>
    public void MarkCompleted(string settingsRevision, string scopeRevision, DateTime day, string fingerprint, DateTime nowUtc) =>
        Update(mark =>
        {
            if (!string.Equals(mark.SettingsRevision, settingsRevision, StringComparison.Ordinal) ||
                !string.Equals(mark.ScopeRevision, scopeRevision, StringComparison.Ordinal)) return;
            mark.CompletedDays[Key(day)] = fingerprint;
            mark.UpdatedAtUtc = nowUtc;
        });

    private static bool IsUsable(PrtgBackfillWatermark mark, string settingsRevision, string scopeRevision, DateTime nowUtc) =>
        string.Equals(mark.SettingsRevision, settingsRevision, StringComparison.Ordinal) &&
        string.Equals(mark.ScopeRevision, scopeRevision, StringComparison.Ordinal) &&
        nowUtc - mark.UpdatedAtUtc <= MaxAge;
}
