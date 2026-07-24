using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

/// <summary>Sentinel 管理頁的畫面呈現。密碼絕不回傳，只有「是否已設定」</summary>
public class SentinelDto
{
    public long SentinelId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    /// <summary>密碼是否已設定（write-only：畫面只顯示「已設定」，不回傳明碼）</summary>
    public bool HasPassword { get; set; }

    public bool CanDiscover { get; set; }
    public bool Active { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>目前掛在這台 Sentinel 下的使用中 NetIQ 主機數——刪除前的確認視窗直接用這個數字</summary>
    public int HostCount { get; set; }
}

public class SaveSentinelRequest
{
    /// <summary>0＝新增</summary>
    public long SentinelId { get; set; }

    [Required(ErrorMessage = "請輸入 Sentinel 名稱")]
    [StringLength(50)]
    public string Name { get; set; } = string.Empty;

    [StringLength(255)]
    public string? BaseUrl { get; set; }

    [StringLength(100)]
    public string? Username { get; set; }

    /// <summary>留空＝不變更（編輯既有 Sentinel 時）；新增時留空＝此 Sentinel 無法主動掃描</summary>
    public string? Password { get; set; }
}

public class SetSentinelActiveRequest
{
    public bool Active { get; set; }
}
