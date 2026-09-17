using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// <see cref="IssueExclusion"/> 的 SQL 端條件（唯一一份）：與 <see cref="IssueExclusion.IsMuted"/> 同一條規則，
/// 條件來源只取 <see cref="IssueExclusion.Spans"/>／<see cref="IssueExclusion.CurrentlyMuted"/>，不另算。
///
/// 形狀（靜音列）：
/// <code>
/// event_id IN (全部靜音鍵的 EventId)
/// AND ( UPPER(source)#event_id IN (目前靜音中的鍵)
///       OR CASE WHEN UPPER(source)#event_id IN (區間 1 的鍵) AND record_date BETWEEN F1 AND T1 THEN 1
///               WHEN ...                                                                     THEN 1
///               ELSE 0 END = 1 )
/// </code>
///
/// **為什麼不是「逐鍵一個 (source = K AND event_id = E) 條件、鍵與鍵 OR 成平衡樹」**：EF Core 產生 SQL 時會把
/// 同一種運算子的巢狀 AND／OR 攤平成一長串（實測 <c>NOT (...)</c> 經 De Morgan 後變成 N 個 AND 串接），
/// C# 端的平衡樹到了資料庫端又變回線性鏈；SQLite 對左深運算式樹有深度上限（500 個鍵時擲
/// 「Expression tree is too large (maximum depth 1000)」）。改成：
///   - 鍵以「大寫來源#EventId」組合字串一次 IN 比對，鍵數再多都只是一個 IN 清單；
///   - 已到期區間依 (From, To) 分組，每組一個 CASE WHEN 分支——CASE 的 WHEN 清單是平的，不累積深度；
///   - 最外層先以 event_id IN 粗篩，讓只看靜音列（<see cref="OnlyMuted"/>）的查詢可以走 event_id 索引。
/// 常數以 <see cref="Expression.Constant(object)"/> 內嵌成 SQL 字面值，不佔參數（避開 SQL Server 2100 參數上限）。
/// </summary>
public static class IssueExclusionSql
{
    private static readonly MethodInfo ToUpperMethod = typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes)!;
    private static readonly MethodInfo IntToStringMethod = typeof(int).GetMethod(nameof(int.ToString), Type.EmptyTypes)!;
    private static readonly MethodInfo ConcatMethod = typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string) })!;
    private static readonly MethodInfo StringContains = EnumerableContains(typeof(string));
    private static readonly MethodInfo IntContains = EnumerableContains(typeof(int));

    /// <summary>排除靜音列；<see cref="IssueExclusion.IsEmpty"/> 時原樣回傳。</summary>
    public static IQueryable<TopIssueRow> Apply(IQueryable<TopIssueRow> q, IssueExclusion exclusion)
    {
        if (exclusion.IsEmpty) return q;
        return q.Where(Build(exclusion, negate: true));
    }

    /// <summary>只留靜音列；<see cref="IssueExclusion.IsEmpty"/> 時回傳恆假的查詢。</summary>
    public static IQueryable<TopIssueRow> OnlyMuted(IQueryable<TopIssueRow> q, IssueExclusion exclusion)
    {
        if (exclusion.IsEmpty) return q.Where(_ => false);
        return q.Where(Build(exclusion, negate: false));
    }

    /// <summary>
    /// 只留「目前靜音中」問題的列：組合鍵 IN，不看日期。
    /// <see cref="IssueExclusion.CurrentlyMuted"/> 為空時回傳恆假的查詢。
    /// </summary>
    public static IQueryable<TopIssueRow> OnlyCurrentlyMuted(IQueryable<TopIssueRow> q, IssueExclusion exclusion)
    {
        if (exclusion.CurrentlyMuted.Count == 0) return q.Where(_ => false);
        var x = Expression.Parameter(typeof(TopIssueRow), "x");
        var eventId = Expression.Property(x, nameof(TopIssueRow.EventId));
        var eventIds = exclusion.CurrentlyMuted.Select(k => k.EventId).Distinct().OrderBy(id => id).ToArray();
        var keys = CurrentKeys(exclusion);
        var body = Expression.AndAlso(
            Expression.Call(IntContains, Expression.Constant(eventIds), eventId),
            Expression.Call(StringContains, Expression.Constant(keys), Composite(x, eventId)));
        return q.Where(Expression.Lambda<Func<TopIssueRow, bool>>(body, x));
    }

    /// <summary>記憶體端的組合鍵：唯一一份在 <see cref="IssueExclusion.CompositeKey"/>。</summary>
    private static string CompositeKey(string sourceKey, int eventId) => IssueExclusion.CompositeKey(sourceKey, eventId);

    private static string[] CurrentKeys(IssueExclusion exclusion) =>
        exclusion.CurrentlyMutedCompositeKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    private static Expression Composite(ParameterExpression x, Expression eventId) =>
        Expression.Add(
            Expression.Add(
                Expression.Call(Expression.Property(x, nameof(TopIssueRow.SourceName)), ToUpperMethod),
                Expression.Constant(IssueExclusion.CompositeKeySeparator), ConcatMethod),
            Expression.Call(eventId, IntToStringMethod), ConcatMethod);

    private static Expression<Func<TopIssueRow, bool>> Build(IssueExclusion exclusion, bool negate)
    {
        var x = Expression.Parameter(typeof(TopIssueRow), "x");
        var eventId = Expression.Property(x, nameof(TopIssueRow.EventId));
        var recordDate = Expression.Property(x, nameof(TopIssueRow.RecordDate));
        var composite = Composite(x, eventId);

        var eventIds = exclusion.Spans.Select(s => s.EventId).Distinct().OrderBy(id => id).ToArray();
        Expression muted = Expression.Call(IntContains, Expression.Constant(eventIds), eventId);

        Expression? keyOrSpan = null;

        // 目前靜音中：整個問題排除，不看日期
        var currentKeys = CurrentKeys(exclusion);
        if (currentKeys.Length > 0)
            keyOrSpan = Expression.Call(StringContains, Expression.Constant(currentKeys), composite);

        // 其他鍵：紀錄日落在該鍵任一區間內（含首尾）。同一區間的鍵併成一個 WHEN 分支
        var spanGroups = exclusion.Spans
            .Where(s => !exclusion.CurrentlyMuted.Contains((s.SourceKey, s.EventId)))
            .GroupBy(s => (From: s.From.Date, To: s.To.Date))
            .OrderBy(g => g.Key.From)
            .ThenBy(g => g.Key.To)
            .ToList();
        if (spanGroups.Count > 0)
        {
            Expression caseExpr = Expression.Constant(0);
            for (var i = spanGroups.Count - 1; i >= 0; i--)
            {
                var g = spanGroups[i];
                var keys = g.Select(s => CompositeKey(s.SourceKey, s.EventId)).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
                var test = Expression.AndAlso(
                    Expression.Call(StringContains, Expression.Constant(keys), composite),
                    Expression.AndAlso(
                        Expression.GreaterThanOrEqual(recordDate, Expression.Constant(g.Key.From, typeof(DateTime))),
                        Expression.LessThanOrEqual(recordDate, Expression.Constant(g.Key.To, typeof(DateTime)))));
                // 巢狀條件運算式由 EF 攤平成單一 CASE 的多個 WHEN
                caseExpr = Expression.Condition(test, Expression.Constant(1), caseExpr);
            }
            var inSpan = Expression.Equal(caseExpr, Expression.Constant(1));
            keyOrSpan = keyOrSpan == null ? inSpan : Expression.OrElse(keyOrSpan, inSpan);
        }

        muted = Expression.AndAlso(muted, keyOrSpan!);
        Expression body = negate ? Expression.Not(muted) : muted;
        return Expression.Lambda<Func<TopIssueRow, bool>>(body, x);
    }

    private static MethodInfo EnumerableContains(Type elementType) =>
        typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(elementType);
}
