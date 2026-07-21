using System.Text.Json;
using LogForesight.Web.Auth;

namespace LogForesight.Web.Services;

/// <summary>
/// 操作稽核的寫入（docs/WEB-SPEC.md §11）。所有寫入類 Service 完成業務寫入後呼叫。
/// </summary>
public interface IAuditService
{
    /// <summary>以目前登入者身分記錄一筆操作</summary>
    void Record(string action, string summary,
        string? targetKind = null, string? targetId = null,
        object? detail = null, AuditResult result = AuditResult.Ok);

    /// <summary>記錄身分事件（登入/登出/登入失敗）——此時可能尚未建立登入身分，需明確指定帳號</summary>
    void RecordAuth(string action, string account, long? userId, string summary, AuditResult result);

    /// <summary>系統自動行為（帳號記為 (system)）</summary>
    void RecordSystem(string action, string summary, string? targetKind = null, string? targetId = null);
}

public class AuditService : IAuditService
{
    private readonly IAuditLogStore _store;
    private readonly ICurrentUser _currentUser;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    public AuditService(IAuditLogStore store, ICurrentUser currentUser, IHttpContextAccessor httpContextAccessor)
    {
        _store = store;
        _currentUser = currentUser;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(string action, string summary,
        string? targetKind = null, string? targetId = null,
        object? detail = null, AuditResult result = AuditResult.Ok)
    {
        Append(new AuditEntry
        {
            OccurredAt = DateTime.Now,
            UserId = _currentUser.IsAuthenticated && _currentUser.UserId > 0 ? _currentUser.UserId : null,
            Account = _currentUser.IsAuthenticated ? _currentUser.Account : "(anonymous)",
            Action = action,
            TargetKind = targetKind,
            TargetId = targetId,
            Summary = summary,
            DetailJson = detail == null ? null : JsonSerializer.Serialize(detail),
            IpAddress = ClientIp(),
            Result = result
        });
    }

    public void RecordAuth(string action, string account, long? userId, string summary, AuditResult result)
    {
        Append(new AuditEntry
        {
            OccurredAt = DateTime.Now,
            UserId = userId,
            Account = account,
            Action = action,
            TargetKind = "auth",
            Summary = summary,
            IpAddress = ClientIp(),
            Result = result
        });
    }

    public void RecordSystem(string action, string summary, string? targetKind = null, string? targetId = null)
    {
        Append(new AuditEntry
        {
            OccurredAt = DateTime.Now,
            UserId = null,
            Account = AuditActions.SystemAccount,
            Action = action,
            TargetKind = targetKind,
            TargetId = targetId,
            Summary = summary,
            Result = AuditResult.Ok
        });
    }

    /// <summary>
    /// 稽核寫入失敗**不得中斷業務操作**（docs/WEB-SPEC.md §11-4）：
    /// 記錄失敗是可惜，但讓使用者的操作因此失敗是更糟的結果。失敗改記診斷 log，
    /// 那裡至少留得下線索。
    /// </summary>
    private void Append(AuditEntry entry)
    {
        try
        {
            _store.Append(entry);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "稽核寫入失敗（動作 {0}，帳號 {1}）：{2}", entry.Action, entry.Account, entry.Summary);
        }
    }

    private string? ClientIp() =>
        _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}
