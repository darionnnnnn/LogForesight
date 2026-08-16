using NLog;

namespace LogForesight.Core.Analysis;

/// <summary>
/// 啟動時載入規則的結果摘要。Run() 本身已把警告與摘要印到 console／NLog，這個物件的用途是讓
/// 呼叫端（目前是單元測試）能斷言載入結果，不必去解析 console 輸出——所以只帶「結論性」欄位，
/// 不重複帶一份已經印出去的警告文字。
/// </summary>
public class RuleBootstrapperResult
{
    public int EnabledCount { get; init; }
    public int DisabledCount { get; init; }
    public int SeedVersion { get; init; }
    public bool UsedFallbackSeed { get; init; }
    public string Source { get; init; } = string.Empty;
    public string? UpdateHint { get; init; }
}

/// <summary>
/// 啟動流程的規則載入編排（見 docs/RULES-SPEC.md）：
/// 規則檔不存在 → 寫入內建種子（初次部署）；存在但載入失敗 → 降級用內建種子且不覆寫壞檔；
/// 載入成功 → 驗證（單條不合格跳過、遮蔽偵測只警告）→ 呼叫 KnownIssueCatalog.Initialize。
/// 全程只在「不存在」時寫入一次，後續規則調整靠 Web「規則維護」頁（編輯／內建規則升級），
/// 不會每次啟動都覆寫使用者的自訂內容。
/// </summary>
public static class RuleBootstrapper
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>目前程式內建種子的完整內容（初次部署寫入的起始內容，也是載入失敗時的降級來源）</summary>
    private static RuleFileContent BuiltInSeedContent() => new()
    {
        SchemaVersion = RuleFileContent.CurrentSchemaVersion,
        SeedVersion = KnownIssueSeed.Version,
        Rules = KnownIssueSeed.CreateRules()
    };

    /// <summary>
    /// 載入規則內容（不驗證、不呼叫 KnownIssueCatalog.Initialize）：初次部署時寫入內建種子，
    /// 存在但載入失敗時降級回傳內建種子（不覆寫壞檔）。抽出成獨立方法供 Run() 與
    /// Web 端（RuleAdminService／站台啟動的規則庫初始化，見 docs/WEB-SPEC.md §9.7）共用——
    /// Web 不需要（也不該）連帶初始化 KnownIssueCatalog 的全域分類狀態，那是分析執行時才用得到的。
    /// </summary>
    public static (RuleFileContent Content, bool UsedFallback) LoadContent(IKnownIssueRuleStore store)
    {
        if (!store.Exists)
        {
            var seedContent = BuiltInSeedContent();

            try
            {
                store.Save(seedContent);
                Log.Info("首次部署：已寫入規則種子 {Path}，共 {Count} 條，seed v{Version}",
                    store.Location, seedContent.Rules.Count, seedContent.SeedVersion);
                return (seedContent, false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "規則種子寫入失敗：{Path}", store.Location);
                return (seedContent, true);
            }
        }

        var outcome = store.Load();
        if (outcome.Success)
        {
            return (outcome.Content!, false);
        }

        Log.Warn("規則檔載入失敗，降級用內建種子：{Path}，原因：{Error}", store.Location, outcome.Error);
        return (BuiltInSeedContent(), true);
    }

    public static RuleBootstrapperResult Run(IKnownIssueRuleStore store)
    {
        var (content, usedFallback) = LoadContent(store);

        var validation = RuleValidator.Validate(content.Rules);
        foreach (var (rule, reason) in validation.SkippedRules)
        {
            var id = string.IsNullOrEmpty(rule.Id) ? "(無 Id)" : rule.Id;
            Log.Warn("規則驗證失敗，已跳過：Id={Id}, 原因={Reason}", rule.Id, reason);
        }
        foreach (var warning in validation.ShadowWarnings)
        {
            Log.Warn("規則遮蔽警告：{Warning}", warning);
        }

        KnownIssueCatalog.Initialize(validation.ValidRules);

        int enabledCount = validation.ValidRules.Count(r => r.Enabled);
        int disabledCount = validation.ValidRules.Count(r => !r.Enabled);

        // 只在「本次確實用了規則檔（非降級）且內建種子版本較新」時提示——降級時記憶體裡的
        // 內容本身就已經是最新種子，不需要提示匯入
        string? updateHint = !usedFallback && KnownIssueSeed.Version > content.SeedVersion
            ? $"內建規則有更新（v{content.SeedVersion} → v{KnownIssueSeed.Version}），可至 Web「規則維護」頁的升級橫幅檢視並套用新增/修改的內容。"
            : null;

        var result = new RuleBootstrapperResult
        {
            EnabledCount = enabledCount,
            DisabledCount = disabledCount,
            SeedVersion = content.SeedVersion,
            UsedFallbackSeed = usedFallback,
            Source = usedFallback ? "內建種子" : store.Location,
            UpdateHint = updateHint
        };

        Log.Info("規則庫：{Source}（{EnabledCount} 條啟用、{DisabledCount} 條停用、seed v{SeedVersion}）",
            result.Source, result.EnabledCount, result.DisabledCount, result.SeedVersion);
        if (updateHint != null)
        {
            Log.Info("規則種子更新提示：{UpdateHint}", updateHint);
        }

        return result;
    }
}
