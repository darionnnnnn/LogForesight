using System.Text.Json;
using LogForesight;
using LogForesight.Web.Services;

namespace LogForesight.Tests;

// ── 測試替身：IAuditService ──────────────────────────────────────────────────
// 原本有兩份幾乎相同的替身（一份不擷取 detail、一份會序列化 DetailJson）。
// 會序列化的這份是超集，統一用它即可，沒有測試依賴「detail 不被擷取」這件事。

/// <summary>捕捉 detail 物件（例如驗證密碼真的沒進稽核明細）</summary>
internal class RecordingAuditService : IAuditService
{
    public List<AuditEntry> Entries { get; } = new();

    public void Record(string action, string summary, string? targetKind = null, string? targetId = null,
        object? detail = null, AuditResult result = AuditResult.Ok) =>
        Entries.Add(new AuditEntry
        {
            Action = action,
            Summary = summary,
            TargetKind = targetKind,
            TargetId = targetId,
            DetailJson = detail == null ? null : JsonSerializer.Serialize(detail),
            Result = result
        });

    public void RecordAuth(string action, string account, long? userId, string summary, AuditResult result) =>
        Entries.Add(new AuditEntry { Action = action, Account = account, UserId = userId, Summary = summary, Result = result });

    public void RecordSystem(string action, string summary, string? targetKind = null, string? targetId = null) =>
        Entries.Add(new AuditEntry { Action = action, Account = AuditActions.SystemAccount, Summary = summary });
}
