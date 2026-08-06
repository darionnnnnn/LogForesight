using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="SentinelStore"/> 的欄位往返（對**真實** store，不是替身）。
///
/// <para><b>為什麼需要這一組</b>：`Upsert` 更新既有資料時是**逐欄複製**，
/// 新增模型欄位卻忘了在那裡加一行，結果就是「新增時存得進去、編輯時被靜默還原」——
/// 建置過、單元測試也可能全綠，因為測試替身各自有一份同樣漏抄的複製邏輯。
/// `UseEsmDirectory` 上線前正是這樣漏掉，靠瀏覽器手動驗證才抓到。</para>
///
/// <para>因此這裡比對的是**整個物件**而不是個別欄位：新增欄位時若沒改 Upsert，
/// 這組測試會直接失敗，不必倚賴有沒有人記得補測試。</para>
/// </summary>
public class SentinelStoreRoundTripTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private SentinelStore Store() => new(_fixture.Blob("sentinels"));

    private static Sentinel FullyPopulated() => new()
    {
        Name = "SENTINEL-A",
        BaseUrl = "https://sentinel.local:8443",
        Username = "svc-lfquery",
        PasswordEnc = "enc:v1:xxxx",
        Active = false,
        Os = WebHost.OsLinux,
        UseEsmDirectory = true
    };

    [Fact]
    public void 新增_全部欄位都存得進去()
    {
        var saved = Store().Upsert(FullyPopulated());

        var reread = Store().Get(saved.SentinelId);
        Assert.NotNull(reread);
        AssertAllFieldsMatch(FullyPopulated(), reread!);
    }

    /// <summary>
    /// **這一條是本檔案的重點**：更新走的是逐欄複製的分支，漏抄一欄就會靜默還原成舊值。
    /// 從「全部欄位都不一樣」的既有資料更新過去，任一欄沒被複製都會當場失敗。
    /// </summary>
    [Fact]
    public void 更新_全部欄位都跟著改_沒有欄位被靜默還原()
    {
        var original = Store().Upsert(new Sentinel
        {
            Name = "OLD", BaseUrl = "https://old:8443", Username = "old-user",
            PasswordEnc = "enc:v1:old", Active = true, Os = WebHost.OsWindows,
            UseEsmDirectory = false
        });

        var wanted = FullyPopulated();
        wanted.SentinelId = original.SentinelId;
        Store().Upsert(wanted);

        var reread = Store().Get(original.SentinelId);
        Assert.NotNull(reread);
        AssertAllFieldsMatch(FullyPopulated(), reread!);
    }

    [Fact]
    public void 更新_保留建立時間並記錄更新時間()
    {
        var original = Store().Upsert(FullyPopulated());
        var createdAt = original.CreatedAt;

        var wanted = FullyPopulated();
        wanted.SentinelId = original.SentinelId;
        var updated = Store().Upsert(wanted);

        Assert.Equal(createdAt, updated.CreatedAt);   // 建立時間是事實，不該被覆寫
        Assert.NotNull(updated.UpdatedAt);
    }

    /// <summary>
    /// 逐欄比對（含 <c>UseEsmDirectory</c>）。新增模型欄位時**這裡也要加一行**——
    /// 加了才會逼出 Upsert 的漏抄，這是刻意的重複，不是可以省的樣板。
    /// </summary>
    private static void AssertAllFieldsMatch(Sentinel expected, Sentinel actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.BaseUrl, actual.BaseUrl);
        Assert.Equal(expected.Username, actual.Username);
        Assert.Equal(expected.PasswordEnc, actual.PasswordEnc);
        Assert.Equal(expected.Active, actual.Active);
        Assert.Equal(expected.Os, actual.Os);
        Assert.Equal(expected.UseEsmDirectory, actual.UseEsmDirectory);
    }
}
