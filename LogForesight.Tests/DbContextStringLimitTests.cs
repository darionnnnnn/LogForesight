using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 外部字串落庫統一處理（第 79 輪批次 D1／D2／D4／D9）：<c>LfDbContext.SaveChanges</c> 依模型 <c>HasMaxLength</c>
/// 截斷非鍵欄；鍵欄與索引欄超長該筆不寫並出聲；截斷只有 <c>TextTruncation.SafeCut</c> 一個出口。
/// 欄位清單一律由模型反射取得，新增實體或欄位自動納入。
/// </summary>
public sealed class DbContextStringLimitTests(ITestOutputHelper output)
{
    private const string ContextLogger = "LogForesight.Core.Persistence.Sql.LfDbContext";

    public static IEnumerable<object[]> NonKeyColumns() => Columns(keyOrIndex: false);
    public static IEnumerable<object[]> KeyOrIndexColumns() => Columns(keyOrIndex: true);

    /// <summary>寫入時由 <c>PrepareForSave</c> 依另一欄計算的衍生欄：呼叫端設的值一律被覆寫，沒有「超長」的情境，不納入逐欄的長度案例
    /// （覆寫行為見 <see cref="衍生欄_寫入時一律被重新計算_呼叫端設的值無效"/>）。</summary>
    private static readonly HashSet<(string Entity, string Column)> DerivedColumns = new()
    {
        (nameof(PermissionChangeRow), nameof(PermissionChangeRow.DedupeKeyHash)),
    };

    private static IEnumerable<object[]> Columns(bool keyOrIndex)
    {
        using var fx = new EfSqliteFixture();
        using var ctx = fx.NewContext();
        foreach (var e in ctx.Model.GetEntityTypes())
            foreach (var p in e.GetProperties())
                if (p.ClrType == typeof(string) && p.GetMaxLength() is not null
                    && !DerivedColumns.Contains((e.ClrType.Name, p.Name))
                    && (p.IsKey() || p.IsIndex() || p.IsForeignKey()) == keyOrIndex)
                    yield return [e.ClrType.Name, p.Name];
    }

    // ── 建立可寫入的最小實體（只靠模型反射，不手寫各實體）─────────────────────────────

    /// <summary>有 CHECK 限制可用值的字串欄：建立實體時給合法值，該欄的「上限剛好」案例也沿用它。</summary>
    private static readonly Dictionary<(string Entity, string Column), string> CheckValues = new()
    {
        [(nameof(PrtgTransferSessionRow), nameof(PrtgTransferSessionRow.State))] = "receiving",
    };

    private static IEntityType EntityOf(LfDbContext ctx, string name) =>
        ctx.Model.GetEntityTypes().Single(e => e.ClrType.Name == name);

    /// <summary>
    /// 建一個可寫入的實體：必填字串給短值、鍵與索引欄給依 <paramref name="seed"/> 區分的值（兩筆不撞唯一索引），
    /// 外鍵指向先寫入的主表列。
    /// </summary>
    private static object Build(LfDbContext ctx, IEntityType et, int seed)
    {
        var entity = Activator.CreateInstance(et.ClrType, nonPublic: true)!;
        var fkProps = et.GetForeignKeys().SelectMany(f => f.Properties).ToHashSet();
        foreach (var p in et.GetProperties())
        {
            if (p.PropertyInfo is null || !p.PropertyInfo.CanWrite) continue;
            var seeded = p.IsKey() || p.IsIndex();
            var t = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
            if (fkProps.Contains(p)) continue;
            if (p.ClrType == typeof(string))
            {
                if (CheckValues.TryGetValue((et.ClrType.Name, p.Name), out var allowed)) p.PropertyInfo.SetValue(entity, allowed);
                else if (!p.IsNullable) p.PropertyInfo.SetValue(entity, $"s{seed}");
            }
            else if (p.PropertyInfo.PropertyType == typeof(byte[]))
            {
                p.PropertyInfo.SetValue(entity, new byte[] { 1 });
            }
            else if (!seeded && !p.IsNullable && (t == typeof(int) || t == typeof(long)))
            {
                // 非鍵數值給 1：滿足「大於 0」「已收不超過宣告」這類 CHECK 限制。
                p.PropertyInfo.SetValue(entity, t == typeof(int) ? (object)1 : 1L);
            }
            else if (seeded && !(p.IsPrimaryKey() && p.ValueGenerated == ValueGenerated.OnAdd))
            {
                if (t == typeof(int)) p.PropertyInfo.SetValue(entity, seed);
                else if (t == typeof(long)) p.PropertyInfo.SetValue(entity, (long)seed);
                else if (t == typeof(DateTime)) p.PropertyInfo.SetValue(entity, new DateTime(2026, 1, 1).AddDays(seed));
                else if (t == typeof(DateTimeOffset)) p.PropertyInfo.SetValue(entity, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(seed));
            }
        }

        foreach (var fk in et.GetForeignKeys())
        {
            var principalType = fk.PrincipalEntityType;
            var principal = Build(ctx, principalType, 1000 + seed);
            ctx.Add(principal);
            ctx.SaveChanges();
            for (var i = 0; i < fk.Properties.Count; i++)
            {
                var v = ctx.Entry(principal).Property(fk.PrincipalKey.Properties[i].Name).CurrentValue;
                fk.Properties[i].PropertyInfo!.SetValue(entity, v);
            }
        }
        return entity;
    }

    private static void SetColumn(object entity, string column, string value) =>
        entity.GetType().GetProperty(column)!.SetValue(entity, value);

    private static string? GetColumn(object entity, string column) =>
        (string?)entity.GetType().GetProperty(column)!.GetValue(entity);

    private static int Max(LfDbContext ctx, string entity, string column) =>
        EntityOf(ctx, entity).FindProperty(column)!.GetMaxLength()!.Value;

    /// <summary>以實際查詢取回該實體所有列（不追蹤）。</summary>
    private static List<object> ReadAll(LfDbContext ctx, IEntityType et)
    {
        var set = typeof(DbContext).GetMethods().First(m => m.Name == "Set" && m.GetParameters().Length == 0)
            .MakeGenericMethod(et.ClrType).Invoke(ctx, null)!;
        var q = (IQueryable<object>)typeof(Queryable).GetMethod("Cast")!.MakeGenericMethod(typeof(object))
            .Invoke(null, [typeof(EntityFrameworkQueryableExtensions).GetMethod(nameof(EntityFrameworkQueryableExtensions.AsNoTracking))!
                .MakeGenericMethod(et.ClrType).Invoke(null, [set])])!;
        return q.ToList();
    }

    // ── D1：非鍵欄截斷 ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(NonKeyColumns))]
    public void 非鍵欄新增超長_落庫時截到上限(string entityName, string column)
    {
        using var fx = new EfSqliteFixture();
        using var ctx = fx.NewContext();
        var et = EntityOf(ctx, entityName);
        var max = Max(ctx, entityName, column);
        var row = Build(ctx, et, 1);
        SetColumn(row, column, new string('甲', max + 1));
        ctx.Add(row);
        ctx.SaveChanges();

        using var read = fx.NewContext();
        var back = ReadAll(read, et).Single(r => ReferenceEquals(r.GetType(), et.ClrType));
        Assert.Equal(new string('甲', max), GetColumn(back, column));
    }

    [Theory]
    [MemberData(nameof(NonKeyColumns))]
    public void 非鍵欄修改超長_上限剛好不動_加一才截(string entityName, string column)
    {
        using var fx = new EfSqliteFixture();
        using var ctx = fx.NewContext();
        var et = EntityOf(ctx, entityName);
        var max = Max(ctx, entityName, column);
        var row = Build(ctx, et, 1);
        SetColumn(row, column, new string('乙', max));
        ctx.Add(row);
        ctx.SaveChanges();
        Assert.Equal(new string('乙', max), GetColumn(row, column));   // 上限剛好：原樣

        SetColumn(row, column, new string('丙', max + 1));
        ctx.SaveChanges();

        using var read = fx.NewContext();
        var back = ReadAll(read, et).Single(r => ReferenceEquals(r.GetType(), et.ClrType));
        Assert.Equal(new string('丙', max), GetColumn(back, column));
    }

    [Fact]
    public void 截斷點落在代理對中間_結果不含孤立代理字元()
    {
        using var fx = new EfSqliteFixture();
        using var ctx = fx.NewContext();
        var max = Max(ctx, nameof(PrtgManualMapRow), nameof(PrtgManualMapRow.Note));
        var emoji = "\U0001F600";                                   // 兩個 UTF-16 單元
        var text = new string('a', max - 1) + emoji;                // 高代理落在第 max 位、低代理在 max+1
        var row = (PrtgManualMapRow)Build(ctx, EntityOf(ctx, nameof(PrtgManualMapRow)), 1);
        row.Note = text;
        ctx.Add(row);
        ctx.SaveChanges();

        Assert.Equal(new string('a', max - 1), row.Note);
        Assert.DoesNotContain(row.Note!, c => char.IsSurrogate(c));
    }

    // ── D4：單一截斷方法 ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(5, "aaaa\U0001F600", "aaaa")]          // 切點在代理對中間：退一個字元
    [InlineData(6, "aaaa\U0001F600", "aaaa\U0001F600")] // 剛好容納整個代理對
    [InlineData(4, "aaaa\U0001F600", "aaaa")]          // 切點在代理對之前：不退
    [InlineData(5, "abcde", "abcde")]                   // 上限剛好
    [InlineData(5, "abcdef", "abcde")]                  // 上限加一
    [InlineData(0, "abc", "")]
    public void SafeCut_邊界與代理對(int max, string input, string expected)
    {
        var cut = LogForesight.Core.TextTruncation.SafeCut(input, max);
        Assert.Equal(expected, cut);
        Assert.False(cut.Any(c => char.IsHighSurrogate(c) && !(cut.IndexOf(c) + 1 < cut.Length && char.IsLowSurrogate(cut[cut.IndexOf(c) + 1]))));
    }

    [Fact]
    public void SafeCut_後綴只在被截掉時附加()
    {
        Assert.Equal("abc…", LogForesight.Core.TextTruncation.SafeCut("abcdef", 3, "…"));
        Assert.Equal("abc", LogForesight.Core.TextTruncation.SafeCut("abc", 3, "…"));
        Assert.Equal("ab…", LogForesight.Core.TextTruncation.SafeCut("ab\U0001F600c", 3, "…"));
    }

    [Fact]
    public void ColumnClamp_Clamp走同一個代理對安全出口()
    {
        Assert.Equal("aaaa", ColumnClamp.Clamp("aaaa\U0001F600", 5));
        Assert.Null(ColumnClamp.Clamp(null, 5));
    }

    // ── D2：鍵欄與索引欄超長不寫、出聲 ─────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(KeyOrIndexColumns))]
    public void 鍵欄或索引欄超長_該筆不寫並記警告_同批其他筆照常寫入(string entityName, string column)
    {
        using var fx = new EfSqliteFixture();
        using var ctx = fx.NewContext();
        var et = EntityOf(ctx, entityName);
        var max = Max(ctx, entityName, column);

        var good = Build(ctx, et, 1);
        var bad = Build(ctx, et, 2);
        var goodValue = CheckValues.TryGetValue((entityName, column), out var allowed) ? allowed : new string('g', max);
        if (goodValue.Length < max) goodValue = goodValue.PadRight(max, 'g') is var padded && !CheckValues.ContainsKey((entityName, column)) ? padded : goodValue;
        SetColumn(good, column, goodValue);                   // 上限剛好（有 CHECK 限制的欄用合法值）
        SetColumn(bad, column, new string('b', max + 1));     // 上限加一：不寫
        ctx.AddRange(good, bad);

        var logs = CaptureWarnings(() => ctx.SaveChanges());

        using var read = fx.NewContext();
        var rows = ReadAll(read, et).Where(r => ReferenceEquals(r.GetType(), et.ClrType)).ToList();
        Assert.Single(rows.Where(r => GetColumn(r, column) == goodValue));
        Assert.DoesNotContain(rows, r => GetColumn(r, column)?.StartsWith('b') == true);
        Assert.Contains(logs, l => l.StartsWith("Warn|") && l.Contains($"{entityName}.{column}"));
    }

    [Fact]
    public void 衍生欄_寫入時一律被重新計算_呼叫端設的值無效()
    {
        using var fx = new EfSqliteFixture();
        using var ctx = fx.NewContext();
        var row = (PermissionChangeRow)Build(ctx, EntityOf(ctx, nameof(PermissionChangeRow)), 1);
        row.DedupeKey = "k1";
        row.DedupeKeyHash = new string('x', 500);   // 超長且錯誤：會被覆寫，不會被當成超長索引欄丟棄
        ctx.Add(row);
        ctx.SaveChanges();

        using var read = fx.NewContext();
        Assert.Equal(PermissionChangeDedupeHash.Of("k1"), read.PermissionChanges.AsNoTracking().Single().DedupeKeyHash);
    }

    [Fact]
    public void 鍵欄修改為超長_該筆維持原值不寫並記警告()
    {
        using var fx = new EfSqliteFixture();
        using var ctx = fx.NewContext();
        var et = EntityOf(ctx, nameof(LogLineRow));
        var max = Max(ctx, nameof(LogLineRow), nameof(LogLineRow.LogKey));
        var row = (LogLineRow)Build(ctx, et, 1);
        row.LogKey = "k";
        ctx.Add(row);
        ctx.SaveChanges();

        row.LogKey = new string('x', max + 1);
        var logs = CaptureWarnings(() => ctx.SaveChanges());

        using var read = fx.NewContext();
        Assert.Equal("k", read.LogLines.AsNoTracking().Single().LogKey);
        Assert.Contains(logs, l => l.StartsWith("Warn|") && l.Contains("LogLineRow.LogKey"));
    }

    [Fact]
    public void 明確交易內略過鍵欄超長_警告照常分派給執行紀錄()
    {
        using var fx = new EfSqliteFixture();
        using var ctx = fx.NewContext();
        var row = (LogLineRow)Build(ctx, EntityOf(ctx, nameof(LogLineRow)), 1);
        row.LogKey = new string('x', Max(ctx, nameof(LogLineRow), nameof(LogLineRow.LogKey)) + 1);
        ctx.Add(row);

        // 常駐 target 只做記憶體內的分派與入佇列、不寫庫，交易內記 log 不會與交易互鎖，所以不再暫停擷取。
        // 以登記一個帶識別的分派委派驗證：交易內的警告確實送進該識別的 recorder。
        RunLogCapture.EnsureInstalled();
        var token = "tx-" + Guid.NewGuid().ToString("N");
        var dispatched = new List<string>();
        RunLogCapture.Register(token, e => { lock (dispatched) dispatched.Add(e.FormattedMessage); });
        try
        {
            using var scope = ScopeContext.PushProperty("lf_run_scope", token);
            using var tx = ctx.Database.BeginTransaction();
            ctx.SaveChanges();
            tx.Commit();
        }
        finally
        {
            RunLogCapture.Unregister(token);
        }
        lock (dispatched)
        {
            var message = Assert.Single(dispatched);
            Assert.Contains("LogLineRow.LogKey", message);
        }
    }

    private static List<string> CaptureWarnings(Action act)
    {
        var target = new MemoryTarget("lf-ctx-" + Guid.NewGuid().ToString("N")) { Layout = "${level}|${message}" };
        var config = LogManager.Configuration ??= new LoggingConfiguration();
        config.AddTarget(target);
        config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target, ContextLogger);
        LogManager.ReconfigExistingLoggers();
        try
        {
            act();
            return target.Logs.ToList();
        }
        finally
        {
            config.RemoveTarget(target.Name);
            config.LoggingRules.Where(r => r.Targets.Contains(target)).ToList().ForEach(r => config.LoggingRules.Remove(r));
            LogManager.ReconfigExistingLoggers();
        }
    }

    // ── D9：成本 ───────────────────────────────────────────────────────────────────

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count;
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r) { Count++; return r; }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand c, CommandEventData e, InterceptionResult<int> r) { Count++; return r; }
        public override InterceptionResult<object> ScalarExecuting(DbCommand c, CommandEventData e, InterceptionResult<object> r) { Count++; return r; }
    }

    private (int Commands, long Ms) WriteBulk(int rows, bool overlong)
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var counter = new CommandCounter();
        var opts = new DbContextOptionsBuilder<LfDbContext>().UseSqlite(conn).AddInterceptors(counter).Options;
        using (var init = new LfDbContext(opts)) init.Database.EnsureCreated();
        using var ctx = new LfDbContext(opts);
        // 兩個字串欄各自的上限不同：未超長批次要每欄剛好等於自己的上限（不被截斷），超長批次每欄多 50 字元
        var noteMax = Max(ctx, nameof(PrtgIpExcludeRow), nameof(PrtgIpExcludeRow.Note));
        var createdByMax = Max(ctx, nameof(PrtgIpExcludeRow), nameof(PrtgIpExcludeRow.CreatedBy));
        var note = new string('字', overlong ? noteMax + 50 : noteMax);
        var createdBy = new string('字', overlong ? createdByMax + 50 : createdByMax);
        for (var i = 0; i < rows; i++)
            ctx.Add(new PrtgIpExcludeRow { Ip = $"10.0.{i / 250}.{i % 250}", Note = note, CreatedBy = createdBy });
        counter.Count = 0;
        var sw = Stopwatch.StartNew();
        ctx.SaveChanges();
        sw.Stop();
        Assert.Equal(rows, ctx.PrtgIpExcludes.AsNoTracking().Count());
        return (counter.Count, sw.ElapsedMilliseconds);
    }

    [Fact]
    public void 一次寫入五千筆含超長字串_截斷不增加資料庫指令且耗時與未超長基準的比值在守門內()
    {
        WriteBulk(200, overlong: true);   // 暖機（JIT、模型建立、截斷計畫快取）
        // 各取三次最小值：負載造成的尖峰只會讓單次變慢，取最小值讓基準與比較都貼近無干擾時的成本
        var plainRuns = Enumerable.Range(0, 3).Select(_ => WriteBulk(5000, overlong: false)).ToList();
        var clampedRuns = Enumerable.Range(0, 3).Select(_ => WriteBulk(5000, overlong: true)).ToList();
        var plainMs = plainRuns.Min(r => r.Ms);
        var clampedMs = clampedRuns.Min(r => r.Ms);
        output.WriteLine($"5000 筆：未超長 {plainRuns[0].Commands} 道指令／最小 {plainMs} ms；全部超長 {clampedRuns[0].Commands} 道指令／最小 {clampedMs} ms");

        // 主要斷言：截斷步驟不產生額外資料庫指令（每次執行皆同）
        Assert.All(plainRuns.Concat(clampedRuns), r => Assert.Equal(plainRuns[0].Commands, r.Commands));
        // 輔助斷言：超長批次耗時不超過同批未超長基準的 3 倍加 1 秒固定餘裕（基準很小時比值不穩，餘裕吸收）
        Assert.True(clampedMs <= plainMs * 3 + 1000,
            $"5000 筆超長寫入最小耗時 {clampedMs} ms，超過未超長基準 {plainMs} ms 的 3 倍加 1 秒");
    }
}
