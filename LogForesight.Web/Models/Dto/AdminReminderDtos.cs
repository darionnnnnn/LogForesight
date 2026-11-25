namespace LogForesight.Web.Models.Dto;

/// <summary>
/// 管理者提醒的彙整結果（<c>GET api/work-orders/admin-reminders</c>）：三類的筆數與明細，
/// 交辦總覽的提醒區與管理者摘要信共用同一份（<c>AdminReminderService.Compute</c>）。
/// 明細每類最多 50 筆，筆數是全部。只含回應欄位；入列與信件用的內部彙整欄位在 <c>AdminReminderResult</c>。
/// </summary>
public sealed class AdminReminderDto
{
    /// <summary>設定的提醒間隔天數（0＝不寄提醒信；總覽的提醒區不受影響）</summary>
    public int ReminderDays { get; set; }

    /// <summary>判定「上報逾期」用的天數：設定的間隔天數，0（不提醒）時用出廠預設</summary>
    public int ThresholdDays { get; set; }

    /// <summary>上報後超過間隔天數仍沒人處理的成員數</summary>
    public int EscalatedCount { get; set; }

    /// <summary>
    /// 派不出去的問題數：畫面路徑（儀表板、交辦總覽提醒區）是待派試跑第二段的即時問題數，與待派卡同一口徑；
    /// 沒有即時計數的路徑（夜間摘要信）是最近一趟夜間派工的快照。
    /// </summary>
    public int UnassignableCount { get; set; }

    /// <summary>
    /// <see cref="UnassignableCount"/> 是不是即時試跑的數字（true＝與待派卡同一份；false＝夜間快照，
    /// 例如沒有 Assign 的檢視者，或試跑超過上限時退回快照）。前端依此決定文字要不要寫「最近一次夜間派工」。
    /// </summary>
    public bool UnassignableLive { get; set; }

    /// <summary>上報時沒有任何收件人、且仍有上報成員的單數</summary>
    public int NoRecipientCount { get; set; }

    /// <summary>今天新上報（上報時間是今天、仍待處理）的成員數；與「超過 N 天」分開計數，只在總覽顯示</summary>
    public int NewEscalatedCount { get; set; }

    /// <summary>
    /// 反覆結案又復發的進行中成員數：同主機同問題已有 <c>AdminReminderService.RecurrenceReminderThreshold</c> 件以上「已處理」結案又再出現
    /// （復發次數 N，判定見 <c>IssueRecurrence</c>）。成員之後被結案為不處理類就不再列。靜音中的問題與停用主機不列。
    /// </summary>
    public int RecurrenceCount { get; set; }

    /// <summary>
    /// 是否含「管理員待辦」的新增類別（昨晚取數結果、可派未派、處理人已停用或暫停仍有單、未分組主機）。
    /// 只有具 Assign 的人會是 true；Manager、Dev（只有 ViewAll）看到的是 false，上述四類一律為 0。
    /// </summary>
    public bool TodoIncluded { get; set; }

    /// <summary>
    /// 最近一趟取數（排程或手動）失敗的趟數：最近一趟結果不是成功算 1，成功或站台啟動後還沒跑過算 0。
    /// 判定與健康頁「最近一趟分析執行失敗」同一個來源（<c>SchedulerRunState.LastOutcome</c>）。
    /// </summary>
    public int FetchFailedCount { get; set; }

    /// <summary>失敗那一趟的時間與觸發者；<see cref="FetchFailedCount"/> 為 0 時為 null</summary>
    public AdminReminderFetchDto? FetchFailed { get; set; }

    /// <summary>
    /// 可派未派：待派試跑（近 7 天、呼叫者的可見範圍）中「可以派」的問題數，亦即按下立即派工會派出去的問題數。
    /// 夜間摘要信沒有使用者範圍，不含這一類（恆為 0）；站內提醒區與儀表板才有。
    /// </summary>
    public int DispatchableCount { get; set; }

    /// <summary>可派未派是否有數字可信：false＝試跑出現點超過上限（請到待派頁縮小期間），或這次呼叫沒有提供試跑結果</summary>
    public bool DispatchableKnown { get; set; }

    /// <summary>
    /// 處理人已停用或暫停接單、名下仍有進行中單的人數（單位：人，不是單數）。
    /// 明細列出每人的進行中單數與台數；沒有進行中單的停用／暫停處理人不算。
    /// </summary>
    public int InactiveHandlerCount { get; set; }

    public List<AdminReminderInactiveHandlerDto> InactiveHandlers { get; set; } = new();

    /// <summary>未分組主機台數：啟用中、沒有任何主機群組的主機（一般使用者看不到）；判定同主機頁「未分組」（<c>NetiqHostList.Ungrouped</c>）</summary>
    public int UngroupedHostCount { get; set; }

    /// <summary>各類（含今天新上報與新增的待辦類別）任一不是 0 就顯示提醒區</summary>
    public bool Any => EscalatedCount + UnassignableCount + NoRecipientCount + NewEscalatedCount + RecurrenceCount
        + FetchFailedCount + DispatchableCount + InactiveHandlerCount + UngroupedHostCount > 0;

    /// <summary>派不出去清單的彙整時間；null＝尚未跑過夜間派工</summary>
    public DateTime? UnassignableAt { get; set; }

    public List<AdminReminderEscalatedDto> Escalated { get; set; } = new();

    /// <summary>今天新上報的成員明細（<see cref="AdminReminderEscalatedDto.Days"/> 恆為 0），上限同其他類</summary>
    public List<AdminReminderEscalatedDto> NewEscalated { get; set; } = new();
    public List<AdminReminderUnassignableDto> Unassignable { get; set; } = new();
    public List<AdminReminderNoRecipientDto> NoRecipient { get; set; } = new();

    /// <summary>反覆結案又復發的成員明細（復發次數多的在前），上限同其他類</summary>
    public List<AdminReminderRecurrenceDto> Recurrence { get; set; } = new();

    /// <summary>
    /// 依呼叫者疊上待辦類別的複本（快取內的彙整是共用實例，不能直接改）：<paramref name="includeTodo"/> 為 false 時
    /// 昨晚取數結果、可派未派、處理人已停用或暫停仍有單、未分組主機全部歸零；為 true 時疊上 <paramref name="pending"/>。
    /// 明細清單不複製（唯讀共用）。
    /// </summary>
    internal AdminReminderDto ForCaller(bool includeTodo, PendingDispatchCounts? pending)
    {
        var copy = (AdminReminderDto)MemberwiseClone();
        copy.TodoIncluded = includeTodo;
        copy.DispatchableKnown = false;
        copy.DispatchableCount = 0;
        copy.UnassignableLive = false;
        if (!includeTodo)
        {
            copy.FetchFailedCount = 0;
            copy.FetchFailed = null;
            copy.InactiveHandlerCount = 0;
            copy.InactiveHandlers = new();
            copy.UngroupedHostCount = 0;
            return copy;
        }
        if (pending is { TooLarge: false })
        {
            copy.DispatchableKnown = true;
            copy.DispatchableCount = pending.DispatchableIssues;
            // 有即時計數（畫面路徑）時「派不出去」與同頁待派卡同一口徑：就地授權後待派試跑已經派得出去，不再沿用夜間快照
            if (pending.UnassignableIssues is { } live)
            {
                copy.UnassignableCount = live;
                copy.UnassignableLive = true;
                if (live == 0) copy.Unassignable = new();
            }
        }
        return copy;
    }
}

/// <summary>反覆結案又復發的一個成員（主機＋問題）</summary>
public sealed class AdminReminderRecurrenceDto
{
    public long WorkOrderId { get; set; }
    public string IssueLabel { get; set; } = "";
    public string HostName { get; set; } = "";

    /// <summary>復發次數 N（近 30 天內以已處理結案、之後又再出現的次數）</summary>
    public int RecurrenceCount { get; set; }
}

/// <summary>上報逾期的一個成員（主機＋問題）</summary>
public sealed class AdminReminderEscalatedDto
{
    public long WorkOrderId { get; set; }
    public string IssueLabel { get; set; } = "";
    public string HostName { get; set; } = "";

    /// <summary>上報後已過幾天</summary>
    public int Days { get; set; }
}

/// <summary>派不出去的問題；<see cref="Reason"/> 是 no_pool／all_paused／no_visibility</summary>
public sealed class AdminReminderUnassignableDto
{
    public string IssueLabel { get; set; } = "";
    public string Reason { get; set; } = "";
    public int Hosts { get; set; }
}

/// <summary>上報時沒有任何收件人的單</summary>
public sealed class AdminReminderNoRecipientDto
{
    public long WorkOrderId { get; set; }
    public string IssueLabel { get; set; } = "";
}

/// <summary>最近一趟失敗的取數</summary>
public sealed class AdminReminderFetchDto
{
    public DateTime EndedAt { get; set; }
    public string Trigger { get; set; } = "";
}

/// <summary>已停用或暫停接單、名下仍有進行中單的處理人</summary>
public sealed class AdminReminderInactiveHandlerDto
{
    public long UserId { get; set; }

    /// <summary>顯示名稱(帳號)；已刪除的使用者為「（已刪除）」</summary>
    public string Name { get; set; } = "";

    /// <summary>true＝帳號已停用；false＝帳號啟用但暫停接單</summary>
    public bool Disabled { get; set; }

    public int ActiveWorkOrders { get; set; }
    public int ActiveMembers { get; set; }
}

/// <summary>
/// 待派試跑的可派問題數（<see cref="AdminReminderDto.DispatchableCount"/> 的來源）：呼叫者可見範圍、近 7 天。
/// <paramref name="TooLarge"/> 時 <paramref name="DispatchableIssues"/> 不可信。
/// <paramref name="UnassignableIssues"/>：同一次試跑第二段「派不出去」的問題數（與待派卡同一份）；null＝呼叫端沒有即時計數，
/// 「派不出去」維持夜間快照（摘要信等沒有使用者範圍的路徑）。
/// </summary>
public sealed record PendingDispatchCounts(int DispatchableIssues, bool TooLarge, int? UnassignableIssues = null);

/// <summary>
/// 待派試跑只回計數的結果（儀表板待派卡與「需要你處理」共用）：可派與派不出去的問題數、期間。
/// 與 <c>GetGaps</c> 的 <c>Dispatchable.Issues</c>／<c>UnassignableSection.Issues</c> 同一份判定；
/// <paramref name="TooLarge"/> 時兩個問題數都是 0、不可信。
/// </summary>
public sealed record PendingDispatchSummary(bool TooLarge, DateTime From, DateTime To, int DispatchableIssues, int UnassignableIssues);
