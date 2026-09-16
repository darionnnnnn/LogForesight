namespace LogForesight.Core.Models;

/// <summary>交辦單的一個成員：一台主機 × 一個問題簽章（呼叫端已解析好主機名與完整簽章）</summary>
public sealed class WorkOrderMember
{
    public string HostName { get; set; } = string.Empty;

    /// <summary>完整四段或五段簽章（<see cref="IssueSignatureKey.For"/>）</summary>
    public string IssueKey { get; set; } = string.Empty;

    public string IssueLabel { get; set; } = string.Empty;

    /// <summary>使用者操作所在的風險日；新案件的 First/LastLinkedDate 以它的日期起算</summary>
    public DateTime TriggerDate { get; set; }
}

/// <summary>操作者與操作時間（寫入案件、逐日歷程、交辦單事件共用同一份）</summary>
public sealed class WorkOrderActor
{
    public long? ActorId { get; set; }

    public string ActorAccount { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }
}

/// <summary>建立交辦單的請求（同處理人同問題已有進行中單時改為追加）</summary>
public sealed class WorkOrderCreateRequest
{
    public string Source { get; set; } = string.Empty;

    public int EventId { get; set; }

    public string IssueLabel { get; set; } = string.Empty;

    public long HandlerId { get; set; }

    /// <summary><see cref="WorkOrderOrigins"/> 值域</summary>
    public string Origin { get; set; } = WorkOrderOrigins.Manual;

    /// <summary><see cref="WorkOrderScopes"/> 值域</summary>
    public string ScopeKind { get; set; } = WorkOrderScopes.Hosts;

    public List<long> ScopeGroupIds { get; set; } = new();

    public bool AutoAttach { get; set; }

    public string? Note { get; set; }

    public DateTime? DueDate { get; set; }

    /// <summary>成員已由他人處理中時是否改派過來；false＝略過並回報衝突</summary>
    public bool ReassignConflicts { get; set; }

    public List<WorkOrderMember> Members { get; set; } = new();

    public WorkOrderActor Actor { get; set; } = new();
}

/// <summary>成員處理時略過的衝突：該主機該問題已由他人處理中</summary>
public sealed class WorkOrderConflict
{
    public string HostName { get; set; } = string.Empty;

    public string IssueKey { get; set; } = string.Empty;

    public long? HandlerId { get; set; }
}

/// <summary>建單／追加的結果</summary>
public sealed class WorkOrderMemberOutcome
{
    public long WorkOrderId { get; set; }

    /// <summary>false＝同處理人同問題已有進行中單，本次併入該單</summary>
    public bool CreatedOrder { get; set; }

    public int NewCases { get; set; }

    /// <summary>同處理人既有進行中案件（原本無單或屬別張單）改連到本單的數量</summary>
    public int LinkedExisting { get; set; }

    public int Reassigned { get; set; }

    public List<WorkOrderConflict> SkippedConflicts { get; set; } = new();

    public CaseDaySubmitResult DaySync { get; set; }
}

/// <summary>改派／拆單的結果</summary>
public sealed class WorkOrderMoveResult
{
    /// <summary>成員最後所在的單（改派無目標單時就是原單）</summary>
    public long TargetWorkOrderId { get; set; }

    public bool MergedIntoExisting { get; set; }

    public int MovedCases { get; set; }

    /// <summary>原處理人（供呼叫端寄移交信）</summary>
    public long PreviousHandlerId { get; set; }
}

/// <summary>取消／代為結案的結果</summary>
public sealed class WorkOrderCloseResult
{
    public int ClosedCases { get; set; }

    public CaseDaySubmitResult DaySync { get; set; }

    public long HandlerId { get; set; }
}

/// <summary>處理人回覆的結果</summary>
public sealed class WorkOrderReplyResult
{
    public int Cases { get; set; }

    /// <summary>回覆後成員全結案、單因而結案</summary>
    public bool WorkOrderClosed { get; set; }

    public CaseDaySubmitResult DaySync { get; set; }
}
