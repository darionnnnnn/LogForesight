using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// 「某日用哪一份 PRTG 主機對應」的唯一選法（純函式，docs/PRTG-SPEC.md §4）：
/// 先取回看窗內（<c>day - lookbackDays &lt; MapDate &lt;= day</c>）最晚的一份；窗內沒有時，往後沿用 day 之後、今天（含）之前最早的一份。
/// SQL 端的 <c>EfPrtgStore.FindLatestHostMapDate</c> 以查詢表達同一規則（不便直接共用此函式），兩處規則須一起改。
/// </summary>
public static class PrtgHostMapLookup
{
    /// <summary>過去日沿用既有主機對應時往回找的天數（與歷史回填的「近 31 天」同一個窗）；觸發式取數與數值評估共用。</summary>
    public const int LookbackDays = 31;

    /// <param name="rows">同一個 device 的對應列（順序不限）。</param>
    /// <param name="day">目標日（只看日期部分）。</param>
    /// <param name="lookbackDays">回看窗長（含 day 當日共 N 天）。</param>
    /// <param name="today">往後沿用的上界（含）。</param>
    public static PrtgHostMapRow? Select(IEnumerable<PrtgHostMapRow> rows, DateTime day, int lookbackDays, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (lookbackDays <= 0) return null;
        var d = day.Date;
        var cutoff = d.AddDays(-lookbackDays);
        var t = today.Date;
        PrtgHostMapRow? before = null;
        PrtgHostMapRow? after = null;
        foreach (var row in rows)
        {
            var date = row.MapDate.Date;
            if (date > cutoff && date <= d)
            {
                if (before is null || date > before.MapDate.Date) before = row;
            }
            else if (date > d && date <= t)
            {
                if (after is null || date < after.MapDate.Date) after = row;
            }
        }
        return before ?? after;
    }
}
