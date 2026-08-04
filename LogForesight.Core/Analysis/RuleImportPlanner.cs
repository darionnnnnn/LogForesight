namespace LogForesight.Core.Analysis;

public enum RuleImportAction
{
    Added,
    UpdatedBuiltin,
    SkippedUnchanged,
    SkippedModifiedBuiltin,
    Conflict
}

public class RuleImportItem
{
    public string Id { get; init; } = string.Empty;
    public RuleImportAction Action { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>依 Id 分類後的匯入計畫：ResultingRules 是套用後的完整規則清單（未套用前僅供預覽參考）</summary>
public class RuleImportPlan
{
    public List<RuleImportItem> Items { get; } = new();
    public List<KnownIssueRule> ResultingRules { get; init; } = new();

    public int Added => Items.Count(i => i.Action == RuleImportAction.Added);
    public int Updated => Items.Count(i => i.Action == RuleImportAction.UpdatedBuiltin);
    public int Skipped => Items.Count(i => i.Action is RuleImportAction.SkippedUnchanged or RuleImportAction.SkippedModifiedBuiltin);
    public int Conflicts => Items.Count(i => i.Action == RuleImportAction.Conflict);
}

/// <summary>
/// 內建規則種子的匯入計畫與套用（docs/RULES-SPEC.md「初次部署寫入、後續手動匯入」；
/// docs/archive/WEB-SCHEDULER-PLAN.md §1.4.9 自 console 的 RuleImporter 拆出，console／Web 共用同一份，
/// 不寫兩套會漂移的分類邏輯）。以 Id 為鍵去重，custom 規則一律不碰；builtin 規則預設只補缺，
/// 內容有異動需要 overwriteBuiltin 才會覆蓋——覆蓋時保留使用者對 Enabled 的選擇（使用者停用
/// 某條 builtin 不是「修改內容」，是操作決定，匯入不應該把它打開）。
/// </summary>
public static class RuleImportPlanner
{
    /// <summary>純函數：依既有規則與種子規則算出匯入計畫，不做任何 I/O，方便單元測試。</summary>
    public static RuleImportPlan BuildPlan(List<KnownIssueRule> existingRules, List<KnownIssueRule> seedRules, bool overwriteBuiltin)
    {
        var plan = new RuleImportPlan { ResultingRules = new List<KnownIssueRule>(existingRules) };
        var existingById = existingRules
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var resultingIndexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < plan.ResultingRules.Count; i++)
        {
            resultingIndexById[plan.ResultingRules[i].Id] = i;
        }

        foreach (var seedRule in seedRules)
        {
            if (!existingById.TryGetValue(seedRule.Id, out var existing))
            {
                plan.ResultingRules.Add(seedRule);
                plan.Items.Add(new RuleImportItem
                {
                    Id = seedRule.Id,
                    Action = RuleImportAction.Added,
                    Detail = "尚無此規則，新增（builtin）"
                });
                continue;
            }

            if (existing.Origin != "builtin")
            {
                plan.Items.Add(new RuleImportItem
                {
                    Id = seedRule.Id,
                    Action = RuleImportAction.Conflict,
                    Detail = $"同 Id 的規則 Origin 為「{existing.Origin}」而非 builtin，衝突，未處理"
                });
                continue;
            }

            if (ContentEqualExceptEnabled(existing, seedRule))
            {
                plan.Items.Add(new RuleImportItem
                {
                    Id = seedRule.Id,
                    Action = RuleImportAction.SkippedUnchanged,
                    Detail = "內容與內建種子相同，略過"
                });
                continue;
            }

            if (!overwriteBuiltin)
            {
                plan.Items.Add(new RuleImportItem
                {
                    Id = seedRule.Id,
                    Action = RuleImportAction.SkippedModifiedBuiltin,
                    Detail = "builtin 內容與內建種子不同（程式已更新此規則），需勾選「連同已修改的內建規則一併覆蓋」才會覆蓋"
                });
                continue;
            }

            // 覆蓋：內容改用種子最新版本，但保留使用者對 Enabled 的選擇——
            // 停用某條 builtin 是操作決定，不是「內容被改過」，匯入不該把它悄悄打開。
            var updated = seedRule.CloneForSeedOverwrite(existing.Enabled);
            plan.ResultingRules[resultingIndexById[seedRule.Id]] = updated;
            plan.Items.Add(new RuleImportItem
            {
                Id = seedRule.Id,
                Action = RuleImportAction.UpdatedBuiltin,
                Detail = "已用內建種子最新內容覆蓋（保留原本的 Enabled 設定）"
            });
        }

        return plan;
    }

    /// <summary>
    /// 套用計畫：寫入新的 RuleFileContent（SeedVersion 更新為程式目前的種子版本）並重新驗證。
    /// 呼叫端（console/Web）各自決定要不要套用、以及如何呈現 <see cref="RuleValidationResult"/>
    /// 裡的警告——這裡只負責「寫檔＋驗證」這件事本身。
    /// </summary>
    public static RuleValidationOutcome Apply(IKnownIssueRuleStore store, RuleImportPlan plan)
    {
        var newContent = new RuleFileContent
        {
            SchemaVersion = RuleFileContent.CurrentSchemaVersion,
            SeedVersion = KnownIssueSeed.Version,
            Rules = plan.ResultingRules
        };
        store.Save(newContent);
        return RuleValidator.Validate(newContent.Rules);
    }

    /// <summary>
    /// 比對「除了 Enabled 以外的內容是否相同」——決定一條 builtin 規則要不要列入「程式已更新此規則」。
    /// **漏掉任何一個欄位的後果是靜默的**：種子明明改了，匯入卻回報「內容與內建種子相同，略過」，
    /// 使用者照著做完匯入也拿不到修正。新增規則欄位時務必同步這裡，
    /// <c>RuleImporterTests</c> 的反射逐欄比對測試會抓到漏抄。
    /// </summary>
    private static bool ContentEqualExceptEnabled(KnownIssueRule a, KnownIssueRule b) =>
        a.Origin == b.Origin &&
        a.Scope == b.Scope &&
        a.Platform == b.Platform &&
        a.MatchAllEventIds == b.MatchAllEventIds &&
        a.MatchFilter == b.MatchFilter &&
        a.SourcePattern == b.SourcePattern &&
        a.EventIds.SequenceEqual(b.EventIds) &&
        a.ProgramPattern == b.ProgramPattern &&
        a.EventNamePattern == b.EventNamePattern &&
        a.MessagePatterns.SequenceEqual(b.MessagePatterns) &&
        a.Category == b.Category &&
        a.Severity == b.Severity &&
        a.ElevatesDayRisk == b.ElevatesDayRisk &&
        a.Description == b.Description &&
        a.CountThreshold == b.CountThreshold &&
        a.PlainExplanation == b.PlainExplanation &&
        a.Impact == b.Impact &&
        a.LikelyCauses.SequenceEqual(b.LikelyCauses) &&
        a.NextSteps.SequenceEqual(b.NextSteps);
}
