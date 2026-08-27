using System.Text;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 既有 <c>export\</c> 報告檔一次性搬進 <c>lf_reports</c>。
///
/// 遷移由**分析紀錄驅動**而不是掃目錄：升級前所有檔案都落在 export 根目錄，
/// 光看檔案無從得知它屬於哪一台主機，掃目錄只能全部歸給本機、等於丟掉所有 NetIQ 主機的
/// 報告連結。紀錄的 <c>ReportFile</c> 存的是那筆紀錄實際指向的檔案路徑，照著它搬才歸得對。
/// 權限異動報告是唯一例外（路徑從未存下來，且只有本機監控會產生）。
/// </summary>
public sealed class ReportFileMigrationTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "lf-report-mig-" + Guid.NewGuid().ToString("N"));

    private string ExportDir => Path.Combine(_dataRoot, "export");

    public ReportFileMigrationTests() => Directory.CreateDirectory(ExportDir);

    public void Dispose()
    {
        _fx.Dispose();
        if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true);
    }

    private ReportFileMigrator Migrator(string localHost = "LOCALHOST") =>
        new(_fx.NewContext, new ReportMigrationStateStore(_fx.Blob(ReportFileMigrator.StateBlobKey)),
            _dataRoot, localHost);

    private string WriteExportFile(string relativePath, string content)
    {
        var full = Path.Combine(ExportDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
        return full;
    }

    /// <summary>寫一筆帶報告參照的分析紀錄（形狀比照 EfAnalysisRecordStore 的落地格式）</summary>
    private void SeedRecord(long hostId, string hostName, DateTime date, string riskLevel,
        string? reportPath, (DateTime CheckupDate, string ReportPath)? checkup = null)
    {
        var content = new Dictionary<string, object?>
        {
            ["Date"] = date,
            ["Host"] = hostName,
            ["HostId"] = hostId,
            ["RiskLevel"] = riskLevel,
            ["ReportFile"] = reportPath
        };
        if (checkup != null)
        {
            content["WeeklyCheckup"] = new Dictionary<string, object?>
            {
                ["CheckupDate"] = checkup.Value.CheckupDate,
                ["HasFindings"] = true,
                ["Conclusion"] = "測試結論",
                ["ReportFile"] = checkup.Value.ReportPath
            };
        }

        using var ctx = _fx.NewContext();
        ctx.DailyRecords.Add(new DailyRecordRow
        {
            HostId = hostId,
            HostName = hostName,
            RecordDate = date.Date,
            RiskLevel = riskLevel,
            WeeklyCheckupDate = checkup?.CheckupDate.Date,
            ContentJson = JsonSerializer.Serialize(content),
            CreatedAt = date.Date.AddHours(3),
            SecurityLogAvailable = true
        });
        ctx.SaveChanges();
    }

    private ReportContent? Read(long hostId, string hostName, DateTime date, string kind) =>
        new EfReportStore(_fx.NewContext).Read(new HostKey { HostId = hostId, HostName = hostName }, date, kind);

    [Fact]
    public void 紀錄指向的風險報告_搬入並歸到該紀錄的主機()
    {
        var day = new DateTime(2026, 8, 20);
        var path = WriteExportFile("2026-08-20_高風險_儲存裝置.txt", "風險報告內容");
        SeedRecord(42, "SRV-NETIQ-07", day, RiskLevels.High, path);

        Migrator().Run(CancellationToken.None);

        var read = Read(42, "SRV-NETIQ-07", day, ReportKinds.DailyRisk);
        Assert.Equal("風險報告內容", read?.Content);
        Assert.Equal("2026-08-20_高風險_儲存裝置.txt", read?.FileName);
        Assert.Equal(RiskLevels.High, read?.RiskLevel);
    }

    /// <summary>
    /// 檔案時代的碰撞不會被「修好」：幾筆紀錄指向同一個檔就是同一份內容，遷移忠實保留
    /// 使用者今天看到的東西。重點是**兩台主機各自有列**——升級後新產生的報告不再碰撞。
    /// </summary>
    [Fact]
    public void 多台主機指向同一個舊檔_各自建列內容相同()
    {
        var day = new DateTime(2026, 8, 20);
        var path = WriteExportFile("2026-08-20_高風險_安全.txt", "被覆蓋後倖存的那一份");
        SeedRecord(1, "SRV-A", day, RiskLevels.High, path);
        SeedRecord(2, "SRV-B", day, RiskLevels.High, path);

        Migrator().Run(CancellationToken.None);

        Assert.Equal("被覆蓋後倖存的那一份", Read(1, "SRV-A", day, ReportKinds.DailyRisk)?.Content);
        Assert.Equal("被覆蓋後倖存的那一份", Read(2, "SRV-B", day, ReportKinds.DailyRisk)?.Content);
    }

    /// <summary>體檢報告掛在體檢基準日，不是紀錄日期——兩者可以不同天</summary>
    [Fact]
    public void 體檢報告_掛在體檢基準日()
    {
        var recordDay = new DateTime(2026, 8, 20);
        var checkupDay = new DateTime(2026, 8, 18);
        var path = WriteExportFile("2026-08-18_週檢.txt", "體檢內容");
        SeedRecord(5, "SRV01", recordDay, RiskLevels.Low, reportPath: null, checkup: (checkupDay, path));

        Migrator().Run(CancellationToken.None);

        Assert.Equal("體檢內容", Read(5, "SRV01", checkupDay, ReportKinds.WeeklyCheckup)?.Content);
        Assert.Null(Read(5, "SRV01", recordDay, ReportKinds.WeeklyCheckup));
    }

    /// <summary>權限異動報告的路徑從未被存下來，只能掃目錄；它只由本機監控產生，歸給本機</summary>
    [Fact]
    public void 權限異動報告_掃目錄並歸給本機()
    {
        WriteExportFile("2026-08-20_權限異動.txt", "權限異動內容");

        Migrator("LOCALHOST").Run(CancellationToken.None);

        Assert.Equal("權限異動內容",
            Read(0, "LOCALHOST", new DateTime(2026, 8, 20), ReportKinds.Permission)?.Content);
    }

    [Fact]
    public void 連跑兩次_不重複搬入()
    {
        var day = new DateTime(2026, 8, 20);
        SeedRecord(1, "SRV-A", day, RiskLevels.High, WriteExportFile("2026-08-20_高風險.txt", "內容"));
        WriteExportFile("2026-08-20_權限異動.txt", "權限");

        var migrator = Migrator();
        migrator.Run(CancellationToken.None);

        // 第二次要能真的跑（不是靠 Completed 短路），才驗得到逐筆比對的重入保護
        migrator.Evaluate();
        Migrator().Run(CancellationToken.None);

        using var ctx = _fx.NewContext();
        Assert.Equal(2, ctx.Reports.Count());
    }

    /// <summary>
    /// **重入保護不能是「表裡有資料就整批跳過」**：夜間分析也會寫這張表，遷移完成前只要
    /// 先寫進一列，整批舊資料就會被誤判成已搬而永久消失，狀態還顯示一切正常。
    /// </summary>
    [Fact]
    public void 表中已有夜間分析寫入的列_舊資料仍完整搬入()
    {
        var day = new DateTime(2026, 8, 20);

        // 夜間分析先寫了另一台主機的新報告
        new EfReportStore(_fx.NewContext).Write(ReportKinds.DailyRisk,
            new HostKey { HostId = 99, HostName = "SRV-TONIGHT" }, "2026-08-27_高風險.txt", "今晚的報告", null, DateTime.Now);

        SeedRecord(1, "SRV-A", day, RiskLevels.High, WriteExportFile("2026-08-20_高風險.txt", "舊報告"));

        Migrator().Run(CancellationToken.None);

        Assert.Equal("舊報告", Read(1, "SRV-A", day, ReportKinds.DailyRisk)?.Content);
        Assert.Equal("今晚的報告", Read(99, "SRV-TONIGHT", new DateTime(2026, 8, 27), ReportKinds.DailyRisk)?.Content);
    }

    /// <summary>遷移期間夜間分析已寫入同一主機日的新報告時，舊檔不得覆蓋它——新的比較新</summary>
    [Fact]
    public void 同一主機日已有新報告_舊檔不覆蓋()
    {
        var day = new DateTime(2026, 8, 20);
        new EfReportStore(_fx.NewContext).Write(ReportKinds.DailyRisk,
            new HostKey { HostId = 1, HostName = "SRV-A" }, "2026-08-20_高風險.txt", "重新分析後的新報告", null, DateTime.Now);

        SeedRecord(1, "SRV-A", day, RiskLevels.High, WriteExportFile("2026-08-20_高風險.txt", "舊報告"));

        Migrator().Run(CancellationToken.None);

        Assert.Equal("重新分析後的新報告", Read(1, "SRV-A", day, ReportKinds.DailyRisk)?.Content);
    }

    /// <summary>來源檔已被保留期清掉不是失敗，誠實計入「略過」</summary>
    [Fact]
    public void 來源檔不存在_計入略過而不是失敗()
    {
        var day = new DateTime(2026, 8, 20);
        SeedRecord(1, "SRV-A", day, RiskLevels.High, Path.Combine(ExportDir, "2026-08-20_已被清掉.txt"));

        var migrator = Migrator();
        migrator.Run(CancellationToken.None);

        Assert.Equal(ReportMigrationState.Completed, migrator.State.State);
        Assert.Equal(0, migrator.State.MigratedRows);
        Assert.Equal(1, migrator.State.MissingFiles);
    }

    /// <summary>已經是新形式的參照（純數字 report_id）不是待搬對象</summary>
    [Fact]
    public void 參照已是report_id_不當成待搬檔案()
    {
        SeedRecord(1, "SRV-A", new DateTime(2026, 8, 20), RiskLevels.High, "123");

        var migrator = Migrator();
        migrator.Run(CancellationToken.None);

        Assert.Equal(0, migrator.State.MigratedRows);
        Assert.Equal(0, migrator.State.MissingFiles);
    }

    [Fact]
    public void export目錄不存在_直接標記完成()
    {
        Directory.Delete(ExportDir, recursive: true);

        var migrator = Migrator();
        migrator.Evaluate();

        Assert.Equal(ReportMigrationState.Completed, migrator.State.State);
    }

    /// <summary>低風險且無體檢的日子不會有報告，掃描要略過——不然 3000 台環境會白解幾十萬列 JSON</summary>
    [Fact]
    public void 低風險且無體檢的紀錄_不進候選()
    {
        SeedRecord(1, "SRV-A", new DateTime(2026, 8, 20), RiskLevels.Low,
            WriteExportFile("2026-08-20_低風險.txt", "不該被搬"));

        var migrator = Migrator();
        migrator.Run(CancellationToken.None);

        Assert.Equal(0, migrator.State.MigratedRows);
        using var ctx = _fx.NewContext();
        Assert.Equal(0, ctx.Reports.Count());
    }
}
