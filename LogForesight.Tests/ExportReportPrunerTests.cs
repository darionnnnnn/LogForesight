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

    [Fact]
    public void 過期報告刪除後連帶移除空目錄且保留根目錄()
    {
        var hostDir = Path.Combine(_dir, "SRV01");
        var monthDir = Path.Combine(hostDir, "2026-01");
        var old = WriteReport($"{DateTime.Today.AddDays(-100):yyyy-MM-dd}_高風險_硬體.txt", subDir: Path.Combine("SRV01", "2026-01"));

        var removed = ExportReportPruner.Prune(_dir, retentionDays: 90);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.False(Directory.Exists(monthDir));
        Assert.False(Directory.Exists(hostDir));
        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void 目錄下含未過期報告時該目錄與上層目錄皆保留()
    {
        var hostDir = Path.Combine(_dir, "SRV01");
        var monthDir = Path.Combine(hostDir, "2026-01");
        var recent = WriteReport($"{DateTime.Today.AddDays(-10):yyyy-MM-dd}_高風險_硬體.txt", subDir: Path.Combine("SRV01", "2026-01"));

        var removed = ExportReportPruner.Prune(_dir, retentionDays: 90);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(recent));
        Assert.True(Directory.Exists(monthDir));
        Assert.True(Directory.Exists(hostDir));
        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void 目錄下含非報告檔時不算空目錄必須保留()
    {
        var hostDir = Path.Combine(_dir, "SRV01");
        var monthDir = Path.Combine(hostDir, "2026-01");
        var old = WriteReport($"{DateTime.Today.AddDays(-100):yyyy-MM-dd}_高風險_硬體.txt", subDir: Path.Combine("SRV01", "2026-01"));
        var readme = Path.Combine(monthDir, "readme.md");
        File.WriteAllText(readme, "說明文件");

        var removed = ExportReportPruner.Prune(_dir, retentionDays: 90);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(readme));
        Assert.True(Directory.Exists(monthDir));
        Assert.True(Directory.Exists(hostDir));
        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void 空目錄底下含未過期檔案的子目錄時上層目錄必須保留()
    {
        var hostDir = Path.Combine(_dir, "SRV01");
        var expiredMonthDir = Path.Combine(hostDir, "2025-01");
        var activeMonthDir = Path.Combine(hostDir, "2026-01");

        var old = WriteReport($"{DateTime.Today.AddDays(-100):yyyy-MM-dd}_高風險_硬體.txt", subDir: Path.Combine("SRV01", "2025-01"));
        var recent = WriteReport($"{DateTime.Today.AddDays(-10):yyyy-MM-dd}_高風險_硬體.txt", subDir: Path.Combine("SRV01", "2026-01"));

        var removed = ExportReportPruner.Prune(_dir, retentionDays: 90);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.False(Directory.Exists(expiredMonthDir));
        Assert.True(File.Exists(recent));
        Assert.True(Directory.Exists(activeMonthDir));
        Assert.True(Directory.Exists(hostDir));
        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void 清理多層空目錄後回傳值仍精確等於刪除檔案數不含目錄數()
    {
        var f1 = WriteReport($"{DateTime.Today.AddDays(-100):yyyy-MM-dd}_高風險_硬體.txt", subDir: Path.Combine("SRV01", "2025-01"));
        var f2 = WriteReport($"{DateTime.Today.AddDays(-120):yyyy-MM-dd}_高風險_軟體.txt", subDir: Path.Combine("SRV01", "2025-02"));

        var removed = ExportReportPruner.Prune(_dir, retentionDays: 90);

        Assert.Equal(2, removed);
        Assert.False(File.Exists(f1));
        Assert.False(File.Exists(f2));
        Assert.False(Directory.Exists(Path.Combine(_dir, "SRV01", "2025-01")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "SRV01", "2025-02")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "SRV01")));
        Assert.True(Directory.Exists(_dir));
    }
}
