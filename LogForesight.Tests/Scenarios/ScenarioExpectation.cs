using System.Text;
using Xunit.Sdk;

namespace LogForesight.Tests.Scenarios;

// ── 預期與「已知差異」機制 ─────────────────────────────────────────────────────
// 情境可以宣告預期（第 3 天主機 A 應為高風險、應建單……），驅動器跑完後比對實際。
// 已知差異清單的每一項帶評估記錄編號（如 E3-01），指向某個預期的 Key：
//   清單外的不一致        → 失敗（列出預期與實際）
//   清單內但實際已一致    → 失敗（要求從清單移除，避免清單變成永遠不清的垃圾桶）
//   清單內且仍不一致      → 通過（這就是已知、已記錄的差異）
//   清單為空              → 全部預期都必須一致

/// <summary>一項預期：<see cref="Actual"/> 從結果算出一個可比對的字串，與 <see cref="Expected"/> 逐字比較。</summary>
public sealed class ScenarioExpectation
{
    public ScenarioExpectation(string key, int day, string description, string expected, Func<ScenarioResult, string> actual)
    {
        Key = key;
        Day = day;
        Description = description;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>預期的唯一鍵；已知差異用它指到這一項</summary>
    public string Key { get; }

    public int Day { get; }

    public string Description { get; }

    public string Expected { get; }

    public Func<ScenarioResult, string> Actual { get; }
}

/// <summary>一項已知差異：記錄編號（如 E3-01）＋它指向的預期鍵</summary>
public sealed record KnownDifference(string RecordId, string ExpectationKey);

/// <summary>預期的建構輔助。鍵的格式固定（D{天}.{類別}.{對象}），已知差異清單寫起來才不易打錯。</summary>
public static class Expect
{
    /// <summary>某台主機當天的主機日風險（高／中／低）</summary>
    public static ScenarioExpectation HostRisk(int day, string host, string risk) =>
        new($"D{day}.risk.{host}", day, $"第 {day} 天 {host} 主機日風險", risk,
            r => Safe(() => r.Day(day).Host(host) is { HasRecord: true } h ? h.RiskLevel : "（無紀錄）"));

    /// <summary>當天新建的交辦單總數（夜間派工與人為建單都算）</summary>
    public static ScenarioExpectation NewOrderCount(int day, int expected) =>
        new($"D{day}.newOrders", day, $"第 {day} 天新建交辦單數", expected.ToString(),
            r => Safe(() => r.Day(day).NewOrders.Count().ToString()));

    /// <summary>當天新建給某處理人的交辦單數</summary>
    public static ScenarioExpectation NewOrdersFor(int day, string handler, int expected) =>
        new($"D{day}.newOrders.{handler}", day, $"第 {day} 天新建給 {handler} 的交辦單數", expected.ToString(),
            r => Safe(() => r.Day(day).NewOrders.Count(o => o.Handler == handler).ToString()));

    /// <summary>當天通知佇列新增某種類、某收件人的筆數</summary>
    public static ScenarioExpectation OutboxCount(int day, string kind, string recipient, int expected) =>
        new($"D{day}.outbox.{kind}.{recipient}", day, $"第 {day} 天通知佇列新增 {kind}→{recipient} 筆數", expected.ToString(),
            r => Safe(() => r.Day(day).OutboxAdded.Count(o => o.Kind == kind && o.Recipient == recipient).ToString()));

    /// <summary>當天稽核新增某動作的筆數</summary>
    public static ScenarioExpectation AuditCount(int day, string action, int expected) =>
        new($"D{day}.audit.{action}", day, $"第 {day} 天稽核新增 {action} 筆數", expected.ToString(),
            r => Safe(() => r.Day(day).AuditAdded.Count(a => a.Action == action).ToString()));

    /// <summary>當天第 <paramref name="index"/> 個人為動作（0 起）的 HTTP 狀態碼（0＝未送出）</summary>
    public static ScenarioExpectation ActionStatus(int day, int index, int expectedStatus) =>
        new($"D{day}.action[{index}].status", day, $"第 {day} 天第 {index} 個動作的 HTTP 狀態", expectedStatus.ToString(),
            r => Safe(() => r.Day(day).Actions[index].StatusCode.ToString()));

    /// <summary>某單某成員（主機）當天結束時的狀態</summary>
    public static ScenarioExpectation MemberStatus(int day, long orderId, string host, string expectedStatus) =>
        new($"D{day}.order#{orderId}.member.{host}", day, $"第 {day} 天 單#{orderId} 成員 {host} 狀態", expectedStatus,
            r => Safe(() => r.Day(day).Order(orderId).Members.First(m => m.Host == host).Status));

    /// <summary>夜間派工對某主機某問題（標籤含 <paramref name="issueContains"/>）的結果，格式「結果」或「結果:原因代碼」</summary>
    public static ScenarioExpectation Decision(int day, string host, string issueContains, string expectedOutcomeAndReason) =>
        new($"D{day}.decision.{host}.{issueContains}", day, $"第 {day} 天 {host} 的 {issueContains} 派工結果", expectedOutcomeAndReason,
            r => Safe(() =>
            {
                var d = r.Day(day).Decisions.FirstOrDefault(x => x.Host == host && x.Issue.Contains(issueContains, StringComparison.Ordinal));
                return d == null ? "（沒有決策）" : d.Reason == null ? d.Outcome : $"{d.Outcome}:{d.Reason}";
            }));

    /// <summary>自訂預期：<paramref name="actual"/> 算出字串與 <paramref name="expected"/> 逐字比對</summary>
    public static ScenarioExpectation Value(string key, int day, string description, string expected, Func<ScenarioResult, string> actual) =>
        new(key, day, description, expected, actual);

    // 取實際值失敗（缺天、缺主機）時，不讓比對本身炸掉，而是把失敗原因當成「實際值」，訊息才讀得懂
    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return $"（讀不到：{ex.Message}）";
        }
    }
}

/// <summary>預期比對失敗。繼承 xUnit 的例外，測試框架把它當一般斷言失敗呈現。</summary>
public sealed class ScenarioAssertionException : XunitException
{
    public ScenarioAssertionException(string message) : base(message) { }
}

/// <summary>預期與已知差異的比對結果</summary>
public sealed class ScenarioVerdict
{
    public IReadOnlyList<string> Problems { get; }

    /// <summary>已列入已知差異且確實仍不一致的項目（通過，但輸出時列出來，讓人知道還有幾項沒修）</summary>
    public IReadOnlyList<string> StillKnown { get; }

    public bool Ok => Problems.Count == 0;

    private ScenarioVerdict(List<string> problems, List<string> stillKnown)
    {
        Problems = problems;
        StillKnown = stillKnown;
    }

    public void AssertOk()
    {
        if (Ok) return;
        throw new ScenarioAssertionException("情境預期比對失敗：\n" + string.Join("\n", Problems));
    }

    internal static ScenarioVerdict Evaluate(Scenario scenario, ScenarioResult result)
    {
        var problems = new List<string>();
        var stillKnown = new List<string>();

        var duplicate = scenario.Expectations.GroupBy(e => e.Key).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) problems.Add($"預期鍵重複：{duplicate.Key}（每項預期的鍵必須唯一，已知差異才指得準）");

        var byKey = scenario.Expectations.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First());
        var known = scenario.KnownDifferences.GroupBy(k => k.ExpectationKey).ToDictionary(g => g.Key, g => g.First());

        foreach (var diff in scenario.KnownDifferences.Where(k => !byKey.ContainsKey(k.ExpectationKey)))
            problems.Add($"已知差異 {diff.RecordId} 指向不存在的預期「{diff.ExpectationKey}」（鍵打錯或預期已刪除，請修正清單）");

        foreach (var exp in scenario.Expectations)
        {
            var actual = exp.Actual(result);
            var matches = string.Equals(exp.Expected, actual, StringComparison.Ordinal);
            var isKnown = known.TryGetValue(exp.Key, out var knownDiff);

            if (!matches && !isKnown)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"- 預期不一致（未列入已知差異）：{exp.Key}｜{exp.Description}");
                sb.AppendLine($"    預期：{exp.Expected}");
                sb.Append($"    實際：{actual}");
                problems.Add(sb.ToString());
            }
            else if (matches && isKnown)
            {
                problems.Add($"- 已知差異 {knownDiff!.RecordId}（{exp.Key}｜{exp.Description}）實際已與預期一致（{actual}），請從已知差異清單移除。");
            }
            else if (!matches)
            {
                stillKnown.Add($"{knownDiff!.RecordId} {exp.Key}：預期 {exp.Expected}／實際 {actual}");
            }
        }

        return new ScenarioVerdict(problems, stillKnown);
    }
}
