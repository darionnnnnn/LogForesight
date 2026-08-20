using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>
/// 權限異動明細擷取結果。
/// </summary>
public readonly record struct PermissionExtractedDetails(
    string Target,
    string Before,
    string After,
    string? InitiatorAccount,
    string? TargetAccount);

/// <summary>
/// 權限異動事件訊息與告警文字的結構化擷取工具（共用純邏輯）。
/// 支援分區段（Subject、Member、Group、Object、Audit Policy 等）精確剖析，
/// 避免操作者帳號誤充當被異動成員。
/// </summary>
public static class PermissionChangeExtractor
{
    public const string DefaultNotProvided = "（訊息未提供）";
    public const string DefaultNotInGroup = "（不在群組中）";
    public const string DefaultRemovedFromGroup = "（已移出群組）";
    public const string DefaultNotGranted = "（未授與）";
    public const string DefaultRemoved = "（已移除）";

    private enum Section
    {
        None,
        Subject,
        Member,
        Group,
        Object,
        TargetAccount,
        AuditPolicyChange,
        AuditingSettings,
        PermissionsChange,
        Other
    }

    private sealed class ParsedFields
    {
        public string? SubjectAccountName { get; set; }
        public string? SubjectSecurityId { get; set; }
        public string? MemberAccountName { get; set; }
        public string? MemberSecurityId { get; set; }
        public string? GroupName { get; set; }
        public string? ObjectName { get; set; }
        public string? TargetAccount { get; set; }
        public string? TargetSecurityId { get; set; }
        public string? OriginalSecDesc { get; set; }
        public string? NewSecDesc { get; set; }
        public string? AuditCategory { get; set; }
        public string? AuditSubcategory { get; set; }
        public string? AuditChanges { get; set; }
        public string? AccessGranted { get; set; }
        public string? AccessRemoved { get; set; }
        public string? AccessRight { get; set; }
    }

    /// <summary>
    /// 從事件訊息與事件欄位擷取結構化權限異動明細。
    /// </summary>
    public static PermissionExtractedDetails Extract(
        string? message,
        string changeType,
        int eventId = 0,
        string? eventSource = null,
        string? initialInitiatorAccount = null)
    {
        var parsed = ParseMessage(message);

        // 1. 操作者帳號：優先取事件自帶的 InitiatorAccount（NetIQ sun 欄位），無值時取 Subject 區段帳戶名稱
        string? initiatorAccount = CleanValue(initialInitiatorAccount);
        if (initiatorAccount == null)
        {
            initiatorAccount = parsed.SubjectAccountName;
        }

        // 2. 目標資源名稱（群組/物件/目標帳戶/退路值）
        string? target = parsed.GroupName ?? parsed.ObjectName ?? parsed.TargetAccount;
        if (string.IsNullOrWhiteSpace(target))
        {
            target = string.IsNullOrWhiteSpace(eventSource)
                ? (eventId > 0 ? $"Event {eventId}" : string.Empty)
                : (eventId > 0 ? $"{eventSource} (EventId {eventId})" : eventSource);
        }

        // 3. 被異動目標帳號（群組成員或授權目標帳戶）
        string? targetAccount = null;
        if (changeType is "成員新增" or "成員移除")
        {
            targetAccount = parsed.MemberAccountName ?? parsed.MemberSecurityId;
        }
        else if (changeType == "稽核政策變更")
        {
            targetAccount = parsed.TargetAccount ?? parsed.TargetSecurityId;
        }

        // 4. 異動前後值
        string before = string.Empty;
        string after = string.Empty;

        if (changeType == "成員新增")
        {
            before = DefaultNotInGroup;
            after = parsed.MemberAccountName ?? parsed.MemberSecurityId ?? string.Empty;
        }
        else if (changeType == "成員移除")
        {
            before = parsed.MemberAccountName ?? parsed.MemberSecurityId ?? string.Empty;
            after = DefaultRemovedFromGroup;
        }
        else if (changeType == "權限變更")
        {
            before = parsed.OriginalSecDesc ?? string.Empty;
            after = parsed.NewSecDesc ?? string.Empty;
        }
        else if (changeType == "稽核政策變更")
        {
            if (eventId == 4717 || parsed.AccessGranted != null)
            {
                var right = parsed.AccessGranted ?? parsed.AccessRight;
                if (right != null)
                {
                    before = DefaultNotGranted;
                    after = right;
                }
                else
                {
                    before = DefaultNotProvided;
                    after = DefaultNotProvided;
                }
            }
            else if (eventId == 4718 || parsed.AccessRemoved != null)
            {
                var right = parsed.AccessRemoved ?? parsed.AccessRight;
                if (right != null)
                {
                    before = right;
                    after = DefaultRemoved;
                }
                else
                {
                    before = DefaultNotProvided;
                    after = DefaultNotProvided;
                }
            }
            else if (eventId == 4719)
            {
                string? policyDesc = null;
                if (parsed.AuditCategory != null && parsed.AuditSubcategory != null)
                {
                    policyDesc = $"{parsed.AuditCategory} - {parsed.AuditSubcategory}";
                }
                else
                {
                    policyDesc = parsed.AuditSubcategory ?? parsed.AuditCategory;
                }

                if (parsed.AuditChanges != null)
                {
                    before = DefaultNotProvided;
                    after = policyDesc != null ? $"{policyDesc}：{parsed.AuditChanges}" : parsed.AuditChanges;
                }
                else if (policyDesc != null)
                {
                    before = DefaultNotProvided;
                    after = policyDesc;
                }
                else
                {
                    before = DefaultNotProvided;
                    after = DefaultNotProvided;
                }
            }
            else if (eventId == 4907)
            {
                if (parsed.OriginalSecDesc != null || parsed.NewSecDesc != null)
                {
                    before = parsed.OriginalSecDesc ?? DefaultNotProvided;
                    after = parsed.NewSecDesc ?? DefaultNotProvided;
                }
                else if (parsed.AuditChanges != null)
                {
                    before = DefaultNotProvided;
                    after = parsed.AuditChanges;
                }
                else
                {
                    before = DefaultNotProvided;
                    after = DefaultNotProvided;
                }
            }
            else
            {
                if (parsed.OriginalSecDesc != null || parsed.NewSecDesc != null)
                {
                    before = parsed.OriginalSecDesc ?? DefaultNotProvided;
                    after = parsed.NewSecDesc ?? DefaultNotProvided;
                }
                else
                {
                    before = DefaultNotProvided;
                    after = DefaultNotProvided;
                }
            }
        }
        else
        {
            before = parsed.OriginalSecDesc ?? string.Empty;
            after = parsed.NewSecDesc ?? string.Empty;
        }

        return new PermissionExtractedDetails(target, before, after, initiatorAccount, targetAccount);
    }

    /// <summary>
    /// 供遷移器與舊資料相容使用的帳號擷取函式。
    /// 優先採用既有欄位值，未設定時從 Before/After 與 AlertText 中剖析。
    /// </summary>
    public static (string? InitiatorAccount, string? TargetAccount) ExtractAccounts(
        string? alertText,
        string? changeType,
        string? before,
        string? after,
        string? initialInitiatorAccount = null,
        string? initialTargetAccount = null)
    {
        string? initiator = CleanValue(initialInitiatorAccount);
        string? target = CleanValue(initialTargetAccount);

        // 1. TargetAccount 從 Before / After 提取
        if (target == null && !string.IsNullOrWhiteSpace(changeType))
        {
            if (changeType == "成員新增")
            {
                if (!string.IsNullOrWhiteSpace(after) &&
                    after != DefaultNotInGroup &&
                    after != DefaultRemovedFromGroup &&
                    after != DefaultNotProvided)
                {
                    target = CleanValue(after);
                }
            }
            else if (changeType == "成員移除")
            {
                if (!string.IsNullOrWhiteSpace(before) &&
                    before != DefaultNotInGroup &&
                    before != DefaultRemovedFromGroup &&
                    before != DefaultNotProvided)
                {
                    target = CleanValue(before);
                }
            }
            else if (changeType == "權限新增（ACL 規則）" && !string.IsNullOrWhiteSpace(after))
            {
                var parts = after.Split('｜');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    target = CleanValue(parts[0]);
                }
            }
            else if (changeType == "權限移除（ACL 規則）" && !string.IsNullOrWhiteSpace(before))
            {
                var parts = before.Split('｜');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    target = CleanValue(parts[0]);
                }
            }
        }

        // 2. 若仍有任一欄位為空，以分區段剖析器掃描 AlertText
        if ((initiator == null || target == null) && !string.IsNullOrWhiteSpace(alertText))
        {
            var parsed = ParseMessage(alertText);
            initiator ??= parsed.SubjectAccountName;

            if (target == null && (changeType is null or "成員新增" or "成員移除" or "稽核政策變更"))
            {
                target = parsed.MemberAccountName ?? parsed.TargetAccount ?? parsed.MemberSecurityId ?? parsed.TargetSecurityId;
            }
        }

        return (initiator, target);
    }

    private static ParsedFields ParseMessage(string? message)
    {
        var fields = new ParsedFields();
        if (string.IsNullOrWhiteSpace(message)) return fields;

        var lines = message.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var section = Section.None;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (TryMatchSectionHeader(line, out var newSection))
            {
                section = newSection;
                continue;
            }

            var targetVal = TryExtractValue(line,
                "Target Account:", "目標帳戶:", "目標帳戶：",
                "被異動帳戶:", "被異動帳戶：");
            if (targetVal != null && fields.TargetAccount == null)
            {
                fields.TargetAccount = targetVal;
            }

            var memberVal = TryExtractValue(line,
                "Member Name:", "成員名稱:", "成員名稱：");
            if (memberVal != null && fields.MemberAccountName == null)
            {
                fields.MemberAccountName = memberVal;
            }

            var groupVal = TryExtractValue(line,
                "Group Name:", "群組名稱:", "群組名稱：");
            if (groupVal != null && fields.GroupName == null)
            {
                fields.GroupName = groupVal;
            }

            var objectVal = TryExtractValue(line,
                "Object Name:", "物件名稱:", "物件名稱：");
            if (objectVal != null && fields.ObjectName == null)
            {
                fields.ObjectName = objectVal;
            }

            var origVal = TryExtractValue(line,
                "Original Security Descriptor:", "原始安全性描述元:", "原始安全性描述元：");
            if (origVal != null && fields.OriginalSecDesc == null)
            {
                fields.OriginalSecDesc = origVal;
            }

            var newVal = TryExtractValue(line,
                "New Security Descriptor:", "新的安全性描述元:", "新的安全性描述元：");
            if (newVal != null && fields.NewSecDesc == null)
            {
                fields.NewSecDesc = newVal;
            }

            var grantedVal = TryExtractValue(line,
                "Access Granted:", "授與的存取權:", "授與的存取權：");
            if (grantedVal != null && fields.AccessGranted == null)
            {
                fields.AccessGranted = grantedVal;
            }

            var removedVal = TryExtractValue(line,
                "Access Removed:", "移除的存取權:", "移除的存取權：");
            if (removedVal != null && fields.AccessRemoved == null)
            {
                fields.AccessRemoved = removedVal;
            }

            var rightVal = TryExtractValue(line,
                "Access Right:", "Access Right(s):", "存取權:", "存取權：",
                "存取權限:", "存取權限：", "Accesses:", "存取:", "存取：");
            if (rightVal != null && fields.AccessRight == null)
            {
                fields.AccessRight = rightVal;
            }

            var catVal = TryExtractValue(line,
                "Category:", "類別:", "類別：");
            if (catVal != null && fields.AuditCategory == null)
            {
                fields.AuditCategory = catVal;
            }

            var subCatVal = TryExtractValue(line,
                "Subcategory:", "子類別:", "子類別：");
            if (subCatVal != null && fields.AuditSubcategory == null)
            {
                fields.AuditSubcategory = subCatVal;
            }

            var changesVal = TryExtractValue(line,
                "Changes:", "變更:", "變更：");
            if (changesVal != null && fields.AuditChanges == null)
            {
                fields.AuditChanges = changesVal;
            }

            var acctNameVal = TryExtractValue(line,
                "Account Name:", "帳戶名稱:", "帳戶名稱：");
            if (acctNameVal != null)
            {
                if (section == Section.Subject && fields.SubjectAccountName == null)
                {
                    fields.SubjectAccountName = acctNameVal;
                }
                else if (section == Section.Member && fields.MemberAccountName == null)
                {
                    fields.MemberAccountName = acctNameVal;
                }
                else if (section == Section.TargetAccount && fields.TargetAccount == null)
                {
                    fields.TargetAccount = acctNameVal;
                }
            }

            var secIdVal = TryExtractValue(line,
                "Security ID:", "安全性識別碼:", "安全性識別碼：",
                "安全性 ID:", "安全性 ID：");
            if (secIdVal != null)
            {
                if (section == Section.Subject && fields.SubjectSecurityId == null)
                {
                    fields.SubjectSecurityId = secIdVal;
                }
                else if (section == Section.Member && fields.MemberSecurityId == null)
                {
                    fields.MemberSecurityId = secIdVal;
                }
                else if (section == Section.TargetAccount && fields.TargetSecurityId == null)
                {
                    fields.TargetSecurityId = secIdVal;
                }
            }
        }

        return fields;
    }

    public static string? TryExtractValue(string line, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            var idx = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            // 命中位置的前一個字元必須不是字母，否則是「較長欄名剛好包含較短欄名」的誤判：
            // 「Subcategory:」整個字串就含有「Category:」，沒有這道守衛的話子類別那一行
            // 會把類別的值覆寫掉，4719 的異動說明就會變成「子類別 - 子類別」。
            if (idx >= 0 && (idx == 0 || !char.IsLetter(line[idx - 1])))
            {
                var val = line[(idx + prefix.Length)..].Trim();
                return CleanValue(val);
            }
        }
        return null;
    }

    public static string? CleanValue(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        var trimmed = val.Trim();
        if (trimmed is "-" or "—" or "null" or "NULL") return null;
        return trimmed;
    }

    private static bool TryMatchSectionHeader(string line, out Section section)
    {
        section = Section.None;
        var colonIdx = line.IndexOfAny(new[] { ':', '：' });
        if (colonIdx < 0)
        {
            var trimmed = line.Trim();
            return TryGetSectionByName(trimmed, out section);
        }

        var afterColon = line[(colonIdx + 1)..].Trim();
        if (string.IsNullOrEmpty(afterColon))
        {
            var beforeColon = line[..colonIdx].Trim();
            return TryGetSectionByName(beforeColon, out section);
        }

        return false;
    }

    private static bool TryGetSectionByName(string name, out Section section)
    {
        if (name.Equals("Subject", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("主體", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.Subject;
            return true;
        }
        if (name.Equals("Member", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("成員", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.Member;
            return true;
        }
        if (name.Equals("Group", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("群組", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.Group;
            return true;
        }
        if (name.Equals("Object", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("物件", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.Object;
            return true;
        }
        if (name.Equals("Audit Policy Change", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("稽核原則變更", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("稽核原則", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.AuditPolicyChange;
            return true;
        }
        if (name.Equals("Auditing Settings", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("稽核設定", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.AuditingSettings;
            return true;
        }
        if (name.Equals("Permissions Change", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("權限變更", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.PermissionsChange;
            return true;
        }
        if (name.Equals("Account That Was Granted Access", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("已授與存取權的帳戶", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Account That Was Removed Access", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("已移除存取權的帳戶", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Account Modified", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("修改的帳戶", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.TargetAccount;
            return true;
        }
        if (name.Equals("Process Information", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("程序資訊", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Access Request Information", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("存取要求資訊", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Detailed Authentication Information", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("網路資訊", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.Other;
            return true;
        }

        section = Section.None;
        return false;
    }
}
