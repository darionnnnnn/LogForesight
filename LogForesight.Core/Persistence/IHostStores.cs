namespace LogForesight;

/// <summary>
/// 主機的讀寫（↔ lf_hosts ＋ lf_host_group_members ＋ lf_host_owners）。
///
/// **這是唯一由批次與 Web 共同寫入的 store**（docs/WEB-SPEC.md §10.2），
/// 因此職責切得很清楚：
/// - 批次只呼叫 <see cref="Touch"/>（登記主機存在、更新最近回報時間）
/// - Web 呼叫 <see cref="Upsert"/>／<see cref="SetGroups"/>／<see cref="SetOwners"/> 維護其餘欄位
///
/// 分開的理由：批次每晚執行，如果它走一般的 Upsert，就會用自己不知道的空值
/// 蓋掉 Web 維護的角色描述、群組與負責人。
/// </summary>
public interface IHostStore
{
    List<WebHost> GetAll();

    WebHost? Get(long hostId);

    /// <summary>依主機名稱查詢（不分大小寫）</summary>
    WebHost? FindByName(string hostName);

    /// <summary>依 HostName 自然鍵新增或更新（Web 維護用，會覆寫描述性欄位）</summary>
    WebHost Upsert(WebHost host);

    /// <summary>
    /// 批次專用：登記主機存在並更新最近回報時間。
    /// 主機不存在則建立（只填 HostName／Source／LastReportAt），存在則**只更新
    /// LastReportAt**，不動 Web 維護的任何欄位。
    /// </summary>
    WebHost Touch(string hostName, DateTime reportedAt, string source = "local");

    void SetGroups(long hostId, IEnumerable<long> groupIds);

    void SetOwners(long hostId, IEnumerable<long> userIds);

    /// <summary>
    /// 人工綁定：把 <paramref name="sourceHostId"/> 併入 <paramref name="targetHostId"/>。
    /// 來源主機標記 MergedInto＋停用（留墓碑，不刪除），綁錯時可反向修復。
    /// </summary>
    void Merge(long sourceHostId, long targetHostId);
}

/// <summary>主機群組的讀寫（↔ lf_host_groups）</summary>
public interface IHostGroupStore
{
    List<HostGroup> GetAll();

    HostGroup? Get(long groupId);

    HostGroup? FindByName(string groupName);

    HostGroup Upsert(HostGroup group);

    void Delete(long groupId);
}

/// <summary>
/// 授權對應的讀寫（↔ lf_group_access）。
/// 授權筆數少、查詢一律整份讀出後在記憶體比對，介面因此保持極簡。
/// </summary>
public interface IGroupAccessStore
{
    List<GroupAccess> GetAll();

    /// <summary>整組取代某使用者群組可存取的主機群組</summary>
    void SetForUserGroup(long userGroupId, IEnumerable<long> hostGroupIds);

    /// <summary>整份取代（CSV 匯入 group_access.csv 的全量取代語意）</summary>
    void ReplaceAll(IEnumerable<GroupAccess> accesses);
}
