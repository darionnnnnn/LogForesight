namespace LogForesight;

/// <summary>驗證後的結果：合格規則、逐條不合格原因、遮蔽警告（見 docs/RULES-PLAN.md）</summary>
public class RuleValidationOutcome
{
    /// <summary>通過驗證的規則，保留原始順序（比對順序＝清單順序，與 FindRule 的語意一致）</summary>
    public List<KnownIssueRule> ValidRules { get; } = new();

    /// <summary>未通過驗證而被跳過的規則與原因——單條不合格不影響其餘規則載入</summary>
    public List<(KnownIssueRule Rule, string Reason)> SkippedRules { get; } = new();

    /// <summary>遮蔽偵測：規則永遠不會被命中（列在更前面的規則已經涵蓋它），只警告不跳過，由人決定順序</summary>
    public List<string> ShadowWarnings { get; } = new();
}

/// <summary>
/// 規則載入後的驗證：純函數，不做任何 I/O。單條規則的欄位/長度不合格就跳過該條、其餘規則
/// 照常載入——手動編輯 rules.json 打錯一條不該讓整份規則失效（見 docs/RULES-PLAN.md 陷阱 3）。
/// </summary>
public static class RuleValidator
{
    public static RuleValidationOutcome Validate(List<KnownIssueRule> rules)
    {
        var outcome = new RuleValidationOutcome();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            var reason = CheckRule(rule, seenIds);
            if (reason != null)
            {
                outcome.SkippedRules.Add((rule, reason));
                continue;
            }

            seenIds.Add(rule.Id);
            outcome.ValidRules.Add(rule);
        }

        outcome.ShadowWarnings.AddRange(DetectShadowing(outcome.ValidRules));
        return outcome;
    }

    private static string? CheckRule(KnownIssueRule rule, HashSet<string> seenIds)
    {
        if (string.IsNullOrWhiteSpace(rule.Id))
        {
            return "Id 空白";
        }
        if (rule.Id.Length > RuleSchemaLimits.IdMaxLength)
        {
            return $"Id 超過長度上限（{RuleSchemaLimits.IdMaxLength}）";
        }
        if (seenIds.Contains(rule.Id))
        {
            return $"Id 重複（已有規則使用同一個 Id：{rule.Id}）";
        }
        if (rule.Origin != "builtin" && rule.Origin != "custom")
        {
            return $"Origin 必須是 builtin 或 custom，實際為「{rule.Origin}」";
        }
        if (rule.Scope != "all")
        {
            return $"Scope「{rule.Scope}」此版本尚未支援，僅接受 all";
        }
        if (rule.MatchFilter != null)
        {
            return "MatchFilter 此版本尚未支援，必須為 null";
        }
        if (string.IsNullOrWhiteSpace(rule.SourcePattern))
        {
            return "SourcePattern 空白";
        }
        if (rule.SourcePattern.Length > RuleSchemaLimits.SourcePatternMaxLength)
        {
            return $"SourcePattern 超過長度上限（{RuleSchemaLimits.SourcePatternMaxLength}）";
        }
        if (!rule.MatchAllEventIds && rule.EventIds.Length == 0)
        {
            return "EventIds 為空但 MatchAllEventIds 未設為 true（全比對必須顯式宣告，見 docs/RULES-PLAN.md）";
        }
        if (rule.EventIds.Any(id => id <= 0))
        {
            return "EventIds 內含非正整數";
        }
        if (rule.CountThreshold < 1)
        {
            return "CountThreshold 必須 >= 1";
        }
        if (string.IsNullOrWhiteSpace(rule.Description))
        {
            return "Description 空白";
        }
        if (rule.Description.Length > RuleSchemaLimits.DescriptionMaxLength)
        {
            return $"Description 超過長度上限（{RuleSchemaLimits.DescriptionMaxLength}）";
        }
        if (string.IsNullOrWhiteSpace(rule.PlainExplanation))
        {
            return "PlainExplanation 空白";
        }
        if (rule.PlainExplanation.Length > RuleSchemaLimits.PlainExplanationMaxLength)
        {
            return $"PlainExplanation 超過長度上限（{RuleSchemaLimits.PlainExplanationMaxLength}）";
        }
        if (string.IsNullOrWhiteSpace(rule.Impact))
        {
            return "Impact 空白";
        }
        if (rule.Impact.Length > RuleSchemaLimits.ImpactMaxLength)
        {
            return $"Impact 超過長度上限（{RuleSchemaLimits.ImpactMaxLength}）";
        }
        if (rule.LikelyCauses.Length == 0)
        {
            return "LikelyCauses 不可為空";
        }
        if (rule.LikelyCauses.Any(string.IsNullOrWhiteSpace))
        {
            return "LikelyCauses 內含空白項目";
        }
        if (rule.LikelyCauses.Any(c => c.Length > RuleSchemaLimits.CauseOrStepMaxLength))
        {
            return $"LikelyCauses 有項目超過長度上限（{RuleSchemaLimits.CauseOrStepMaxLength}）";
        }
        if (rule.NextSteps.Length == 0)
        {
            return "NextSteps 不可為空";
        }
        if (rule.NextSteps.Any(string.IsNullOrWhiteSpace))
        {
            return "NextSteps 內含空白項目";
        }
        if (rule.NextSteps.Any(s => s.Length > RuleSchemaLimits.CauseOrStepMaxLength))
        {
            return $"NextSteps 有項目超過長度上限（{RuleSchemaLimits.CauseOrStepMaxLength}）";
        }

        return null;
    }

    /// <summary>
    /// 遮蔽偵測（充分條件，非完整精確語意）：FindRule 依清單順序取第一個命中的規則，
    /// 若排在後面的規則 later，其比對範圍已被排在前面且啟用中的規則 earlier 完全涵蓋
    /// （任何會命中 later 的實際事件來源，也一定會先命中 earlier），later 就永遠不會被命中。
    /// 「涵蓋」判定：earlier.SourcePattern 是 later.SourcePattern 的子字串（越具體的 pattern 越長，
    /// 被越泛用的 pattern 涵蓋），且 earlier 為 match-all，或 later 的 EventIds 全部被 earlier 涵蓋。
    /// 兩側都只看啟用中的規則：停用的規則本來就不參與比對（<see cref="KnownIssueCatalog.Initialize"/>
    /// 只收啟用規則），說它「被遮蔽、永遠不會命中」沒有意義，而且 selftest 把遮蔽警告視為失敗，
    /// 停用規則的假警報會讓「停用 builtin ＋另外加一條 custom」這個官方建議的改法無故變成紅燈。
    /// </summary>
    private static List<string> DetectShadowing(List<KnownIssueRule> validRules)
    {
        var warnings = new List<string>();

        for (int i = 0; i < validRules.Count; i++)
        {
            var later = validRules[i];
            if (!later.Enabled)
            {
                continue;
            }

            for (int j = 0; j < i; j++)
            {
                var earlier = validRules[j];
                if (!earlier.Enabled)
                {
                    continue;
                }

                bool sourceCovered = later.SourcePattern.Contains(earlier.SourcePattern, StringComparison.OrdinalIgnoreCase);
                if (!sourceCovered)
                {
                    continue;
                }

                bool idsCovered = earlier.MatchAllEventIds ||
                    (!later.MatchAllEventIds && later.EventIds.All(id => earlier.EventIds.Contains(id)));
                if (!idsCovered)
                {
                    continue;
                }

                warnings.Add($"規則 {later.Id} 被排在前面的規則 {earlier.Id} 遮蔽，永遠不會命中" +
                             $"（{earlier.Id} 的 SourcePattern「{earlier.SourcePattern}」與 EventIds 已涵蓋 {later.Id}），" +
                             "請調整順序或縮小其中一條的比對範圍");
                break;
            }
        }

        return warnings;
    }
}
