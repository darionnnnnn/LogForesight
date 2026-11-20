using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 案件授與者（只被授與某些問題）看到的風險日詳情：白名單投影，只複製下列欄位（docs/WEB-SPEC.md §7）。
/// 主機身分、被授與的問題與其分類、顯示設定可以給；整日的風險等級與判定依據、事件數、AI 敘事、關聯訊號、
/// 報告與檢查涵蓋率都不給——交辦一個問題不等於交出整台主機這一天的狀況。
/// 新增 <see cref="RecordDetailDto"/> 欄位時預設不會出現在這裡；要給案件授與者看必須明確加進白名單
/// （<c>CaseGrantDetailViewTests</c> 要求每個欄位都被歸類）。
/// </summary>
public static class CaseGrantDetailView
{
    /// <summary>白名單（與 <see cref="Project"/> 的複製清單一致，測試以反射核對）</summary>
    public static readonly IReadOnlySet<string> AllowedProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(RecordDetailDto.HostId), nameof(RecordDetailDto.HostName), nameof(RecordDetailDto.HostDisplayName),
        nameof(RecordDetailDto.HostIpAddress), nameof(RecordDetailDto.HostOs), nameof(RecordDetailDto.HostRoleDesc),
        nameof(RecordDetailDto.Date), nameof(RecordDetailDto.DetailPruned),
        nameof(RecordDetailDto.TopIssues), nameof(RecordDetailDto.Categories),
        nameof(RecordDetailDto.CaseGrantOnly), nameof(RecordDetailDto.CanHandle),
        nameof(RecordDetailDto.UnhandledSeverities), nameof(RecordDetailDto.SeverityDisplayMode)
    };

    /// <param name="full">已依授與的問題鍵篩過 <see cref="RecordDetailDto.TopIssues"/> 的完整詳情</param>
    public static RecordDetailDto Project(RecordDetailDto full) => new()
    {
        HostId = full.HostId,
        HostName = full.HostName,
        HostDisplayName = full.HostDisplayName,
        HostIpAddress = full.HostIpAddress,
        HostOs = full.HostOs,
        HostRoleDesc = full.HostRoleDesc,
        Date = full.Date,
        DetailPruned = full.DetailPruned,
        TopIssues = full.TopIssues,
        Categories = full.Categories,
        CaseGrantOnly = true,
        CanHandle = full.CanHandle,
        UnhandledSeverities = full.UnhandledSeverities,
        SeverityDisplayMode = full.SeverityDisplayMode
    };
}
