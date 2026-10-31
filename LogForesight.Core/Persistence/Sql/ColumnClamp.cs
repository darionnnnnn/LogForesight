using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 寫入前依欄位長度上限截斷的單一出口。SQL Server 端超長字串會讓整批寫入擲截斷例外，
/// SQLite 端卻靜默通過，所以外部來源字串落地前一律走這裡。上限來自 <see cref="LfDbContext"/> 的
/// <c>HasMaxLength</c>，不在呼叫端寫死。截斷不加省略符號（這是儲存用、不是顯示用）。
/// </summary>
internal static class ColumnClamp
{
    private static readonly Lazy<Dictionary<(Type Entity, string Property), int>> Limits = new(BuildLimits);

    /// <summary>超過上限取前 <paramref name="maxLength"/> 字元；null 與未超長原樣回傳。</summary>
    public static string? Clamp(string? value, int maxLength) =>
        value != null && value.Length > maxLength ? value[..maxLength] : value;

    /// <summary>該實體屬性的 <c>MaxLength</c>；未設定上限或查無屬性回 null。</summary>
    public static int? For<TEntity>(string propertyName) =>
        Limits.Value.TryGetValue((typeof(TEntity), propertyName), out var max) ? max : null;

    /// <summary>有上限就截斷，無上限原樣回傳。</summary>
    public static string? ClampFor<TEntity>(string? value, string propertyName) =>
        For<TEntity>(propertyName) is { } max ? Clamp(value, max) : value;

    private static Dictionary<(Type Entity, string Property), int> BuildLimits()
    {
        // 只讀 Model、不開連線。
        var options = new DbContextOptionsBuilder<LfDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var ctx = new LfDbContext(options);
        var map = new Dictionary<(Type, string), int>();
        foreach (var entity in ctx.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(string) && prop.GetMaxLength() is { } max)
                    map.TryAdd((entity.ClrType, prop.Name), max);
            }
        }
        return map;
    }
}
