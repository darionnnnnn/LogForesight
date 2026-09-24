using System.Text.Json;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Web.Services;

/// <summary>以小時為單位保存快照診斷計數；不記錄單次或 sensor 級事件。</summary>
internal sealed class PrtgSnapshotDiagnosticsStore
{
    private const string BlobKey = "prtg_snapshot_diagnostics_v1";
    private const int RetentionDays = 14;
    private readonly EfJsonBlobStore _blob;
    private readonly Func<int> _retentionDays;
    private readonly object _gate = new();
    private readonly HashSet<(DateTime Hour, string Reason)> _recordedSkips = new();
    private bool _recordedSkipsLoaded;

    internal PrtgSnapshotDiagnosticsStore(EfJsonBlobStore blob, Func<int>? retentionDays = null)
    { _blob = blob; _retentionDays = retentionDays ?? (() => RetentionDays); }

    internal void Record(DateTime at, string outcome, string? reason = null, int targets = 0, int sampled = 0,
        string? reasonCode = null)
    {
        var hour = new DateTime(at.Year, at.Month, at.Day, at.Hour, 0, 0);
        var retentionDays = Math.Clamp(_retentionDays(), 1, RetentionDays);
        lock (_gate)
        {
            var skipKey = !string.IsNullOrWhiteSpace(reasonCode) ? reasonCode : reason;
            if (outcome == "skip" && !string.IsNullOrWhiteSpace(skipKey))
            {
                LoadRecordedSkips();
                if (_recordedSkips.Contains((hour, skipKey))) return;
            }

            _blob.Mutate(raw =>
            {
                var doc = Read(raw);
                var row = doc.Hours.FirstOrDefault(x => x.Hour == hour);
                if (row == null) { row = new PrtgSnapshotDiagnosticHour { Hour = hour }; doc.Hours.Add(row); }
                row.Targets = Math.Max(row.Targets, targets);
                switch (outcome)
                {
                    case "attempt": row.Attempts++; break;
                    case "success": row.Successes++; break;
                    case "persisted": row.Sampled += sampled; break;
                    case "startup": break;
                    // 快照輪詢每分鐘醒來；同一原因每小時記一次，避免每個 skip tick 都重寫 blob。
                    case "skip":
                        row.Skips++;
                        if (!string.IsNullOrWhiteSpace(skipKey)) row.SkipReasons.Add(skipKey);
                        break;
                    case "write-failure": row.WriteFailures++; break;
                }
                if (!string.IsNullOrWhiteSpace(reason))
                    row.Reasons[reason] = row.Reasons.GetValueOrDefault(reason) + 1;
                if (!string.IsNullOrWhiteSpace(reasonCode))
                    row.ReasonCodes[reasonCode] = row.ReasonCodes.GetValueOrDefault(reasonCode) + 1;
                var cutoff = at.Date.AddDays(-retentionDays);
                doc.Hours.RemoveAll(x => x.Hour < cutoff);
                return (JsonSerializer.Serialize(doc), true);
            });
            if (outcome == "skip" && !string.IsNullOrWhiteSpace(skipKey))
                _recordedSkips.Add((hour, skipKey));
        }
    }

    private void LoadRecordedSkips()
    {
        if (_recordedSkipsLoaded) return;
        foreach (var row in Read(_blob.Read()).Hours)
            foreach (var reason in row.SkipReasons)
                _recordedSkips.Add((row.Hour, reason));
        _recordedSkipsLoaded = true;
    }

    internal IReadOnlyList<PrtgSnapshotDiagnosticHour> ReadHours()
    {
        lock (_gate) return Read(_blob.Read()).Hours.OrderBy(x => x.Hour).ToArray();
    }

    private static PrtgSnapshotDiagnosticsDocument Read(string? json)
    {
        var document = string.IsNullOrWhiteSpace(json)
            ? new PrtgSnapshotDiagnosticsDocument()
            : JsonSerializer.Deserialize<PrtgSnapshotDiagnosticsDocument>(json) ?? new();
        foreach (var hour in document.Hours)
        {
            hour.Reasons ??= new Dictionary<string, int>(StringComparer.Ordinal);
            hour.ReasonCodes ??= new Dictionary<string, int>(StringComparer.Ordinal);
            hour.SkipReasons ??= new HashSet<string>(StringComparer.Ordinal);
            foreach (var (legacyText, count) in hour.Reasons)
            {
                var code = LegacyReasonCode(legacyText);
                if (code != null && !hour.ReasonCodes.ContainsKey(code)) hour.ReasonCodes[code] = count;
            }
            foreach (var legacyText in hour.SkipReasons.ToArray())
            {
                var code = LegacyReasonCode(legacyText);
                if (code == null) continue;
                hour.SkipReasons.Remove(legacyText);
                hour.SkipReasons.Add(code);
            }
        }
        return document;
    }

    private static string? LegacyReasonCode(string reason) =>
        reason.Contains("夜間取數", StringComparison.OrdinalIgnoreCase) ? PrtgSnapshotSkipReasonCodes.NightlyFetchPrtgPhase
        : reason.Contains("結構同步", StringComparison.OrdinalIgnoreCase) ? PrtgSnapshotSkipReasonCodes.StructureSyncActive
        : reason.Contains("歷史回填", StringComparison.OrdinalIgnoreCase) ? PrtgSnapshotSkipReasonCodes.BackfillActive
        : reason.Contains("維護", StringComparison.OrdinalIgnoreCase) ? PrtgSnapshotSkipReasonCodes.MaintenanceWindow
        : reason.Contains("已確認", StringComparison.OrdinalIgnoreCase) ? PrtgSnapshotSkipReasonCodes.MaintenanceConfirmed
        : reason.Contains("未啟用", StringComparison.OrdinalIgnoreCase) ? PrtgSnapshotSkipReasonCodes.PrtgDisabled
        : reason.Contains("設定不齊", StringComparison.OrdinalIgnoreCase) ? PrtgSnapshotSkipReasonCodes.ConnectionNotConfigured
        : null;
}

internal sealed class PrtgSnapshotDiagnosticsDocument
{
    public List<PrtgSnapshotDiagnosticHour> Hours { get; set; } = new();
}

internal sealed class PrtgSnapshotDiagnosticHour
{
    public DateTime Hour { get; set; }
    public int Targets { get; set; }
    public int Attempts { get; set; }
    public int Successes { get; set; }
    public int Skips { get; set; }
    public int WriteFailures { get; set; }
    public int Sampled { get; set; }
    public Dictionary<string, int> Reasons { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ReasonCodes { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> SkipReasons { get; set; } = new(StringComparer.Ordinal);
}
