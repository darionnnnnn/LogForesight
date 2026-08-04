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

    /// <summary>'windows'／'linux'——這台 Sentinel 轄下主機的作業系統，掃描匯入精靈以此預填</summary>
    public string Os { get; set; } = "windows";

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

    /// <summary>'windows'／'linux'。省略或不合法值＝windows（新增時的既有行為零改變）</summary>
    public string? Os { get; set; }
}

public class SetSentinelActiveRequest
{
    public bool Active { get; set; }
}

/// <summary>
/// 測試連線（項目 6）：驗證網址／帳密是否正確，不落地任何東西。帳密僅過境不落地——
/// 與 create-and-scan 同一先例（docs/archive/HISTORY.md 定案 6）。
/// </summary>
public class TestSentinelConnectionRequest
{
    /// <summary>0＝尚未存檔（新增中）；>0＝編輯既有 Sentinel。</summary>
    public long SentinelId { get; set; }

    [Required(ErrorMessage = "請輸入連線位址")]
    [StringLength(255)]
    public string BaseUrl { get; set; } = string.Empty;

    [Required(ErrorMessage = "請輸入帳號")]
    [StringLength(100)]
    public string Username { get; set; } = string.Empty;

    /// <summary>留空＝使用這台既有 Sentinel 已存的密碼（僅 <see cref="SentinelId"/>大於 0 時有效，
    /// 與 <see cref="SaveSentinelRequest.Password"/> 的 write-only 語意一致）。</summary>
    public string? Password { get; set; }
}

public class TestSentinelConnectionResultDto
{
    public bool Success { get; set; }

    /// <summary>成功時為「連線成功」，失敗時為可直接顯示的原因（不含密碼）。</summary>
    public string Message { get; set; } = string.Empty;

    public long? ElapsedMs { get; set; }
}
