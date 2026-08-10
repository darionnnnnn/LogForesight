using LogForesight.Web.Auth;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 規則庫初始化缺口的迴歸測試（docs/archive/FEEDBACK-5-PLAN.md §10）：全新環境（rules blob
/// 從未被任何一端寫入過，批次從未執行過）時，Web 端的 <see cref="RuleBootstrapper.LoadContent"/>
/// 必須能自行補上內建種子，讓 <see cref="RuleAdminService"/> 正常運作——不像既有的
/// <c>FakeRuleStore</c>（<c>Exists</c> 恆 true，見 RuleAdminServiceTests）那樣掩蓋這個案例，
/// 這裡刻意用真實的 EF 後端 store（<see cref="EfSqliteFixture"/>）重現「blob 真的不存在」的狀態。
/// </summary>
public class RuleBootstrapWebIntegrationTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 全新環境_Web端Bootstrap後規則頁可正常運作()
    {
        var ruleStore = new KnownIssueRuleStore(_fx.Blob("rules"));
        var seedStore = new RuleSeedStore(_fx.Blob("rule_seeds"));
        var suppressionStore = new SuppressionStore(_fx.Blob("suppressions"));

        // 修正前的原始現場：blob 真的不存在，直接呼叫 GetSuppressions（經 RuleAdminService.LoadContent）
        // 會拋 InvalidOperationException「規則庫載入失敗：檔案不存在」
        Assert.False(ruleStore.Exists);

        // 模擬 Web Program.cs「啟動時的資料準備」區段的初始化順序
        RuleBootstrapper.LoadContent(ruleStore);
        seedStore.Sync(KnownIssueSeed.CreateRules(), KnownIssueSeed.Version);

        var admin = new RuleAdminService(
            ruleStore, seedStore, suppressionStore,
            new FakeUserStore(), FakeCurrentUser.WithCapabilities(Capability.Maintain), new RecordingAuditService(),
            new FakeHostGroupStore());

        Assert.True(ruleStore.Exists);
        Assert.NotEmpty(admin.GetRules());
        Assert.Empty(admin.GetSuppressions()); // 不拋例外即通過——這正是修正前的失敗現場
    }

    [Fact]
    public void Bootstrap不會覆寫既有規則內容()
    {
        var ruleStore = new KnownIssueRuleStore(_fx.Blob("rules"));

        // 模擬「已存在」狀態（批次已跑過，或 Web 已 bootstrap 過一次）：空清單也是合法的
        // 已存在內容，重點是 Exists 已為 true 之後不該被種子覆寫
        ruleStore.Save(new RuleFileContent
        {
            SchemaVersion = RuleFileContent.CurrentSchemaVersion,
            SeedVersion = 1,
            Rules = new List<KnownIssueRule>()
        });

        var (content, usedFallback) = RuleBootstrapper.LoadContent(ruleStore);

        Assert.False(usedFallback);
        Assert.Empty(content.Rules);
    }
}
