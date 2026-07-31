namespace LogForesight;

/// <summary>
/// `--import-rules` CLI 薄包裝（docs/WEB-SCHEDULER-PLAN.md §1.4.9）：分類與套用邏輯已拆到 Core 的
/// <see cref="RuleImportPlanner"/>（console／Web 共用同一份），這裡只負責 console 輸出格式——
/// 過渡期既有使用者照 README 的升級 SOP 操作時看到的畫面逐字不變。
/// </summary>
public static class RuleImporter
{
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

        var plan = RuleImportPlanner.BuildPlan(outcome.Content!.Rules, KnownIssueSeed.CreateRules(), overwriteBuiltin);

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

        var revalidation = RuleImportPlanner.Apply(store, plan);
        Console.WriteLine($"已套用匯入並將 SeedVersion 更新為 v{KnownIssueSeed.Version}，寫入 {store.Location}。");

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
}
