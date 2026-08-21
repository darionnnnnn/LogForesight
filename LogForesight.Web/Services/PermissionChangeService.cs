using System.Text.RegularExpressions;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 權限異動待辦（docs/WEB-SPEC.md §9.5）。
///
/// 這是把 README 的「被異動項目明細（人工防護層）」搬上 Web 的自然延伸：
/// 自動檢查負責「發現有異動」，逐筆確認讓了解環境的人判斷「這筆是否正常」——
/// 同一筆 ACL 新增，管理員自己設定的就是正常維運，非預期出現的就是入侵訊號。
/// </summary>
public class PermissionChangeService
{
    private readonly PermissionChangeStore _store;
    private readonly IHostStore _hosts;
    private readonly IVisibilityService _visibility;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUserStore _users;

    public PermissionChangeService(
        PermissionChangeStore store,
        IHostStore hosts,
        IVisibilityService visibility,
        ICurrentUser currentUser,
        IAuditService audit,
        IUserStore users)
    {
        _store = store;
        _hosts = hosts;
        _visibility = visibility;
        _currentUser = currentUser;
        _audit = audit;
        _users = users;
    }

    /// <summary>組裝查詢條件（供查詢端點與批次核准端點共用）</summary>
    public PermissionChangeQueryFilter BuildFilter(PermissionChangeQueryRequest request)
    {
        var (page, pageSize) = Paging.Normalize(request.Page, request.PageSize);

        CidrRange? cidr = null;
        if (!string.IsNullOrWhiteSpace(request.Subnet))
        {
            cidr = CidrMatcher.Parse(request.Subnet)
                   ?? throw DomainException.Validation(
                       $"「{request.Subnet}」不是有效的網段格式（可用 192.168.1.0/24、192.168.1.* 或 192.168.1.100）。");
        }

        IReadOnlyCollection<string>? hostNames;
        if (_currentUser.Has(Capability.ViewAll))
        {
            if (cidr != null)
            {
                hostNames = _hosts.GetAll()
                    .Where(h => CidrMatcher.Matches(cidr, ResolveHostIp(h, h.HostName)))
                    .Select(h => h.HostName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                hostNames = null; // 可見範圍為全體時不下推主機名單（不限）
            }
        }
        else
        {
            var visibleHosts = _visibility.GetVisibleHosts();
            if (cidr != null)
            {
                hostNames = visibleHosts
                    .Where(h => CidrMatcher.Matches(cidr, ResolveHostIp(h, h.HostName)))
                    .Select(h => h.HostName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                hostNames = visibleHosts
                    .Select(h => h.HostName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        return new PermissionChangeQueryFilter
        {
            HostNames = hostNames,
            Keyword = request.Keyword,
            Categories = request.Categories,
            Status = request.Status,
            Source = request.Source,
            From = request.From,
            To = request.To,
            Sort = request.Sort,
            Ascending = request.Ascending,
            Page = page,
            PageSize = pageSize
        };
    }

    public PagedResult<PermissionChangeDto> Query(PermissionChangeQueryRequest request)
    {
        var filter = BuildFilter(request);
        var pagedRecords = _store.Query(filter);
        if (pagedRecords.Items.Count == 0)
        {
            return new PagedResult<PermissionChangeDto>
            {
                Items = new List<PermissionChangeDto>(),
                Page = pagedRecords.Page,
                PageSize = pagedRecords.PageSize,
                Total = pagedRecords.Total
            };
        }

        var confirmations = _store.GetConfirmations(pagedRecords.Items.Select(c => c.ChangeId))
            .ToDictionary(c => c.ChangeId, StringComparer.OrdinalIgnoreCase);

        var hostsByName = _hosts.GetAll().ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);

        var items = pagedRecords.Items.Select(change =>
        {
            confirmations.TryGetValue(change.ChangeId, out var confirmation);
            hostsByName.TryGetValue(change.HostName, out var host);

            var confirmedByDisplayName = !string.IsNullOrEmpty(confirmation?.ConfirmedByAccount)
                ? _users.FindByAccount(confirmation.ConfirmedByAccount)?.DisplayName
                : null;

            return MapToDto(change, confirmation, host, confirmedByDisplayName);
        }).ToList();

        return new PagedResult<PermissionChangeDto>
        {
            Items = items,
            Page = pagedRecords.Page,
            PageSize = pagedRecords.PageSize,
            Total = pagedRecords.Total
        };
    }

    // 全選上限刻意與批次確認上限相同：兩者不一致的話，使用者會選到一批「選得起來、送不出去」
    // 的項目，而且畫面上沒有「取消掉多出來那些」的路徑，等於卡死。
    public const int MaxSelectAllChanges = MaxBatchConfirmChanges;
    public const int MaxBatchConfirmChanges = 500;

    /// <summary>
    /// 「全選符合目前篩選的權限異動」ID 清單。
    /// 與清單查詢共用 BuildFilter 條件組裝，只回傳 pending 狀態的項目。
    /// </summary>
    public PermissionChangeIdListDto MatchingChangeIds(PermissionChangeQueryRequest request)
    {
        var filter = BuildFilter(request);
        filter.Status = PermissionConfirmStatuses.Pending;

        var (ids, total) = _store.QueryIds(filter, MaxSelectAllChanges);
        return new PermissionChangeIdListDto
        {
            ChangeIds = ids,
            Total = total,
            Truncated = total > MaxSelectAllChanges
        };
    }

    public PermissionChangeDto Confirm(string changeId, ConfirmPermissionChangeRequest request)
    {
        if (!PermissionConfirmStatuses.IsValid(request.Status) ||
            request.Status == PermissionConfirmStatuses.Pending)
        {
            throw DomainException.Validation("確認結果只能是「授權操作」或「可疑」。");
        }

        var change = _store.Get(changeId)
                     ?? throw DomainException.NotFound("找不到這筆權限異動紀錄。");

        // 授權範圍檢查：權限異動屬於主機的資料，同樣受可見範圍限制
        var host = _hosts.FindByName(change.HostName);
        if (host == null || !_visibility.GetVisibleHostIds().Contains(host.HostId))
            throw DomainException.NotFound("找不到這筆權限異動紀錄。");

        // 標記可疑必須說明原因：那是要交給別人接手調查的訊號，
        // 沒有說明的「可疑」對後續處理的人毫無幫助
        if (request.Status == PermissionConfirmStatuses.Suspicious &&
            string.IsNullOrWhiteSpace(request.Note))
        {
            throw DomainException.Validation("標記為可疑時請填寫說明，供後續調查參考。");
        }

        var confirmation = new PermissionChangeConfirmation
        {
            ChangeId = changeId,
            Status = request.Status,
            ConfirmedBy = _currentUser.UserId > 0 ? _currentUser.UserId : null,
            ConfirmedByAccount = _currentUser.Account,
            ConfirmedAt = DateTime.Now,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()
        };

        if (!_store.SaveConfirmation(confirmation))
        {
            throw DomainException.Conflict("這筆權限異動已被其他使用者處理過，請重新整理頁面。");
        }

        _audit.Record(
            action: request.Status == PermissionConfirmStatuses.Authorized
                ? AuditActions.PermConfirmAuthorized
                : AuditActions.PermConfirmSuspicious,
            // Target 剖不出時是空字串，直接串會留一個沒有下文的「／」
            summary: request.Status == PermissionConfirmStatuses.Authorized
                ? $"確認 {change.HostName} 的權限異動為授權操作：{AuditChangeDesc(change)}"
                : $"將 {change.HostName} 的權限異動標記為可疑：{AuditChangeDesc(change)}",
            targetKind: "permission_change",
            targetId: changeId,
            detail: new { change.Target, change.ChangeType, change.Before, change.After, confirmation.Note });

        var savedConfirmation = _store.GetConfirmations(new[] { changeId }).FirstOrDefault() ?? confirmation;
        var confirmedByDisplayName = !string.IsNullOrEmpty(savedConfirmation.ConfirmedByAccount)
            ? _users.FindByAccount(savedConfirmation.ConfirmedByAccount)?.DisplayName
            : null;

        return MapToDto(change, savedConfirmation, host, confirmedByDisplayName);
    }

    /// <summary>
    /// 批次確認權限異動（授權操作或標記可疑）。
    /// 逐筆判斷可見範圍與是否已被處理，支援條件式原子更新。
    /// </summary>
    public BatchConfirmPermissionChangesResultDto ConfirmBatch(BatchConfirmPermissionChangesRequest request)
    {
        if (request.ChangeIds == null || request.ChangeIds.Count == 0 || request.ChangeIds.All(string.IsNullOrWhiteSpace))
        {
            throw DomainException.Validation("請至少勾選一筆權限異動。");
        }

        if (request.ChangeIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() > MaxBatchConfirmChanges)
        {
            throw DomainException.Validation($"單次批次確認上限為 {MaxBatchConfirmChanges} 筆，請分批操作。");
        }

        var requestedIds = request.ChangeIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (requestedIds.Count == 0)
        {
            throw DomainException.Validation("請至少勾選一筆權限異動。");
        }

        if (requestedIds.Count > MaxBatchConfirmChanges)
        {
            throw DomainException.Validation($"單次批次確認上限為 {MaxBatchConfirmChanges} 筆，請分批操作。");
        }

        if (!PermissionConfirmStatuses.IsValid(request.Status) ||
            request.Status == PermissionConfirmStatuses.Pending)
        {
            throw DomainException.Validation("確認結果只能是「授權操作」或「可疑」。");
        }

        if (request.Status == PermissionConfirmStatuses.Suspicious &&
            string.IsNullOrWhiteSpace(request.Note))
        {
            throw DomainException.Validation("標記為可疑時請填寫說明，供後續調查參考。");
        }

        var records = _store.GetByChangeIds(requestedIds);
        var recordsById = records.ToDictionary(r => r.ChangeId, StringComparer.OrdinalIgnoreCase);

        var hasViewAll = _currentUser.Has(Capability.ViewAll);
        var visibleHostIds = hasViewAll ? null : _visibility.GetVisibleHostIds().ToHashSet();
        var hostsByName = _hosts.GetAll().ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);

        var candidateIds = new List<string>();
        var skipped = new List<SkippedPermissionChangeDto>();

        foreach (var id in requestedIds)
        {
            if (!recordsById.TryGetValue(id, out var record))
            {
                skipped.Add(new SkippedPermissionChangeDto
                {
                    ChangeId = id,
                    HostName = string.Empty,
                    Reason = "找不到這筆權限異動紀錄。"
                });
                continue;
            }

            var isVisible = hasViewAll ||
                (hostsByName.TryGetValue(record.HostName, out var host) && visibleHostIds!.Contains(host.HostId));

            if (!isVisible)
            {
                // 授權範圍保護：不可見主機的異動不得洩漏其存在或主機名稱
                skipped.Add(new SkippedPermissionChangeDto
                {
                    ChangeId = id,
                    HostName = string.Empty,
                    Reason = "找不到這筆權限異動紀錄。"
                });
                continue;
            }

            candidateIds.Add(id);
        }

        var updatedRecords = new List<PermissionChangeRecord>();
        var updatedCount = 0;
        var occurredAt = DateTime.Now;
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var confirmedBy = _currentUser.UserId > 0 ? _currentUser.UserId : (long?)null;
        var confirmedByAccount = _currentUser.Account;

        if (candidateIds.Count > 0)
        {
            // 以資料庫實際更新的列數為權威值。下面的逐筆分類是用「重讀後比對確認時間戳」
            // 推出來的，只用於組略過清單的訊息；若哪天時間戳的往返精度改變（例如欄位型別
            // 從 datetime2 降精度），分類會全數落空——那時若連 UpdatedCount 與稽核都跟著它走，
            // 就會變成「資料已經全部更新、稽核卻一筆都沒寫」，那比報錯更糟。
            updatedCount = _store.SaveConfirmations(
                candidateIds,
                request.Status,
                confirmedBy,
                confirmedByAccount,
                occurredAt,
                note);

            var confirmations = _store.GetConfirmations(candidateIds)
                .ToDictionary(c => c.ChangeId, StringComparer.OrdinalIgnoreCase);

            foreach (var id in candidateIds)
            {
                var record = recordsById[id];
                if (confirmations.TryGetValue(id, out var conf) &&
                    conf.Status == request.Status &&
                    conf.ConfirmedAt == occurredAt &&
                    string.Equals(conf.ConfirmedByAccount, confirmedByAccount, StringComparison.OrdinalIgnoreCase))
                {
                    updatedRecords.Add(record);
                }
                else
                {
                    skipped.Add(new SkippedPermissionChangeDto
                    {
                        ChangeId = id,
                        HostName = record.HostName,
                        Reason = "這筆權限異動已被其他使用者處理過"
                    });
                }
            }
        }

        if (updatedCount > 0)
        {
            var statusLabel = request.Status == PermissionConfirmStatuses.Authorized ? "授權操作" : "可疑";
            _audit.Record(
                action: AuditActions.PermConfirmBatch,
                summary: $"批次確認 {updatedCount} 筆權限異動為{statusLabel}",
                targetKind: "permission_change",
                targetId: "batch",
                detail: new
                {
                    Status = request.Status,
                    Count = updatedCount,
                    ChangeIds = updatedRecords.Select(r => r.ChangeId).ToList(),
                    HostNames = updatedRecords.Select(r => r.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Categories = updatedRecords.Select(r => r.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Note = note,
                    Skipped = skipped.Select(s => new { s.ChangeId, s.HostName, s.Reason }).ToList()
                });
        }

        return new BatchConfirmPermissionChangesResultDto
        {
            UpdatedCount = updatedCount,
            Skipped = skipped
        };
    }

    public int CountPending()
    {
        var hostNames = _currentUser.Has(Capability.ViewAll) ? null : VisibleHostNames();
        return _store.CountPending(hostNames);
    }

    /// <summary>處理程序的檔名（完整路徑對句子太長，svchost.exe 才是有資訊量的那一段）</summary>
    private static string? ProcessFileName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        var trimmed = processName.Trim();
        var idx = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        var name = idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>稽核摘要用的「異動類型／對象」，任一段為空就不留分隔符</summary>
    private static string AuditChangeDesc(PermissionChangeRecord change) =>
        string.Join("／", new[] { change.ChangeType, change.Target }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>舊資料的對象欄可能被寫成事件來源名而非真正的異動對象，兩種形狀：
    /// 「來源名 (EventId 4756)」與無來源時的「Event 4756」。只認形狀不認特定來源名。</summary>
    private static readonly Regex BadTargetRegex =
        new(@"(\(EventId\s+(?<id>\d+)\)$|^Event\s+(?<id>\d+)$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="eventId">本列的 EventId。舊退路值裡的數字必然等於它——比對數字才不會把
    /// 真的叫「Event 5」的群組或路徑誤判成壞資料。</param>
    private static string ResolveTarget(string? rawTarget, string fallback, int? eventId)
    {
        if (string.IsNullOrWhiteSpace(rawTarget))
            return fallback;

        var trimmed = rawTarget.Trim();
        var bad = BadTargetRegex.Match(trimmed);
        if (bad.Success && eventId is > 0 &&
            int.TryParse(bad.Groups["id"].Value, out var idInTarget) && idInTarget == eventId)
        {
            return fallback;
        }

        return trimmed;
    }

    /// <summary>把句子的兩段接起來：中文與英數之間留半形空格是刻意的排版，但全形標點
    /// （降級佔位字「（未能解析…）」的前後括號）兩側再加空格會在句子中間開一個洞。</summary>
    private static string Sp(string left, string right)
    {
        if (left.Length == 0) return right;
        if (right.Length == 0) return left;
        var noSpace = IsFullWidth(left[^1]) || IsFullWidth(right[0]);
        return noSpace ? left + right : $"{left} {right}";

        static bool IsFullWidth(char c) => c is '（' or '）' or '、' or '，' or '。';
    }

    /// <summary>把句子的各段依 <see cref="Sp"/> 的規則接起來，空段自動略過。
    /// 句子一律用它組——直接把空格寫進插值字串的話，遇到降級佔位字就會在句中留下孤兒空格。</summary>
    private static string Sentence(params string?[] parts)
    {
        var result = string.Empty;
        foreach (var part in parts)
        {
            if (!string.IsNullOrEmpty(part)) result = Sp(result, part);
        }
        return result;
    }

    private static PermissionChangeDto MapToDto(
        PermissionChangeRecord change,
        PermissionChangeConfirmation? confirmation,
        WebHost? host,
        string? confirmedByDisplayName)
    {
        var category = string.IsNullOrWhiteSpace(change.Category) ? PermissionCategory.Other : change.Category;
        return new PermissionChangeDto
        {
            ChangeId = change.ChangeId,
            HostId = host?.HostId ?? 0,
            HostName = change.HostName,
            HostIp = ResolveHostIp(host, change.HostName),
            DetectedAt = change.DetectedAt,
            Target = change.Target,
            ChangeType = change.ChangeType,
            Category = category,
            CategoryLabel = PermissionCategory.GetLabel(category),
            IsPrivilegedTarget = change.IsPrivilegedTarget,
            InitiatorAccount = change.InitiatorAccount,
            InitiatorAccountDisplay = AccountDisplayFormatter.ToShortName(change.InitiatorAccount),
            TargetAccount = change.TargetAccount,
            TargetAccountDisplay = AccountDisplayFormatter.ToShortName(change.TargetAccount),
            // 舊的彙總列 EventId 存 0（沒有對應的單一事件），前端只擋 null 會顯示「0」
            EventId = change.EventId == 0 ? null : change.EventId,
            Before = change.Before,
            After = change.After,
            AlertText = change.AlertText,
            SummaryText = GenerateSummaryText(change),
            ObjectType = change.ObjectType,
            ProcessName = change.ProcessName,
            CoveredFrom = change.CoveredFrom,
            CoveredTo = change.CoveredTo,
            PairCount = change.PairCount,
            Source = string.IsNullOrWhiteSpace(change.Source) ? PermissionChangeSources.Local : change.Source,
            Status = confirmation?.Status ?? PermissionConfirmStatuses.Pending,
            ConfirmedByAccount = confirmation?.ConfirmedByAccount,
            ConfirmedByDisplayName = confirmedByDisplayName,
            ConfirmedAt = confirmation?.ConfirmedAt,
            ConfirmNote = confirmation?.Note
        };
    }

    /// <summary>產生異動摘要說明（後端統一組裝，避免前後端規則漂移）</summary>
    public static string GenerateSummaryText(PermissionChangeRecord change)
    {
        var category = string.IsNullOrWhiteSpace(change.Category) ? PermissionCategory.Other : change.Category;
        var categoryLabel = PermissionCategory.GetLabel(category);
        var initiator = AccountDisplayFormatter.ToShortName(change.InitiatorAccount);
        var hasInitiator = !string.IsNullOrWhiteSpace(initiator);

        switch (category)
        {
            case PermissionCategory.GroupMember:
            {
                var target = ResolveTarget(change.Target, "（未能解析群組名稱）", change.EventId);
                var rawMember = change.ChangeType == "成員移除"
                    ? (!string.IsNullOrWhiteSpace(change.Before) ? change.Before : (!string.IsNullOrWhiteSpace(change.TargetAccount) ? change.TargetAccount : change.After))
                    : (!string.IsNullOrWhiteSpace(change.After) ? change.After : (!string.IsNullOrWhiteSpace(change.TargetAccount) ? change.TargetAccount : change.Before));

                var member = AccountDisplayFormatter.ToShortName(rawMember);
                if (string.IsNullOrWhiteSpace(member))
                    member = "（未能解析成員）";

                // 本機監控的對象字面已是「本機 Administrators 群組」，再加前綴會變成
                // 「加入群組 本機 Administrators 群組」；降級佔位字（全形括號起頭）仍要前綴
                var group = target.Contains("群組") && !target.StartsWith('（')
                    ? target
                    : Sp("群組", target);

                if (change.ChangeType == "成員新增")
                {
                    return hasInitiator
                        ? Sentence(initiator, "將", member, "加入" + group)
                        : Sentence(member, "被加入" + group);
                }

                if (change.ChangeType == "成員移除")
                {
                    return hasInitiator
                        ? Sentence(initiator, "將", member, "移出" + group)
                        : Sentence(member, "被移出" + group);
                }

                // 理論上不可達（group_member 只由成員新增／移除推導出）；保守給一個成句的退路
                return hasInitiator
                    ? Sentence(initiator, "變更" + group, "的成員")
                    : Sentence(group, "成員變更");
            }

            case PermissionCategory.FolderAcl:
            {
                var target = ResolveTarget(change.Target, "（未能解析路徑）", change.EventId);

                if (change.ChangeType == "權限新增（ACL 規則）")
                {
                    return hasInitiator
                        ? Sentence(initiator, "於", target, "新增權限規則")
                        : Sentence(target, "的權限規則被新增");
                }

                if (change.ChangeType == "權限移除（ACL 規則）")
                {
                    return hasInitiator
                        ? Sentence(initiator, "於", target, "移除權限規則")
                        : Sentence(target, "的權限規則被移除");
                }

                return hasInitiator
                    ? Sentence(initiator, "變更", target, "的權限")
                    : Sentence(target, "的權限被變更");
            }

            case PermissionCategory.ObjectAcl:
            {
                // 4670 的非檔案物件（Token、登錄機碼…）：物件名稱常是「-」，此時用物件類型
                // 與處理程序名稱組句——「（未能解析路徑）」對 Token 物件是錯的語意
                var objectType = string.IsNullOrWhiteSpace(change.ObjectType) ? null : change.ObjectType.Trim();
                var process = ProcessFileName(change.ProcessName);
                var objectName = string.IsNullOrWhiteSpace(change.Target) ? null : change.Target.Trim();

                var what = objectName != null
                    ? (objectType != null ? Sp(objectName, $"（{objectType}）") : objectName)
                    : (objectType != null ? $"{objectType} 物件" : "物件");
                if (process != null) what = Sp(what, $"（{process}）");

                return hasInitiator
                    ? Sentence(initiator, "變更", what, "的權限")
                    : Sentence(what, "的權限被變更");
            }

            case PermissionCategory.OwnerChange:
            {
                var target = ResolveTarget(change.Target, "（未能解析路徑）", change.EventId);
                var beforeOwner = AccountDisplayFormatter.ToShortName(change.Before);
                var afterOwner = AccountDisplayFormatter.ToShortName(change.After);

                if (!string.IsNullOrWhiteSpace(beforeOwner) && !string.IsNullOrWhiteSpace(afterOwner))
                {
                    return hasInitiator
                        ? Sentence(initiator, "將", target, "的擁有者由", beforeOwner, "變更為", afterOwner)
                        : Sentence(target, "的擁有者由", beforeOwner, "變更為", afterOwner);
                }

                if (!string.IsNullOrWhiteSpace(afterOwner))
                {
                    return hasInitiator
                        ? Sentence(initiator, "將", target, "的擁有者變更為", afterOwner)
                        : Sentence(target, "的擁有者變更為", afterOwner);
                }

                return hasInitiator
                    ? Sentence(initiator, "變更", target, "的擁有者")
                    : Sentence(target, "的擁有者被變更");
            }

            case PermissionCategory.FolderAccess:
            {
                var target = ResolveTarget(change.Target, "（未能解析路徑）", change.EventId);
                // 本機監控來源沒有操作者，這裡不組操作者前綴；異動類型本身就是動詞
                // （「無法存取」「恢復可存取」），類型缺漏時給一個不會變成孤立路徑的退路
                return Sentence(target,
                    !string.IsNullOrWhiteSpace(change.ChangeType) ? change.ChangeType : "存取狀態變更");
            }

            case PermissionCategory.AuditPolicy:
            {
                var target = ResolveTarget(change.Target, "（未能解析對象）", change.EventId);
                return hasInitiator
                    ? Sentence(initiator, "變更", target, "的稽核政策")
                    : Sentence(target, "的稽核政策被變更");
            }

            case PermissionCategory.Summary:
            {
                // 彙總列的對象是主機名（不是 Extract 出來的異動對象），不套壞形狀辨識。
                // 句首**不重複類別標籤**：列表已有「類別」欄，重複的六個字只是把說明擠掉
                var target = string.IsNullOrWhiteSpace(change.Target) ? "（未指定對象）" : change.Target.Trim();
                return Sentence(target, change.AlertText);
            }

            default:
            {
                // 對象是群組或路徑，不是帳號——不套帳號短名規則
                var target = ResolveTarget(change.Target, "（未指定對象）", change.EventId);

                var detail = !string.IsNullOrWhiteSpace(change.AlertText) ? change.AlertText : change.ChangeType;
                return Sentence($"{categoryLabel}：{target}", detail);
            }
        }
    }

    private static string? ResolveHostIp(WebHost? host, string hostName)
    {
        if (!string.IsNullOrWhiteSpace(host?.IpAddress))
            return host.IpAddress;

        if (!string.IsNullOrWhiteSpace(hostName) && System.Net.IPAddress.TryParse(hostName.Trim(), out _))
            return hostName.Trim();

        return null;
    }

    private List<string> VisibleHostNames() =>
        _visibility.GetVisibleHosts().Select(h => h.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
