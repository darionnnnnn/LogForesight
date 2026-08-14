using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using Microsoft.AspNetCore.Hosting;

namespace LogForesight.Web.Services;

/// <summary>NetIQ 連線與節流參數維護（「系統管理 > NetIQ 維護」頁）</summary>
public class NetiqOptionsService
{
    private readonly NetiqOptionsStore _store;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IWebHostEnvironment _env;

    public NetiqOptionsService(NetiqOptionsStore store, ICurrentUser currentUser, IAuditService audit, IWebHostEnvironment env)
    {
        _store = store;
        _currentUser = currentUser;
        _audit = audit;
        _env = env;
    }

    /// <summary>
    /// 讀取時把 <see cref="NetiqOptions.MaxParallelServers"/> 夾在
    /// <see cref="NetiqOptions.MaxParallelServersLimit"/> 內（docs/archive/FEEDBACK-12-PLAN.md §二）：
    /// 上限從 8 收斂到 3 之後，既有環境若曾存過 4~8 的值，維護頁載入時要顯示夾住後的值，
    /// 否則表單顯示 8、瀏覽器 <c>max=3</c> 驗證會擋住整張表單存不了檔。<see cref="_store"/>
    /// 每次 <c>Get()</c> 都回傳新反序列化的物件，就地修改不影響其他呼叫端或底層儲存。
    /// <see cref="NetiqOptions.MaxParallelQueriesPerServer"/>（回饋十三輪 D）同一套理由，一併夾住。
    /// </summary>
    public NetiqOptions Get()
    {
        var options = _store.Get();
        if (options.MaxParallelServers > NetiqOptions.MaxParallelServersLimit)
            options.MaxParallelServers = NetiqOptions.MaxParallelServersLimit;
        if (options.MaxParallelQueriesPerServer > NetiqOptions.MaxParallelQueriesPerServerLimit)
            options.MaxParallelQueriesPerServer = NetiqOptions.MaxParallelQueriesPerServerLimit;
        return options;
    }

    public NetiqOptions Update(UpdateNetiqOptionsRequest request)
    {
        var before = _store.Get();

        // §13：離線示範資料只允許在非 Production 開啟——假資料不得上正式（雙保險，DI factory
        // 在 Production 也不理會此開關）。Production 嘗試開啟直接擋下並說明。
        if (request.UseOfflineDemoData && _env.IsProduction())
            throw DomainException.Validation("正式環境不允許開啟「離線示範資料」（那是固定台數的假資料，不是真實 Sentinel 掃描）。");

        var saved = _store.Update(o =>
        {
            o.QueryDelayMs = request.QueryDelayMs;
            o.PageSize = request.PageSize;
            o.MaxResultsPerJob = request.MaxResultsPerJob;
            o.TimeoutSeconds = request.TimeoutSeconds;
            o.RetryCount = request.RetryCount;
            o.AllowInvalidCertificates = request.AllowInvalidCertificates;
            o.BackfillDays = request.BackfillDays;
            o.MaxParallelServers = request.MaxParallelServers;
            o.MaxParallelQueriesPerServer = request.MaxParallelQueriesPerServer;
            o.ChatLiveFetchEnabled = request.ChatLiveFetchEnabled;
            // Production 環境一律強制關閉（即使 DB 殘留 true）——真連線是正式環境的唯一行為
            o.UseOfflineDemoData = request.UseOfflineDemoData && !_env.IsProduction();
            o.UpdatedByAccount = _currentUser.Account;
        });

        _audit.Record(
            action: AuditActions.NetiqOptionsUpdate,
            summary: "更新 NetIQ 連線與節流參數",
            targetKind: "netiq_options",
            targetId: "netiq_options",
            detail: new { Before = before, After = saved });

        return saved;
    }
}
