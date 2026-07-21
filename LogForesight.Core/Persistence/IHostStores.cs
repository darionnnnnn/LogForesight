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

    /// <summary>
    /// 批次專用（NetIQ 來源）：更新最近回報時間與 Sentinel 回報的顯示名稱。
    ///
    /// 與 <see cref="Touch"/> 分開的兩個理由：NetIQ 主機由 Web 清單維護、**必定已經存在**，
    /// 所以以 HostId 定位而不是用名稱建立（用名稱建立會在 admin 打錯字時默默多出一台幽靈主機）；
    /// 以及它多回填一個 <see cref="WebHost.DisplayName"/>。同樣不動任何 Web 維護欄位。
    ///
    /// 主機不存在時回 null 並安靜略過——清單項目剛被刪除的競態，不該讓當晚分析中斷。
    /// </summary>
    WebHost? TouchNetiq(long hostId, string? displayName, DateTime reportedAt);

    void SetGroups(long hostId, IEnumerable<long> groupIds);

    void SetOwners(long hostId, IEnumerable<long> userIds);

    /// <summary>
    /// 人工綁定：把 <paramref name="sourceHostId"/> 併入 <paramref name="targetHostId"/>。
    /// 來源主機標記 MergedInto＋停用（留墓碑，不刪除），綁錯時可用 <see cref="Unmerge"/> 修復。
    ///
    /// **描述性欄位搬移**：目標的角色描述／群組／負責人／顯示名／IP／Sentinel 若是空的，
    /// 自來源帶入；目標已有值則保留目標的，不覆蓋。沒有這一步的話，把「CSV 預先登錄、
    /// 已設好群組與負責人的那一列」併入「NetIQ 剛回報、什麼都還沒設的那一列」，
    /// 結果會是群組全掉——而且要等到有人發現「怎麼大家都看不到這台主機了」才知道。
    ///
    /// 搬移是**複製不是移動**：來源保留自己的值，所以 <see cref="Unmerge"/> 能完整還原來源；
    /// 目標則保留當時帶入的值（解除後若不合適，由人工再調整）。
    /// </summary>
    void Merge(long sourceHostId, long targetHostId);

    /// <summary>
    /// 解除人工綁定：清除墓碑標記並恢復啟用。綁錯時的反向修復路徑——
    /// 「留墓碑不刪除」的設計要能真的救得回來，才不只是一句安慰。
    /// </summary>
    void Unmerge(long hostId);
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
