using LogForesight.Core.Service;

namespace LogForesight.Core.Analysis;

/// <summary>驗證後的結果：合格規則、逐條不合格原因、遮蔽警告（見 docs/RULES-SPEC.md）</summary>
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
/// 照常載入——手動編輯 rules.json 打錯一條不該讓整份規則失效（見 docs/RULES-SPEC.md 陷阱 3）。
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
        if (rule.Platform != "windows" && rule.Platform != "linux" && rule.Platform != "prtg")
        {
            return $"Platform 必須是 windows、linux 或 prtg，實際為「{rule.Platform}」";
        }

        var platformReason = rule.Platform switch
        {
            "windows" => CheckWindowsFields(rule),
            "linux" => CheckLinuxFields(rule),
            "prtg" => CheckPrtgFields(rule),
            _ => null
        };
        if (platformReason != null)
        {
            return platformReason;
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

    /// <summary>Windows 規則欄位（docs/RULES-SPEC.md 陷阱說明）：Linux 專用三欄必空，
    /// SourcePattern 必填，EventIds 非空或 MatchAllEventIds 二擇一成立。</summary>
    private static string? CheckWindowsFields(KnownIssueRule rule)
    {
        if (!string.IsNullOrEmpty(rule.ProgramPattern) || !string.IsNullOrEmpty(rule.EventNamePattern) || rule.MessagePatterns.Length > 0)
        {
            return "windows 規則不可填 ProgramPattern/EventNamePattern/MessagePatterns（Linux 專用欄位）";
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
            return "EventIds 為空但 MatchAllEventIds 未設為 true（全比對必須顯式宣告，見 docs/RULES-SPEC.md）";
        }
        if (rule.EventIds.Any(id => id <= 0))
        {
            return "EventIds 內含非正整數";
        }
        return null;
    }

    /// <summary>Linux 規則欄位（docs/LINUX-RULES.md §1.3）：Windows 專用欄位必空，
    /// ProgramPattern／EventNamePattern 至少一個非空（兩條比對路至少通一條），
    /// MessagePatterns 每條非空白、不過長、最多 8 條（超過代表規則想做的事太多，該拆條）。</summary>
    private static string? CheckLinuxFields(KnownIssueRule rule)
    {
        if (!string.IsNullOrEmpty(rule.SourcePattern) || rule.EventIds.Length > 0 || rule.MatchAllEventIds)
        {
            return "linux 規則不可填 SourcePattern/EventIds/MatchAllEventIds（Windows 專用欄位）";
        }
        if (string.IsNullOrEmpty(rule.ProgramPattern) && string.IsNullOrEmpty(rule.EventNamePattern))
        {
            return "linux 規則的 ProgramPattern 與 EventNamePattern 至少要填一個";
        }
        if (rule.ProgramPattern.Length > RuleSchemaLimits.ProgramPatternMaxLength)
        {
            return $"ProgramPattern 超過長度上限（{RuleSchemaLimits.ProgramPatternMaxLength}）";
        }
        // ProgramPattern 會以裸 term 形式進 Sentinel 的 Lucene filter（SentinelQueryBuilder.
        // LinuxRuleProgramClauses 的 `sp:{pattern}*`，不像 MessagePatterns 有引號＋跳脫保護）——
        // 空白或 Lucene 特殊字元（(、:、* 等）會讓整份 Q1 filter 語法壞掉，夜間取數整批查詢
        // 失敗。字元集對齊 SentinelEventMapper.LinuxMessagePrefixRegex 的 program 字元類別
        // （syslog identifier 的實務形狀），17 條種子全數天然合格。
        if (rule.ProgramPattern.Length > 0 && !rule.ProgramPattern.All(IsLuceneSafeTermChar))
        {
            return "ProgramPattern 僅接受英數字與 _ . -（會直接進 Sentinel 查詢子句，" +
                   "空白或特殊字元會破壞查詢語法）";
        }
        if (rule.EventNamePattern.Length > RuleSchemaLimits.EventNamePatternMaxLength)
        {
            return $"EventNamePattern 超過長度上限（{RuleSchemaLimits.EventNamePatternMaxLength}）";
        }
        if (rule.MessagePatterns.Length > RuleSchemaLimits.MessagePatternsMaxCount)
        {
            return $"MessagePatterns 超過條數上限（{RuleSchemaLimits.MessagePatternsMaxCount}）";
        }
        if (rule.MessagePatterns.Any(string.IsNullOrWhiteSpace))
        {
            return "MessagePatterns 內含空白項目";
        }
        if (rule.MessagePatterns.Any(p => p.Length > RuleSchemaLimits.MessagePatternMaxLength))
        {
            return $"MessagePatterns 有項目超過長度上限（{RuleSchemaLimits.MessagePatternMaxLength}）";
        }
        return null;
    }

    /// <summary>PRTG 規則欄位：Windows／Linux 專用欄位必空，PrtgRuleCode 必須為合法代碼，
    /// PrtgThreshold 依代碼而定（down/warning >= 1 分鐘，flapping >= 2 次，silent == 0）。</summary>
    private static string? CheckPrtgFields(KnownIssueRule rule)
    {
        if (!string.IsNullOrEmpty(rule.SourcePattern) || rule.EventIds.Length > 0 || rule.MatchAllEventIds ||
            !string.IsNullOrEmpty(rule.ProgramPattern) || !string.IsNullOrEmpty(rule.EventNamePattern) || rule.MessagePatterns.Length > 0)
        {
            return "prtg 規則不可填 SourcePattern/EventIds/MatchAllEventIds/ProgramPattern/EventNamePattern/MessagePatterns（Windows/Linux 專用欄位）";
        }

        if (rule.PrtgRuleCode != PrtgRuleEvaluator.RuleDown &&
            rule.PrtgRuleCode != PrtgRuleEvaluator.RuleFlapping &&
            rule.PrtgRuleCode != PrtgRuleEvaluator.RuleWarning &&
            rule.PrtgRuleCode != PrtgRuleEvaluator.RuleSilent)
        {
            return $"PrtgRuleCode 必須是 {PrtgRuleEvaluator.RuleDown}、{PrtgRuleEvaluator.RuleFlapping}、{PrtgRuleEvaluator.RuleWarning} 或 {PrtgRuleEvaluator.RuleSilent}，實際為「{rule.PrtgRuleCode}」";
        }

        if (rule.PrtgRuleCode == PrtgRuleEvaluator.RuleDown && rule.PrtgThreshold < 1)
        {
            return $"PrtgThreshold 必須 >= 1（{PrtgRuleEvaluator.RuleDown} 規則門檻為分鐘數）";
        }
        if (rule.PrtgRuleCode == PrtgRuleEvaluator.RuleWarning && rule.PrtgThreshold < 1)
        {
            return $"PrtgThreshold 必須 >= 1（{PrtgRuleEvaluator.RuleWarning} 規則門檻為分鐘數）";
        }
        if (rule.PrtgRuleCode == PrtgRuleEvaluator.RuleFlapping && rule.PrtgThreshold < 2)
        {
            return $"PrtgThreshold 必須 >= 2（{PrtgRuleEvaluator.RuleFlapping} 規則門檻為往返次數，至少需 2 次）";
        }
        if (rule.PrtgRuleCode == PrtgRuleEvaluator.RuleSilent && rule.PrtgThreshold != 0)
        {
            return $"PrtgThreshold 必須為 0（{PrtgRuleEvaluator.RuleSilent} 規則不使用門檻）";
        }

        return null;
    }

    /// <summary>Lucene 裸 term 安全字元（見 <see cref="CheckLinuxFields"/> 的 ProgramPattern 檢查）：
    /// 英數字與 <c>_</c>／<c>.</c>／<c>-</c>，與 SentinelEventMapper 的 msg 前綴 program
    /// 字元類別一致。</summary>
    private static bool IsLuceneSafeTermChar(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '-';

    /// <summary>
    /// 遮蔽偵測入口：Windows 與 Linux 規則各自的比對邏輯完全獨立（FindRule／FindLinuxRule
    /// 明確按 Platform 分路，見 KnownIssueCatalog），一個平台的規則不可能遮蔽另一個平台的規則，
    /// 所以分區偵測——Windows 規則永遠不會被判定遮蔽 Linux 規則，反之亦然
    /// （docs/LINUX-RULES.md §1.3）。Where 保留原始清單的相對順序，
    /// 「比對順序＝清單順序」的語意在各自分區內成立。
    /// </summary>
    private static List<string> DetectShadowing(List<KnownIssueRule> validRules)
    {
        var warnings = new List<string>();
        warnings.AddRange(DetectWindowsShadowing(validRules.Where(r => r.Platform == "windows").ToList()));
        warnings.AddRange(DetectLinuxShadowing(validRules.Where(r => r.Platform == "linux").ToList()));
        // PRTG 平台規則按 RuleCode 獨立判定，非 pattern 比對命中，無相互遮蔽概念，故不做遮蔽偵測。
        return warnings;
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
    private static List<string> DetectWindowsShadowing(List<KnownIssueRule> validRules)
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
                             $"（{earlier.Id} 的 SourcePattern「{earlier.SourcePattern}」與 EventIds 已涵蓋 {later.Id}）。" +
                             $"解法：停用其中一條（建議停用 {earlier.Id} 並以它為範本建立更精確的自訂規則），" +
                             $"或縮小 {earlier.Id} 的比對範圍使兩者不重疊。本頁不支援調整規則順序，順序由建立先後決定。");
                break;
            }
        }

        return warnings;
    }

    /// <summary>
    /// Linux 版遮蔽偵測，充分條件更保守：只有 earlier 的 <c>MessagePatterns</c> 為空
    /// （program 命中即算，不篩訊息）時，才可能完全涵蓋 later 的 program 範圍——
    /// 訊息子字串之間的涵蓋關係不做精確判定，比對成本高，且誤報遮蔽警告比漏報更擾人
    /// （docs/LINUX-RULES.md §1.3）。EventNamePattern 路徑同理不參與涵蓋判定，
    /// 事件名比對與 program 比對是獨立的兩條路，沒有清楚的「涵蓋」語意可用。
    /// </summary>
    private static List<string> DetectLinuxShadowing(List<KnownIssueRule> validRules)
    {
        var warnings = new List<string>();

        for (int i = 0; i < validRules.Count; i++)
        {
            var later = validRules[i];
            if (!later.Enabled || string.IsNullOrEmpty(later.ProgramPattern))
            {
                continue;
            }

            for (int j = 0; j < i; j++)
            {
                var earlier = validRules[j];
                if (!earlier.Enabled || string.IsNullOrEmpty(earlier.ProgramPattern) || earlier.MessagePatterns.Length > 0)
                {
                    continue;
                }

                // 子字串比對是刻意的、不是誤判：Sentinel 端 program 比對走 sp:{program}* 前綴，
                // "su" 這種短 program 只要沒有 MessagePatterns 收斂，就真的會把 "sudo" 的事件
                // 一併吃下去——遮蔽在這裡是真實發生的，不是規則寫法巧合撞名（回饋十三輪 A10）。
                bool programCovered = later.ProgramPattern.Contains(earlier.ProgramPattern, StringComparison.OrdinalIgnoreCase);
                if (!programCovered)
                {
                    continue;
                }

                warnings.Add($"規則 {later.Id} 被排在前面的規則 {earlier.Id} 遮蔽，永遠不會命中" +
                             $"（{earlier.Id} 的 ProgramPattern「{earlier.ProgramPattern}」不篩訊息，" +
                             $"依前綴比對已涵蓋 {later.Id} 的「{later.ProgramPattern}」）。" +
                             $"解法：幫 {earlier.Id} 加上 MessagePatterns，縮小到只比對它自己的訊息內容；" +
                             $"或停用其中一條。本頁不支援調整規則順序，順序由建立先後決定。");
                break;
            }
        }

        return warnings;
    }
}
