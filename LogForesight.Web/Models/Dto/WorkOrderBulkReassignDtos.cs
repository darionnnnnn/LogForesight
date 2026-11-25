using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

// ── 批次改派（/api/work-orders/bulk-reassign）──────────────────────────────

/// <summary>
/// 批次改派的請求（預覽與套用共用）：把勾選的進行中交辦單整張改派給同一位處理人。
/// 一次上限 <c>WorkOrderBulkReassignService.MaxOrders</c> 張（去重後超過整批被拒，不做任何事）。
/// </summary>
public class BulkReassignRequest
{
    [Required(ErrorMessage = "請選擇要改派的交辦單")]
    public List<long> WorkOrderIds { get; set; } = new();

    public long HandlerId { get; set; }

    /// <summary>勾「立即通知」：新處理人的通知不等摘要時刻（只在套用時有意義）。</summary>
    public bool NotifyNow { get; set; }
}

/// <summary>
/// 批次改派每一張單的分類（預覽與結果共用同一份，預覽數字＝結果）：
/// <c>new</c>＝成員連同單一起換處理人（目標處理人沒有同問題進行中單，或這批裡同問題已有一張先成了他的單時是 <c>merge</c>）；
/// <c>merge</c>＝併入目標處理人同問題進行中單（<see cref="BulkReassignRowDto.TargetWorkOrderId"/>），原單以「移交」結案；
/// <c>skip</c>＝不改派（已是該處理人、已結案、找不到）。
/// </summary>
public static class BulkReassignOutcomes
{
    public const string New = "new";
    public const string Merge = "merge";
    public const string Skip = "skip";
}

/// <summary>套用後單張的狀態：<c>ok</c>＝已改派、<c>skipped</c>＝依分類略過、<c>failed</c>＝改派時失敗（其餘張不受影響）</summary>
public static class BulkReassignStatuses
{
    public const string Ok = "ok";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

public class BulkReassignRowDto
{
    public long WorkOrderId { get; set; }
    public string IssueLabel { get; set; } = string.Empty;
    public long CurrentHandlerId { get; set; }
    public string CurrentHandlerName { get; set; } = string.Empty;

    /// <summary><see cref="BulkReassignOutcomes"/></summary>
    public string Outcome { get; set; } = BulkReassignOutcomes.Skip;

    /// <summary>併入時的目標單號；其餘為 null（<c>new</c> 時單號不變，不另填）</summary>
    public long? TargetWorkOrderId { get; set; }

    /// <summary>這張單目前進行中的成員數（會移動的台數；略過的單為 0）</summary>
    public int MemberCount { get; set; }

    /// <summary>這張單的進行中成員主機中，目標處理人看不到、要以交辦授與補上的台數（主機去重）</summary>
    public int GrantHostCount { get; set; }

    /// <summary>略過的原因（<c>skip</c> 才有）</summary>
    public string? Reason { get; set; }
}

public class BulkReassignPreviewDto
{
    public long HandlerId { get; set; }
    public string HandlerName { get; set; } = string.Empty;

    /// <summary>目標處理人暫停接單（改派仍可執行，只是提醒）</summary>
    public bool HandlerPaused { get; set; }

    /// <summary>目標處理人沒有設定 email（不會收到通知）</summary>
    public bool HandlerHasNoEmail { get; set; }

    /// <summary>目標處理人不具處理能力（改派仍可執行，案件授與不含處理權）</summary>
    public bool HandlerCannotHandle { get; set; }

    /// <summary>請求的張數（去重後）</summary>
    public int TotalOrders { get; set; }

    public int NewCount { get; set; }
    public int MergeCount { get; set; }
    public int SkipCount { get; set; }

    /// <summary>會改派的台數（<c>new</c>＋<c>merge</c> 各張進行中成員數加總）</summary>
    public int MovedMembers { get; set; }

    /// <summary>全批要補上授與的主機數（跨單去重）；各單的數字在 <see cref="BulkReassignRowDto.GrantHostCount"/></summary>
    public int GrantHostCount { get; set; }

    public List<BulkReassignRowDto> Rows { get; set; } = new();
}

public class BulkReassignResultRowDto
{
    public long WorkOrderId { get; set; }
    public string IssueLabel { get; set; } = string.Empty;

    /// <summary><see cref="BulkReassignStatuses"/></summary>
    public string Status { get; set; } = BulkReassignStatuses.Ok;

    /// <summary>預覽時的分類（<see cref="BulkReassignOutcomes"/>）；失敗的單仍帶預覽分類</summary>
    public string Outcome { get; set; } = BulkReassignOutcomes.Skip;

    /// <summary>成員最後所在的單（new＝原單、merge＝目標單）；略過或失敗為 null</summary>
    public long? TargetWorkOrderId { get; set; }

    public int MovedCases { get; set; }

    /// <summary>略過或失敗的原因（中文說明，不含例外原文）</summary>
    public string? Reason { get; set; }
}

public class BulkReassignResultDto
{
    public long HandlerId { get; set; }
    public string HandlerName { get; set; } = string.Empty;
    public bool HandlerHasNoEmail { get; set; }
    public bool HandlerPaused { get; set; }
    public bool HandlerCannotHandle { get; set; }

    public int SucceededCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }

    /// <summary>成功改派的單中，新建（原單換人）與併入既有單的張數</summary>
    public int NewCount { get; set; }
    public int MergeCount { get; set; }

    public int MovedMembers { get; set; }
    public List<BulkReassignResultRowDto> Rows { get; set; } = new();
}
