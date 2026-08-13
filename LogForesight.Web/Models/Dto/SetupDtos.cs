namespace LogForesight.Web.Models.Dto;

/// <summary>首次啟動精靈的單一步驟（回饋十八輪批次H）</summary>
public class SetupStepDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>是否完成——由系統狀態自動判定，不需要使用者手動勾選</summary>
    public bool Done { get; set; }

    /// <summary>使用者是否明確跳過這一步（可逆）</summary>
    public bool Skipped { get; set; }

    /// <summary>這一步能不能跳過——儲存體與管理員帳號不能跳過，其餘可以</summary>
    public bool CanSkip { get; set; }

    public string Detail { get; set; } = string.Empty;

    /// <summary>「前往設定」導向的頁面；null＝這一步沒有可前往的頁面（如儲存體）</summary>
    public string? TargetUrl { get; set; }
}

public class SetupStatusDto
{
    public List<SetupStepDto> Steps { get; set; } = new();

    /// <summary>全部步驟達終態（完成或跳過）——只有此時前端才顯示「隱藏精靈入口」選項</summary>
    public bool AllSettled { get; set; }

    public bool Hidden { get; set; }
}
