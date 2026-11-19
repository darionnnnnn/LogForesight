using System.Collections;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 79 輪批次 G2：案件授與者的詳情是白名單投影。每個 <see cref="RecordDetailDto"/> 屬性都必須被歸類
/// （允許或排除）；新增屬性沒歸類就紅，避免「新欄位預設外流」。
/// </summary>
public class CaseGrantDetailViewTests
{
    /// <summary>明確不給案件授與者的欄位（整日資訊）</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "RiskLevel", "RiskBasisText", "RiskReview", "LogSource", "LatestNetiqAttemptStatus", "LatestNetiqAttemptAtUtc",
        "PrtgSupplementEligible", "HiddenIssueCount", "Headline", "Brief", "Summary", "TrendAssessment", "Action",
        "AiAnalyzed", "AiPending", "AiDegradedAttempts", "ErrorCount", "WarningCount", "AuditEventCount",
        "TrendAlerts", "CorrelationAlerts", "TrendAlertRefs", "CorrelationAlertRefs", "SuppressedTrendAlerts",
        "SuppressedCorrelationAlerts", "DeepDives", "DataIncomplete", "SecurityLogAvailable", "UncoveredChecks",
        "HasReport", "RiskReportPending", "WeeklyCheckup"
    };

    [Fact]
    public void 詳情的每個屬性都被歸類為允許或排除()
    {
        var unclassified = typeof(RecordDetailDto).GetProperties()
            .Select(p => p.Name)
            .Where(n => !CaseGrantDetailView.AllowedProperties.Contains(n) && !Excluded.Contains(n))
            .ToList();
        Assert.True(unclassified.Count == 0, "請把新欄位歸類到 CaseGrantDetailView.AllowedProperties 或本測試的 Excluded：" + string.Join("、", unclassified));
        Assert.Empty(CaseGrantDetailView.AllowedProperties.Intersect(Excluded));
    }

    [Fact]
    public void 投影後只有白名單欄位帶值_排除欄位都是預設值()
    {
        var full = new RecordDetailDto();
        foreach (var property in typeof(RecordDetailDto).GetProperties().Where(p => p.CanWrite))
            property.SetValue(full, NonDefault(property.PropertyType));

        var projected = CaseGrantDetailView.Project(full);
        var empty = new RecordDetailDto { CaseGrantOnly = true };

        foreach (var property in typeof(RecordDetailDto).GetProperties())
        {
            var actual = property.GetValue(projected);
            if (CaseGrantDetailView.AllowedProperties.Contains(property.Name))
                Assert.True(Equals(actual, property.GetValue(full)) || property.Name == nameof(RecordDetailDto.CaseGrantOnly), property.Name);
            else
                Assert.True(IsDefaultLike(actual, property.GetValue(empty)), $"排除欄位 {property.Name} 不該帶值");
        }
    }

    private static object? NonDefault(Type type)
    {
        if (type == typeof(string)) return "x";
        if (type == typeof(bool) || type == typeof(bool?)) return true;
        if (type == typeof(int) || type == typeof(int?)) return 7;
        if (type == typeof(long) || type == typeof(long?)) return 7L;
        if (type == typeof(DateTime) || type == typeof(DateTime?)) return new DateTime(2026, 1, 1);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(type)!;
            var element = type.GetGenericArguments()[0];
            list.Add(element == typeof(string) ? "x" : Activator.CreateInstance(element));
            return list;
        }
        return type.IsClass && type.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(type) : null;
    }

    private static bool IsDefaultLike(object? actual, object? emptyValue) =>
        actual is null || Equals(actual, emptyValue) || (actual is ICollection c && c.Count == 0);
}
