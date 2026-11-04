using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>排程作業診斷匯出的組裝（控制器只依賴這個介面，測試可換成替身）</summary>
public interface IRunExportService
{
    /// <summary>
    /// 組出近 <paramref name="days"/> 天的診斷匯出（已去識別化、已通過結果掃描）。
    /// 去識別化來源失敗或結果掃描有殘留時丟 <see cref="RunExportDeidentificationException"/>，不回傳任何內容。
    /// </summary>
    RunExportResult Build(int days, CancellationToken ct);
}

/// <summary>組裝結果：檔案內容與稽核用的數字（不含任何內容）</summary>
public sealed record RunExportResult(
    byte[] Bytes, int Days, int RunCount, int SignalGroupCount, int AbnormalRunCount, int AiCaseCount,
    IReadOnlyDictionary<string, int> TokenCounts);

/// <summary>各區塊的上限（暫定值；超過的部分一律寫進 <c>meta.omitted</c>，不靜默截斷）</summary>
public sealed record RunExportLimits
{
    public int SignalGroups { get; init; } = 300;
    public int GroupDictionary { get; init; } = 5000;
    public int SampleChars { get; init; } = 500;
    public int SamplesPerGroup { get; init; } = 3;
    public int StackChars { get; init; } = 8 * 1024;
    public int AbnormalRuns { get; init; } = 200;
    public int SlowSqlOperations { get; init; } = 100;
    public int AiCases { get; init; } = 100;
    public int AiCasesScanned { get; init; } = 500;
    public int AiCaseInputs { get; init; } = 50;
    public int AiInputIssues { get; init; } = 20;
    public int AiInputAlerts { get; init; } = 10;
    public int AiInputTextChars { get; init; } = 300;
    public int DiagnosticsPerCase { get; init; } = 3;
    public int ComparisonWindowDays { get; init; } = 7;
    public int RecentRunIds { get; init; } = 3;
    public int RunSignalCodes { get; init; } = 20;
    public int UnsuccessfulItems { get; init; } = 10;

    /// <summary>期間起點之前再多載入幾天的趟次，只用來解析「開始時間早於期間起點、跨進期間」的長跑趟次</summary>
    public int RunLookbackExtraDays { get; init; } = 7;

    /// <summary>檔案硬上限（位元組）；超過回 413，不截斷</summary>
    public long OutputBytes { get; init; } = 16L * 1024 * 1024;
}

/// <summary>
/// 排程作業診斷匯出的內容組裝（docs/FEEDBACK-63-PLAN.md 批次 C）。
///
/// 流程：建遮罩器（來源讀不到就中止）→ 查格式異常案例 → 讀趟次 → <b>逐行</b>讀診斷行累計訊號組 →
/// 組各區塊 → 序列化 → 結果掃描（失敗即停）→ 輸出。診斷行一律走 <see cref="BatchRunStore.ForEachRecentLog"/>，
/// 不把整段期間的行收進清單。
///
/// 去識別化的責任分兩層：自由文字一律先經遮罩器；程式自己產生的值（狀態、等級、階段、logger、版本、日期…）
/// 登錄成「整串放行」的值。結果掃描只掃值、不掃屬性名稱（欄位名是固定結構），<c>guide</c> 說明文字不進掃描。
/// </summary>
public sealed class RunExportService : IRunExportService
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    public const int SchemaVersion = 1;

    // 訊號等級（排序用）
    private const int RankWarn = 1;
    private const int RankError = 2;
    private const int RankFatal = 3;

    private const string SourceLog = "log";
    private const string SourceOutput = "output";
    private const string UnknownStage = "unknown";

    private static readonly string[] TriggerOrder = { "schedule", "manual", "fetch-followup", "console", "other" };

    private static readonly Regex VersionPattern = new(@"^[0-9]+(\.[0-9]+){1,3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IdentPattern = new(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TimePattern = new(@"^[0-9]{1,2}:[0-9]{2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DailyLabelPattern = new(@"^daily-[0-9]{8}-h[0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> KnownCategories = new(StringComparer.Ordinal)
    {
        AiJsonDiagnostics.CategoryEmpty, AiJsonDiagnostics.CategoryNoJson, AiJsonDiagnostics.CategoryTruncated,
        AiJsonDiagnostics.CategoryTypeMismatch, AiJsonDiagnostics.CategoryParseOther, AiJsonDiagnostics.CategoryValidation
    };

    private static readonly HashSet<string> KnownEndedBy = new(StringComparer.Ordinal)
    {
        AiJsonDiagnostics.EndedExhausted, AiJsonDiagnostics.EndedEmpty, AiJsonDiagnostics.EndedTransport
    };

    private static readonly HashSet<string> KnownPrtgOutcomes = new(StringComparer.Ordinal)
    {
        BatchRun.PrtgOutcomeDisabled, BatchRun.PrtgOutcomeSuccess, BatchRun.PrtgOutcomePartial,
        BatchRun.PrtgOutcomeFailed, BatchRun.PrtgOutcomeNoOutput
    };

    private static readonly HashSet<string> KnownPhaseOutcomes = new(StringComparer.Ordinal)
    {
        RunPhaseOutcomes.Success, RunPhaseOutcomes.Partial, RunPhaseOutcomes.Failed, RunPhaseOutcomes.Stopped, RunPhaseOutcomes.Skipped
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly BatchRunStore _runs;
    private readonly RunMonitorService _monitor;
    private readonly ISystemSettingsStore _settings;
    private readonly ScheduleOptionsStore _schedule;
    private readonly IHostStore _hosts;
    private readonly IAnalysisRecordQuery _records;
    private readonly Func<IRunExportMasker> _maskerFactory;
    private readonly RunExportLimits _limits;

    public RunExportService(
        BatchRunStore runs, RunMonitorService monitor, ISystemSettingsStore settings, ScheduleOptionsStore schedule,
        IHostStore hosts, IAnalysisRecordQuery records, Func<IRunExportMasker> maskerFactory,
        RunExportLimits? limits = null)
    {
        _runs = runs;
        _monitor = monitor;
        _settings = settings;
        _schedule = schedule;
        _hosts = hosts;
        _records = records;
        _maskerFactory = maskerFactory;
        _limits = limits ?? new RunExportLimits();
    }

    public RunExportResult Build(int days, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 字典來源讀不到就在這裡中止（B-7），不先花時間讀幾十萬行再失敗
        var masker = _maskerFactory();
        return new Session(this, masker, days, ct).Run();
    }

    // ===================================================================================
    // 純函式（internal 供測試直接驗證）
    // ===================================================================================

    /// <summary>
    /// 判斷執行輸出行是不是問題訊號：去空白後行首為 ⚠ 或 ✗。「頻率異常／慢速惡化」那一行是分析結果類輸出
    /// （被監控主機的狀況，不是專案缺陷），明確排除。回傳推定的等級（⚠＝Warn、✗＝Error）。
    /// </summary>
    internal static bool TryGetOutputSignal(string? message, out string level)
    {
        level = string.Empty;
        if (string.IsNullOrEmpty(message)) return false;

        var trimmed = message.AsSpan().TrimStart();
        if (trimmed.Length == 0) return false;

        if (trimmed[0] == '⚠') level = "Warn";
        else if (trimmed[0] == '✗') level = "Error";
        else return false;

        if (message.Contains(AnalysisOrchestrator.TrendAlertConsoleHeading, StringComparison.Ordinal))
        {
            level = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 把訊息裡的「AI 回覆片段」換成「有片段／空回覆＋長度」。片段來源有三處，都是從
    /// <see cref="AIService"/> 共用的常數辨識：契約失敗錯誤訊息（<see cref="AIService.JsonContractError"/> 之後）、
    /// 兩個逐次嘗試 Warn 範本的預覽／解析結果欄。同一則回覆已完整出現在 aiFormatAnomalies，不在訊號組重複。
    /// </summary>
    internal static string StripReplyFragment(string message)
    {
        var contract = AIService.JsonContractError + "：";
        var at = message.IndexOf(contract, StringComparison.Ordinal);
        if (at >= 0)
        {
            var start = at + contract.Length;
            return message[..start] + FragmentNote(message[start..]);
        }

        foreach (var (prefix, marker) in new[]
                 {
                     (AIService.JsonParseFailedPrefix, AIService.JsonParseFailedPreviewMarker),
                     (AIService.JsonValidationFailedPrefix, AIService.JsonValidationParsedMarker)
                 })
        {
            if (!message.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var markerAt = message.IndexOf(marker, StringComparison.Ordinal);
            if (markerAt < 0) continue;
            var start = markerAt + marker.Length;
            return message[..start] + FragmentNote(message[start..]);
        }

        return message;
    }

    private const string EmptyReplyNote = "（空回覆）";

    private static string FragmentNote(string rest)
    {
        var trimmed = rest.Trim();
        if (trimmed.Length == 0 || trimmed == EmptyReplyNote) return EmptyReplyNote;
        return $"（回覆片段已省略：有片段，{trimmed.Length} 字）";
    }

    /// <summary>
    /// 階段判定：<paramref name="at"/> 落在該趟哪個階段的起訖內；落在多個取最內層（子階段優先，再取最晚開始者），
    /// 都不符合或舊紀錄缺 <c>Phases</c> 一律 <c>unknown</c>。未結束的階段以該趟結束時間為界（趟次也沒結束＝無上界）。
    /// </summary>
    internal static string StageOf(BatchRun? run, DateTime at)
    {
        if (run?.Phases is not { Count: > 0 } phases) return UnknownStage;

        RunPhaseRecord? best = null;
        var bestIsSub = false;
        foreach (var phase in phases)
        {
            var end = phase.FinishedAt ?? run.FinishedAt ?? DateTime.MaxValue;
            if (at < phase.StartedAt || at > end) continue;

            var isSub = !RunTimingPhases.IsTopLevel(phase.Key);
            if (best == null || (isSub && !bestIsSub) || (isSub == bestIsSub && phase.StartedAt > best.StartedAt))
            {
                best = phase;
                bestIsSub = isSub;
            }
        }

        if (best == null) return UnknownStage;
        return RunTimingPhases.Labels.ContainsKey(best.Key) ? best.Key : "other";
    }

    /// <summary>觸發來源只輸出類別，不輸出帳號</summary>
    internal static string TriggerCategory(string? trigger)
    {
        if (string.IsNullOrEmpty(trigger)) return "console";
        if (trigger == BatchRunTriggers.Schedule) return "schedule";
        if (trigger == BatchRunTriggers.FetchFollowUp) return "fetch-followup";
        if (trigger == "console") return "console";
        if (trigger.StartsWith("manual:", StringComparison.Ordinal) || trigger == "manual") return "manual";
        return "other";
    }

    internal static string JobTypeOf(BatchRun run) => run.JobType == BatchRun.JobTypeAi ? "ai" : "fetch";

    internal static double Round4(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Round(value, 4, MidpointRounding.AwayFromZero);

    internal static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return Round4(sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0);
    }

    /// <summary>最近排名法（nearest-rank）的 P95</summary>
    internal static double? Percentile95(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(0.95 * sorted.Count);
        return Round4(sorted[Math.Clamp(rank, 1, sorted.Count) - 1]);
    }

    /// <summary>截到 <paramref name="max"/> 字，不從代理對中間切開；被截掉時結尾補「…」</summary>
    internal static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + "…";
    }

    /// <summary>孤立的代理字元（遭截斷的 emoji 等）換成 U+FFFD，否則 JSON 序列化會擲例外</summary>
    internal static string Clean(string s)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb?.Append(c).Append(s[i + 1]);
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                sb ??= new StringBuilder(s.Length).Append(s, 0, i);
                sb.Append('�');
                continue;
            }

            sb?.Append(c);
        }

        return sb?.ToString() ?? s;
    }

    // ===================================================================================
    // 單次匯出的工作階段
    // ===================================================================================

    private sealed class SignalGroup
    {
        public string Source = string.Empty;
        public string Logger = string.Empty;
        public int Rank;
        public int Count;
        public int ErrorFatalCount;
        public bool Exempt;
        public HashSet<long> Runs = new();
        public DateTime First = DateTime.MaxValue;
        public DateTime Last = DateTime.MinValue;
        public Dictionary<DateTime, int> Daily = new();
        public Dictionary<string, int> Versions = new(StringComparer.Ordinal);
        public Dictionary<string, int> Stages = new(StringComparer.Ordinal);
        public List<string> Samples = new();
        public string? Stack;
        public List<(DateTime At, long RunId)> Recent = new();
        public string SortKey = string.Empty;
    }

    private sealed class SlowSqlAcc
    {
        public int Count;
        public long MaxMs;
        public double TotalMs;
        public HashSet<long> Runs = new();
        public DateTime First = DateTime.MaxValue;
        public DateTime Last = DateTime.MinValue;
    }

    private sealed record DiagnosticHit(DateTime LoggedAt, long RunId, AiJsonDiagnosticsEntry Entry);

    private sealed class Session
    {
        private readonly RunExportService _s;
        private readonly IRunExportMasker _m;
        private readonly RunExportLimits _l;
        private readonly int _days;
        private readonly CancellationToken _ct;

        private readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _versionLabels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _loggerLabels = new(StringComparer.Ordinal);
        private readonly List<ExportOmittedDto> _omitted = new();

        private readonly DateTime _now = DateTime.Now;
        private readonly DateTime _cutoff;

        // 訊號累計
        private readonly Dictionary<string, SignalGroup> _groups = new(StringComparer.Ordinal);
        private int _overflowCount;
        private int _overflowErrorFatal;
        private int _totErrorFatal, _totWarn, _totOutput, _totSlowSql, _totDiagnostics;
        private readonly Dictionary<string, SlowSqlAcc> _slowSql = new(StringComparer.Ordinal);
        private Dictionary<long, BatchRun> _runsById = new();

        // AI 診斷
        private HashSet<string> _caseKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DiagnosticHit>> _diagHits = new(StringComparer.Ordinal);
        private int _diagEntries;
        private DateTime? _diagFirstSeen;
        private readonly Dictionary<string, int> _diagByCategory = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _diagByEndedBy = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _diagByResponseType = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (int Parse, int Validation)> _failureHistory = new(StringComparer.Ordinal);

        public Session(RunExportService service, IRunExportMasker masker, int days, CancellationToken ct)
        {
            _s = service;
            _m = masker;
            _l = service._limits;
            _days = days;
            _ct = ct;
            _cutoff = DateTime.Today.AddDays(-days + 1);
        }

        // ---------- 放行值 ----------

        /// <summary>登錄「程式自己產生」的值為整串放行（結果掃描不檢查它）。資料來源的字串不得走這裡</summary>
        private string Lit(string value)
        {
            _allowed.Add(value);
            return value;
        }

        private string? LitOrNull(string? value) => value == null ? null : Lit(value);

        private string Ts(DateTime value) => Lit(value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));

        private string? Ts(DateTime? value) => value.HasValue ? Ts(value.Value) : null;

        private string Day(DateTime value) => Lit(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        /// <summary>識別字形狀（類別名、欄位名）的值整串放行；其他形狀走遮罩</summary>
        private string Ident(string? value)
        {
            if (string.IsNullOrEmpty(value)) return Lit("unknown");
            return IdentPattern.IsMatch(value) ? Lit(value) : Text(value);
        }

        /// <summary>屬於已知封閉集合的值整串放行；不在集合內的當資料處理（遮罩後交給掃描）</summary>
        private string Closed(string? value, HashSet<string> known)
        {
            if (string.IsNullOrEmpty(value)) return Lit("unknown");
            return known.Contains(value) ? Lit(value) : Text(value);
        }

        /// <summary>自由文字：先遮罩，再清掉孤立代理字元</summary>
        private string Text(string? value) => Clean(_m.Mask(value));

        private string TextCut(string? value, int max) => Cut(Text(value), max);

        private string VersionLabel(string? raw)
        {
            raw ??= string.Empty;
            if (_versionLabels.TryGetValue(raw, out var cached)) return cached;
            string label;
            if (raw.Length == 0) label = Lit("unknown");
            else if (VersionPattern.IsMatch(raw)) label = Lit(raw);
            else label = Text(raw);
            _versionLabels[raw] = label;
            return label;
        }

        private string LoggerLabel(string? raw)
        {
            raw ??= string.Empty;
            if (_loggerLabels.TryGetValue(raw, out var cached)) return cached;
            var label = raw.Length == 0 ? Lit("unknown") : IdentPattern.IsMatch(raw) ? Lit(raw) : Text(raw);
            _loggerLabels[raw] = label;
            return label;
        }

        // ---------- 主流程 ----------

        public RunExportResult Run()
        {
            var settings = _s._settings.Get();
            var schedule = _s._schedule.Get();
            var hosts = _s._hosts.GetAll();
            var hostById = hosts.GroupBy(h => h.HostId).ToDictionary(g => g.Key, g => g.First());

            // 1. 格式異常案例（先查，才知道要為哪些主機日保留詳細診斷）
            var (caseRecords, caseLowerBound) = QueryCases();
            foreach (var rec in caseRecords.Take(_l.AiCases))
                if (rec.HostId > 0) _caseKeys.Add($"daily-{rec.Date:yyyyMMdd}-h{rec.HostId}");

            // 2. 趟次（多載入幾天，讓跨進期間起點的長跑趟次也解析得到）
            _ct.ThrowIfCancellationRequested();
            var loaded = _s._runs.GetRecentRuns(_days + _l.RunLookbackExtraDays, null);
            foreach (var run in loaded) _runsById[run.RunId] = run;
            var periodRuns = loaded.Where(r => r.StartedAt.Date >= _cutoff).ToList();

            // 3. 診斷行：逐行累計
            _s._runs.ForEachRecentLog(_days, OnLog);
            _ct.ThrowIfCancellationRequested();

            // 4. 各區塊
            var statuses = periodRuns.ToDictionary(r => r.RunId, r => _s._monitor.StatusOfRun(r, _now));
            var dailyHealth = BuildDailyHealth();
            var runStats = BuildRunStats(periodRuns, statuses);

            var (signalGroups, stacks, runCodes) = BuildSignalGroups();
            var slowSql = BuildSlowSql();
            var abnormal = BuildAbnormalRuns(periodRuns, statuses, runCodes);
            var ai = BuildAiSection(caseRecords, caseLowerBound, hostById);
            var context = BuildContext(settings, schedule, hosts);

            // 5. meta 最後組：代號數量要等所有遮罩都做完
            var meta = new RunExportMetaDto
            {
                SchemaVersion = SchemaVersion,
                ExportedAt = Ts(_now),
                UtcOffset = Lit(FormatOffset(TimeZoneInfo.Local.GetUtcOffset(_now))),
                AppVersion = VersionLabel(typeof(BatchRunRecorder).Assembly.GetName().Version?.ToString()),
                Period = new ExportPeriodDto { From = Day(_cutoff), To = Day(_now) },
                Days = _days,
                RetentionDays = settings.RunLogRetentionDays,
                HasData = periodRuns.Count > 0,
                IncludesEventText = true,
                Deidentification = new ExportDeidentificationDto
                {
                    Tokens = _m.TokenCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => new ExportTokenCountDto(Lit(kv.Key), kv.Value)).ToList(),
                    UndecryptableSecrets = _m.UndecryptableSecrets.Select(Lit).ToList(),
                    Notice = Lit("主機、帳號、IP、網址、路徑、SID 與密鑰已換成代號，代號只在本檔內有效、檔案內沒有對照表；"
                                 + "自由文字（訊息樣本、堆疊、AI 回覆與問題訊息）無法保證完全遮除，含空白的人名若不在系統字典內，後半可能漏遮；群組與角色名稱若在兩字以內不會被遮。")
                },
                SignalTotals = new ExportSignalTotalsDto
                {
                    ErrorFatal = _totErrorFatal,
                    Warn = _totWarn,
                    OutputSignals = _totOutput,
                    SlowSql = _totSlowSql,
                    AiDiagnosticsLines = _totDiagnostics
                },
                Omitted = _omitted.OrderBy(o => o.Item, StringComparer.Ordinal).ToList()
            };

            var doc = new RunExportDocument
            {
                Meta = meta,
                Guide = BuildGuide(),
                Context = context,
                DailyHealth = dailyHealth,
                RunStats = runStats,
                SignalGroups = signalGroups,
                Stacks = stacks,
                SlowSql = slowSql,
                AbnormalRuns = abnormal,
                AiFormatAnomalies = ai
            };

            // 6. 序列化 → 結果掃描（guide 是程式寫死的說明，不進掃描；其餘全掃，只掃值）→ 輸出
            _ct.ThrowIfCancellationRequested();
            var scanJson = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(doc with { Guide = null }, JsonOptions));
            _m.EnsureNoResidual(scanJson, _allowed, scanPropertyNames: false);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(doc, JsonOptions);
            if (bytes.LongLength > _l.OutputBytes)
            {
                Log.Warn("診斷匯出超過檔案上限（{0} 位元組 > {1}），回 413；上限是依各區塊上限推算的，發生代表推算有誤。", bytes.LongLength, _l.OutputBytes);
                throw new DomainException(ApiErrorCodes.CapacityExceeded,
                    "匯出內容超過檔案大小上限，未產生檔案；請縮短匯出天數後重試。");
            }

            return new RunExportResult(bytes, _days, periodRuns.Count, signalGroups.Count, abnormal.Count,
                ai.Cases.Count, _m.TokenCounts);
        }

        private static string FormatOffset(TimeSpan offset) =>
            (offset < TimeSpan.Zero ? "-" : "+") + Math.Abs(offset.Hours).ToString("00", CultureInfo.InvariantCulture)
            + ":" + Math.Abs(offset.Minutes).ToString("00", CultureInfo.InvariantCulture);

        // ---------- 逐行處理 ----------

        private void OnLog(BatchRunLog l)
        {
            _ct.ThrowIfCancellationRequested();

            var logger = l.Logger ?? string.Empty;
            if (string.Equals(logger, AiJsonDiagnostics.LoggerName, StringComparison.Ordinal))
            {
                OnDiagnostic(l);
                return;
            }

            var message = l.Message ?? string.Empty;
            var isLogLevel = l.Level is "Warn" or "Error" or "Fatal";
            var isErrorFatal = l.Level is "Error" or "Fatal";

            if (isLogLevel)
            {
                if (isErrorFatal) _totErrorFatal++;
                else _totWarn++;

                if (l.Level == "Warn" && string.Equals(logger, nameof(AIService), StringComparison.Ordinal))
                    CountAiWarn(message);

                // 慢 SQL 依操作彙總；訊息解析不到的退回一般訊號組，不丟棄
                if (l.Level == "Warn" && string.Equals(logger, BatchRunRecorder.ExemptWarnLogger, StringComparison.Ordinal)
                    && SqlPerformanceMonitor.TryParseSlowLog(message, out var operation, out var elapsedMs))
                {
                    _totSlowSql++;
                    AccumulateSlowSql(operation, elapsedMs, l);
                    return;
                }

                Accumulate(SourceLog, l.Level == "Fatal" ? RankFatal : l.Level == "Error" ? RankError : RankWarn,
                    logger, message, l.ExceptionText, l);
                return;
            }

            if (string.Equals(logger, BatchRunRecorder.OutputLogger, StringComparison.Ordinal)
                && TryGetOutputSignal(message, out var outputLevel))
            {
                _totOutput++;
                Accumulate(SourceOutput, outputLevel == "Error" ? RankError : RankWarn, logger, message, null, l);
            }
        }

        private void OnDiagnostic(BatchRunLog l)
        {
            _totDiagnostics++;
            var entry = AiJsonDiagnostics.TryRead(l.Message);
            if (entry == null) return;

            _diagEntries++;
            var day = l.LoggedAt.Date;
            if (_diagFirstSeen == null || day < _diagFirstSeen) _diagFirstSeen = day;

            Bump(_diagByEndedBy, entry.EndedBy);
            Bump(_diagByResponseType, entry.ResponseType);
            foreach (var attempt in entry.AttemptDetails) Bump(_diagByCategory, attempt.Category);

            if (!_caseKeys.Contains(entry.Label)) return;
            if (!_diagHits.TryGetValue(entry.Label, out var list))
                _diagHits[entry.Label] = list = new List<DiagnosticHit>();
            list.Add(new DiagnosticHit(l.LoggedAt, l.RunId, entry));
            if (list.Count > _l.DiagnosticsPerCase)
            {
                // 只留最近的幾筆
                list.Sort((a, b) => b.LoggedAt.CompareTo(a.LoggedAt));
                list.RemoveRange(_l.DiagnosticsPerCase, list.Count - _l.DiagnosticsPerCase);
            }
        }

        private static void Bump(Dictionary<string, int> map, string? key)
        {
            key ??= string.Empty;
            map[key] = map.GetValueOrDefault(key) + 1;
        }

        /// <summary>執行紀錄 Warn 的歷史統計：比對 AIService 的兩個逐次嘗試範本，依回覆型別計次；比對不到就是不計</summary>
        private void CountAiWarn(string message)
        {
            bool isParse;
            if (message.StartsWith(AIService.JsonParseFailedPrefix, StringComparison.Ordinal)) isParse = true;
            else if (message.StartsWith(AIService.JsonValidationFailedPrefix, StringComparison.Ordinal)) isParse = false;
            else return;

            var at = message.IndexOf(AIService.JsonFailureTypeMarker, StringComparison.Ordinal);
            if (at < 0) return;
            var start = at + AIService.JsonFailureTypeMarker.Length;
            var end = message.IndexOf('，', start);
            if (end <= start) return;

            var type = message[start..end];
            if (!IdentPattern.IsMatch(type)) return;

            var current = _failureHistory.GetValueOrDefault(type);
            _failureHistory[type] = isParse ? (current.Parse + 1, current.Validation) : (current.Parse, current.Validation + 1);
        }

        private void AccumulateSlowSql(string operation, long elapsedMs, BatchRunLog l)
        {
            var key = Text(operation);
            if (!_slowSql.TryGetValue(key, out var acc)) _slowSql[key] = acc = new SlowSqlAcc();
            acc.Count++;
            acc.MaxMs = Math.Max(acc.MaxMs, elapsedMs);
            acc.TotalMs += elapsedMs;
            acc.Runs.Add(l.RunId);
            if (l.LoggedAt < acc.First) acc.First = l.LoggedAt;
            if (l.LoggedAt > acc.Last) acc.Last = l.LoggedAt;
        }

        private void Accumulate(string source, int rank, string logger, string message, string? exceptionText, BatchRunLog l)
        {
            // 順序：移除回覆片段 → 遮罩 → 正規化（日期／數字／GUID）→ 分組。遮罩必須在正規化之前，
            // 否則 SRV01 會先變成 SRV{n}，字典就認不得
            var masked = Text(StripReplyFragment(message.Trim()));
            var normalized = RunMessageNormalizer.Normalize(masked);
            var key = source + "\u0001" + logger + "\u0001" + normalized;

            if (!_groups.TryGetValue(key, out var g))
            {
                if (_groups.Count >= _l.GroupDictionary)
                {
                    _overflowCount++;
                    if (rank >= RankError && source == SourceLog) _overflowErrorFatal++;
                    return;
                }

                g = new SignalGroup
                {
                    Source = source,
                    Logger = LoggerLabel(logger),
                    Exempt = false,
                    SortKey = key
                };
                _groups[key] = g;
            }

            g.Rank = Math.Max(g.Rank, rank);
            g.Count++;
            if (rank >= RankError && source == SourceLog) g.ErrorFatalCount++;
            if (l.Level == "Warn" && string.Equals(logger, BatchRunRecorder.ExemptWarnLogger, StringComparison.Ordinal))
                g.Exempt = true;
            g.Runs.Add(l.RunId);
            if (l.LoggedAt < g.First) g.First = l.LoggedAt;
            if (l.LoggedAt > g.Last) g.Last = l.LoggedAt;
            var date = l.LoggedAt.Date;
            g.Daily[date] = g.Daily.GetValueOrDefault(date) + 1;

            _runsById.TryGetValue(l.RunId, out var run);
            var version = run == null ? Lit("unknown") : VersionLabel(run.AppVersion);
            g.Versions[version] = g.Versions.GetValueOrDefault(version) + 1;
            var stage = StageOf(run, l.LoggedAt);
            g.Stages[stage] = g.Stages.GetValueOrDefault(stage) + 1;

            if (g.Samples.Count < _l.SamplesPerGroup)
            {
                var sample = Cut(masked, _l.SampleChars);
                if (!g.Samples.Contains(sample, StringComparer.Ordinal)) g.Samples.Add(sample);
            }

            if (g.Stack == null && !string.IsNullOrWhiteSpace(exceptionText))
                g.Stack = Cut(Clean(_m.MaskStack(exceptionText)), _l.StackChars);

            TrackRecent(g, l.LoggedAt, l.RunId);
        }

        private void TrackRecent(SignalGroup g, DateTime at, long runId)
        {
            var existing = g.Recent.FindIndex(x => x.RunId == runId);
            if (existing >= 0)
            {
                if (at > g.Recent[existing].At) g.Recent[existing] = (at, runId);
                return;
            }

            g.Recent.Add((at, runId));
            if (g.Recent.Count > _l.RecentRunIds)
            {
                var oldest = 0;
                for (var i = 1; i < g.Recent.Count; i++)
                    if (g.Recent[i].At < g.Recent[oldest].At) oldest = i;
                g.Recent.RemoveAt(oldest);
            }
        }

        // ---------- 訊號組 / 慢 SQL ----------

        private (List<ExportSignalGroupDto> Groups, List<ExportStackDto> Stacks, Dictionary<long, List<string>> RunCodes)
            BuildSignalGroups()
        {
            var ordered = _groups.Values
                .OrderByDescending(g => g.Rank)
                .ThenByDescending(g => g.Count)
                .ThenBy(g => g.SortKey, StringComparer.Ordinal)
                .ToList();
            var kept = ordered.Take(_l.SignalGroups).ToList();
            var dropped = ordered.Skip(_l.SignalGroups).ToList();

            if (dropped.Count > 0 || _overflowCount > 0)
            {
                _omitted.Add(new ExportOmittedDto
                {
                    Item = Lit("signalGroups"),
                    Count = dropped.Count + _overflowCount,
                    Limit = _l.SignalGroups,
                    Occurrences = dropped.Sum(g => g.Count) + _overflowCount,
                    ErrorFatalOccurrences = dropped.Sum(g => g.ErrorFatalCount) + _overflowErrorFatal
                });
            }

            var stackIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var stacks = new List<ExportStackDto>();
            var runCodes = new Dictionary<long, List<string>>();
            var result = new List<ExportSignalGroupDto>();

            for (var i = 0; i < kept.Count; i++)
            {
                var g = kept[i];
                var code = Lit($"S{i + 1:000}");

                string? stackId = null;
                if (g.Stack != null)
                {
                    if (!stackIds.TryGetValue(g.Stack, out stackId))
                    {
                        stackId = Lit($"T{stackIds.Count + 1:000}");
                        stackIds[g.Stack] = stackId;
                        stacks.Add(new ExportStackDto(stackId, g.Stack));
                    }
                }

                foreach (var runId in g.Runs)
                {
                    if (!runCodes.TryGetValue(runId, out var codes)) runCodes[runId] = codes = new List<string>();
                    codes.Add(code);
                }

                var versionRuns = g.Runs
                    .GroupBy(id => _runsById.TryGetValue(id, out var r) ? VersionLabel(r.AppVersion) : Lit("unknown"), StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

                result.Add(new ExportSignalGroupDto
                {
                    Code = code,
                    Level = Lit(g.Rank >= RankFatal ? "Fatal" : g.Rank >= RankError ? "Error" : "Warn"),
                    Source = Lit(g.Source),
                    Logger = g.Logger,
                    Stages = g.Stages.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x => new ExportStageCountDto(Lit(x.Key), x.Value)).ToList(),
                    Count = g.Count,
                    AffectedRuns = g.Runs.Count,
                    ErrorFatalCount = g.ErrorFatalCount,
                    FirstSeen = Ts(g.First),
                    LastSeen = Ts(g.Last),
                    Daily = g.Daily.OrderBy(x => x.Key).Select(x => new ExportDayCountDto(Day(x.Key), x.Value)).ToList(),
                    Versions = g.Versions.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x => new ExportVersionCountDto(x.Key, x.Value, versionRuns.GetValueOrDefault(x.Key))).ToList(),
                    Samples = g.Samples,
                    StackId = stackId,
                    RecentRunIds = g.Recent.OrderByDescending(x => x.At).ThenByDescending(x => x.RunId).Select(x => x.RunId).ToList(),
                    ExcludedFromWarnCount = g.Exempt
                });
            }

            return (result, stacks, runCodes);
        }

        private List<ExportSlowSqlDto> BuildSlowSql()
        {
            var ordered = _slowSql
                .OrderByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
            var kept = ordered.Take(_l.SlowSqlOperations).ToList();
            if (ordered.Count > kept.Count)
            {
                _omitted.Add(new ExportOmittedDto
                {
                    Item = Lit("slowSql"),
                    Count = ordered.Count - kept.Count,
                    Limit = _l.SlowSqlOperations,
                    Occurrences = ordered.Skip(kept.Count).Sum(kv => kv.Value.Count)
                });
            }

            return kept.Select(kv => new ExportSlowSqlDto
            {
                Operation = kv.Key,
                Count = kv.Value.Count,
                MaxMs = kv.Value.MaxMs,
                AvgMs = Round4(kv.Value.TotalMs / kv.Value.Count),
                AffectedRuns = kv.Value.Runs.Count,
                FirstSeen = Ts(kv.Value.First),
                LastSeen = Ts(kv.Value.Last)
            }).ToList();
        }

        // ---------- 每日狀態 / 統計 ----------

        private List<ExportDailyHealthDto> BuildDailyHealth()
        {
            _ct.ThrowIfCancellationRequested();
            // 服務層一次取完整天數（分頁上限 90 只在控制器）；總表不含今天
            var summaries = _s._monitor.GetDaySummaries(_days, 1, _days);
            return summaries.Select(s => new ExportDailyHealthDto
            {
                Date = Lit(s.Date),
                TotalHosts = s.TotalHosts,
                Success = s.SuccessCount,
                Backfilled = s.BackfilledCount,
                Warning = s.WarningCount,
                Failed = s.FailedCount,
                Stuck = s.StuckCount,
                Running = s.RunningCount,
                NotRun = s.NotRunCount,
                LocalDisabled = s.LocalDisabledCount,
                Stopped = s.StoppedCount,
                PrtgOutcome = s.PrtgOutcome == null ? null : Closed(s.PrtgOutcome, KnownPrtgOutcomes),
                PrtgFindings = s.PrtgFindings,
                PrtgTargetSensors = s.PrtgTargetSensors,
                PrtgNote = s.PrtgNote == null ? null : TextCut(s.PrtgNote, _l.AiInputTextChars),
                TotalSeconds = s.Timing?.TotalSeconds
            }).ToList();
        }

        private ExportRunStatsDto BuildRunStats(List<BatchRun> runs, Dictionary<long, string> statuses)
        {
            var byJob = runs
                .GroupBy(r => (Job: JobTypeOf(r), Trigger: TriggerCategory(r.Trigger)))
                .OrderBy(g => g.Key.Job, StringComparer.Ordinal)
                .ThenBy(g => Array.IndexOf(TriggerOrder, g.Key.Trigger))
                .Select(g =>
                {
                    int Count(string status) => g.Count(r => statuses[r.RunId] == status);
                    var calls = g.Sum(r => r.AiCalls);
                    var failures = g.Sum(r => r.AiFailures);
                    return new ExportJobStatDto
                    {
                        JobType = Lit(g.Key.Job),
                        Trigger = Lit(g.Key.Trigger),
                        Runs = g.Count(),
                        Success = Count(BatchRunStatus.Success),
                        Warning = Count(BatchRunStatus.Warning),
                        Failed = Count(BatchRunStatus.Failed),
                        Stopped = Count(BatchRunStatus.Stopped),
                        Running = Count(BatchRunStatus.Running),
                        Stuck = Count(BatchRunStatus.Stuck),
                        CatchUpRuns = g.Count(r => r.CatchUp == true),
                        AiCalls = calls,
                        AiFailures = failures,
                        AiFailureRate = calls == 0 ? null : Round4((double)failures / calls)
                    };
                }).ToList();

            var phaseRecords = runs
                .Where(r => r.Phases != null)
                .SelectMany(r => r.Phases!.Select(p => (Run: r, Phase: p)))
                .GroupBy(x => RunTimingPhases.Labels.ContainsKey(x.Phase.Key) ? x.Phase.Key : "other")
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var seconds = g.Where(x => x.Phase.FinishedAt.HasValue)
                        .Select(x => Math.Max(0, (x.Phase.FinishedAt!.Value - x.Phase.StartedAt).TotalSeconds)).ToList();
                    var notSucceeded = g.Count(x =>
                        x.Phase.FinishedAt.HasValue
                            ? x.Phase.Outcome is RunPhaseOutcomes.Failed or RunPhaseOutcomes.Partial or RunPhaseOutcomes.Stopped
                            : x.Run.FinishedAt.HasValue);
                    return new ExportPhaseStatDto
                    {
                        Phase = Lit(g.Key),
                        Runs = g.Count(),
                        Completed = seconds.Count,
                        NotSucceeded = notSucceeded,
                        MedianSeconds = Median(seconds),
                        P95Seconds = Percentile95(seconds),
                        MaxSeconds = seconds.Count == 0 ? null : Round4(seconds.Max())
                    };
                }).ToList();

            var versions = runs
                .GroupBy(r => VersionLabel(r.AppVersion), StringComparer.Ordinal)
                .OrderBy(g => g.Min(r => r.StartedAt)).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new ExportVersionStatDto
                {
                    Version = g.Key,
                    Runs = g.Count(),
                    FirstSeen = Ts(g.Min(r => r.StartedAt)),
                    LastSeen = Ts(g.Max(r => r.StartedAt)),
                    WarnCount = g.Sum(r => r.WarnCount),
                    ErrorCount = g.Sum(r => r.ErrorCount)
                }).ToList();

            return new ExportRunStatsDto
            {
                TotalRuns = runs.Count,
                FailedOrStuckWithoutErrorRuns = runs.Count(r =>
                    r.ErrorCount == 0 && statuses[r.RunId] is BatchRunStatus.Failed or BatchRunStatus.Stuck),
                ByJob = byJob,
                Phases = phaseRecords,
                Versions = versions
            };
        }

        // ---------- 異常趟 ----------

        private List<ExportAbnormalRunDto> BuildAbnormalRuns(
            List<BatchRun> runs, Dictionary<long, string> statuses, Dictionary<long, List<string>> runCodes)
        {
            var abnormal = runs
                .Where(r => statuses[r.RunId] is BatchRunStatus.Failed or BatchRunStatus.Stuck
                    or BatchRunStatus.Stopped or BatchRunStatus.Warning)
                .OrderBy(r => statuses[r.RunId] is BatchRunStatus.Failed or BatchRunStatus.Stuck ? 0 : 1)
                .ThenByDescending(r => r.StartedAt)
                .ThenByDescending(r => r.RunId)
                .ToList();
            var kept = abnormal.Take(_l.AbnormalRuns).ToList();
            if (abnormal.Count > kept.Count)
                _omitted.Add(new ExportOmittedDto { Item = Lit("abnormalRuns"), Count = abnormal.Count - kept.Count, Limit = _l.AbnormalRuns });

            return kept.Select(r => new ExportAbnormalRunDto
            {
                RunId = r.RunId,
                JobType = Lit(JobTypeOf(r)),
                Trigger = Lit(TriggerCategory(r.Trigger)),
                CatchUp = r.CatchUp,
                Host = _m.MaskHost(r.HostName),
                AppVersion = VersionLabel(r.AppVersion),
                Args = Text(r.Args),
                StartedAt = Ts(r.StartedAt),
                FinishedAt = Ts(r.FinishedAt),
                DurationSeconds = r.FinishedAt.HasValue && !r.Interrupted ? (int)(r.FinishedAt.Value - r.StartedAt).TotalSeconds : null,
                Status = Lit(statuses[r.RunId]),
                ExitCode = r.ExitCode,
                Interrupted = r.Interrupted,
                Stopped = r.Stopped,
                WarnCount = r.WarnCount,
                ErrorCount = r.ErrorCount,
                AiFailures = r.AiFailures,
                LocalDaysAnalyzed = r.LocalDaysAnalyzed,
                LocalDaysFailed = r.LocalDaysFailed,
                NetiqDaysAnalyzed = r.NetiqDaysAnalyzed,
                NetiqDaysFailed = r.NetiqDaysFailed,
                NetiqHostsSkipped = r.NetiqHostsSkipped,
                PrtgOutcome = r.PrtgOutcome == null ? null : Closed(r.PrtgOutcome, KnownPrtgOutcomes),
                PrtgOutcomeNote = string.IsNullOrWhiteSpace(r.PrtgOutcomeNote) ? null : TextCut(r.PrtgOutcomeNote, _l.AiInputTextChars),
                UnsuccessfulPhases = (r.Phases ?? new List<RunPhaseRecord>())
                    .Where(p => p.FinishedAt.HasValue
                        ? p.Outcome is RunPhaseOutcomes.Failed or RunPhaseOutcomes.Partial or RunPhaseOutcomes.Stopped
                        : r.FinishedAt.HasValue)
                    .Take(_l.UnsuccessfulItems)
                    .Select(p => new ExportUnsuccessfulPhaseDto(
                        Lit(RunTimingPhases.Labels.ContainsKey(p.Key) ? p.Key : "other"),
                        p.Outcome == null ? null : Closed(p.Outcome, KnownPhaseOutcomes),
                        string.IsNullOrWhiteSpace(p.Note) ? null : TextCut(p.Note, _l.AiInputTextChars)))
                    .ToList(),
                UnsuccessfulPrtgDays = (r.PrtgDays ?? new List<PrtgDayStat>())
                    .Where(d => d.Outcome is BatchRun.PrtgOutcomePartial or BatchRun.PrtgOutcomeFailed || d.NeedsReevaluation)
                    .OrderBy(d => d.Date)
                    .Take(_l.UnsuccessfulItems)
                    .Select(d => new ExportPrtgDayDto(Day(d.Date), Closed(d.Outcome, KnownPrtgOutcomes),
                        string.IsNullOrWhiteSpace(d.Note) ? null : TextCut(d.Note, _l.AiInputTextChars), d.NeedsReevaluation))
                    .ToList(),
                SignalCodes = runCodes.TryGetValue(r.RunId, out var codes)
                    ? codes.OrderBy(c => c, StringComparer.Ordinal).Take(_l.RunSignalCodes).ToList()
                    : new List<string>()
            }).ToList();
        }

        // ---------- context ----------

        private RunExportContextDto BuildContext(SystemSettings settings, ScheduleOptions schedule, List<WebHost> hosts)
        {
            ExportWindowDto Window(ScheduleWindow w) => new(
                TimePattern.IsMatch(w.Start ?? "") ? Lit(w.Start!) : Text(w.Start),
                TimePattern.IsMatch(w.End ?? "") ? Lit(w.End!) : Text(w.End));

            return new RunExportContextDto
            {
                Schedule = new ExportScheduleContextDto
                {
                    Enabled = schedule.Enabled,
                    Windows = schedule.Windows.Select(Window).ToList(),
                    AiWindows = schedule.AiWindows.Select(Window).ToList(),
                    AiConcurrency = schedule.AiConcurrency,
                    LocalAnalysisEnabled = schedule.LocalAnalysisEnabled,
                    AutoCatchUp = schedule.AutoCatchUp,
                    DebugDump = schedule.DebugDump
                },
                Prtg = new ExportPrtgContextDto
                {
                    Enabled = settings.PrtgEnabled,
                    FetchStrategy = Text(settings.PrtgFetchStrategy),
                    ValueFetchScope = Text(settings.PrtgValueFetchScope)
                },
                Ai = new ExportAiContextDto
                {
                    Provider = Lit(AiProviders.Normalize(settings.AiProvider)),
                    Model = Text(settings.AiModel),
                    TimeoutSeconds = settings.AiTimeoutSeconds,
                    RetryCount = settings.AiRetryCount,
                    RetryDelaySeconds = settings.AiRetryDelaySeconds,
                    AiJsonRetryCount = settings.AiJsonRetryCount,
                    AiMaxTokens = settings.AiMaxTokens,
                    DeepDiveMaxTokens = settings.AiDeepDiveMaxTokens,
                    HeadlineMaxChars = LogAnalysisService.MaxHeadlineChars,
                    OtherFieldMaxChars = LogAnalysisService.MaxSummaryChars
                },
                Hosts = new ExportHostsContextDto
                {
                    Total = hosts.Count,
                    Active = hosts.Count(h => h.Active),
                    BySource = hosts
                        .GroupBy(h => string.Equals(h.Source, "netiq", StringComparison.OrdinalIgnoreCase) ? "netiq"
                            : string.Equals(h.Source, "local", StringComparison.OrdinalIgnoreCase) ? "local" : "other")
                        .OrderBy(g => g.Key, StringComparer.Ordinal)
                        .Select(g => new ExportHostSourceDto(Lit(g.Key), g.Count(), g.Count(h => h.Active))).ToList()
                },
                RunLogRetentionDays = settings.RunLogRetentionDays,
                RecordRetentionDays = settings.RetentionDays
            };
        }

        // ---------- AI 格式異常 ----------

        private (List<DailyAnalysisRecord> Records, bool LowerBound) QueryCases()
        {
            _ct.ThrowIfCancellationRequested();
            var records = _s._records.QueryByHeadline(_cutoff.AddDays(-1), DateTime.Today,
                LogAnalysisService.AiFormatAnomalyHeadline, _l.AiCasesScanned);
            return (records, records.Count >= _l.AiCasesScanned);
        }

        private static readonly (string Name, Func<AiInputFeatures, double?> Get)[] FeatureColumns =
        {
            ("issueCount", f => f.IssueCount), ("ruleHitCount", f => f.RuleHitCount), ("errorCount", f => f.ErrorCount),
            ("warningCount", f => f.WarningCount), ("auditCount", f => f.AuditCount),
            ("trendAlertCount", f => f.TrendAlertCount), ("correlationAlertCount", f => f.CorrelationAlertCount),
            ("inputChars", f => f.InputChars), ("longestItemChars", f => f.LongestItemChars),
            ("quoteCount", f => f.QuoteCount), ("backslashCount", f => f.BackslashCount),
            ("openBraceCount", f => f.OpenBraceCount), ("openBracketCount", f => f.OpenBracketCount),
            ("lessThanCount", f => f.LessThanCount), ("controlCharCount", f => f.ControlCharCount),
            ("nonBmpCharCount", f => f.NonBmpCharCount), ("pathLikeCount", f => f.PathLikeCount),
            ("jsonLikeCount", f => f.JsonLikeCount), ("xmlLikeCount", f => f.XmlLikeCount),
            ("nonAsciiRatio", f => f.NonAsciiRatio)
        };

        private static ExportFeaturesDto ToDto(AiInputFeatures f) => new()
        {
            IssueCount = f.IssueCount, RuleHitCount = f.RuleHitCount, ErrorCount = f.ErrorCount, WarningCount = f.WarningCount,
            AuditCount = f.AuditCount, TrendAlertCount = f.TrendAlertCount, CorrelationAlertCount = f.CorrelationAlertCount,
            DataIncomplete = f.DataIncomplete, InputChars = f.InputChars, LongestItemChars = f.LongestItemChars,
            QuoteCount = f.QuoteCount, BackslashCount = f.BackslashCount, OpenBraceCount = f.OpenBraceCount,
            OpenBracketCount = f.OpenBracketCount, LessThanCount = f.LessThanCount, ControlCharCount = f.ControlCharCount,
            NonBmpCharCount = f.NonBmpCharCount, PathLikeCount = f.PathLikeCount, JsonLikeCount = f.JsonLikeCount,
            XmlLikeCount = f.XmlLikeCount, NonAsciiRatio = f.NonAsciiRatio.HasValue ? Round4(f.NonAsciiRatio.Value) : null
        };

        private ExportAiFormatAnomaliesDto BuildAiSection(
            List<DailyAnalysisRecord> scanned, bool lowerBound, Dictionary<long, WebHost> hostById)
        {
            var shown = scanned.Take(_l.AiCases).ToList();
            if (scanned.Count > shown.Count)
                _omitted.Add(new ExportOmittedDto { Item = Lit("aiFormatCases"), Count = scanned.Count - shown.Count, Limit = _l.AiCases });

            // 摘要統計：涵蓋所有掃到的案例（不只顯示的那些）
            var parsed = scanned.Select(rec =>
            {
                if (rec.DetailPruned) return (Rec: rec, Attempts: (int?)null, Reply: (string?)null, Shape: (AiReplyShapeResult?)null);
                LogAnalysisService.TryParseAiFormatAnomalySummary(rec.Summary, out var attempts, out var reply);
                return (Rec: rec, Attempts: attempts, Reply: reply, Shape: AiReplyShape.Classify(reply, LogAnalysisService.MaxSummaryChars));
            }).ToList();

            string OsOf(DailyAnalysisRecord r) => r.HostId > 0 && hostById.TryGetValue(r.HostId, out var h) ? Ident(h.Os) : Lit("unknown");
            string SourceOf(DailyAnalysisRecord r) => Lit(r.LogSource switch
            {
                AnalysisLogSource.Local => "local",
                AnalysisLogSource.Netiq => "netiq",
                AnalysisLogSource.Prtg => "prtg",
                _ => "unknown"
            });

            List<ExportCountDto> Counts(IEnumerable<string> values) => values
                .GroupBy(v => v, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new ExportCountDto(g.Key, g.Count())).ToList();

            var summary = new ExportAiSummaryDto
            {
                ByDate = Counts(scanned.Select(r => Day(r.Date))),
                ByOs = Counts(scanned.Select(OsOf)),
                BySource = Counts(scanned.Select(SourceOf)),
                ByReplyShape = Counts(parsed.Select(p => p.Shape == null ? Lit("unknown") : Lit(p.Shape.Shape))),
                CutOffCount = parsed.Count(p => p.Shape?.CutOff == true),
                FailureHistory = _failureHistory.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new ExportFailureHistoryDto(Ident(kv.Key), kv.Value.Parse, kv.Value.Validation)).ToList(),
                Diagnostics = new ExportAiDiagnosticsSummaryDto
                {
                    Entries = _diagEntries,
                    ByCategory = _diagByCategory.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => new ExportCountDto(Closed(kv.Key, KnownCategories), kv.Value)).ToList(),
                    ByEndedBy = _diagByEndedBy.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => new ExportCountDto(Closed(kv.Key, KnownEndedBy), kv.Value)).ToList(),
                    ByResponseType = _diagByResponseType.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => new ExportCountDto(Ident(kv.Key), kv.Value)).ToList()
                }
            };

            var cases = new List<ExportAiCaseDto>();
            var caseFeatures = new List<AiInputFeatures>();
            var controlFeatures = new List<AiInputFeatures>();
            var inputsGiven = 0;
            var inputsOmitted = 0;

            for (var i = 0; i < shown.Count; i++)
            {
                _ct.ThrowIfCancellationRequested();
                var (rec, attempts, reply, shape) = parsed[i];

                AiInputFeatures? features = rec.DetailPruned ? null : AiInputFeatures.From(rec);
                if (features != null) caseFeatures.Add(features);

                ExportAiInputDto? input = null;
                if (!rec.DetailPruned)
                {
                    if (inputsGiven < _l.AiCaseInputs)
                    {
                        input = BuildInput(rec);
                        inputsGiven++;
                    }
                    else
                    {
                        inputsOmitted++;
                    }
                }

                var (control, controlFeature) = FindControl(rec);
                if (controlFeature != null) controlFeatures.Add(controlFeature);

                cases.Add(new ExportAiCaseDto
                {
                    Host = _m.MaskHost(rec.Host),
                    Date = Day(rec.Date),
                    Os = OsOf(rec),
                    DataSource = SourceOf(rec),
                    Risk = RiskLevels.All.Contains(rec.RiskLevel) ? Lit(rec.RiskLevel) : Text(rec.RiskLevel),
                    Attempts = attempts,
                    ReplyShape = shape == null ? null : Lit(shape.Shape),
                    CutOff = shape?.CutOff == true,
                    Reply = reply == null ? null : Text(reply),
                    DetailPruned = rec.DetailPruned,
                    Features = features == null ? null : ToDto(features),
                    Diagnostics = BuildCaseDiagnostics(rec),
                    Input = input,
                    Control = control
                });
            }

            if (inputsOmitted > 0)
                _omitted.Add(new ExportOmittedDto { Item = Lit("aiFormatInputs"), Count = inputsOmitted, Limit = _l.AiCaseInputs });

            ExportAiComparisonDto? comparison = null;
            if (shown.Count > 0)
            {
                var both = caseFeatures.Count > 0 && controlFeatures.Count > 0;
                comparison = new ExportAiComparisonDto
                {
                    CaseSamples = caseFeatures.Count,
                    ControlSamples = controlFeatures.Count,
                    Features = both
                        ? FeatureColumns.Select(c => new ExportFeatureMedianDto(Lit(c.Name),
                            Median(caseFeatures.Select(c.Get).Where(v => v.HasValue).Select(v => v!.Value).ToList()),
                            Median(controlFeatures.Select(c.Get).Where(v => v.HasValue).Select(v => v!.Value).ToList()))).ToList()
                        : null
                };
            }

            return new ExportAiFormatAnomaliesDto
            {
                CaseCount = scanned.Count,
                CaseCountIsLowerBound = lowerBound,
                DetailedDiagnosticsFirstSeen = _diagFirstSeen.HasValue ? Day(_diagFirstSeen.Value) : null,
                Summary = summary,
                Cases = cases,
                Comparison = comparison
            };
        }

        private ExportAiInputDto BuildInput(DailyAnalysisRecord rec) => new()
        {
            IssueTotal = rec.TopIssues.Count,
            Issues = rec.TopIssues.Take(_l.AiInputIssues).Select(issue => new ExportAiInputIssueDto(
                TextCut(issue.Source, _l.AiInputTextChars), issue.EventId, issue.Count,
                issue.SampleMessages is { Count: > 0 } && !string.IsNullOrEmpty(issue.SampleMessages[0])
                    ? TextCut(issue.SampleMessages[0], _l.AiInputTextChars)
                    : null)).ToList(),
            TrendAlerts = rec.TrendAlerts.Take(_l.AiInputAlerts).Select(a => TextCut(a, _l.AiInputTextChars)).ToList(),
            CorrelationAlerts = rec.CorrelationAlerts.Take(_l.AiInputAlerts).Select(a => TextCut(a, _l.AiInputTextChars)).ToList()
        };

        /// <summary>
        /// 對照組：同主機、日期最接近（前後 N 天內，同距離取較新）、AI 正常分析完成、
        /// 標題不是格式異常也不是低風險跳過的紀錄，每案最多一筆；找不到是 null，不換別台主機。
        /// </summary>
        private (ExportControlDto? Control, AiInputFeatures? Features) FindControl(DailyAnalysisRecord rec)
        {
            var candidates = _s._records.Query(new RecordQueryFilter
            {
                Hosts = new[] { new HostKey { HostId = rec.HostId, HostName = rec.Host } },
                From = rec.Date.AddDays(-_l.ComparisonWindowDays),
                To = rec.Date.AddDays(_l.ComparisonWindowDays)
            });

            var best = candidates
                .Where(c => c.RecordId != rec.RecordId
                            && c.AiAnalyzed && !c.AiPending && !c.DetailPruned
                            && !string.IsNullOrEmpty(c.Headline)
                            && c.Headline != LogAnalysisService.AiFormatAnomalyHeadline
                            && c.Headline != LogAnalysisService.LowRiskHeadline
                            && Math.Abs((c.Date.Date - rec.Date.Date).TotalDays) <= _l.ComparisonWindowDays)
                .OrderBy(c => Math.Abs((c.Date.Date - rec.Date.Date).TotalDays))
                .ThenByDescending(c => c.Date)
                .FirstOrDefault();
            if (best == null) return (null, null);

            var features = AiInputFeatures.From(best);
            return (new ExportControlDto
            {
                Date = Day(best.Date),
                DaysApart = (int)Math.Abs((best.Date.Date - rec.Date.Date).TotalDays),
                Features = ToDto(features)
            }, features);
        }

        private List<ExportDiagnosticDto> BuildCaseDiagnostics(DailyAnalysisRecord rec)
        {
            if (rec.HostId <= 0) return new List<ExportDiagnosticDto>();
            var key = $"daily-{rec.Date:yyyyMMdd}-h{rec.HostId}";
            if (!_diagHits.TryGetValue(key, out var hits)) return new List<ExportDiagnosticDto>();

            return hits.OrderByDescending(h => h.LoggedAt).ThenByDescending(h => h.RunId).Select(h => new ExportDiagnosticDto
            {
                LoggedAt = Ts(h.LoggedAt),
                RunId = h.RunId,
                ResponseType = Ident(h.Entry.ResponseType),
                Model = string.IsNullOrEmpty(h.Entry.Model) ? null : Text(h.Entry.Model),
                MaxTokens = h.Entry.MaxTokens,
                Attempts = h.Entry.Attempts,
                EndedBy = Closed(h.Entry.EndedBy, KnownEndedBy),
                AttemptDetails = h.Entry.AttemptDetails.Select(a => new ExportAttemptDto
                {
                    Attempt = a.Attempt,
                    Category = Closed(a.Category, KnownCategories),
                    FinishReason = string.IsNullOrEmpty(a.FinishReason) ? null : Ident(a.FinishReason),
                    PromptChars = a.PromptChars,
                    SystemPromptChars = a.SystemPromptChars,
                    ResponseChars = a.ResponseChars,
                    PromptTokens = a.PromptTokens,
                    CompletionTokens = a.CompletionTokens,
                    ParseErrorPath = string.IsNullOrEmpty(a.ParseErrorPath) ? null : Text(a.ParseErrorPath),
                    FieldLengths = a.FieldLengths?.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => FieldLength(kv.Key, kv.Value)).ToList(),
                    Head = Text(a.Head),
                    Tail = a.Tail == null ? null : Text(a.Tail)
                }).ToList()
            }).ToList();
        }

        private ExportFieldLengthDto FieldLength(string field, object? value)
        {
            var name = Ident(field);
            if (value is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return new ExportFieldLengthDto(name, n, null);
                if (el.ValueKind == JsonValueKind.String) return new ExportFieldLengthDto(name, null, Ident(el.GetString()));
            }

            return new ExportFieldLengthDto(name, null, null);
        }

        // ---------- guide ----------

        private static IReadOnlyList<string> BuildGuide() => new[]
        {
            "【用途】本檔是 LogForesight 排程作業頁匯出的診斷資料，請協助找出這個專案「尚未完善的細節」：反覆出現的錯誤、沒跑完的趟次、效能瓶頸、AI 回覆格式異常的成因。",
            "【去識別化】主機、帳號、IP、網址、路徑、SID 與密鑰已換成 host-001、user-001、ip-001、secret 之類代號；代號只在本檔內有效，檔案內沒有對照表。自由文字（樣本、堆疊、AI 回覆與問題訊息）無法保證完全遮除。看到代號請當成「某一台／某一位」，不要試圖還原。",
            "【解不開的憑證】meta.deidentification.undecryptableSecrets 列出站台存著、但用目前金鑰解不開的憑證名稱，這些值只靠 password=、passhash= 等樣式遮罩。它也是可回報的設定問題：代表憑證需要重新設定。",
            "【時間】所有時間是伺服器本地時間（meta.utcOffset 是與 UTC 的位移）。dailyHealth 的 date 是「執行日」，它分析的資料日期是前一天（D 對應 D-1）；dailyHealth 不含今天，但 signalGroups 與 abnormalRuns 含今天。",
            "【狀態】success＝完成且無警告；warning＝完成但有警告或 AI 失敗；failed＝結束代碼非 0 或有 Error；stopped＝使用者或執行窗口到點停止，不算失敗；stuck＝站台重啟時還沒結束，或逾 6 小時沒有人持有；running＝匯出當下仍在跑的快照，只計入 runStats，不進 abnormalRuns。",
            "【null 與 0】null 代表沒有資料或無法計算（舊紀錄缺欄位、樣本數為 0 的中位數與 P95、AI 呼叫數為 0 的失敗率）；0 才是真的零。請不要把 null 當成 0 解讀。",
            "【執行站台不等於被分析主機】abnormalRuns.host 是跑這一趟的主機（站台），不是被分析的對象。同一個錯誤在幾個「執行站台」出現，不代表它影響了幾台被分析主機；要判斷個案或通案，請看 affectedRuns 與 versions。",
            "【訊號組】signalGroups 收 Warn、Error、Fatal 的診斷行，加上白話輸出（source=output）裡行首為 ⚠ 或 ✗ 的行；output 的等級是依符號推定（⚠＝Warn、✗＝Error），不是原本的等級。同類訊息先去識別化，再把日期、數字、GUID 正規化後分組。count 是次數（同一組可能同時含 Warn 與 Error 等級的行）、errorFatalCount 是其中 Error／Fatal 的次數、affectedRuns 是涉及趟數、stages 是發生在哪個階段（unknown＝落在階段之外或舊紀錄沒有階段資料）、daily 與 versions 只列有發生的日子與版本。samples 最多 3 個不同樣本，stackId 對應 stacks（只保留專案框架，框架層收成略過行數）。",
            "【不計警告】excludedFromWarnCount 為 true 的是慢 SQL 監控的 Warn，不計入該趟的警告數。慢 SQL 依操作彙總在 slowSql（格式解析不到的才留在訊號組）。AI JSON 失敗診斷行不進訊號組，改在 aiFormatAnomalies 使用。",
            "【AI 回覆片段已移除】訊號組樣本裡原本附的 AI 回覆片段已替換成「有片段／空回覆＋字數」。同一則回覆的內容在 aiFormatAnomalies 的案例中，不在這裡重複。",
            "【次數對得上畫面】meta.signalTotals.errorFatal 等於排程作業頁「異常彙總」（預設只看 Error）各組次數的總和；組數可以不同，因為這裡先去識別化再分組，會把只差主機名的訊息併在一起。",
            "【省略】meta.omitted 列出每一處因上限而省略的項目、數量與上限；signalGroups 的省略會同時列出被省略的次數（occurrences）與其中 Error／Fatal 的次數（errorFatalOccurrences）。沒有出現的項目代表沒有被省略。",
            "【runStats】byJob 依作業類型（fetch＝取數分析，ai＝AI 分析排程）與觸發類別（schedule、manual、fetch-followup、console＝工作排程器或舊紀錄）統計；phases 是各階段耗時（只算有結束時間的）與未成功次數，P95 用最近排名法；versions 看問題是不是某一版之後才出現。",
            "【aiFormatAnomalies】「案例」是標題為「AI 回覆格式異常，以下為原始內容」的主機日紀錄，表示 AI 重試用盡後仍未通過 JSON 檢查、系統保留原文降級。attempts 是紀錄摘要前綴寫的嘗試次數（解析不到為 null）。replyShape：object＝以 { 開頭、fence＝Markdown 圍欄、array＝以 [ 開頭、text＝一般文字、blank＝空白；cutOff 為 true 代表保留字數已達 600 字上限，後面可能還有被截掉的內容，不要推測它是什麼。",
            "【失敗類別】診斷的 category：empty＝空回覆；no_json＝完全沒有 {；truncated＝括號未閉合或 finishReason 為 length（被 maxTokens 截斷）；type_mismatch＝JSON 合法但欄位型別不符（parseErrorPath 指到欄位）；parse_other＝其他解析失敗；validation＝解析得出但內容檢查沒過（fieldLengths 是各欄字數，請對照 context.ai 的上限：headlineMaxChars 與 otherFieldMaxChars）。context.ai 的 aiMaxTokens 與 aiJsonRetryCount 是呼叫當時的設定值。",
            "【診斷起始日】detailedDiagnosticsFirstSeen 是期間內最早一筆詳細診斷的日期；在那之前的案例只有摘要，沒有 category、finishReason 與字數。null 代表期間內沒有任何詳細診斷，可能是還沒發生過失敗，也可能是功能上線前的資料，兩者無法區分。",
            "【失敗歷史統計】summary.failureHistory 是從執行紀錄 Warn 訊息比對出的解析失敗與內容檢查失敗次數（依回覆型別）。比對靠固定的訊息字首，找不到就是 0，0 不代表沒發生過。",
            "【對照組】每個案例最多一筆 control：同一台主機、日期最接近（前後 7 天內，同距離取較新）、AI 正常分析完成的紀錄，只放輸入特徵；找不到是 null，不會換別台主機。comparison 是案例與對照各特徵的中位數與樣本數，任一組樣本為 0 時 features 為 null。請用它判斷是輸入內容造成的，還是模型本來就不穩。",
            "【輸入】input 只給最新的 50 個案例（問題清單與趨勢、關聯提示，已遮罩並限制字數）；詳情已被保留期清除的案例 detailPruned 為 true，沒有回覆與輸入。",
            "【數字】小數一律四捨五入到第 4 位；陣列順序固定，同一份資料重複匯出內容相同（exportedAt 除外）。"
        };
    }
}
