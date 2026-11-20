using System.Linq.Expressions;
using LogForesight.Core.Models;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// lf_prtg_values「可用列」判定的單一定義（SQL 端）。可用列＝ok，或 coverage ≥ <see cref="PrtgValueUsability.SampledMinCoverage"/> 的 sampled。
/// 判定只寫在 <see cref="SampledUsable"/> 與 <see cref="Usable"/> 兩個 <see cref="Expression"/>，兩個後端（SQLite、SQL Server）共用同一棵樹。
/// 投影內無法直接引用運算式，改呼叫 <see cref="IsUsable"/> 等標記方法，查詢送出前以
/// <see cref="ExpandPrtgUsability{T}"/> 換成運算式本體；漏呼叫展開時 EF 會因無法翻譯而擲例外，不會靜默變成別的行為。
/// 記憶體端的數值可信評估仍只有 <c>PrtgValueAssessment</c> 一個入口，這裡不新增任何閘門。
/// </summary>
public static class PrtgValueRowUsability
{
    /// <summary>取樣列（sampled）且 coverage 達標。</summary>
    public static readonly Expression<Func<PrtgValueRow, bool>> SampledUsable =
        v => v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage;

    /// <summary>取樣列（sampled）但 coverage 不足。</summary>
    public static readonly Expression<Func<PrtgValueRow, bool>> SampledLowCoverage =
        v => v.Quality == PrtgDataQuality.Sampled && v.Coverage < PrtgValueUsability.SampledMinCoverage;

    /// <summary>可用列：ok，或 coverage 達標的 sampled。由 <see cref="SampledUsable"/> 組成，不另寫一份門檻。</summary>
    public static readonly Expression<Func<PrtgValueRow, bool>> Usable = BuildUsable();

    private static readonly Func<PrtgValueRow, bool> UsableCompiled = Usable.Compile();
    private static readonly Func<PrtgValueRow, bool> SampledUsableCompiled = SampledUsable.Compile();
    private static readonly Func<PrtgValueRow, bool> SampledLowCoverageCompiled = SampledLowCoverage.Compile();

    private static Expression<Func<PrtgValueRow, bool>> BuildUsable()
    {
        var p = Expression.Parameter(typeof(PrtgValueRow), "v");
        var isOk = Expression.Equal(
            Expression.Property(p, nameof(PrtgValueRow.Quality)),
            Expression.Constant(PrtgDataQuality.Ok));
        var sampled = new ParameterReplacer(SampledUsable.Parameters[0], p).Visit(SampledUsable.Body);
        return Expression.Lambda<Func<PrtgValueRow, bool>>(Expression.OrElse(isOk, sampled), p);
    }

    /// <summary>標記方法：可用列。只能用在經 <see cref="ExpandPrtgUsability{T}"/> 展開的查詢；記憶體端呼叫等同運算式本體。</summary>
    public static bool IsUsable(PrtgValueRow v) => UsableCompiled(v);

    /// <summary>標記方法：coverage 達標的取樣列。</summary>
    public static bool IsSampledUsable(PrtgValueRow v) => SampledUsableCompiled(v);

    /// <summary>標記方法：coverage 不足的取樣列。</summary>
    public static bool IsSampledLowCoverage(PrtgValueRow v) => SampledLowCoverageCompiled(v);

    /// <summary>把查詢內的 <see cref="IsUsable"/> 等標記呼叫換成運算式本體，使 EF 能翻譯成 SQL。</summary>
    public static IQueryable<T> ExpandPrtgUsability<T>(this IQueryable<T> source) =>
        source.Provider.CreateQuery<T>(new MarkerExpander().Visit(source.Expression));

    private sealed class MarkerExpander : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(PrtgValueRowUsability))
            {
                var lambda = node.Method.Name switch
                {
                    nameof(IsUsable) => Usable,
                    nameof(IsSampledUsable) => SampledUsable,
                    nameof(IsSampledLowCoverage) => SampledLowCoverage,
                    _ => null
                };
                if (lambda != null)
                    return new ParameterReplacer(lambda.Parameters[0], Visit(node.Arguments[0])).Visit(lambda.Body);
            }
            return base.VisitMethodCall(node);
        }
    }

    private sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
