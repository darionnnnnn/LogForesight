namespace LogForesight;

/// <summary>
/// 日風險等級（高/中/低）的單一事實來源（docs/archive/HISTORY.md S2）。
/// 取代原本散落在 Web／批次／Core 各處的字面值與三份幾乎相同的排序權重複本。
///
/// 與問題嚴重度（<see cref="IssueSeverity"/>：Critical/High/Medium/Low）是不同的兩套層級——
/// 這裡的高/中/低是「主機×日期」整天的批次判定結果（規則命中＋趨勢異常＋關聯訊號綜合），
/// 不是任何單一問題嚴重度的別名，兩者不可互相推導。
/// </summary>
public static class RiskLevels
{
    public const string High = "高";
    public const string Medium = "中";
    public const string Low = "低";

    /// <summary>合法值，由重到輕——與畫面篩選鈕、下鑽 URL 的預期順序一致</summary>
    public static readonly string[] All = { High, Medium, Low };

    /// <summary>
    /// 排序權重（高=3 中=2 低=1，未知/其他=0）。供記憶體排序共用；
    /// EF Core 查詢翻譯 SQL 的場合無法呼叫此方法（表達式樹限制），
    /// 該處改用內嵌三元運算式並引用 <see cref="High"/> 等 const，見 EfAnalysisRecordStore。
    /// </summary>
    public static int Rank(string riskLevel) => riskLevel switch
    {
        High => 3,
        Medium => 2,
        Low => 1,
        _ => 0
    };

    /// <summary>待辦／受影響主機等「需要處理」統計的母體判定：高或中風險日</summary>
    public static bool IsActionable(string riskLevel) => riskLevel is High or Medium;

    /// <summary>兩者取風險較高者（不能往下壓，只能往上拉——見 docs/archive/HISTORY.md）</summary>
    public static string MoreSevere(string a, string b) => Rank(a) >= Rank(b) ? a : b;

    /// <summary>從 AI 回傳的風險等級文字（或 JSON 解析失敗時的原文）歸一化為 高/中/低/未知</summary>
    public static string Normalize(string text)
    {
        foreach (var level in All)
        {
            if (text.Contains(level))
            {
                return level;
            }
        }

        return "未知";
    }
}
