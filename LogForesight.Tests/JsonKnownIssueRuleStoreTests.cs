using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// rules.json 的讀寫容錯（見 docs/RULES-PLAN.md 陷阱 3）：整檔 JSON 語法錯誤時 Load 失敗且
/// 不覆寫使用者的壞檔；單一規則物件解析失敗只跳過該條，其餘規則照常載入；列舉值以字串儲存。
///
/// 這些容錯邏輯全部寫在 <see cref="JsonKnownIssueRuleStore"/> 本身（blob 無關），透過
/// <see cref="IJsonBlobStore.Mutate{TResult}"/> 直接寫入原始（可能損毀的）內容即可跑在
/// 檔案或 DB blob 上——SQLite（EF）現為主要測試方式，與 Jsonl 版跑同一組案例。
/// 「原子寫入不留暫存檔」是 <see cref="FileJsonBlobStore"/> 特有的實作細節，留在檔案版另立測試。
/// </summary>
public abstract class KnownIssueRuleStoreContractTests : IDisposable
{
    protected abstract IJsonBlobStore CreateBlob();

    private IJsonBlobStore? _blob;
    private IJsonBlobStore Blob => _blob ??= CreateBlob();

    private JsonKnownIssueRuleStore Store() => new(Blob);

    private static void WriteRaw(IJsonBlobStore blob, string text) =>
        blob.Mutate<object?>(_ => (text, null));

    public virtual void Dispose() { }

    protected static KnownIssueRule SampleRule(string id = "custom-sample") => new()
    {
        Id = id,
        Origin = "custom",
        Enabled = true,
        Scope = "all",
        MatchAllEventIds = false,
        MatchFilter = null,
        SourcePattern = "TestSource",
        EventIds = new[] { 1, 2 },
        Category = IssueCategory.Other,
        Severity = IssueSeverity.Medium,
        Description = "desc",
        CountThreshold = 2,
        PlainExplanation = "explanation",
        Impact = "impact",
        LikelyCauses = new[] { "cause1", "cause2" },
        NextSteps = new[] { "step1" }
    };

    [Fact]
    public void 不存在的檔案Exists為false且Load失敗()
    {
        var store = Store();

        Assert.False(store.Exists);
        Assert.False(store.Load().Success);
    }

    [Fact]
    public void SaveAndLoadRoundTrip保留規則內容()
    {
        var store = Store();
        var content = new RuleFileContent { SchemaVersion = 1, SeedVersion = 3, Rules = new List<KnownIssueRule> { SampleRule() } };

        store.Save(content);
        Assert.True(store.Exists);

        var outcome = store.Load();
        Assert.True(outcome.Success);
        Assert.Equal(3, outcome.Content!.SeedVersion);
        var loaded = Assert.Single(outcome.Content.Rules);
        Assert.Equal("custom-sample", loaded.Id);
        Assert.Equal(IssueCategory.Other, loaded.Category);
        Assert.Equal(IssueSeverity.Medium, loaded.Severity);
        Assert.Equal(new[] { 1, 2 }, loaded.EventIds);
        Assert.Equal(new[] { "cause1", "cause2" }, loaded.LikelyCauses);
        Assert.True(loaded.MatchAllEventIds == false);
    }

    [Fact]
    public void 列舉值以字串儲存而非數字_方便人工編輯()
    {
        var store = Store();
        store.Save(new RuleFileContent { Rules = new List<KnownIssueRule> { SampleRule() } });

        var text = Blob.Read()!;
        Assert.Contains("\"Medium\"", text);
        Assert.Contains("\"Other\"", text);
    }

    [Fact]
    public void 單條規則物件解析失敗時跳過_其餘規則照常載入()
    {
        var json = """
        {
          "SchemaVersion": 1,
          "SeedVersion": 1,
          "Rules": [
            { "Id": "good-1", "Origin": "custom", "Enabled": true, "Scope": "all", "MatchAllEventIds": false,
              "SourcePattern": "A", "EventIds": [1], "Category": "Other", "Severity": "Low",
              "Description": "d", "CountThreshold": 1, "PlainExplanation": "p", "Impact": "i",
              "LikelyCauses": ["c"], "NextSteps": ["s"] },
            { "Id": "bad-1", "Origin": "custom", "Enabled": true, "Scope": "all", "MatchAllEventIds": false,
              "SourcePattern": "B", "EventIds": ["not-a-number"], "Category": "Other", "Severity": "Low",
              "Description": "d", "CountThreshold": 1, "PlainExplanation": "p", "Impact": "i",
              "LikelyCauses": ["c"], "NextSteps": ["s"] }
          ]
        }
        """;
        WriteRaw(Blob, json);

        var store = Store();
        var outcome = store.Load();

        Assert.True(outcome.Success);
        var rule = Assert.Single(outcome.Content!.Rules);
        Assert.Equal("good-1", rule.Id);
    }

    [Fact]
    public void 整檔JSON語法錯誤時Load失敗且不覆寫原檔()
    {
        const string original = "{ this is not valid json ";
        WriteRaw(Blob, original);

        var store = Store();
        var outcome = store.Load();

        Assert.False(outcome.Success);
        Assert.Equal(original, Blob.Read());
    }

    [Fact]
    public void SchemaVersion高於程式支援版本時Load失敗()
    {
        var json = $$"""{ "SchemaVersion": {{RuleFileContent.CurrentSchemaVersion + 1}}, "SeedVersion": 1, "Rules": [] }""";
        WriteRaw(Blob, json);

        var store = Store();
        var outcome = store.Load();

        Assert.False(outcome.Success);
        Assert.Contains("升級程式", outcome.Error);
    }
}

/// <summary>JSONL 後端（單機檔案相容模式）＋原子寫入不留暫存檔的檔案特有行為</summary>
public class JsonKnownIssueRuleStoreTests : KnownIssueRuleStoreContractTests
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"logforesight-rules-test-{Guid.NewGuid():N}.json");

    protected override IJsonBlobStore CreateBlob() => new FileJsonBlobStore(_path);

    public override void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        var tmp = _path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        GC.SuppressFinalize(this);
    }

    /// <summary>原子寫入是 FileJsonBlobStore 的實作細節（DB 版無暫存檔可言），不進合約基底</summary>
    [Fact]
    public void Save不會在目錄留下暫存檔()
    {
        var store = new JsonKnownIssueRuleStore(_path);
        store.Save(new RuleFileContent { Rules = new List<KnownIssueRule> { SampleRule() } });

        Assert.False(File.Exists(_path + ".tmp"));
    }
}

/// <summary>
/// SQLite（EF）後端——SQLite 現為主要測試方式，驗證規則檔容錯邏輯在 DB blob 上
/// 與 Jsonl 版逐位一致。
/// </summary>
public class EfKnownIssueRuleStoreTests : KnownIssueRuleStoreContractTests
{
    private readonly EfSqliteFixture _fx = new();

    protected override IJsonBlobStore CreateBlob() => _fx.Blob("rules");

    public override void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }
}
