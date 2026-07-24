using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 批次歷史 store 綁定「本機」識別（ownerHost）後，缺日判定與趨勢基準只看本機自己的紀錄
/// （docs/DB-PLAN.md 一致性機制 #3）。JSONL 與 SQL（EF）後端跑同一組案例——
/// **DB 實作必須通過與 txt 完全相同的測試**才算完成。
///
/// 這組測試釘住的是實際踩到的 bug：同一份資料（history.txt 或 DB）內若含別台主機的紀錄
/// （示範資料、或多台共用同一資料根），全域的缺日判定會把「別台在這天有紀錄」誤當成
/// 「本機已分析過」，於是本機整段被跳過、永遠不產生自己的分析——Web 儀表板就一直是空的。
/// </summary>
public abstract class AnalysisRecordStoreHostScopeContractTests : IDisposable
{
    protected const long OwnerId = 4;
    protected const string OwnerName = "DESKTOP-LOCAL";

    /// <summary>依 ownerHost 建立 store；null＝不分主機（用來讀回全部紀錄驗證別台資料仍在）</summary>
    protected abstract IAnalysisRecordStore CreateStore(HostKey? ownerHost);

    protected IAnalysisRecordStore OwnerStore() =>
        CreateStore(new HostKey { HostId = OwnerId, HostName = OwnerName });

    protected IAnalysisRecordStore UnscopedStore() => CreateStore(null);

    protected static DailyAnalysisRecord Rec(DateTime date, long hostId, string host, string risk = "低") => new()
    {
        Date = date,
        HostId = hostId,
        Host = host,
        RiskLevel = risk,
        Headline = $"{host} {date:MM-dd}"
    };

    public virtual void Dispose() { }

    /// <summary>核心迴歸：別台主機在某天有紀錄，不能讓本機把那天當成「已分析過」。</summary>
    [Fact]
    public void HasRecord_別台主機的當日紀錄不算本機已分析()
    {
        var store = OwnerStore();
        var day = new DateTime(2026, 7, 20);

        store.Append(Rec(day, hostId: 2, host: "SRV-OTHER"));   // 別台主機（示範資料）
        Assert.False(store.HasRecord(day));                      // 本機仍視為「這天還沒分析」

        store.Append(Rec(day, hostId: OwnerId, host: OwnerName)); // 本機自己分析後
        Assert.True(store.HasRecord(day));
    }

    /// <summary>趨勢基準只能是本機自己的歷史，別台主機的紀錄不得混入窗口。</summary>
    [Fact]
    public void ReadRecent_排除別台主機的紀錄()
    {
        var store = OwnerStore();
        var anchor = new DateTime(2026, 7, 21);

        store.Append(Rec(anchor.AddDays(-1), OwnerId, OwnerName));
        store.Append(Rec(anchor.AddDays(-2), hostId: 2, host: "SRV-OTHER"));
        store.Append(Rec(anchor.AddDays(-3), hostId: 3, host: "SRV-OTHER2"));

        var window = store.ReadRecent(anchor, 14);

        Assert.Single(window);
        Assert.Equal(OwnerName, window[0].Host);
    }

    [Fact]
    public void HasAnyRecord_只算本機自己的紀錄()
    {
        var store = OwnerStore();

        store.Append(Rec(DateTime.Today.AddDays(-3), hostId: 2, host: "SRV-OTHER"));
        Assert.False(store.HasAnyRecord());

        store.Append(Rec(DateTime.Today.AddDays(-3), OwnerId, OwnerName));
        Assert.True(store.HasAnyRecord());
    }

    /// <summary>本機以真實 HostId 或名稱任一命中即算自己的——跨越「登記失敗那次 HostId 退為 0」的舊紀錄。</summary>
    [Fact]
    public void 本機歸戶_HostId或名稱任一命中()
    {
        var store = OwnerStore();
        var d1 = new DateTime(2026, 7, 18);
        var d2 = new DateTime(2026, 7, 19);

        store.Append(Rec(d1, hostId: 0, host: OwnerName));     // 舊紀錄：登記失敗那次寫成 HostId 0
        store.Append(Rec(d2, hostId: OwnerId, host: "RENAMED")); // 改名後：靠 HostId 仍歸戶

        Assert.True(store.HasRecord(d1));   // 名稱命中
        Assert.True(store.HasRecord(d2));   // id 命中
    }

    /// <summary>
    /// 名稱歸戶不分大小寫（同 BelongsToOwner 的 OrdinalIgnoreCase）。釘住 SQL 端 collation 差異：
    /// SQLite 預設 BINARY 的 `=` 區分大小寫，若 EF 實作直接在 SQL 比名稱，機器名大小寫不一致的
    /// 舊紀錄（HostId 0）會歸戶不到、同一天被重複分析——JSONL 與 SQL 兩後端必須同語意。
    /// </summary>
    [Fact]
    public void 本機歸戶_名稱比對不分大小寫()
    {
        var store = OwnerStore();
        var day = new DateTime(2026, 7, 18);

        store.Append(Rec(day, hostId: 0, host: OwnerName.ToLowerInvariant())); // 舊紀錄以小寫名稱寫入

        Assert.True(store.HasRecord(day));
        Assert.Single(store.ReadRecent(day, 3));
    }

    /// <summary>週體檢附掛與 LastWeeklyCheckupDate 只作用在本機自己的紀錄上。</summary>
    [Fact]
    public void 週體檢附掛_不掛到別台主機的同日紀錄()
    {
        var store = OwnerStore();
        var day = DateTime.Today;

        store.Append(Rec(day, hostId: 2, host: "SRV-OTHER"));   // 別台同日紀錄排在前
        store.Append(Rec(day, OwnerId, OwnerName));             // 本機同日紀錄

        store.AttachWeeklyCheckup(day, new WeeklyCheckupResult
        {
            CheckupDate = day,
            HasFindings = true,
            Conclusion = "本機的體檢結論"
        });

        Assert.Equal(day.Date, store.LastWeeklyCheckupDate());

        // 別台的同日紀錄不該被掛上本機的體檢結論
        var unscoped = UnscopedStore();
        var other = unscoped.ReadRecent(day, 1).Single(r => r.Host == "SRV-OTHER");
        Assert.Null(other.WeeklyCheckup);
    }

    /// <summary>本機修剪保留期時，不得連帶砍掉別台主機的舊紀錄（多台共用資料根的情況）。</summary>
    [Fact]
    public void Prune_只修剪本機自己的舊紀錄()
    {
        var store = OwnerStore();

        store.Append(Rec(DateTime.Today.AddDays(-91), OwnerId, OwnerName));      // 本機過期
        store.Append(Rec(DateTime.Today.AddDays(-91), hostId: 2, host: "SRV-OTHER")); // 別台過期
        store.Append(Rec(DateTime.Today.AddDays(-1), OwnerId, OwnerName));       // 本機保留期內

        var removed = store.Prune(90);

        Assert.Equal(1, removed);   // 只砍本機那筆過期

        var unscoped = UnscopedStore();
        var all = unscoped.ReadRecent(DateTime.Today, 365);
        Assert.Contains(all, r => r.Host == "SRV-OTHER");   // 別台的過期紀錄仍在
    }
}

/// <summary>JSONL 後端（單機檔案相容模式）</summary>
public class JsonlAnalysisRecordStoreHostScopeTests : AnalysisRecordStoreHostScopeContractTests
{
    private readonly string _tempFile =
        Path.Combine(Path.GetTempPath(), $"lf_hostscope_{Guid.NewGuid():N}.txt");

    protected override IAnalysisRecordStore CreateStore(HostKey? ownerHost) =>
        new JsonlAnalysisRecordStore(_tempFile, ownerHost);

    public override void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// SQLite（EF）後端——SQLite 現為主要測試方式，驗證批次面 ownerHost 主機範圍語意
/// 在 SQL 後端與 JSONL 逐位一致。
/// </summary>
public class EfAnalysisRecordStoreHostScopeTests : AnalysisRecordStoreHostScopeContractTests
{
    private readonly EfSqliteFixture _fx = new();

    protected override IAnalysisRecordStore CreateStore(HostKey? ownerHost) =>
        new LogForesight.Sql.EfAnalysisRecordStore(_fx.NewContext, "test", ownerHost);

    public override void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }
}
