namespace LogForesight;

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
/// `--import-rules`：手動把程式內建種子的新增/修訂內容匯入 rules.json（見 docs/RULES-PLAN.md，
/// 「初次部署寫入、後續手動匯入」的決定）。以 Id 為鍵去重，custom 規則一律不碰；
/// builtin 規則預設只補缺，內容有異動需要 --overwrite-builtin 才會覆蓋——覆蓋時保留使用者
/// 對 Enabled 的選擇（使用者停用某條 builtin 不是「修改內容」，是操作決定，匯入不應該把它打開）。
/// </summary>
public static class RuleImporter
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
                    Detail = "rules.json 尚無此規則，新增（builtin）"
                });
                continue;
            }

            if (existing.Origin != "builtin")
            {
                plan.Items.Add(new RuleImportItem
                {
                    Id = seedRule.Id,
                    Action = RuleImportAction.Conflict,
                    Detail = $"rules.json 中同 Id 的規則 Origin 為「{existing.Origin}」而非 builtin，衝突，未處理"
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
                    Detail = "builtin 內容與內建種子不同（程式已更新此規則），需加上 --overwrite-builtin 才會覆蓋"
                });
                continue;
            }

            // 覆蓋：內容改用種子最新版本，但保留使用者對 Enabled 的選擇——
            // 停用某條 builtin 是操作決定，不是「內容被改過」，匯入不該把它悄悄打開
            var updated = new KnownIssueRule
            {
                Id = seedRule.Id,
                Origin = seedRule.Origin,
                Enabled = existing.Enabled,
                Scope = seedRule.Scope,
                MatchAllEventIds = seedRule.MatchAllEventIds,
                MatchFilter = seedRule.MatchFilter,
                SourcePattern = seedRule.SourcePattern,
                EventIds = seedRule.EventIds,
                Category = seedRule.Category,
                Severity = seedRule.Severity,
                Description = seedRule.Description,
                CountThreshold = seedRule.CountThreshold,
                PlainExplanation = seedRule.PlainExplanation,
                Impact = seedRule.Impact,
                LikelyCauses = seedRule.LikelyCauses,
                NextSteps = seedRule.NextSteps
            };
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

    /// <summary>執行匯入：載入現有規則、算計畫、印出結果，apply=true 時才真的寫檔。
    /// rules.json 尚不存在時視同初次部署，直接寫入完整種子（等同一般啟動流程的行為）。</summary>
    public static void Run(IKnownIssueRuleStore store, bool apply, bool overwriteBuiltin)
    {
        if (!store.Exists)
        {
            var seedRules = KnownIssueSeed.CreateRules();
            Console.WriteLine($"{store.Location} 不存在，視同初次部署。");
            if (!apply)
            {
                Console.WriteLine($"（預覽模式）將寫入內建種子全部 {seedRules.Count} 條規則，seed v{KnownIssueSeed.Version}。加上 --apply 才會實際寫入。");
                return;
            }

            store.Save(new RuleFileContent
            {
                SchemaVersion = RuleFileContent.CurrentSchemaVersion,
                SeedVersion = KnownIssueSeed.Version,
                Rules = seedRules
            });
            Console.WriteLine($"已寫入內建種子（{seedRules.Count} 條規則，seed v{KnownIssueSeed.Version}）到 {store.Location}。");
            return;
        }

        var outcome = store.Load();
        if (!outcome.Success)
        {
            Console.WriteLine($"規則檔載入失敗，無法匯入：{outcome.Error}");
            return;
        }

        var plan = BuildPlan(outcome.Content!.Rules, KnownIssueSeed.CreateRules(), overwriteBuiltin);

        Console.WriteLine($"匯入預覽：將新增 {plan.Added}、將更新 {plan.Updated}" +
                          $"（--overwrite-builtin {(overwriteBuiltin ? "已套用" : "未套用，加上此參數才會覆蓋已修改的 builtin")}）、" +
                          $"略過 {plan.Skipped}、衝突 {plan.Conflicts}");
        foreach (var item in plan.Items)
        {
            Console.WriteLine($"  [{ActionZh(item.Action)}] {item.Id}：{item.Detail}");
        }

        if (!apply)
        {
            Console.WriteLine("（預覽模式，未寫入任何檔案。加上 --apply 才會套用上述變更。）");
            return;
        }

        if (plan.Added == 0 && plan.Updated == 0)
        {
            Console.WriteLine("沒有需要套用的變更，未寫入檔案。");
            return;
        }

        var newContent = new RuleFileContent
        {
            SchemaVersion = RuleFileContent.CurrentSchemaVersion,
            SeedVersion = KnownIssueSeed.Version,
            Rules = plan.ResultingRules
        };
        store.Save(newContent);
        Console.WriteLine($"已套用匯入並將 SeedVersion 更新為 v{KnownIssueSeed.Version}，寫入 {store.Location}。");

        var revalidation = RuleValidator.Validate(newContent.Rules);
        foreach (var warning in revalidation.ShadowWarnings)
        {
            Console.WriteLine($"⚠ {warning}");
        }
        foreach (var (rule, reason) in revalidation.SkippedRules)
        {
            Console.WriteLine($"⚠ 規則 {rule.Id} 不合格：{reason}（下次啟動時會被跳過，不影響其餘規則）");
        }
    }

    private static string ActionZh(RuleImportAction action) => action switch
    {
        RuleImportAction.Added => "新增",
        RuleImportAction.UpdatedBuiltin => "更新",
        RuleImportAction.SkippedUnchanged => "略過-未變",
        RuleImportAction.SkippedModifiedBuiltin => "略過-需覆蓋參數",
        RuleImportAction.Conflict => "衝突",
        _ => action.ToString()
    };

    private static bool ContentEqualExceptEnabled(KnownIssueRule a, KnownIssueRule b) =>
        a.Origin == b.Origin &&
        a.Scope == b.Scope &&
        a.MatchAllEventIds == b.MatchAllEventIds &&
        a.MatchFilter == b.MatchFilter &&
        a.SourcePattern == b.SourcePattern &&
        a.EventIds.SequenceEqual(b.EventIds) &&
        a.Category == b.Category &&
        a.Severity == b.Severity &&
        a.Description == b.Description &&
        a.CountThreshold == b.CountThreshold &&
        a.PlainExplanation == b.PlainExplanation &&
        a.Impact == b.Impact &&
        a.LikelyCauses.SequenceEqual(b.LikelyCauses) &&
        a.NextSteps.SequenceEqual(b.NextSteps);
}
