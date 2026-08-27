using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using LogForesight.Web.Models.Dto;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// B2：報告檔保留天數（<see cref="SystemSettingsDto.ReportRetentionDays"/> 與
/// <see cref="UpdateSystemSettingsRequest.ReportRetentionDays"/>）Web 端接線與驗證測試。
/// 驗證模型驗證範圍（90~3650 天）、不與歷史資料 RetentionDays 互相約束、
/// 以及 Settings.cshtml、settings.js、SystemSettingsService.cs 的完整接線。
/// </summary>
public class ReportRetentionWebTests
{
    private static UpdateSystemSettingsRequest CreateValidRequest() => new()
    {
        UnhandledSeverities = new List<string> { "High" },
        SeverityDisplayMode = "DefaultHidden",
        VisibleDayRiskLevels = new List<string> { "高", "中", "低" },
        AiProvider = "Local",
        AiBaseUrl = "",
        AiModel = "local-model",
        AiAzureDeployment = "",
        AiAzureApiVersion = "2024-10-21",
        InitialHistoryDays = 120,
        RetentionDays = 180,
        RunLogRetentionDays = 120,
        AuditRetentionDays = 730,
        RawEventRetentionDays = 120,
        ReportRetentionDays = 1095,
        AiTimeoutSeconds = 600,
        AiRetryCount = 3,
        AiRetryDelaySeconds = 10,
        AiJsonRetryCount = 2,
        AiMaxTokens = 1536,
        AiDeepDiveMaxTokens = 8192,
        AiFrequencyPenalty = 0.8,
        AiPresencePenalty = 0.8,
        CheckupIntervalDays = 7,
        ImportMaxFileSizeKb = 2048,
        ImportMaxRows = 5000
    };

    [Fact]
    public void 更新DTO_帶合法值1095與其餘合法欄位_通過模型驗證()
    {
        var request = CreateValidRequest();
        request.ReportRetentionDays = 1095;

        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(
            request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Fact]
    public void 更新DTO_ReportRetentionDays為89_驗證失敗且錯誤訊息含報告檔保留天數()
    {
        var request = CreateValidRequest();
        request.ReportRetentionDays = 89;

        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(
            request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(isValid);
        var failure = Assert.Single(results, r => r.MemberNames.Contains(nameof(UpdateSystemSettingsRequest.ReportRetentionDays)));
        Assert.Contains("報告檔保留天數", failure.ErrorMessage);
    }

    [Fact]
    public void 更新DTO_ReportRetentionDays為3651_驗證失敗()
    {
        var request = CreateValidRequest();
        request.ReportRetentionDays = 3651;

        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(
            request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(UpdateSystemSettingsRequest.ReportRetentionDays)));
    }

    [Fact]
    public void 更新DTO_報告保留天數大於歷史資料保留天數_驗證通過()
    {
        // 報告保留天數與 RetentionDays 各自獨立，可留存得比 DB 久而不受大小限制約束
        var request = CreateValidRequest();
        request.RetentionDays = 180;
        request.ReportRetentionDays = 1095;

        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(
            request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.True(request.ReportRetentionDays > request.RetentionDays);
        Assert.Empty(results);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(3650)]
    public void 更新DTO_邊界值90與3650_通過模型驗證(int days)
    {
        var request = CreateValidRequest();
        request.ReportRetentionDays = days;

        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(
            request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Fact]
    public void 原始檔字串比對_前端與服務層接線完整()
    {
        var repoRoot = FindRepoRoot();
        var cshtmlPath = Path.Combine(repoRoot, "LogForesight.Web", "Views", "Pages", "Settings.cshtml");
        var jsPath = Path.Combine(repoRoot, "LogForesight.Web", "wwwroot", "js", "pages", "settings.js");
        var servicePath = Path.Combine(repoRoot, "LogForesight.Web", "Services", "SystemSettingsService.cs");

        var cshtml = File.ReadAllText(cshtmlPath);
        var js = File.ReadAllText(jsPath);
        var service = File.ReadAllText(servicePath);

        Assert.Contains("id=\"report-retention-days\"", cshtml);
        Assert.Contains("report-retention-days", js);
        Assert.Contains("reportRetentionDays", js);

        var matches = Regex.Matches(service, @"\bReportRetentionDays\b");
        Assert.True(matches.Count >= 4, $"SystemSettingsService.cs 應包含至少 4 次 ReportRetentionDays，實際為 {matches.Count} 次。");
    }

    /// <summary>從測試組件輸出目錄往上找到含 LogForesight.sln 的專案根目錄（比照 LocalizationLintTests）</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }
}
