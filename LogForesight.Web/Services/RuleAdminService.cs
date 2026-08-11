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
/// **儲存前一律跑 <see cref="RuleValidator"/>**——把規則驗證內建進儲存路徑，
/// 而不是指望使用者改完記得另外去驗證。驗證不過就拒絕寫入，rules.json 永遠是合格的。
/// </summary>
public class RuleAdminService
{
    private readonly IKnownIssueRuleStore _rules;
    private readonly IRuleSeedStore _seeds;
    private readonly ISuppressionStore _suppressions;
    private readonly IUserStore _users;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IHostGroupStore _hostGroups;
    private readonly IHostStore _hosts;
    private readonly IIssueAggregateQuery _issueAggregateQuery;

    /// <summary>抑制影響面預覽（回饋十四輪 C1）的比對窗口天數，與規則清單頁「近 N 日」等處的既有慣例一致</summary>
    private const int SuppressionPreviewWindowDays = 14;

    public RuleAdminService(
        IKnownIssueRuleStore rules,
        IRuleSeedStore seeds,
        ISuppressionStore suppressions,
        IUserStore users,
        ICurrentUser currentUser,
        IAuditService audit,
        IHostGroupStore hostGroups,
        IHostStore hosts,
        IIssueAggregateQuery issueAggregateQuery)
    {
        _rules = rules;
        _seeds = seeds;
        _suppressions = suppressions;
        _users = users;
        _currentUser = currentUser;
        _audit = audit;
        _hostGroups = hostGroups;
        _hosts = hosts;
        _issueAggregateQuery = issueAggregateQuery;
    }

    public List<RuleDto> GetRules()
    {
        var content = LoadContent();
        var seeds = _seeds.GetAll().ToDictionary(s => s.RuleId, StringComparer.OrdinalIgnoreCase);
        var suppressions = _suppressions.LoadAll();
        var groupNames = LoadGroupNames();

        return content.Rules.Select(rule => ToDto(rule, seeds, suppressions, content.SeedVersion, groupNames)).ToList();
    }

    /// <summary>群組 Id → 名稱，供抑制 DTO 帶出可讀名稱用。一次整份撈出避免每筆抑制各查一次 store。</summary>
    private Dictionary<long, string> LoadGroupNames() =>
        _hostGroups.GetAll().ToDictionary(g => g.GroupId, g => g.GroupName);

    /// <summary>內建規則升級橫幅（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.9）：庫內版本 &lt; 程式目前的種子版本時顯示</summary>
    public RuleImportStatusDto GetImportStatus()
    {
        var current = LoadContent().SeedVersion;
        var latest = KnownIssueSeed.Version;
        return new RuleImportStatusDto { CurrentSeedVersion = current, LatestSeedVersion = latest, HasUpdate = current < latest };
    }

    public RuleImportPreviewDto PreviewImport(bool overwriteBuiltin) =>
        ToImportPreviewDto(RuleImportPlanner.BuildPlan(LoadContent().Rules, KnownIssueSeed.CreateRules(), overwriteBuiltin));

    public RuleImportApplyResultDto ApplyImport(bool overwriteBuiltin)
    {
        var plan = RuleImportPlanner.BuildPlan(LoadContent().Rules, KnownIssueSeed.CreateRules(), overwriteBuiltin);
        if (plan.Added == 0 && plan.Updated == 0)
            throw DomainException.Validation("沒有需要套用的變更（可能已經是最新版本，或未修改的 builtin 規則需要勾選「連同已修改的內建規則一併覆蓋」）。");

        var validation = RuleImportPlanner.Apply(_rules, plan);

        _audit.Record(
            action: AuditActions.RuleSeedImport,
            summary: $"匯入內建規則種子更新至 v{KnownIssueSeed.Version}：新增 {plan.Added} 條、更新 {plan.Updated} 條" +
                     (overwriteBuiltin ? "（含覆蓋已修改的內建規則）" : ""),
            targetKind: "rule",
            detail: new { plan.Added, plan.Updated, OverwriteBuiltin = overwriteBuiltin, SeedVersion = KnownIssueSeed.Version });

        var warnings = validation.ShadowWarnings
            .Concat(validation.SkippedRules.Select(s => $"規則 {s.Rule.Id} 不合格：{s.Reason}（下次啟動時會被跳過，不影響其餘規則）"))
            .ToList();

        return new RuleImportApplyResultDto { Added = plan.Added, Updated = plan.Updated, Warnings = warnings };
    }

    private static RuleImportPreviewDto ToImportPreviewDto(RuleImportPlan plan) => new()
    {
        Added = plan.Added,
        Updated = plan.Updated,
        Skipped = plan.Skipped,
        Conflicts = plan.Conflicts,
        Items = plan.Items.Select(i => new RuleImportItemDto
        {
            Id = i.Id,
            Action = i.Action switch
            {
                RuleImportAction.Added => "added",
                RuleImportAction.UpdatedBuiltin => "updated",
                RuleImportAction.SkippedUnchanged => "skipped_unchanged",
                RuleImportAction.SkippedModifiedBuiltin => "skipped_modified",
                RuleImportAction.Conflict => "conflict",
                _ => "unknown"
            },
            ActionText = i.Action switch
            {
                RuleImportAction.Added => "新增",
                RuleImportAction.UpdatedBuiltin => "更新",
                RuleImportAction.SkippedUnchanged => "略過（未變更）",
                RuleImportAction.SkippedModifiedBuiltin => "略過（已修改）",
                RuleImportAction.Conflict => "衝突",
                _ => i.Action.ToString()
            },
            Detail = i.Detail
        }).ToList()
    };

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
            // 讓使用者能造出 builtin- 開頭的規則，日後內建規則升級比對時會產生無解的衝突
            throw DomainException.Validation("新增的規則 Id 必須以「custom-」開頭，以區別於程式內建規則。");
        }

        var rule = BuildRule(request, existing);

        // 儲存前驗證：把規則驗證內建進儲存路徑（§9.7）
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
        var groupNames = LoadGroupNames();

        return new RuleRestorePreviewDto
        {
            Current = ToDto(current, seeds, suppressions, content.SeedVersion, groupNames),
            Seed = ToDto(seedRule, seeds, suppressions, content.SeedVersion, groupNames),
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
    // 回饋十三輪 F：抑制粒度從「主機×規則」擴為 Host／Group／Site 三種範圍（體檢批 3 #13）——
    // 2000 台環境下同一條規則在同類主機上逐台設定的維護成本會讓人乾脆停用整條規則，
    // 反而失去分類與知識庫。實際生效範圍判定在 SuppressionFilter（Core 層純函數）。

    public List<RuleSuppressionDto> GetSuppressions()
    {
        var platformByRuleId = LoadContent().Rules
            .ToDictionary(r => r.Id, r => r.Platform, StringComparer.OrdinalIgnoreCase);
        var groupNames = LoadGroupNames();

        return _suppressions.LoadAll()
            .Select(s => ToSuppressionDto(s, platformByRuleId.GetValueOrDefault(s.RuleId, "windows"), groupNames))
            .ToList();
    }

    /// <summary>
    /// Group／Site 抑制的送出前影響面預覽（回饋十四輪 C1）：一鍵讓一條規則在大量主機上噤聲，
    /// 畫面上原本沒有任何規模提示——送出前先算出「會影響幾台主機、過去這條規則在這些主機上
    /// 命中過幾次」，讓維護者在確認前就看得到規模。Host 範圍本來就只影響單台主機，不需要
    /// 這道關卡（呼叫端不該送 Host，這裡直接拒絕以免誤用）。
    ///
    /// M 值走 <c>lf_top_issues</c>（<see cref="IIssueAggregateQuery"/>）而非
    /// <see cref="IRiskyEventStore"/>：前者的次數是精準加總（不受風險 log 暫存的每簽章
    /// 50／每主機日 500 筆上限與保留期限制），後者才是原始 log 佐證的來源。
    /// Windows 規則靠 SourcePattern＋EventIds 精準對應 <see cref="KnownIssueCatalog.FindRule"/>
    /// 同一套比對邏輯；Linux 規則的比對鍵（EventKey）沒有隨紀錄存進 lf_top_issues，
    /// 只能退而以 ProgramPattern 對 Source 做子字串比對，涵蓋面因此略寬於這條規則實際命中的
    /// 次數（同一 program 底下如果還有其他規則，會被一併算進來）——<see cref="SuppressionPreviewDto.ApproximateForLinux"/>
    /// 讓前端誠實標註這個數字是「同來源程式合計」而非精準值。
    /// </summary>
    public SuppressionPreviewDto PreviewSuppression(string ruleId, string scope, long? hostGroupId)
    {
        if (!SuppressionScopes.IsValid(scope))
            throw DomainException.Validation($"不合法的抑制範圍「{scope}」。");
        if (scope == SuppressionScopes.Host)
            throw DomainException.Validation("Host 範圍只影響單台主機，不需要影響面預覽。");

        var content = LoadContent();
        var rule = content.Rules.FirstOrDefault(r => string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase))
                   ?? throw DomainException.NotFound("找不到這條規則。");

        List<WebHost> targetHosts;
        if (scope == SuppressionScopes.Group)
        {
            if (!hostGroupId.HasValue)
                throw DomainException.Validation("請選擇要預覽的主機群組。");
            if (_hostGroups.Get(hostGroupId.Value) == null)
                throw DomainException.NotFound("找不到這個主機群組。");

            targetHosts = _hosts.GetAll()
                .Where(h => h.Active && h.MergedInto == null && h.GroupIds.Contains(hostGroupId.Value))
                .ToList();
        }
        else // Site：全站存活主機
        {
            targetHosts = _hosts.GetAll().Where(h => h.Active && h.MergedInto == null).ToList();
        }

        var hostIds = targetHosts.Select(h => h.HostId).ToList();
        var isLinux = string.Equals(rule.Platform, "linux", StringComparison.OrdinalIgnoreCase);
        var aggregates = _issueAggregateQuery.Aggregate(
            DateTime.Today.AddDays(-SuppressionPreviewWindowDays), DateTime.Today, hostIds);

        var hitCount = isLinux
            ? aggregates
                .Where(a => a.Source.Contains(rule.ProgramPattern, StringComparison.OrdinalIgnoreCase))
                .Sum(a => a.TotalCount)
            : aggregates
                .Where(a => a.Source.Contains(rule.SourcePattern, StringComparison.OrdinalIgnoreCase) &&
                            (rule.MatchAllEventIds || rule.EventIds.Contains(a.EventId)))
                .Sum(a => a.TotalCount);

        return new SuppressionPreviewDto
        {
            AffectedHostCount = hostIds.Count,
            RecentHitCount = hitCount,
            WindowDays = SuppressionPreviewWindowDays,
            ApproximateForLinux = isLinux
        };
    }

    /// <summary>既有進入點（POST /api/rules/{ruleId}/suppressions）：RuleId 來自路由，內部委派到
    /// 統一的 <see cref="AddSuppression(AddSuppressionRequest)"/>（回饋十五輪 A-6），行為與改版前逐位相同。</summary>
    public void AddSuppression(string ruleId, AddSuppressionRequest request)
    {
        request.TargetType = SuppressionTargetTypes.Rule;
        request.RuleId = ruleId;
        AddSuppression(request);
    }

    /// <summary>
    /// 統一的抑制建立入口（回饋十五輪 A-6）：四型（Rule／Signature／Correlation／Volume，見
    /// <see cref="SuppressionTargetTypes"/>）共用同一套 Scope 處理與到期語意，差別只在
    /// 「比對什麼」與各自的必填驗證——Rule 沿用既有規則存在檢查；Signature／Correlation 需要
    /// 呼叫端帶入人話標籤（伺服器端沒有現成的簽章/模式→人話對照表，前端當下手上有問題描述文字）；
    /// Volume 的標籤固定二選一，留空由伺服器補上。
    /// </summary>
    public void AddSuppression(AddSuppressionRequest request)
    {
        if (!SuppressionTargetTypes.IsValid(request.TargetType))
            throw DomainException.Validation($"不合法的抑制目標型別「{request.TargetType}」。");
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw DomainException.Validation("請說明抑制原因——沒有原因的抑制日後沒人知道能不能解除。");
        if (!SuppressionScopes.IsValid(request.Scope))
            throw DomainException.Validation($"不合法的抑制範圍「{request.Scope}」。");

        var ruleId = "";
        string? signatureKey = null;
        string? correlationPatternId = null;
        string? volumeKind = null;
        string? targetLabel = null;
        string? platform = null;

        switch (request.TargetType)
        {
            case SuppressionTargetTypes.Rule:
                if (string.IsNullOrWhiteSpace(request.RuleId))
                    throw DomainException.Validation("請指定要抑制的規則。");
                var content = LoadContent();
                var rule = content.Rules.FirstOrDefault(r => string.Equals(r.Id, request.RuleId, StringComparison.OrdinalIgnoreCase))
                           ?? throw DomainException.NotFound("找不到這條規則。");
                ruleId = request.RuleId.Trim();
                platform = rule.Platform;
                break;

            case SuppressionTargetTypes.Signature:
                if (string.IsNullOrWhiteSpace(request.SignatureKey))
                    throw DomainException.Validation("缺少要抑制的問題簽章。");
                if (string.IsNullOrWhiteSpace(request.TargetLabel))
                    throw DomainException.Validation("缺少抑制目標的顯示名稱。");
                signatureKey = request.SignatureKey.Trim();
                targetLabel = request.TargetLabel.Trim();
                platform = string.IsNullOrWhiteSpace(request.Platform) ? WebHost.OsWindows : request.Platform;
                break;

            case SuppressionTargetTypes.Correlation:
                if (string.IsNullOrWhiteSpace(request.CorrelationPatternId) || !CorrelationPatternIds.IsValid(request.CorrelationPatternId))
                    throw DomainException.Validation("不合法的關聯模式。");
                if (string.IsNullOrWhiteSpace(request.TargetLabel))
                    throw DomainException.Validation("缺少抑制目標的顯示名稱。");
                correlationPatternId = request.CorrelationPatternId;
                targetLabel = request.TargetLabel.Trim();
                // Linux 模式 Id 統一 "linux-" 前綴（見 CorrelationPatternIds），其餘皆 Windows——
                // 平台只影響清單頁篩選，比對本身不看這個欄位
                platform = correlationPatternId.StartsWith("linux-", StringComparison.OrdinalIgnoreCase)
                    ? WebHost.OsLinux : WebHost.OsWindows;
                break;

            default: // Volume
                if (string.IsNullOrWhiteSpace(request.VolumeKind) || !VolumeKinds.IsValid(request.VolumeKind))
                    throw DomainException.Validation("不合法的總量類別。");
                volumeKind = request.VolumeKind;
                targetLabel = string.IsNullOrWhiteSpace(request.TargetLabel)
                    ? (request.VolumeKind == VolumeKinds.Audit ? "安全稽核事件量突增" : "整體錯誤量突增")
                    : request.TargetLabel.Trim();
                break;
        }

        var host = "";
        long? hostGroupId = null;
        string scopeText;

        switch (request.Scope)
        {
            case SuppressionScopes.Host:
                if (string.IsNullOrWhiteSpace(request.Host))
                    throw DomainException.Validation("請選擇要抑制的主機。");
                host = request.Host.Trim();
                scopeText = $"主機 {host}";
                break;

            case SuppressionScopes.Group:
                if (!request.HostGroupId.HasValue)
                    throw DomainException.Validation("請選擇要抑制的主機群組。");
                var group = _hostGroups.Get(request.HostGroupId.Value)
                            ?? throw DomainException.NotFound("找不到這個主機群組。");
                hostGroupId = group.GroupId;
                scopeText = $"主機群組「{group.GroupName}」";
                break;

            default: // Site：不需要額外目標，全站只可能有一筆
                scopeText = "全站";
                break;
        }

        // ISuppressionStore 的介面是整份載入/寫回（見其註解），這裡沿用同一慣例做 upsert：
        // (TargetType, 目標欄位, Scope, 範圍目標) 是天然的複合鍵，同一組覆寫而不是累積多筆——
        // Site 範圍沒有「目標」可比對，同目標同 Scope=Site 已經是唯一鍵。
        var all = _suppressions.LoadAll();
        all.RemoveAll(s =>
            s.TargetType == request.TargetType &&
            TargetMatches(s, request.TargetType, ruleId, signatureKey, correlationPatternId, volumeKind) &&
            string.Equals(s.Scope, request.Scope, StringComparison.OrdinalIgnoreCase) &&
            (request.Scope switch
            {
                SuppressionScopes.Host => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase),
                SuppressionScopes.Group => s.HostGroupId == hostGroupId,
                _ => true
            }));

        all.Add(new RuleSuppression
        {
            TargetType = request.TargetType,
            RuleId = ruleId,
            SignatureKey = signatureKey,
            CorrelationPatternId = correlationPatternId,
            VolumeKind = volumeKind,
            TargetLabel = targetLabel,
            Platform = platform,
            Scope = request.Scope,
            Host = host,
            HostGroupId = hostGroupId,
            Reason = request.Reason.Trim(),
            SuppressedBy = _currentUser.Account,
            CreatedAt = DateTime.Now,
            ExpiresAt = request.Days.HasValue ? DateTime.Today.AddDays(request.Days.Value) : null
        });

        _suppressions.SaveAll(all);

        var targetDescription = request.TargetType switch
        {
            SuppressionTargetTypes.Rule => $"規則 {ruleId}",
            SuppressionTargetTypes.Signature => $"問題簽章「{targetLabel}」",
            SuppressionTargetTypes.Correlation => $"關聯模式「{targetLabel}」",
            _ => $"總量告警「{targetLabel}」"
        };
        _audit.Record(
            action: AuditActions.SuppressAdd,
            summary: $"抑制{targetDescription}於{scopeText}的告警" +
                     (request.Days.HasValue ? $"（{request.Days} 天後到期）" : "（永久，直到手動解除）") +
                     $"：{request.Reason}。抑制只關掉通知與風險升級，事件仍照常聚合與紀錄",
            targetKind: request.TargetType.ToLowerInvariant(),
            targetId: ruleId.Length > 0 ? ruleId : (signatureKey ?? correlationPatternId ?? volumeKind ?? ""),
            detail: new { request.TargetType, ruleId, signatureKey, correlationPatternId, volumeKind, request.Scope, host, hostGroupId, request.Reason, request.Days });
    }

    /// <summary>既有進入點（DELETE /api/rules/{ruleId}/suppressions）：內部委派到統一的
    /// <see cref="RemoveSuppression(string,string?,string?,string?,string?,string,string?,long?)"/>，
    /// 行為與改版前逐位相同。</summary>
    public void RemoveSuppression(string ruleId, string scope, string? host, long? hostGroupId) =>
        RemoveSuppression(SuppressionTargetTypes.Rule, ruleId, null, null, null, scope, host, hostGroupId);

    /// <summary>統一的抑制解除入口（回饋十五輪 A-6）：四型共用，目標欄位依 targetType 只認對應的一個。</summary>
    public void RemoveSuppression(string targetType, string? ruleId, string? signatureKey,
        string? correlationPatternId, string? volumeKind, string scope, string? host, long? hostGroupId)
    {
        if (!SuppressionTargetTypes.IsValid(targetType))
            throw DomainException.Validation($"不合法的抑制目標型別「{targetType}」。");
        if (!SuppressionScopes.IsValid(scope))
            throw DomainException.Validation($"不合法的抑制範圍「{scope}」。");

        var all = _suppressions.LoadAll();
        var removed = all.RemoveAll(s =>
            s.TargetType == targetType &&
            TargetMatches(s, targetType, ruleId ?? "", signatureKey, correlationPatternId, volumeKind) &&
            string.Equals(s.Scope, scope, StringComparison.OrdinalIgnoreCase) &&
            (scope switch
            {
                SuppressionScopes.Host => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase),
                SuppressionScopes.Group => s.HostGroupId == hostGroupId,
                _ => true
            }));

        if (removed == 0) throw DomainException.NotFound("找不到這筆抑制設定。");

        _suppressions.SaveAll(all);

        var scopeText = scope switch
        {
            SuppressionScopes.Host => $"主機 {host}",
            SuppressionScopes.Group => $"主機群組（Id={hostGroupId}）",
            _ => "全站"
        };
        var targetDescription = targetType switch
        {
            SuppressionTargetTypes.Rule => $"規則 {ruleId}",
            SuppressionTargetTypes.Signature => "問題簽章",
            SuppressionTargetTypes.Correlation => "關聯模式",
            _ => "總量告警"
        };
        _audit.Record(
            action: AuditActions.SuppressRemove,
            summary: $"解除{targetDescription}於{scopeText}的抑制，恢復告警",
            targetKind: targetType.ToLowerInvariant(),
            targetId: ruleId ?? signatureKey ?? correlationPatternId ?? volumeKind ?? "");
    }

    /// <summary>抑制項目的目標欄位是否對得上——四型各自比對不同欄位，Add 的 upsert 去重與
    /// Remove 的定位共用同一套判斷，避免兩處各寫一份日後漂移。</summary>
    private static bool TargetMatches(RuleSuppression s, string targetType, string ruleId,
        string? signatureKey, string? correlationPatternId, string? volumeKind) => targetType switch
    {
        SuppressionTargetTypes.Rule => string.Equals(s.RuleId, ruleId, StringComparison.OrdinalIgnoreCase),
        SuppressionTargetTypes.Signature => string.Equals(s.SignatureKey, signatureKey, StringComparison.OrdinalIgnoreCase),
        SuppressionTargetTypes.Correlation => string.Equals(s.CorrelationPatternId, correlationPatternId, StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(s.VolumeKind, volumeKind, StringComparison.OrdinalIgnoreCase)
    };

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

        var seedRule = RuleSeedStore.Deserialize(snapshot)
                       ?? throw DomainException.Conflict("原廠備份內容損毀，無法回復。");

        return (current, seedRule);
    }

    private KnownIssueRule BuildRule(SaveRuleRequest request, KnownIssueRule? existing)
    {
        if (!Enum.TryParse<IssueCategory>(request.Category, ignoreCase: true, out var category))
            throw DomainException.Validation($"未知的類別「{request.Category}」。");

        if (!Enum.TryParse<IssueSeverity>(request.Severity, ignoreCase: true, out var severity))
            throw DomainException.Validation($"未知的嚴重度「{request.Severity}」。");

        // Platform 一經建立不可變更（哪個分頁新增就是哪個平台）：既有規則沿用原值，
        // 新規則採前端所在分頁送來的值，二者都不看使用者能否事後改動。
        var platform = existing?.Platform ?? (request.Platform == "linux" ? "linux" : "windows");

        return new KnownIssueRule
        {
            Id = request.Id.Trim(),
            // Origin 一經建立不可變更：它決定了這條規則會不會被內建規則升級覆寫
            Origin = existing?.Origin ?? "custom",
            Enabled = request.Enabled,
            Scope = existing?.Scope ?? "all",
            Platform = platform,
            MatchAllEventIds = platform == "windows" && request.MatchAllEventIds,
            MatchFilter = existing?.MatchFilter,
            SourcePattern = platform == "windows" ? request.SourcePattern.Trim() : string.Empty,
            EventIds = platform == "windows" && !request.MatchAllEventIds
                ? request.EventIds.Distinct().ToArray() : Array.Empty<int>(),
            ProgramPattern = platform == "linux" ? request.ProgramPattern.Trim() : string.Empty,
            EventNamePattern = platform == "linux" ? request.EventNamePattern.Trim() : string.Empty,
            MessagePatterns = platform == "linux"
                ? request.MessagePatterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray()
                : Array.Empty<string>(),
            Category = category,
            Severity = severity,
            ElevatesDayRisk = request.ElevatesDayRisk,
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
            Platform = source.Platform,
            MatchAllEventIds = source.MatchAllEventIds,
            MatchFilter = source.MatchFilter,
            SourcePattern = source.SourcePattern,
            EventIds = source.EventIds,
            ProgramPattern = source.ProgramPattern,
            EventNamePattern = source.EventNamePattern,
            MessagePatterns = source.MessagePatterns,
            Category = source.Category,
            Severity = source.Severity,
            ElevatesDayRisk = source.ElevatesDayRisk,
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
        Compare("Program 比對", current.ProgramPattern, seed.ProgramPattern);
        Compare("事件名比對", current.EventNamePattern, seed.EventNamePattern);
        Compare("訊息子字串", string.Join(" / ", current.MessagePatterns), string.Join(" / ", seed.MessagePatterns));
        Compare("類別", current.Category.ToString(), seed.Category.ToString());
        Compare("嚴重度", current.Severity.ToString(), seed.Severity.ToString());
        Compare("命中即列為高風險日", current.ElevatesDayRisk.ToString(), seed.ElevatesDayRisk.ToString());
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
        int currentSeedVersion,
        IReadOnlyDictionary<long, string> groupNames)
    {
        var isBuiltin = string.Equals(rule.Origin, "builtin", StringComparison.OrdinalIgnoreCase);
        seeds.TryGetValue(rule.Id, out var snapshot);

        // 規則清單頁沒有「檢視中的主機」脈絡，這裡沿用既有簡化：同一規則若有多筆抑制
        // （例如同時有 Host 級與 Group 級），只取第一筆代表性顯示徽章；完整清單見「告警抑制」分頁的 GetSuppressions()。
        // TargetType==Rule 是刻意明寫的（回饋十五輪 A）：非 Rule 目標的 RuleId 恆為空字串，
        // 與任何真實規則 Id 都不會相等，本來就不會誤配對——這裡明寫只是不依賴這個巧合。
        var suppression = suppressions.FirstOrDefault(s =>
            s.TargetType == SuppressionTargetTypes.Rule && string.Equals(s.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase));

        return new RuleDto
        {
            Id = rule.Id,
            Origin = rule.Origin,
            Enabled = rule.Enabled,
            Platform = rule.Platform,
            SourcePattern = rule.SourcePattern,
            EventIds = rule.EventIds.ToList(),
            MatchAllEventIds = rule.MatchAllEventIds,
            ProgramPattern = rule.ProgramPattern,
            EventNamePattern = rule.EventNamePattern,
            MessagePatterns = rule.MessagePatterns.ToList(),
            Category = rule.Category.ToString(),
            Severity = rule.Severity.ToString(),
            ElevatesDayRisk = rule.ElevatesDayRisk,
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
            Suppression = suppression == null ? null : ToSuppressionDto(suppression, rule.Platform, groupNames)
        };
    }

    private static RuleSuppressionDto ToSuppressionDto(
        RuleSuppression suppression, string fallbackRulePlatform, IReadOnlyDictionary<long, string> groupNames) => new()
    {
        RuleId = suppression.RuleId,
        TargetType = suppression.TargetType,
        SignatureKey = suppression.SignatureKey,
        CorrelationPatternId = suppression.CorrelationPatternId,
        VolumeKind = suppression.VolumeKind,
        TargetLabel = suppression.TargetLabel,
        Scope = suppression.Scope,
        Host = suppression.Host,
        HostGroupId = suppression.HostGroupId,
        HostGroupName = suppression.HostGroupId.HasValue
            ? groupNames.GetValueOrDefault(suppression.HostGroupId.Value, "（群組已刪除）")
            : null,
        Reason = suppression.Reason,
        ExpiresAt = suppression.ExpiresAt,
        IsExpired = suppression.ExpiresAt.HasValue && suppression.ExpiresAt.Value.Date < DateTime.Today,
        // Rule 目標由呼叫端傳入規則反查到的平台；其餘三型的平台在建立時就記錄在 suppression 本身
        // （見 AddSuppression），這裡直接讀，不需要（也讀不到）規則表反查
        Platform = suppression.TargetType == SuppressionTargetTypes.Rule ? fallbackRulePlatform : suppression.Platform
    };
}
