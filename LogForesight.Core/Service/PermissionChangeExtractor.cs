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
/// 支援行內多對與分區段（Subject、Member、Group、Object、Audit Policy 等）精確剖析，
/// 避免操作者帳號誤充當被異動成員。
/// </summary>
public static class PermissionChangeExtractor
{
    public const string DefaultNotProvided = "（訊息未提供）";
    public const string DefaultNotInGroup = "（不在群組中）";
    public const string DefaultRemovedFromGroup = "（已移出群組）";
    public const string DefaultNotGranted = "（未授與）";
    public const string DefaultRemoved = "（已移除）";

    private static readonly HashSet<string> KnownMultiWordKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Account Name",
        "Security ID",
        "Security Id",
        "Account Domain",
        "Logon ID",
        "Logon Id",
        "Group Name",
        "Member Name",
        "Object Name",
        "Object Server",
        "Object Type",
        "Handle ID",
        "Handle Id",
        "Process ID",
        "Process Id",
        "Process Name",
        "Process Information",
        "Target Account",
        "Target Domain",
        "Target Server",
        "Access Granted",
        "Access Removed",
        "Access Right",
        "Access Right(s)",
        "Access Rights",
        "Audit Policy Change",
        "Auditing Settings",
        "Permissions Change",
        "Original Security Descriptor",
        "New Security Descriptor",
        "Special Privileges",
        "Additional Information",
        "Access Request Information",
        "Detailed Authentication Information",
        "Account That Was Granted Access",
        "Account That Was Removed Access",
        "Account Modified",
        "安全性 ID"
    };

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
        string? initialInitiatorAccount = null)
    {
        var parsed = ParseMessage(message);

        // 1. 操作者帳號：優先取事件自帶的 InitiatorAccount（NetIQ sun 欄位），無值時取 Subject 區段帳戶名稱
        string? initiatorAccount = CleanValue(initialInitiatorAccount);
        if (initiatorAccount == null)
        {
            initiatorAccount = parsed.SubjectAccountName;
        }

        // 2. 目標資源名稱（群組/物件/目標帳戶，剖不出時為空字串）
        string target = parsed.GroupName ?? parsed.ObjectName ?? parsed.TargetAccount ?? string.Empty;

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

            var pairs = ExtractPairs(line);
            if (pairs.Count == 0)
            {
                if (TryGetSectionByName(line, out var headerSection))
                {
                    section = headerSection;
                }
                continue;
            }

            foreach (var (key, value) in pairs)
            {
                if (string.IsNullOrEmpty(value))
                {
                    if (TryGetSectionByName(key, out var newSection))
                    {
                        section = newSection;
                    }
                    continue;
                }

                if (IsKey(key, "Target Account", "目標帳戶", "被異動帳戶"))
                {
                    fields.TargetAccount ??= CleanValue(value);
                }
                else if (IsKey(key, "Member Name", "成員名稱"))
                {
                    fields.MemberAccountName ??= CleanValue(value);
                }
                else if (IsKey(key, "Group Name", "群組名稱"))
                {
                    fields.GroupName ??= CleanValue(value);
                }
                else if (IsKey(key, "Object Name", "物件名稱"))
                {
                    fields.ObjectName ??= CleanValue(value);
                }
                else if (IsKey(key, "Original Security Descriptor", "原始安全性描述元"))
                {
                    fields.OriginalSecDesc ??= CleanValue(value);
                }
                else if (IsKey(key, "New Security Descriptor", "新的安全性描述元"))
                {
                    fields.NewSecDesc ??= CleanValue(value);
                }
                else if (IsKey(key, "Access Granted", "授與的存取權"))
                {
                    fields.AccessGranted ??= CleanValue(value);
                }
                else if (IsKey(key, "Access Removed", "移除的存取權"))
                {
                    fields.AccessRemoved ??= CleanValue(value);
                }
                else if (IsKey(key, "Access Right", "Access Right(s)", "存取權", "存取權限", "Accesses", "存取"))
                {
                    fields.AccessRight ??= CleanValue(value);
                }
                else if (IsKey(key, "Category", "類別"))
                {
                    fields.AuditCategory ??= CleanValue(value);
                }
                else if (IsKey(key, "Subcategory", "子類別"))
                {
                    fields.AuditSubcategory ??= CleanValue(value);
                }
                else if (IsKey(key, "Changes", "變更"))
                {
                    fields.AuditChanges ??= CleanValue(value);
                }
                else if (IsKey(key, "Account Name", "帳戶名稱"))
                {
                    var clean = CleanValue(value);
                    if (clean != null)
                    {
                        if (section == Section.Subject && fields.SubjectAccountName == null)
                        {
                            fields.SubjectAccountName = clean;
                        }
                        else if (section == Section.Member && fields.MemberAccountName == null)
                        {
                            fields.MemberAccountName = clean;
                        }
                        else if (section == Section.Group && fields.GroupName == null)
                        {
                            fields.GroupName = clean;
                        }
                        else if (section == Section.TargetAccount && fields.TargetAccount == null)
                        {
                            fields.TargetAccount = clean;
                        }
                    }
                }
                else if (IsKey(key, "Security ID", "安全性識別碼", "安全性 ID"))
                {
                    var clean = CleanValue(value);
                    if (clean != null)
                    {
                        if (section == Section.Subject && fields.SubjectSecurityId == null)
                        {
                            fields.SubjectSecurityId = clean;
                        }
                        else if (section == Section.Member && fields.MemberSecurityId == null)
                        {
                            fields.MemberSecurityId = clean;
                        }
                        else if (section == Section.TargetAccount && fields.TargetSecurityId == null)
                        {
                            fields.TargetSecurityId = clean;
                        }
                    }
                }
            }
        }

        return fields;
    }

    private static bool IsKey(string key, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (key.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static List<(string Key, string Value)> ExtractPairs(string line)
    {
        var pairs = new List<(string Key, string Value)>();
        if (string.IsNullOrWhiteSpace(line)) return pairs;

        var keyOccurrences = new List<(int KeyStart, int ColonIndex, string KeyName)>();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c != ':' && c != '：') continue;

            int prevColon = keyOccurrences.Count > 0 ? keyOccurrences[^1].ColonIndex : -1;
            int minStart = Math.Max(prevColon + 1, i - 40);

            int selectedStart = -1;
            string? selectedKey = null;

            for (int start = minStart; start < i; start++)
            {
                if (start == 0 || char.IsWhiteSpace(line[start - 1]))
                {
                    var candidate = line[start..i].Trim();
                    if (IsValidKeyCandidate(candidate))
                    {
                        selectedStart = start;
                        selectedKey = candidate;
                        break;
                    }
                }
            }

            if (selectedStart >= 0 && selectedKey != null)
            {
                keyOccurrences.Add((selectedStart, i, selectedKey));
            }
        }

        for (int k = 0; k < keyOccurrences.Count; k++)
        {
            var current = keyOccurrences[k];
            int valStart = current.ColonIndex + 1;
            int valEnd = (k + 1 < keyOccurrences.Count) ? keyOccurrences[k + 1].KeyStart : line.Length;

            string value = string.Empty;
            if (valEnd > valStart)
            {
                value = line[valStart..valEnd].Trim();
            }

            pairs.Add((current.KeyName, value));
        }

        return pairs;
    }

    private static bool IsValidKeyCandidate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length < 2 || text.Length > 35) return false;

        bool hasLetter = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (char.IsLetter(ch))
            {
                hasLetter = true;
            }
            else if (!char.IsDigit(ch) && ch != ' ' && ch != '-' && ch != '(' && ch != ')' && ch != '_')
            {
                return false;
            }
        }

        if (!hasLetter) return false;

        if (text.Contains(' '))
        {
            return KnownMultiWordKeys.Contains(text) || TryGetSectionByName(text, out _);
        }

        return text.Length <= 20;
    }

    public static string? TryExtractValue(string line, params string[] prefixes)
    {
        var pairs = ExtractPairs(line);
        foreach (var prefix in prefixes)
        {
            var normalizedPrefix = prefix.TrimEnd(':', '：').Trim();
            foreach (var (k, v) in pairs)
            {
                if (k.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return CleanValue(v);
                }
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
            name.Equals("主體", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("主旨", StringComparison.OrdinalIgnoreCase))
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
            name.Equals("稽核原則", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("稽核原則已變更", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("稽核原則已變更。", StringComparison.OrdinalIgnoreCase))
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
            name.Equals("修改的帳戶", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Target Account", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("目標帳戶", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("被異動帳戶", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.TargetAccount;
            return true;
        }
        if (name.Equals("Process Information", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("程序資訊", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("處理程序", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("處理程序資訊", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Access Request Information", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("存取要求資訊", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Detailed Authentication Information", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("詳細驗證資訊", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("網路資訊", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("其他資訊", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Additional Information", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.Other;
            return true;
        }

        section = Section.None;
        return false;
    }
}
