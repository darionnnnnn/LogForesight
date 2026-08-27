using System.Text;
using Xunit;

namespace LogForesight.Tests.Persistence;

/// <summary>
/// A1：<see cref="FileReportSink"/>——報告輸出目錄依檔名 yyyy-MM-dd 日期前綴分年月子目錄。
/// </summary>
public class FileReportSinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-export-sink-" + Guid.NewGuid().ToString("N"));

    public FileReportSinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// 1. WriteAsync(ReportKind.DailyRisk, host: "SRV01", "2026-08-27_高風險_安全.txt", ...)
    /// → 回傳路徑為 {export}\SRV01\2026-08\2026-08-27_高風險_安全.txt，且該檔案確實存在。
    /// </summary>
    [Fact]
    public async Task 寫入帶日期前綴報告時建立主機與年月子目錄()
    {
        var sink = new FileReportSink(_dir);
        var content = "2026-08-27 日期風險報告內容";
        var fileName = "2026-08-27_高風險_安全.txt";

        var path = await sink.WriteAsync(ReportKind.DailyRisk, "SRV01", fileName, content);

        var expectedPath = Path.Combine(_dir, "SRV01", "2026-08", fileName);
        Assert.Equal(expectedPath, path);
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// 2. host 為空字串、檔名 2026-08-27_權限異動.txt
    /// → 路徑為 {export}\2026-08\2026-08-27_權限異動.txt。
    /// </summary>
    [Fact]
    public async Task 主機為空字串時直接落於年月子目錄()
    {
        var sink = new FileReportSink(_dir);
        var content = "權限異動報告內容";
        var fileName = "2026-08-27_權限異動.txt";

        var path = await sink.WriteAsync(ReportKind.Permission, string.Empty, fileName, content);

        var expectedPath = Path.Combine(_dir, "2026-08", fileName);
        Assert.Equal(expectedPath, path);
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// 3. 檔名不以 yyyy-MM-dd 開頭（例如 report.txt）
    /// → 路徑為 {export}\SRV01\report.txt（沒有年月層），且不擲例外。
    /// </summary>
    [Theory]
    [InlineData("report.txt")]
    [InlineData("2026-13-01_週檢.txt")]
    [InlineData("not-a-date.txt")]
    public async Task 檔名未帶有效日期前綴時退回舊行為不建年月層(string fileName)
    {
        var sink = new FileReportSink(_dir);
        var content = "一般報告內容";

        var path = await sink.WriteAsync(ReportKind.DailyRisk, "SRV01", fileName, content);

        var expectedPath = Path.Combine(_dir, "SRV01", fileName);
        Assert.Equal(expectedPath, path);
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// 4. 跨月：同一個 host 寫入 2026-07-31_週檢.txt 與 2026-08-01_週檢.txt
    /// → 兩個檔案分別落在 2026-07 與 2026-08 兩個不同子目錄。
    /// </summary>
    [Fact]
    public async Task 跨月報告落在不同年月子目錄()
    {
        var sink = new FileReportSink(_dir);
        var content = "週檢報告內容";

        var pathJuly = await sink.WriteAsync(ReportKind.WeeklyCheckup, "SRV01", "2026-07-31_週檢.txt", content);
        var pathAugust = await sink.WriteAsync(ReportKind.WeeklyCheckup, "SRV01", "2026-08-01_週檢.txt", content);

        var expectedJuly = Path.Combine(_dir, "SRV01", "2026-07", "2026-07-31_週檢.txt");
        var expectedAugust = Path.Combine(_dir, "SRV01", "2026-08", "2026-08-01_週檢.txt");

        Assert.Equal(expectedJuly, pathJuly);
        Assert.Equal(expectedAugust, pathAugust);
        Assert.True(File.Exists(pathJuly));
        Assert.True(File.Exists(pathAugust));
    }

    /// <summary>
    /// 5. host 帶路徑逃逸字元（例如 ..\evil）
    /// → 寫出的完整路徑仍在 export 目錄之內（既有淨化行為未被破壞）。
    /// </summary>
    [Theory]
    [InlineData(@"..\evil", ".._evil")]
    [InlineData("../evil", ".._evil")]
    [InlineData("..", "_")]
    [InlineData(".", "_")]
    public async Task host帶路徑逃逸字元時寫出完整路徑仍在export目錄之內(string maliciousHost, string expectedFolder)
    {
        var sink = new FileReportSink(_dir);
        var content = "安全檢查測試內容";
        var fileName = "2026-08-27_高風險_安全.txt";

        var path = await sink.WriteAsync(ReportKind.DailyRisk, maliciousHost, fileName, content);

        var fileInfo = new FileInfo(path);
        Assert.True(fileInfo.Exists);
        Assert.StartsWith(_dir, fileInfo.FullName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("2026-08", fileInfo.Directory!.Name);
        Assert.Equal(expectedFolder, fileInfo.Directory.Parent!.Name);
    }

    /// <summary>
    /// 6. 寫入內容以 UTF-8 讀回與傳入字串相同（既有行為不變）。
    /// </summary>
    [Fact]
    public async Task 寫入內容以UTF8讀回與傳入字串相同()
    {
        var sink = new FileReportSink(_dir);
        var expectedContent = "測試內容：\n1. 繁體中文風險報告\n2. 特殊字元 !@#$%^&*()_+ 符號\n3. 多行 UTF-8 內容驗證";
        var fileName = "2026-08-27_高風險_安全.txt";

        var path = await sink.WriteAsync(ReportKind.DailyRisk, "SRV01", fileName, expectedContent);

        var actualContent = await File.ReadAllTextAsync(path, Encoding.UTF8);
        Assert.Equal(expectedContent, actualContent);
    }
}
