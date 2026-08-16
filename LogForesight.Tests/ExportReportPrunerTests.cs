using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// P1-4：<see cref="ExportReportPruner"/>——export\ 底下的風險報告檔清理，依檔名日期前綴判斷
/// （<c>RiskReportService.BuildFileName</c> 固定以 yyyy-MM-dd 開頭）。
/// </summary>
public class ExportReportPrunerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-export-prune-" + Guid.NewGuid().ToString("N"));

    public ExportReportPrunerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string WriteReport(string fileName, string subDir = "")
    {
        var dir = string.IsNullOrEmpty(subDir) ? _dir : Path.Combine(_dir, subDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, "報告內容");
        return path;
    }

    [Fact]
    public void 超過保留天數的報告被刪除()
    {
        var old = WriteReport($"{DateTime.Today.AddDays(-91):yyyy-MM-dd}_高風險_硬體.txt");
        var recent = WriteReport($"{DateTime.Today.AddDays(-89):yyyy-MM-dd}_高風險_硬體.txt");

        var removed = ExportReportPruner.Prune(_dir, retentionDays: 90);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    /// <summary>邊界日保留：cutoff 當天還在保留期內，不該被誤刪（與 Prune 家族其他成員同一慣例）</summary>
    [Fact]
    public void 邊界日期保留()
    {
        WriteReport($"{DateTime.Today.AddDays(-90):yyyy-MM-dd}_高風險_硬體.txt");

        var removed = ExportReportPruner.Prune(_dir, retentionDays: 90);

        Assert.Equal(0, removed);
    }

    [Fact]
    public void 無可刪除時回0()
    {
        WriteReport($"{DateTime.Today:yyyy-MM-dd}_高風險_硬體.txt");

        Assert.Equal(0, ExportReportPruner.Prune(_dir, retentionDays: 90));
    }

    /// <summary>NetIQ 多主機情境：報告落在 export\{host}\ 子目錄下，遞迴掃描仍要清得到</summary>
    [Fact]
    public void 子目錄下的報告也會被清理()
    {
        var old = WriteReport($"{DateTime.Today.AddDays(-100):yyyy-MM-dd}_高風險_硬體.txt", subDir: "SRV-A");

        var removed = ExportReportPruner.Prune(_dir, retentionDays: 90);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
    }

    /// <summary>檔名格式不符預期（非本系統產生）一律保守略過，不猜測性刪除</summary>
    [Fact]
    public void 檔名格式不符預期時不刪除()
    {
        var unrelated = WriteReport("readme.txt");
        var tooShort = WriteReport("2026.txt");

        var removed = ExportReportPruner.Prune(_dir, retentionDays: 90);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(tooShort));
    }

    [Fact]
    public void 目錄不存在時回0不拋例外()
    {
        Assert.Equal(0, ExportReportPruner.Prune(Path.Combine(_dir, "not-exist"), retentionDays: 90));
    }

    [Theory]
    [InlineData("2026-07-15_高風險_儲存裝置+安全.txt", true)]
    [InlineData("2026-13-01_x.txt", false)] // 月份不合法
    [InlineData("not-a-date_x.txt", false)]
    [InlineData("short.txt", false)]
    public void TryParseReportDate_辨識檔名日期前綴(string fileName, bool expected)
    {
        Assert.Equal(expected, ExportReportPruner.TryParseReportDate(fileName, out _));
    }
}

public class FileReportSinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-export-sink-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("../outside", ".._outside")]
    [InlineData("..\\outside", ".._outside")]
    [InlineData("..", "_")]
    [InlineData(".", "_")]
    [InlineData("host/name", "host_name")]
    [InlineData("host\\name", "host_name")]
    public async Task 寫入時淨化host避免跳出export目錄(string maliciousHost, string expectedFolder)
    {
        var sink = new LogForesight.Core.Persistence.FileReportSink(_dir);
        var content = "test content";
        var fileName = "report.txt";

        var path = await sink.WriteAsync((LogForesight.Core.Persistence.ReportKind)0, maliciousHost, fileName, content);

        var fileInfo = new FileInfo(path);
        Assert.True(fileInfo.Exists);
        Assert.Equal(expectedFolder, fileInfo.Directory!.Name);
        Assert.StartsWith(_dir, fileInfo.Directory.FullName, StringComparison.OrdinalIgnoreCase);
    }
}
