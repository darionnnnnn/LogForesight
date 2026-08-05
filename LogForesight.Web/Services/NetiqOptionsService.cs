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

    public NetiqOptions Get() => _store.Get();

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
