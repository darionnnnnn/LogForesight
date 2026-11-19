using LogForesight.Core.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>輸入驗證收斂（第 79 輪批次 C）：每條都有剛好通過與剛好不通過的兩側。</summary>
public class InputValidationTests
{
    [Theory]
    [InlineData("09:30", true)]
    [InlineData("00:00", true)]
    [InlineData("23:59", true)]
    [InlineData(" 07:05 ", true)]
    [InlineData("90", false)]
    [InlineData("1.02:00", false)]
    [InlineData("24:00", false)]
    [InlineData("9:30", false)]
    [InlineData("-1:00", false)]
    [InlineData(null, false)]
    public void 時刻只接受HHmm(string? value, bool expected) =>
        Assert.Equal(expected, InputParsing.TryParseClockTime(value, out _));

    [Theory]
    [InlineData("Monday", true)]
    [InlineData("monday", true)]
    [InlineData("99", false)]
    [InlineData("1", false)]
    [InlineData("Monday,Tuesday", false)]
    [InlineData("Someday", false)]
    [InlineData("", false)]
    public void 列舉只接受已定義的成員名稱(string value, bool expected) =>
        Assert.Equal(expected, InputParsing.TryParseDefinedEnum<DayOfWeek>(value, out _));

    [Fact]
    public void 迄日上界涵蓋當天最後一秒內的紀錄_不涵蓋次日零時()
    {
        var end = InputParsing.EndOfDay(new DateTime(2026, 11, 19, 15, 0, 0));
        Assert.True(new DateTime(2026, 11, 19, 23, 59, 59, 500) <= end);
        Assert.False(new DateTime(2026, 11, 20) <= end);
    }

    [Theory]
    [InlineData("a@example.com", true)]
    [InlineData(" a@example.com ", true)]
    [InlineData("a@example.com,b@example.com", false)]
    [InlineData("a@example.com;b@example.com", false)]
    [InlineData("Bob <a@example.com>", false)]
    [InlineData("not-an-address", false)]
    public void 使用者信箱必須是單一位址(string value, bool expected) =>
        Assert.Equal(expected, SystemSettingsMailHelper.IsSingleBareAddress(value));

    [Fact]
    public void 主旨樣板單趟展開_代入值不再被展開且換行換成空白()
    {
        var subject = LogForesight.Web.Services.Mail.MailNotificationService.ExpandTemplate(
            "[{risk}] {host} {date}", host: "{summary}", date: "2026-11-19", risk: "高", type: "t", summary: "S\r\nX");
        Assert.Equal("[高] {summary} 2026-11-19", subject);
        Assert.Equal("S  X", LogForesight.Web.Services.Mail.MailNotificationService.ExpandTemplate("{summary}", "h", "d", "r", "t", "S\r\nX"));
    }

    [Fact]
    public void 已存的寄信時刻不合法_健康原因列出_合法則沒有()
    {
        var bad = new SystemSettings { MailEnabled = true, MailDailyEnabled = true, MailDailyTime = "90" };
        Assert.Contains("每日摘要時刻", HealthService.InvalidMailSchedules(bad));
        var good = new SystemSettings { MailEnabled = true, MailDailyEnabled = true, MailDailyTime = "08:00" };
        Assert.DoesNotContain("每日摘要時刻", HealthService.InvalidMailSchedules(good));
        var disabled = new SystemSettings { MailEnabled = false, MailDailyEnabled = true, MailDailyTime = "90" };
        Assert.Empty(HealthService.InvalidMailSchedules(disabled));
    }
}

/// <summary>請求 DTO 的預設輸入上限（第 79 輪批次 C14）：沒宣告上限的欄位由全域 filter 套預設值</summary>
public class DefaultInputLimitsTests
{
    private sealed class Sample
    {
        public string? Free { get; set; }
        [System.ComponentModel.DataAnnotations.StringLength(10)] public string? Declared { get; set; }
        public List<long>? Ids { get; set; }
    }

    private static void Run(object argument)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var action = new Microsoft.AspNetCore.Mvc.ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var context = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext(action,
            new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>(), new Dictionary<string, object?> { ["request"] = argument }, controller: null!);
        new LogForesight.Web.Filters.DefaultInputLimitsFilter().OnActionExecuting(context);
    }

    [Fact]
    public void 未宣告上限的欄位只看類型_有宣告的交給自己的屬性()
    {
        var names = LogForesight.Web.Filters.DefaultInputLimitsFilter.FindUnbounded(typeof(Sample)).Select(p => p.Name).ToList();
        Assert.Contains("Free", names);
        Assert.Contains("Ids", names);
        Assert.DoesNotContain("Declared", names);
    }

    [Fact]
    public void 字串剛好在預設上限通過_多一字被拒()
    {
        var limit = LogForesight.Web.Filters.DefaultInputLimitsFilter.DefaultMaxStringLength;
        var request = new LogForesight.Web.Models.Dto.WorkOrderCandidatesRequest { Source = new string('x', limit) };
        Run(request);
        request.Source = new string('x', limit + 1);
        Assert.Throws<LogForesight.Web.Models.DomainException>(() => Run(request));
    }

    [Fact]
    public void 集合剛好在預設上限通過_多一筆被拒()
    {
        var limit = LogForesight.Web.Filters.DefaultInputLimitsFilter.DefaultMaxCollectionCount;
        var request = new LogForesight.Web.Models.Dto.WorkOrderCandidatesRequest { HostIds = Enumerable.Range(0, limit).Select(i => (long)i).ToList() };
        Run(request);
        request.HostIds.Add(-1);
        Assert.Throws<LogForesight.Web.Models.DomainException>(() => Run(request));
    }
}

/// <summary>郵件收件人資格的單一出口（第 79 輪批次 G3）</summary>
public class MailRecipientEligibilityTests
{
    [Theory]
    [InlineData(true, "a@example.com", "a@example.com")]
    [InlineData(true, " a@example.com ", "a@example.com")]
    [InlineData(false, "a@example.com", null)]
    [InlineData(true, "a@example.com,b@example.com", null)]
    [InlineData(true, "", null)]
    public void 只有啟用中且信箱為單一位址的使用者收得到信(bool active, string email, string? expected)
    {
        var user = new LogForesight.Core.Models.WebUser { Account = "u", Active = active, Email = email };
        Assert.Equal(expected, LogForesight.Web.Services.Mail.MailNotificationService.EligibleEmail(user));
    }

    [Fact]
    public void 找不到使用者時不寄() =>
        Assert.Null(LogForesight.Web.Services.Mail.MailNotificationService.EligibleEmail(null));
}
