using System.Linq;
using System.Reflection;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 舊日重新分析的行為契約（第三十一輪終檢補測）：保留判定、排程不重跑、欄位對應、取代語意。
/// 這一層原本缺席，讓「本機路徑先刪後分析」與「掃描失敗仍覆蓋」兩個 bug 通過了逐段驗收。
/// </summary>
public class RerunBehaviorTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── 保留判定（本機與 NetIQ 共用的單一出口）────────────────────────────────

    [Fact]
    public void 來源零事件時保留原結果()
    {
        Assert.True(HostDayPostProcessor.ShouldRetainExistingDay(0, dataIncomplete: false, sourceDegraded: false));
    }

    [Fact]
    public void 取數不完整時保留原結果_不以殘缺資料覆蓋當初完整的結果()
    {
        Assert.True(HostDayPostProcessor.ShouldRetainExistingDay(500, dataIncomplete: true, sourceDegraded: false));
    }

    [Fact]
    public void 來源降級時保留原結果_例如頻道存取被拒()
    {
        Assert.True(HostDayPostProcessor.ShouldRetainExistingDay(500, dataIncomplete: false, sourceDegraded: true));
    }

    [Fact]
    public void 取得到完整資料時才取代()
    {
        Assert.False(HostDayPostProcessor.ShouldRetainExistingDay(500, dataIncomplete: false, sourceDegraded: false));
    }

    // ── 自動排程永不進入重跑 ──────────────────────────────────────────────────

    /// <summary>
    /// 斷言排程輪詢**實際使用的**請求組裝函式，不是測試自己 new 一個 RunRequest 再斷言自己寫的值。
    /// </summary>
    [Fact]
    public void 排程輪詢組出的請求不帶重新分析模式()
    {
        var request = SchedulerHostedService.ComposeScheduledRequest();

        Assert.Equal(RerunMode.None, request.RerunMode);
        Assert.Null(request.RerunDays);
        Assert.Equal(RunScope.Full, request.Scope);
        Assert.Equal("schedule", request.Trigger);
    }

    // ── 手動觸發的欄位對應（與批次A同型的靜默失效風險）──────────────────────

    [Fact]
    public void 手動觸發請求的欄位全部帶進執行請求()
    {
        var dto = new TriggerRunRequest
        {
            Scope = "all",
            BackfillDays = 21,
            OnlyMissingOrFailed = false,
            RerunMode = RerunMode.All,
            RerunDays = 30
        };

        var runRequest = ScheduleController.ToRunRequest(dto, RunScope.Full, new[] { 3L }, "tester");

        Assert.Equal(RunScope.Full, runRequest.Scope);
        Assert.Equal(new[] { 3L }, runRequest.HostIds);
        Assert.Equal(21, runRequest.BackfillOverride);
        Assert.Equal(RerunMode.All, runRequest.RerunMode);
        Assert.Equal(30, runRequest.RerunDays);
        Assert.Equal("manual:tester", runRequest.Trigger);
    }

    /// <summary>守衛：TriggerRunRequest 新增欄位卻沒同步 ToRunRequest 時讓測試紅。</summary>
    [Fact]
    public void 新增觸發請求欄位未同步對應時會被抓到()
    {
        var mappedElsewhere = new[]
        {
            nameof(TriggerRunRequest.Scope),      // 由 ResolveScope 轉成 RunScope
            nameof(TriggerRunRequest.Segment),    // 只用於解析主機清單
            nameof(TriggerRunRequest.HostId)      // 同上
        };
        var properties = typeof(TriggerRunRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && !mappedElsewhere.Contains(p.Name))
            .ToArray();

        Assert.NotEmpty(properties);

        foreach (var property in properties)
        {
            var dto = new TriggerRunRequest();
            var value = NonDefaultValue(property.PropertyType);
            if (value is null) continue;

            property.SetValue(dto, value);
            var runRequest = ScheduleController.ToRunRequest(dto, RunScope.Full, null, "t");

            var target = typeof(RunRequest).GetProperties()
                .FirstOrDefault(p => p.Name == property.Name
                    || (property.Name == nameof(TriggerRunRequest.BackfillDays) && p.Name == nameof(RunRequest.BackfillOverride)));

            Assert.True(target != null && Equals(value, target.GetValue(runRequest)),
                $"TriggerRunRequest.{property.Name} 沒有被 ToRunRequest 帶進 RunRequest——新增欄位時要同步該函式");
        }
    }

    private static object? NonDefaultValue(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(bool)) return true;
        if (target == typeof(int)) return 27;
        if (target == typeof(long)) return 27L;
        if (target == typeof(string)) return "非預設值";
        if (target.IsEnum) return Enum.GetValues(target).Cast<object>().Last();
        return null;
    }

    // ── 趨勢基準不含被分析日自己 ──────────────────────────────────────────────

    /// <summary>
    /// 重跑既有日時，舊紀錄要到寫入前才刪（防永久空洞），所以統計段讀基準的當下**舊的「今天」
    /// 還在 store 裡**——不排除的話會混進趨勢基準（事件在舊列出現過就不算新問題、量值墊高基準）。
    /// 以「趨勢基準建立中（歷史 N/13 天）」的 N 當觀測點：今天的舊列＋昨天各一列時，
    /// 基準必須只算到昨天那 1 筆。拿掉 BuildStatisticalRecordAsync 的排除那行，N 會變 2、此測試轉紅。
    /// </summary>
    [Fact]
    public async Task 重跑日的舊紀錄不混進趨勢基準()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var today = DateTime.Today;

        store.Append(new DailyAnalysisRecord { Date = today, Host = "SRV-A", HostId = 1, RiskLevel = "高", Headline = "今天的舊結果" });
        store.Append(new DailyAnalysisRecord { Date = today.AddDays(-1), Host = "SRV-A", HostId = 1, RiskLevel = "低", Headline = "昨天" });

        var service = new LogAnalysisService(new EventLogService(), new FakeAiService(), store, new FakeSuppressionStore());
        var (record, _) = await service.BuildStatisticalRecordAsync(
            today, new List<EventLogEntryData>(), useAi: false);

        // 歷史窗口預設 14 天、門檻 13：基準只該有「昨天」1 筆，今天的舊列必須被排除
        Assert.Contains(record.UncoveredChecks, c => c.Contains("趨勢基準建立中（歷史 1/"));
    }

    // ── 取代語意：刪除後重寫，同一天只會留下新的那一列 ────────────────────────

    [Fact]
    public void 重跑取代後同一天只留下新紀錄且舊問題不殘留()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = new DateTime(2026, 7, 15);

        store.Append(new DailyAnalysisRecord
        {
            Date = date,
            Host = "SRV-A",
            HostId = 1,
            RiskLevel = "高",
            Headline = "舊規則的結果",
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "old-source", EventId = 1, Category = IssueCategory.Storage, Severity = IssueSeverity.Critical, Count = 1 }
            }
        });

        // 重跑：先刪舊列、再寫新結果（AnalyzeDayAsync 的 replaceExisting 走同一組動作）
        store.DeleteDays(new[] { date });
        store.Append(new DailyAnalysisRecord
        {
            Date = date,
            Host = "SRV-A",
            HostId = 1,
            RiskLevel = "低",
            Headline = "新規則的結果",
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "new-source", EventId = 2, Category = IssueCategory.Security, Severity = IssueSeverity.Medium, Count = 1 }
            }
        });

        var records = store.ReadRecent(date, 1);
        Assert.Single(records);
        Assert.Equal("新規則的結果", records[0].Headline);

        using var ctx = _fx.NewContext();
        Assert.Equal(1, ctx.DailyRecords.Count());
        Assert.DoesNotContain(ctx.TopIssues.ToList(), t => t.SourceName == "old-source");
    }
}
