namespace LogForesight.Web.Models.Dto;

/// <summary>
/// 單一問題目前生效的全部「不顯示」設定（問題負責與靜音頁的總覽，第 81 輪批次 N）。
/// 一列一個問題（來源＋事件編號）；四種設定各自為 null＝這個問題沒有該設定，四者全為 null 的問題不列出。
/// </summary>
public sealed class IssueHideSummaryDto
{
    public string SourceName { get; set; } = string.Empty;
    public int EventId { get; set; }

    /// <summary>顯示用的問題名稱（全站問題名稱的共用出口）</summary>
    public string DisplayLabel { get; set; } = string.Empty;

    /// <summary>今天所在的靜音區間；沒有進行中的靜音為 null</summary>
    public IssueHideMuteDto? Mute { get; set; }

    /// <summary>機房結論；沒有為 null</summary>
    public IssueHideConclusionDto? Conclusion { get; set; }

    /// <summary>目前生效的抑制（規則型依問題對得到的規則、簽章型依簽章鍵）；沒有為 null</summary>
    public IssueHideSuppressionDto? Suppression { get; set; }

    /// <summary>被「不再自動派工」擋住的主機；沒有為 null</summary>
    public IssueHideDismissedDto? Dismissed { get; set; }
}

public sealed class IssueHideMuteDto
{
    /// <summary>靜音迄日（含當天）</summary>
    public DateTime To { get; set; }
    public string? Reason { get; set; }
    public string? ByAccount { get; set; }
}

public sealed class IssueHideConclusionDto
{
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;

    /// <summary>之後新出現的主機日是否自動套用</summary>
    public bool AutoApply { get; set; }
}

/// <summary>
/// 抑制彙總。什麼算一條：一筆尚未到期的抑制設定（規則型、簽章型各算一筆）；到期未清除的不算。
/// 範圍分三欄計數（全站／主機群組／單一主機），三欄加總＝<see cref="Total"/>。
/// </summary>
public sealed class IssueHideSuppressionDto
{
    public int Total { get; set; }
    public int SiteCount { get; set; }
    public int GroupCount { get; set; }
    public int HostCount { get; set; }

    /// <summary>問題對得到的規則 Id（前往規則維護用）；只有簽章型抑制、找不到規則時為 null</summary>
    public string? RuleId { get; set; }
}

/// <summary>
/// 不再打擾彙總：<see cref="Hosts"/>＝被擋住的（主機, 問題）件數（與待派頁「不再自動派工」清單每組的台數同一單位）；
/// <see cref="ReleasingHosts"/>＝其中會在 N 天後自動恢復派工的件數，<see cref="NextReleaseInDays"/>＝最早恢復的剩餘天數（沒有會恢復的為 null）。
/// </summary>
public sealed class IssueHideDismissedDto
{
    public int Hosts { get; set; }
    public int ReleasingHosts { get; set; }
    public int? NextReleaseInDays { get; set; }
}

/// <summary>單一問題的「不再自動派工」彙總（<see cref="LogForesight.Web.Services.DismissedIssueService.GetIssueSummaries"/> 的回傳列）</summary>
public sealed record DismissedIssueSummary(string Source, int EventId, int Hosts, int ReleasingHosts, int? NextReleaseInDays);
