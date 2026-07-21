using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 規則維護（docs/WEB-SPEC.md §9.7）。
///
/// **四層保護**（2026-07-21 定案）：
/// | 操作 | builtin | custom |
/// |---|---|---|
/// | 停用/啟用 | ✅ | ✅ |
/// | 修改內容 | ✅（標示「已修改」） | ✅ |
/// | 刪除 | ❌ | ✅ |
/// | 回復預設 | ✅（自原廠種子還原） | — |
///
/// **儲存前一律跑 <see cref="RuleValidator"/>**——把 `--selftest` 的規則驗證內建進儲存路徑，
/// 而不是指望使用者改完記得去跑一次。驗證不過就拒絕寫入，rules.json 永遠是合格的。
/// </summary>
public interface IRuleAdminService
{
    List<RuleDto> GetRules();

    RuleDto SaveRule(SaveRuleRequest request);

    void SetEnabled(string ruleId, bool enabled);

    void DeleteRule(string ruleId);

    RuleRestorePreviewDto PreviewRestore(string ruleId);

    RuleDto RestoreSeed(string ruleId);

    /// <summary>不寫入，只回報這份規則內容是否合格（前端即時提示用）</summary>
    RuleValidationDto ValidateRule(SaveRuleRequest request);

    List<RuleSuppressionDto> GetSuppressions();

    void AddSuppression(string ruleId, AddSuppressionRequest request);

    void RemoveSuppression(string ruleId, string host);
}

public class RuleAdminService : IRuleAdminService
{
    private readonly IKnownIssueRuleStore _rules;
    private readonly IRuleSeedStore _seeds;
    private readonly ISuppressionStore _suppressions;
    private readonly IUserStore _users;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;

    public RuleAdminService(
        IKnownIssueRuleStore rules,
        IRuleSeedStore seeds,
        ISuppressionStore suppressions,
        IUserStore users,
        ICurrentUser currentUser,
        IAuditService audit)
    {
        _rules = rules;
        _seeds = seeds;
        _suppressions = suppressions;
        _users = users;
        _currentUser = currentUser;
        _audit = audit;
    }

    public List<RuleDto> GetRules()
    {
        var content = LoadContent();
        var seeds = _seeds.GetAll().ToDictionary(s => s.RuleId, StringComparer.OrdinalIgnoreCase);
        var suppressions = _suppressions.LoadAll();

        return content.Rules.Select(rule => ToDto(rule, seeds, suppressions, content.SeedVersion)).ToList();
    }

    public RuleValidationDto ValidateRule(SaveRuleRequest request)
    {
        var content = LoadContent();
        var candidate = BuildRule(request, content.Rules
            .FirstOrDefault(r => string.Equals(r.Id, request.Id, StringComparison.OrdinalIgnoreCase)));

        // 把候選規則放回完整清單一起驗證：單條合格不代表放進整份規則表就合格
        // （Id 重複、被前面的規則遮蔽，都要看整體才知道）
        var candidateList = content.Rules
            .Select(r => string.Equals(r.Id, candidate.Id, StringComparison.OrdinalIgnoreCase) ? candidate : r)
            .ToList();

        if (!candidateList.Any(r => string.Equals(r.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)))
            candidateList.Add(candidate);

        var outcome = RuleValidator.Validate(candidateList);

        var errors = outcome.SkippedRules
            .Where(s => string.Equals(s.Rule.Id, candidate.Id, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Reason)
            .ToList();

        var warnings = outcome.ShadowWarnings
            .Where(w => w.Contains(candidate.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new RuleValidationDto
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings
        };
    }

    public RuleDto SaveRule(SaveRuleRequest request)
    {
        var content = LoadContent();
        var existing = content.Rules
            .FirstOrDefault(r => string.Equals(r.Id, request.Id, StringComparison.OrdinalIgnoreCase));

        var isNew = existing == null;

        if (isNew && !request.Id.StartsWith("custom-", StringComparison.OrdinalIgnoreCase))
        {
            // 新規則一律 custom- 前綴：builtin 的命名空間屬於程式內建種子，
            // 讓使用者能造出 builtin- 開頭的規則，日後 --import-rules 比對時會產生無解的衝突
            throw DomainException.Validation("新增的規則 Id 必須以「custom-」開頭，以區別於程式內建規則。");
        }

        var rule = BuildRule(request, existing);

        // 儲存前驗證：把 --selftest 的檢查內建進儲存路徑（§9.7）
        var validation = ValidateRule(request);
        if (!validation.IsValid)
            throw DomainException.Validation("規則不合格，未儲存：" + string.Join("；", validation.Errors));

        if (isNew) content.Rules.Add(rule);
        else content.Rules[content.Rules.IndexOf(existing!)] = rule;

        _rules.Save(content);

        _audit.Record(
            action: isNew ? AuditActions.RuleCreate : AuditActions.RuleUpdate,
            summary: isNew
                ? $"新增規則 {rule.Id}（{rule.SourcePattern}／{rule.Category}／{rule.Severity}）"
                : $"修改規則 {rule.Id}：{rule.Description}",
            targetKind: "rule",
            targetId: rule.Id,
            detail: new { rule.SourcePattern, rule.EventIds, Category = rule.Category.ToString(), Severity = rule.Severity.ToString(), rule.CountThreshold });

        return GetRules().First(r => string.Equals(r.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
    }

    public void SetEnabled(string ruleId, bool enabled)
    {
        var content = LoadContent();
        var index = content.Rules.FindIndex(r => string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw DomainException.NotFound("找不到這條規則。");

        var rule = content.Rules[index];
        // stampModified: false——「已修改」徽章指的是**內容**被改過（決定程式改版時
        // 要不要跟進新種子），啟用/停用是獨立的營運狀態（--overwrite-builtin 本來就會保留它）。
        // 只停用就掛上「已修改」會讓人誤以為內容動過，該查的差異其實不存在。
        content.Rules[index] = CloneWith(rule, enabled: enabled, stampModified: false);
        _rules.Save(content);

        _audit.Record(
            action: enabled ? AuditActions.RuleEnable : AuditActions.RuleDisable,
            summary: $"{(enabled ? "啟用" : "停用")}規則 {ruleId}（{rule.Description}）" +
                     (enabled ? "" : "。停用只影響規則命中的分類與知識庫，趨勢層與關聯層對同一事件的偵測不受影響"),
            targetKind: "rule",
            targetId: ruleId);
    }

    public void DeleteRule(string ruleId)
    {
        var content = LoadContent();
        var rule = content.Rules.FirstOrDefault(r => string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase))
                   ?? throw DomainException.NotFound("找不到這條規則。");

        if (!string.Equals(rule.Origin, "custom", StringComparison.OrdinalIgnoreCase))
        {
            throw DomainException.Validation(
                $"「{ruleId}」是程式內建規則，不可刪除。若不需要它，請改為停用（可隨時恢復）。");
        }

        content.Rules.Remove(rule);
        _rules.Save(content);

        // 連同該規則的抑制設定一併清除，否則會留下指向不存在規則的孤兒設定
        var allSuppressions = _suppressions.LoadAll();
        var removedSuppressions = allSuppressions
            .RemoveAll(s => string.Equals(s.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
        if (removedSuppressions > 0) _suppressions.SaveAll(allSuppressions);

        _audit.Record(
            action: AuditActions.RuleDelete,
            summary: $"刪除自訂規則 {ruleId}（{rule.Description}）" +
                     (removedSuppressions > 0 ? $"，連同 {removedSuppressions} 筆抑制設定" : ""),
            targetKind: "rule",
            targetId: ruleId,
            detail: new { rule.SourcePattern, rule.EventIds, Category = rule.Category.ToString() });
    }

    public RuleRestorePreviewDto PreviewRestore(string ruleId)
    {
        var (current, seedRule) = LoadForRestore(ruleId);
        var content = LoadContent();
        var seeds = _seeds.GetAll().ToDictionary(s => s.RuleId, StringComparer.OrdinalIgnoreCase);
        var suppressions = _suppressions.LoadAll();

        return new RuleRestorePreviewDto
        {
            Current = ToDto(current, seeds, suppressions, content.SeedVersion),
            Seed = ToDto(seedRule, seeds, suppressions, content.SeedVersion),
            Differences = Diff(current, seedRule)
        };
    }

    public RuleDto RestoreSeed(string ruleId)
    {
        var (current, seedRule) = LoadForRestore(ruleId);

        var content = LoadContent();
        var index = content.Rules.FindIndex(r => string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase));

        // 回復內容但**保留使用者的 Enabled 設定**——回復內容不等於重新啟用，
        // 沿用 --overwrite-builtin 的既有語意（停用不會被悄悄打開）
        content.Rules[index] = CloneWith(seedRule, enabled: current.Enabled, clearModified: true);
        _rules.Save(content);

        _audit.Record(
            action: AuditActions.RuleRestoreSeed,
            summary: $"將規則 {ruleId} 回復為程式內建預設內容（保留目前的{(current.Enabled ? "啟用" : "停用")}狀態）",
            targetKind: "rule",
            targetId: ruleId,
            detail: new { Differences = Diff(current, seedRule) });

        return GetRules().First(r => string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase));
    }

    // ── 抑制設定 ─────────────────────────────────────────────────────────────

    public List<RuleSuppressionDto> GetSuppressions() =>
        _suppressions.LoadAll().Select(ToSuppressionDto).ToList();

    public void AddSuppression(string ruleId, AddSuppressionRequest request)
    {
        var content = LoadContent();
        if (!content.Rules.Any(r => string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase)))
            throw DomainException.NotFound("找不到這條規則。");

        if (string.IsNullOrWhiteSpace(request.Reason))
            throw DomainException.Validation("請說明抑制原因——沒有原因的抑制日後沒人知道能不能解除。");

        // ISuppressionStore 的介面是整份載入/寫回（見其註解），這裡沿用同一慣例做 upsert：
        // (RuleId, Host) 是天然的複合鍵，同一組覆寫而不是累積多筆
        var all = _suppressions.LoadAll();
        all.RemoveAll(s =>
            string.Equals(s.RuleId, ruleId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.Host, request.Host.Trim(), StringComparison.OrdinalIgnoreCase));

        all.Add(new RuleSuppression
        {
            RuleId = ruleId,
            Host = request.Host.Trim(),
            Reason = request.Reason.Trim(),
            SuppressedBy = _currentUser.Account,
            CreatedAt = DateTime.Now,
            ExpiresAt = request.Days.HasValue ? DateTime.Today.AddDays(request.Days.Value) : null
        });

        _suppressions.SaveAll(all);

        _audit.Record(
            action: AuditActions.SuppressAdd,
            summary: $"抑制規則 {ruleId} 於主機 {request.Host} 的告警" +
                     (request.Days.HasValue ? $"（{request.Days} 天後到期）" : "（永久，直到手動解除）") +
                     $"：{request.Reason}。抑制只關掉通知與風險升級，事件仍照常聚合與紀錄",
            targetKind: "rule",
            targetId: ruleId,
            detail: new { request.Host, request.Reason, request.Days });
    }

    public void RemoveSuppression(string ruleId, string host)
    {
        var all = _suppressions.LoadAll();
        var removed = all.RemoveAll(s =>
            string.Equals(s.RuleId, ruleId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));

        if (removed == 0) throw DomainException.NotFound("找不到這筆抑制設定。");

        _suppressions.SaveAll(all);

        _audit.Record(
            action: AuditActions.SuppressRemove,
            summary: $"解除規則 {ruleId} 於主機 {host} 的抑制，恢復告警",
            targetKind: "rule",
            targetId: ruleId);
    }

    // ── 內部 ─────────────────────────────────────────────────────────────────

    private RuleFileContent LoadContent()
    {
        var outcome = _rules.Load();
        if (!outcome.Success || outcome.Content == null)
            throw new InvalidOperationException($"規則庫載入失敗：{outcome.Error}");

        return outcome.Content;
    }

    private (KnownIssueRule Current, KnownIssueRule Seed) LoadForRestore(string ruleId)
    {
        var content = LoadContent();
        var current = content.Rules.FirstOrDefault(r => string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase))
                      ?? throw DomainException.NotFound("找不到這條規則。");

        if (!string.Equals(current.Origin, "builtin", StringComparison.OrdinalIgnoreCase))
            throw DomainException.Validation("自訂規則沒有原廠預設可回復。");

        var snapshot = _seeds.Get(ruleId)
                       ?? throw DomainException.NotFound(
                           "找不到這條規則的原廠備份。請先執行一次批次程式以同步內建種子。");

        var seedRule = JsonRuleSeedStore.Deserialize(snapshot)
                       ?? throw DomainException.Conflict("原廠備份內容損毀，無法回復。");

        return (current, seedRule);
    }

    private KnownIssueRule BuildRule(SaveRuleRequest request, KnownIssueRule? existing)
    {
        if (!Enum.TryParse<IssueCategory>(request.Category, ignoreCase: true, out var category))
            throw DomainException.Validation($"未知的類別「{request.Category}」。");

        if (!Enum.TryParse<IssueSeverity>(request.Severity, ignoreCase: true, out var severity))
            throw DomainException.Validation($"未知的嚴重度「{request.Severity}」。");

        return new KnownIssueRule
        {
            Id = request.Id.Trim(),
            // Origin 一經建立不可變更：它決定了這條規則會不會被 --import-rules 覆寫
            Origin = existing?.Origin ?? "custom",
            Enabled = request.Enabled,
            Scope = existing?.Scope ?? "all",
            MatchAllEventIds = request.MatchAllEventIds,
            MatchFilter = existing?.MatchFilter,
            SourcePattern = request.SourcePattern.Trim(),
            EventIds = request.MatchAllEventIds ? Array.Empty<int>() : request.EventIds.Distinct().ToArray(),
            Category = category,
            Severity = severity,
            Description = request.Description.Trim(),
            CountThreshold = request.CountThreshold,
            PlainExplanation = request.PlainExplanation.Trim(),
            Impact = request.Impact.Trim(),
            LikelyCauses = request.LikelyCauses.Where(c => !string.IsNullOrWhiteSpace(c)).ToArray(),
            NextSteps = request.NextSteps.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray(),
            ModifiedBy = _currentUser.UserId > 0 ? _currentUser.UserId : null,
            ModifiedAt = DateTime.Now
        };
    }

    private KnownIssueRule CloneWith(
        KnownIssueRule source, bool? enabled = null, bool stampModified = false, bool clearModified = false)
    {
        return new KnownIssueRule
        {
            Id = source.Id,
            Origin = source.Origin,
            Enabled = enabled ?? source.Enabled,
            Scope = source.Scope,
            MatchAllEventIds = source.MatchAllEventIds,
            MatchFilter = source.MatchFilter,
            SourcePattern = source.SourcePattern,
            EventIds = source.EventIds,
            Category = source.Category,
            Severity = source.Severity,
            Description = source.Description,
            CountThreshold = source.CountThreshold,
            PlainExplanation = source.PlainExplanation,
            Impact = source.Impact,
            LikelyCauses = source.LikelyCauses,
            NextSteps = source.NextSteps,
            ModifiedBy = clearModified ? null : (stampModified ? _currentUser.UserId : source.ModifiedBy),
            ModifiedAt = clearModified ? null : (stampModified ? DateTime.Now : source.ModifiedAt)
        };
    }

    private static List<RuleFieldDiffDto> Diff(KnownIssueRule current, KnownIssueRule seed)
    {
        var diffs = new List<RuleFieldDiffDto>();

        void Compare(string field, string currentValue, string seedValue)
        {
            if (currentValue != seedValue)
                diffs.Add(new RuleFieldDiffDto { Field = field, Current = currentValue, Seed = seedValue });
        }

        Compare("來源比對", current.SourcePattern, seed.SourcePattern);
        Compare("Event ID", string.Join(", ", current.EventIds), string.Join(", ", seed.EventIds));
        Compare("類別", current.Category.ToString(), seed.Category.ToString());
        Compare("嚴重度", current.Severity.ToString(), seed.Severity.ToString());
        Compare("說明", current.Description, seed.Description);
        Compare("次數門檻", current.CountThreshold.ToString(), seed.CountThreshold.ToString());
        Compare("白話說明", current.PlainExplanation, seed.PlainExplanation);
        Compare("影響", current.Impact, seed.Impact);
        Compare("常見原因", string.Join(" / ", current.LikelyCauses), string.Join(" / ", seed.LikelyCauses));
        Compare("處置步驟", string.Join(" / ", current.NextSteps), string.Join(" / ", seed.NextSteps));

        return diffs;
    }

    private RuleDto ToDto(
        KnownIssueRule rule,
        IReadOnlyDictionary<string, RuleSeedSnapshot> seeds,
        List<RuleSuppression> suppressions,
        int currentSeedVersion)
    {
        var isBuiltin = string.Equals(rule.Origin, "builtin", StringComparison.OrdinalIgnoreCase);
        seeds.TryGetValue(rule.Id, out var snapshot);

        var suppression = suppressions.FirstOrDefault(s =>
            string.Equals(s.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase));

        return new RuleDto
        {
            Id = rule.Id,
            Origin = rule.Origin,
            Enabled = rule.Enabled,
            SourcePattern = rule.SourcePattern,
            EventIds = rule.EventIds.ToList(),
            MatchAllEventIds = rule.MatchAllEventIds,
            Category = rule.Category.ToString(),
            Severity = rule.Severity.ToString(),
            Description = rule.Description,
            CountThreshold = rule.CountThreshold,
            PlainExplanation = rule.PlainExplanation,
            Impact = rule.Impact,
            LikelyCauses = rule.LikelyCauses.ToList(),
            NextSteps = rule.NextSteps.ToList(),
            IsModified = rule.ModifiedAt.HasValue,
            ModifiedByName = rule.ModifiedBy.HasValue ? _users.Get(rule.ModifiedBy.Value)?.DisplayName : null,
            ModifiedAt = rule.ModifiedAt,
            SeedHasNewerVersion = snapshot != null && snapshot.SeedVersion > currentSeedVersion,
            CanRestore = isBuiltin && snapshot != null,
            CanDelete = !isBuiltin,
            Suppression = suppression == null ? null : ToSuppressionDto(suppression)
        };
    }

    private static RuleSuppressionDto ToSuppressionDto(RuleSuppression suppression) => new()
    {
        RuleId = suppression.RuleId,
        Host = suppression.Host,
        Reason = suppression.Reason,
        ExpiresAt = suppression.ExpiresAt,
        IsExpired = suppression.ExpiresAt.HasValue && suppression.ExpiresAt.Value.Date < DateTime.Today
    };
}
