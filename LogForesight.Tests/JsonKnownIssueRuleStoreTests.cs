using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// rules.json 的讀寫容錯（見 docs/RULES-PLAN.md 陷阱 3）：整檔 JSON 語法錯誤時 Load 失敗且
/// 不覆寫使用者的壞檔；單一規則物件解析失敗只跳過該條，其餘規則照常載入；原子寫入不留暫存檔。
/// </summary>
public class JsonKnownIssueRuleStoreTests : IDisposable
{
    private readonly string _path;

    public JsonKnownIssueRuleStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"logforesight-rules-test-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
        var tmp = _path + ".tmp";
        if (File.Exists(tmp))
        {
            File.Delete(tmp);
        }
    }

    private static KnownIssueRule SampleRule(string id = "custom-sample") => new()
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
        var store = new JsonKnownIssueRuleStore(_path);

        Assert.False(store.Exists);
        Assert.False(store.Load().Success);
    }

    [Fact]
    public void SaveAndLoadRoundTrip保留規則內容()
    {
        var store = new JsonKnownIssueRuleStore(_path);
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
        var store = new JsonKnownIssueRuleStore(_path);
        store.Save(new RuleFileContent { Rules = new List<KnownIssueRule> { SampleRule() } });

        var text = File.ReadAllText(_path);
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
        File.WriteAllText(_path, json);

        var store = new JsonKnownIssueRuleStore(_path);
        var outcome = store.Load();

        Assert.True(outcome.Success);
        var rule = Assert.Single(outcome.Content!.Rules);
        Assert.Equal("good-1", rule.Id);
    }

    [Fact]
    public void 整檔JSON語法錯誤時Load失敗且不覆寫原檔()
    {
        const string original = "{ this is not valid json ";
        File.WriteAllText(_path, original);

        var store = new JsonKnownIssueRuleStore(_path);
        var outcome = store.Load();

        Assert.False(outcome.Success);
        Assert.Equal(original, File.ReadAllText(_path));
    }

    [Fact]
    public void SchemaVersion高於程式支援版本時Load失敗()
    {
        var json = $$"""{ "SchemaVersion": {{RuleFileContent.CurrentSchemaVersion + 1}}, "SeedVersion": 1, "Rules": [] }""";
        File.WriteAllText(_path, json);

        var store = new JsonKnownIssueRuleStore(_path);
        var outcome = store.Load();

        Assert.False(outcome.Success);
        Assert.Contains("升級程式", outcome.Error);
    }

    [Fact]
    public void Save不會在目錄留下暫存檔()
    {
        var store = new JsonKnownIssueRuleStore(_path);
        store.Save(new RuleFileContent { Rules = new List<KnownIssueRule> { SampleRule() } });

        Assert.False(File.Exists(_path + ".tmp"));
    }
}
