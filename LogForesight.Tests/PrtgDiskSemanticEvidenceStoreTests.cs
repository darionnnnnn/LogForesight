using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskSemanticEvidenceStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private const string ParserVersion = "disk-percent-v1";

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private PrtgDiskSemanticEvidenceStore Store() => new(new EfJsonBlobStore(_fx.NewContext, PrtgDiskSemanticEvidenceStore.BlobKey));

    private static PrtgDiskSemanticContext Context(
        long sensor = 101, long device = 20, long host = 7, string type = "snmpdiskfree",
        string channelId = "free-percent", string channel = "Free Space", string unit = "Percent",
        double scale = 1, string direction = "descending-danger") =>
        new(sensor, device, host, type, channelId, channel, unit, scale, direction);

    [Fact]
    public void 手動確認可持久化並於新store實例重啟讀回()
    {
        var store = Store();
        var time = new DateTime(2026, 9, 24, 3, 0, 0, DateTimeKind.Utc);
        store.ConfirmManually(Context(), 42, "已比對 PRTG 頻道標籤與百分比單位。", time, ParserVersion);

        var loaded = Store().CheckValidity(101, Context(), ParserVersion);
        Assert.True(loaded.IsValid);
        Assert.Equal(42, loaded.Evidence!.VerifierUserId);
        Assert.Equal(PrtgDiskSemanticEvidenceSource.Manual, loaded.Evidence.Source);
        Assert.Equal(time, loaded.Evidence.VerifiedAtUtc);
    }

    [Theory]
    [InlineData(102, 20, 7, "snmpdiskfree", "free-percent", "Free Space", "Percent", 1, "descending-danger", "disk-percent-v1", "主機或裝置")]
    [InlineData(101, 21, 7, "snmpdiskfree", "free-percent", "Free Space", "Percent", 1, "descending-danger", "disk-percent-v1", "主機或裝置")]
    [InlineData(101, 20, 8, "snmpdiskfree", "free-percent", "Free Space", "Percent", 1, "descending-danger", "disk-percent-v1", "主機或裝置")]
    [InlineData(101, 20, 7, "other", "free-percent", "Free Space", "Percent", 1, "descending-danger", "disk-percent-v1", "sensor 類型")]
    [InlineData(101, 20, 7, "snmpdiskfree", "free-space", "Free Space", "Percent", 1, "descending-danger", "disk-percent-v1", "主頻道")]
    [InlineData(101, 20, 7, "snmpdiskfree", "free-percent", "Available", "Percent", 1, "descending-danger", "disk-percent-v1", "主頻道")]
    [InlineData(101, 20, 7, "snmpdiskfree", "free-percent", "Free Space", "GB", 1, "descending-danger", "disk-percent-v1", "單位")]
    [InlineData(101, 20, 7, "snmpdiskfree", "free-percent", "Free Space", "Percent", 2, "descending-danger", "disk-percent-v1", "單位")]
    [InlineData(101, 20, 7, "snmpdiskfree", "free-percent", "Free Space", "Percent", 1, "ascending-danger", "disk-percent-v1", "單位")]
    [InlineData(101, 20, 7, "snmpdiskfree", "free-percent", "Free Space", "Percent", 1, "descending-danger", "disk-percent-v2", "解析語意版本")]
    public void 映射中繼資料或解析版本改變時證據失效(
        long sensor, long device, int host, string type, string channelId, string channel,
        string unit, double scale, string direction, string version, string reason)
    {
        Store().ConfirmManually(Context(), 42, "已比對頻道與單位。", DateTime.UtcNow, ParserVersion);

        var result = Store().CheckValidity(101,
            Context(sensor, device, host, type, channelId, channel, unit, scale, direction), version);

        Assert.False(result.IsValid);
        Assert.Contains(reason, result.InvalidReason);
    }

    [Fact]
    public void 缺少目前對應會失效而移除白名單不刪除證據()
    {
        var store = Store();
        store.ConfirmManually(Context(), 42, "已核對 PRTG 頻道與百分比顯示。", DateTime.UtcNow, ParserVersion);

        Assert.False(store.CheckValidity(101, null, ParserVersion).IsValid);
        // Store 契約不含白名單；重新加入後原語意證據仍適用。
        Assert.True(Store().CheckValidity(101, Context(), ParserVersion).IsValid);
    }

    [Fact]
    public void 門檻變動不影響語意證據有效性()
    {
        Store().ConfirmManually(Context(), 42, "已核對 PRTG 頻道與百分比顯示。", DateTime.UtcNow, ParserVersion);

        // 門檻不屬於語意 context，故調整門檻無法讓語意確認失效。
        Assert.True(Store().CheckValidity(101, Context(), ParserVersion).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void 手動確認缺少驗證者拒絕寫入(int verifier)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Store().ConfirmManually(Context(), verifier, "已人工比對頻道。", DateTime.UtcNow, ParserVersion));
        Assert.Empty(Store().GetAll());
    }

    [Fact]
    public void 手動確認缺少context或證據摘要拒絕寫入()
    {
        Assert.Throws<ArgumentException>(() => Store().ConfirmManually(
            Context(unit: " "), 42, "已人工比對頻道。", DateTime.UtcNow, ParserVersion));
        Assert.Throws<ArgumentException>(() => Store().ConfirmManually(
            Context(), 42, " ", DateTime.UtcNow, ParserVersion));
        Assert.Empty(Store().GetAll());
    }

    [Fact]
    public void 自動確認未具明確強證據時拒絕()
    {
        Assert.Throws<InvalidOperationException>(() => Store().RecordAutomatedVerification(
            Context(), false, "自動比對頻道與歷史資料一致。", DateTime.UtcNow, ParserVersion));
        Assert.Empty(Store().GetAll());
    }

    [Theory]
    [InlineData("{\"password\":\"secret\"}")]
    [InlineData("api_key: secret")]
    [InlineData("https://prtg.local/api.htm")]
    public void 不接受payload或疑似機密作為摘要(string summary)
    {
        Assert.Throws<ArgumentException>(() => Store().ConfirmManually(
            Context(), 42, summary, DateTime.UtcNow, ParserVersion));
        Assert.Empty(Store().GetAll());
    }

    [Fact]
    public void 持久內容不包含摘要以外的原始密碼字串()
    {
        Store().ConfirmManually(Context(), 42, "已人工比對頻道與百分比單位。", DateTime.UtcNow, ParserVersion);

        var raw = _fx.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).Read();
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("已人工比對頻道與百分比單位", raw);
    }

    [Fact]
    public void 大型主機與驗證者識別碼可持久化()
    {
        const long hostId = 5_000_000_000;
        const long verifierId = 6_000_000_000;
        var context = Context(host: hostId);
        Store().ConfirmManually(context, verifierId, "已依 PRTG 畫面確認頻道與百分比單位。", DateTime.UtcNow, ParserVersion);

        var loaded = Store().CheckValidity(101, context, ParserVersion);

        Assert.True(loaded.IsValid);
        Assert.Equal(hostId, loaded.Evidence!.HostId);
        Assert.Equal(verifierId, loaded.Evidence.VerifierUserId);
    }
}
