using System;
using System.Linq;
using System.Reflection;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SchedulerHostedService.ComposeEffectiveRequest（第三十一輪批次A）：手動觸發 API 與排程輪詢
/// 共用的「請求重建」。原本這段內嵌在 TriggerRunAsync 裡逐欄重建，漏抄了 OnlyMissingOrFailed——
/// ScheduleController 有設、稽核訊息也印「僅補跑失敗或未執行」，執行端卻永遠收到 false，
/// 該勾選從未生效。抽成純函式後這裡逐欄鎖住，並以反射守衛擋住未來新增欄位再漏抄。
/// </summary>
public class ComposeEffectiveRequestTests
{
    private static ScheduleOptions Options(bool debugDump, bool localEnabled) =>
        new() { DebugDump = debugDump, LocalAnalysisEnabled = localEnabled };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyMissingOrFailed照實傳遞給執行端(bool onlyMissingOrFailed)
    {
        var request = new RunRequest { Scope = RunScope.Full, OnlyMissingOrFailed = onlyMissingOrFailed };

        var effective = SchedulerHostedService.ComposeEffectiveRequest(request, Options(false, true));

        Assert.Equal(onlyMissingOrFailed, effective.OnlyMissingOrFailed);
    }

    [Fact]
    public void 呼叫端欄位全部帶到()
    {
        var request = new RunRequest
        {
            Scope = RunScope.NetiqHosts,
            HostIds = new[] { 7L, 9L },
            BackfillOverride = 45,
            OnlyMissingOrFailed = true,
            Trigger = "manual:tester"
        };

        var effective = SchedulerHostedService.ComposeEffectiveRequest(request, Options(false, true));

        Assert.Equal(RunScope.NetiqHosts, effective.Scope);
        Assert.Equal(new[] { 7L, 9L }, effective.HostIds);
        Assert.Equal(45, effective.BackfillOverride);
        Assert.True(effective.OnlyMissingOrFailed);
        Assert.Equal("manual:tester", effective.Trigger);
    }

    /// <summary>DebugDump／IncludeLocal 一律以排程設定為準，覆寫呼叫端傳入的值。</summary>
    [Fact]
    public void 傾印與本機分析以排程設定為準而非呼叫端()
    {
        var request = new RunRequest { Scope = RunScope.Full, DebugDump = true, IncludeLocal = true };

        var effective = SchedulerHostedService.ComposeEffectiveRequest(request, Options(false, false));

        Assert.False(effective.DebugDump);
        Assert.False(effective.IncludeLocal);
    }

    /// <summary>
    /// 守衛：RunRequest 新增了欄位卻沒同步 ComposeEffectiveRequest 時讓測試紅。
    /// 逐欄設非預設值後檢查——排程設定覆寫的兩個欄位除外（它們刻意不跟呼叫端）。
    /// </summary>
    [Fact]
    public void 新增RunRequest欄位未同步時會被抓到()
    {
        var overriddenByScheduleOptions = new[] { nameof(RunRequest.DebugDump), nameof(RunRequest.IncludeLocal) };
        var properties = typeof(RunRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && !overriddenByScheduleOptions.Contains(p.Name))
            .ToArray();

        Assert.NotEmpty(properties);

        foreach (var property in properties)
        {
            var request = new RunRequest();
            var value = NonDefaultValue(property.PropertyType);
            if (value is null) continue;

            property.SetValue(request, value);
            var effective = SchedulerHostedService.ComposeEffectiveRequest(request, Options(true, true));

            Assert.True(
                Equals(value, property.GetValue(effective)),
                $"RunRequest.{property.Name} 沒有被 ComposeEffectiveRequest 帶過去——新增欄位時要同步該函式");
        }
    }

    private static object? NonDefaultValue(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(bool)) return true;
        if (target == typeof(int)) return 33;
        if (target == typeof(long)) return 33L;
        if (target == typeof(string)) return "非預設值";
        if (target.IsEnum) return Enum.GetValues(target).Cast<object>().Last();
        if (target == typeof(long[])) return new[] { 5L };
        return null;
    }
}
