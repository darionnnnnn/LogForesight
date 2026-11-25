/**
 * 「不再顯示這個問題：選哪一種」比較表的內容（N1）：問題查詢「依問題」視角的「更多」選單旁與操作說明書共用同一份說法。
 * 純資料、不碰 DOM；每一格都對應程式行為（逐格核對紀錄見執行紀錄與 HideGuideTests）：
 * 靜音＝IssueOwnerAdminService.SetMute；抑制＝RuleAdminService.AddSuppression、SuppressionFilter；
 * 已知雜訊／不處理／誤報＝IssueHandlingStatuses.Dismissals、DispatchContext.DismissedCases、NoiseMark；
 * 統一標記＝IssueHandlingCommandService.BulkCloseIssue、IssueCaseCoordinator.AttachNewDay。
 */

/** 表頭（四欄；第一欄是做法的名稱） */
export const HIDE_GUIDE_COLUMNS = ['做法', '範圍與期限', '進行中的交辦單', '之後會不會再派工'];

/** 每一列：name 與三個說明欄；每個欄位是一到三句，一句一行 */
export const HIDE_GUIDE_ROWS = [
    {
        name: '靜音',
        scope: ['整個機房的這個問題。', '1～365 天，到期自動恢復。'],
        orders: ['可選「暫停，到期自動恢復」，或代為結案為「不處理」。'],
        dispatch: ['靜音期間不派工。', '到期後問題再出現，會重新派工。']
    },
    {
        name: '抑制',
        scope: ['規則或問題簽章。', '規則可設單一主機、主機群組或全站，簽章只能設單一主機。', '可設天數或永久。'],
        orders: ['不動，單與成員維持原狀。'],
        dispatch: ['抑制期間不派工。', '到期後設定還在，但告警與派工恢復。']
    },
    {
        name: '已知雜訊',
        scope: ['這台主機的這個問題。', '沒有期限。'],
        orders: ['有進行中的單：這台主機的成員跟著結案，單上其他主機不受影響。'],
        dispatch: ['不再派工。', '在風險日詳情或統一標記標的，會另外記住這是雜訊，要「調回未處理」並刪除記憶才恢復。']
    },
    {
        name: '不處理',
        scope: ['這台主機的這個問題。', '嚴重度高或重大的問題，處理人回覆滿 30 天會自動恢復派工；其他沒有期限。'],
        orders: ['有進行中的單：這台主機的成員跟著結案，單上其他主機不受影響。'],
        dispatch: ['有交辦單的問題不再派工，可到交辦總覽「待派」頁籤的「不再自動派工」恢復。', '管理者代為結案要勾選才會停派。', '沒有交辦單時只標被標的那一天。']
    },
    {
        name: '誤報',
        scope: ['同「不處理」。'],
        orders: ['同「不處理」。'],
        dispatch: ['同「不處理」。', '規則常誤判時，建議同時到規則維護調整規則。']
    },
    {
        name: '統一標記',
        scope: ['查詢期間內、還沒有人接手的主機。', '一次性；勾「之後新出現的主機日也自動套用」才會持續。'],
        orders: ['不動。已有進行中單的主機整台略過。'],
        dispatch: ['標出的日子不再派工。', '之後新出現的日子，沒勾自動套用會重新派工；標「已知雜訊」則不再派工。']
    }
];

/** 比較表的說明句（放在表上方） */
export const HIDE_GUIDE_LEAD = '想讓一個問題不再出現或不再派工，依範圍與期限選一種：';
