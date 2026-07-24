using System.Text.Json;

namespace LogForesight;

/// <summary>單條內建規則的原廠快照（↔ lf_rule_seeds）</summary>
public class RuleSeedSnapshot
{
    public string RuleId { get; set; } = string.Empty;

    /// <summary>來自哪一版種子（<see cref="KnownIssueSeed.Version"/>）</summary>
    public int SeedVersion { get; set; }

    /// <summary>整條規則的 JSON 快照</summary>
    public string ContentJson { get; set; } = string.Empty;
}

/// <summary>
/// 內建規則的原廠種子鏡像（docs/WEB-SPEC.md §14-#4「回復預設」的支撐）。
///
/// 為什麼需要獨立一份而不是直接用程式裡的 <see cref="KnownIssueSeed"/>：
/// 「回復預設」要回復的是**這條規則出貨時的樣子**，而使用者改過的內容就存在 rules.json 裡，
/// 兩者必須各有一份才比較得出差異。這份鏡像由批次啟動時同步、使用者永遠碰不到。
/// </summary>
public interface IRuleSeedStore
{
    /// <summary>取得某條規則的原廠快照；非內建規則或尚未同步過回 null</summary>
    RuleSeedSnapshot? Get(string ruleId);

    List<RuleSeedSnapshot> GetAll();

    /// <summary>以目前程式版本的內建種子覆寫整份鏡像（批次啟動時執行）</summary>
    void Sync(IEnumerable<KnownIssueRule> seedRules, int seedVersion);
}

/// <summary><see cref="IRuleSeedStore"/> 的實作（blob key=rule_seeds，整份型）</summary>
public class JsonRuleSeedStore : JsonBlobCollection<RuleSeedSnapshot>, IRuleSeedStore
{
    private static readonly JsonSerializerOptions RuleJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public JsonRuleSeedStore(IJsonBlobStore blob) : base(blob) { }

    public RuleSeedSnapshot? Get(string ruleId) =>
        Read().FirstOrDefault(s => string.Equals(s.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

    public List<RuleSeedSnapshot> GetAll() => Read();

    public void Sync(IEnumerable<KnownIssueRule> seedRules, int seedVersion)
    {
        var snapshots = seedRules.Select(rule => new RuleSeedSnapshot
        {
            RuleId = rule.Id,
            SeedVersion = seedVersion,
            ContentJson = JsonSerializer.Serialize(rule, RuleJsonOptions)
        }).ToList();

        Mutate(items =>
        {
            items.Clear();
            items.AddRange(snapshots);
        });
    }

    /// <summary>把快照還原成規則物件；內容損毀時回 null（呼叫端提示無法回復，而不是寫入壞資料）</summary>
    public static KnownIssueRule? Deserialize(RuleSeedSnapshot snapshot)
    {
        try
        {
            return JsonSerializer.Deserialize<KnownIssueRule>(snapshot.ContentJson, RuleJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
