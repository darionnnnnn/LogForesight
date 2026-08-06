using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 規模壓測的資料量設定（docs/SCALE-ISSUE-FIRST-PLAN.md P0）。
///
/// 為什麼要有 profile 而不是寫死數字：同一組壓測要在三種量級下跑——
/// <see cref="Small"/> 供常態測試驗證產生器本身沒壞（秒級），
/// <see cref="Baseline2000"/>／<see cref="Target6000"/> 供人工執行取基準數字（分鐘級）。
/// 沒有前者，產生器會在往後幾輪改動中悄悄爛掉而沒人發現。
/// </summary>
public sealed record ScaleProfile(
    string Name,
    int HostCount,
    int Days,
    int DistinctIssues,
    int IssuesPerRecord,
    /// <summary>每（主機, 日）平均產生幾列問題層級處理標記——決定 issue_handling blob 的大小</summary>
    double HandlingRowsPerHostDay,
    /// <summary>進行中案件數（跨主機）</summary>
    int OpenCaseCount,
    /// <summary>被合併的墓碑主機數：別名展開（HostIdentityResolver）只有在有墓碑列時才會真的做事</summary>
    int TombstoneCount)
{
    /// <summary>
    /// 常態測試用：只驗證產生器與量測程式本身能跑，不代表任何效能結論。
    ///
    /// <c>DistinctIssues</c> 刻意維持在 200（而非隨主機數等比縮小）：重尾分布要成立，
    /// 簽章池必須遠大於「主機日數 × 每日問題數」能均勻蓋滿的量，否則每個簽章都會出現在
    /// 幾乎每台主機上，尾端就不存在了——那會讓這個 profile 失去代表性，
    /// 產生器的形狀驗證（<c>問題分布為重尾</c>）也就形同虛設。
    /// </summary>
    public static readonly ScaleProfile Small =
        new("small", HostCount: 40, Days: 10, DistinctIssues: 200, IssuesPerRecord: 6,
            HandlingRowsPerHostDay: 0.5, OpenCaseCount: 12, TombstoneCount: 4);

    /// <summary>體檢報告的推算基準（2000 台 × 90 天）</summary>
    public static readonly ScaleProfile Baseline2000 =
        new("2000x90", HostCount: 2000, Days: 90, DistinctIssues: 600, IssuesPerRecord: 15,
            HandlingRowsPerHostDay: 1.0, OpenCaseCount: 3000, TombstoneCount: 60);

    /// <summary>本規劃的設計上限（6000 台 × 90 天）</summary>
    public static readonly ScaleProfile Target6000 =
        new("6000x90", HostCount: 6000, Days: 90, DistinctIssues: 1200, IssuesPerRecord: 15,
            HandlingRowsPerHostDay: 1.0, OpenCaseCount: 9000, TombstoneCount: 180);

    public int RecordCount => HostCount * Days;

    public override string ToString() => $"{Name}（{HostCount} 台 × {Days} 天 ＝ {RecordCount:N0} 筆紀錄）";
}

/// <summary>
/// 規模壓測專用的 <see cref="FactAttribute"/>：**預設略過**，設環境變數
/// <c>LF_SCALE_BENCH=1</c> 才執行。
///
/// 理由：這些案例會產生數十萬到數百萬列資料、耗時數分鐘，放進每次 <c>dotnet test</c>
/// 會讓常態回歸從 20 秒變成十幾分鐘。但它們又必須跟產品程式碼放在同一個方案裡——
/// 壓測若是外掛的一次性腳本，改版後沒人會再跑它。
/// </summary>
public sealed class ScaleFactAttribute : FactAttribute
{
    public const string EnvVar = "LF_SCALE_BENCH";

    public ScaleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvVar) != "1")
            Skip = $"規模壓測預設不執行（設 {EnvVar}=1 啟用）";
    }
}
