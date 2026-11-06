using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 健康頁的金鑰檔位置提示：只帶出文字，不影響整體狀態。
/// 靜態屬性是行程層級狀態，放進不並行的 CryptoKeyState 集合並在 try/finally 還原。
/// </summary>
[Collection("CryptoKeyState")]
public class HealthServiceKeyLocationWarningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-healthloc-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public HealthServiceKeyLocationWarningTests()
    {
        CryptoHelper.ResetForTests();
        CryptoKeyBootstrapper.KeyMismatch = false;
        CryptoKeyBootstrapper.KeyLocationWarning = null;
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "h.db")}" }, _dir);
    }

    public void Dispose()
    {
        CryptoKeyBootstrapper.KeyLocationWarning = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
        GC.SuppressFinalize(this);
    }

    private ScheduleFreshnessService NewFreshnessService() => new(
        new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
        new ScheduleOptionsStore(_backend.Blob("schedule_options")));

    private HealthService NewService() => new(_backend, new SchedulerRunState(), _backend.TopIssueBackfiller(), NewMailService(), NewFreshnessService());

    private MailNotificationService NewMailService() => new(
        new FakeSystemSettingsStore(), new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
        new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
        new MailNotifyStateStore(_backend.Blob("mail_notify_state")), NewFreshnessService(), new WorkOrderOutboxStore(_backend.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()), new FakeIssueCaseStore(), new FakeRuleStore());

    [Fact]
    public void 診斷檢查_帶出金鑰檔位置提示_但不改變整體狀態()
    {
        var baseline = NewService().GetDetail();
        Assert.Null(baseline.CryptoKeyLocationWarning);

        try
        {
            CryptoKeyBootstrapper.KeyLocationWarning = "測試提示文字";
            var warned = NewService().GetDetail();

            Assert.Equal("測試提示文字", warned.CryptoKeyLocationWarning);
            Assert.Equal(baseline.Status, warned.Status);
        }
        finally
        {
            CryptoKeyBootstrapper.KeyLocationWarning = null;
        }
    }
}
