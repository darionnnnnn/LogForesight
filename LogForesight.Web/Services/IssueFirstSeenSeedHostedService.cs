using NLog;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Web.Services;

/// <summary>
/// 在背景執行機房首見日的冪等合併。
///
/// 改成冪等合併之後，沒跑完的後果只是「部分問題的首見日還是退化值」，屬於「數字暫時不準」而不是
/// 「不能讓人寫入」——與 TopIssueBackfillHostedService 同一類，不是 HandlingMigrationHostedService
/// 那一類，因此不需要 gate（如 MigrationGateMiddleware 閘門）。
/// </summary>
public class IssueFirstSeenSeedHostedService : BackgroundService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public const int MaxRetries = 3;
    public const int RetryIntervalMinutes = 30;

    private readonly StorageBackend _backend;

    internal TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(20);
    internal TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(RetryIntervalMinutes);

    public IssueFirstSeenSeedProgress Progress { get; } = new();

    public IssueFirstSeenSeedHostedService(StorageBackend backend)
    {
        _backend = backend;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 讓站台其餘啟動流程先完成，避免與其他背景維護工作同時搶資料庫
        // 延遲用 20 秒（比回填的 15 秒晚）
        try
        {
            if (InitialDelay > TimeSpan.Zero)
            {
                await Task.Delay(InitialDelay, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            Progress.State = IssueFirstSeenSeedStates.Running;
            IssueFirstSeenSeedMergeOutcome? outcome = null;

            await Task.Run(() =>
            {
                try
                {
                    outcome = _backend.MergeIssueFirstSeenSeed();
                }
                catch (Exception ex)
                {
                    Progress.Failures++;
                    Progress.LastError = ex.Message;
                    Progress.State = IssueFirstSeenSeedStates.Failed;
                    Log.Error(ex, "[SQL] lf_issue_first_seen 冪等合併失敗（第 {Failures}/{Max} 次）：{Msg}",
                        Progress.Failures, MaxRetries, ex.Message);
                }
            }, stoppingToken);

            if (outcome != null)
            {
                Progress.State = outcome == IssueFirstSeenSeedMergeOutcome.Skipped
                    ? IssueFirstSeenSeedStates.Skipped
                    : IssueFirstSeenSeedStates.Completed;
                Progress.LastError = null;
                return;
            }

            if (Progress.Failures >= MaxRetries)
            {
                Progress.State = IssueFirstSeenSeedStates.Failed;
                Log.Error("[SQL] lf_issue_first_seen 冪等合併已連續失敗達上限 {Max} 次，停止重試", MaxRetries);
                return;
            }

            try
            {
                if (RetryInterval > TimeSpan.Zero)
                {
                    await Task.Delay(RetryInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task RunAsync(CancellationToken stoppingToken) => await ExecuteAsync(stoppingToken);
}

/// <summary>首見日合併進度與狀態（供 /api/health/detail 申報）</summary>
public sealed class IssueFirstSeenSeedProgress
{
    /// <summary>not_started｜running｜completed｜skipped｜failed</summary>
    public string State { get; set; } = IssueFirstSeenSeedStates.NotStarted;

    /// <summary>已失敗/重試次數（0～3）</summary>
    public int Failures { get; set; }

    /// <summary>最後一次錯誤訊息（成功/未發生為 null）</summary>
    public string? LastError { get; set; }

    /// <summary>連續失敗達上限（3次）即視為失敗／健康降級</summary>
    public bool IsFailed => State == IssueFirstSeenSeedStates.Failed && Failures >= IssueFirstSeenSeedHostedService.MaxRetries;
}

public static class IssueFirstSeenSeedStates
{
    public const string NotStarted = "not_started";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}
