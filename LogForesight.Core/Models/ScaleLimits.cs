namespace LogForesight.Core.Models;

/// <summary>
/// 站台規模上限的單一來源。數字散在各處時，放寬其中一處就會與其他處互相矛盾
/// （例如監看範圍頁收得下、store 卻拒絕），所以控制器、store、匯出與前端（經 API 回傳）都引用這裡。
/// </summary>
public static class ScaleLimits
{
    /// <summary>
    /// 一次操作可涵蓋的主機數：PRTG 監看範圍的主機清單、主機頁「全選符合篩選」。與 NetIQ 帳本每日讀取、
    /// PRTG 校準的 1 萬上限一致。超過時拒絕，不截斷。清單都以分頁回傳，放寬上限不會讓單一回應變大。
    /// </summary>
    public const int MaxHosts = 10_000;

    /// <summary>
    /// PRTG 監看政策明確選取的 sensor 上限（空＝不限）。PRTG 校準與診斷匯出的記憶體預算依它設計，放寬前要重算那兩處的預算。
    /// </summary>
    public const int MaxPolicySensors = 15_000;

    /// <summary>監看範圍預覽每頁 100 筆時可翻到的最遠位置。</summary>
    public const int MaxPreviewOffset = 30_000;
}
