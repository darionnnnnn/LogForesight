namespace LogForesight;

/// <summary>
/// 受監控的主機（↔ lf_hosts）。<see cref="HostName"/> 是自然鍵——
/// 批次分析寫入紀錄時以主機名稱識別，CSV 匯入也以它 upsert。
/// </summary>
public class WebHost
{
    public long HostId { get; set; }

    /// <summary>本機為 Environment.MachineName；NetIQ 來源為 Sentinel 主機名。比對不分大小寫</summary>
    public string HostName { get; set; } = string.Empty;

    /// <summary>
    /// 最近已知 IP。**純顯示用線索**，人在辨認新舊主機時最實用——
    /// 程式不拿它做任何比對（2026-07-21 定案：主機識別採純人工綁定）。
    /// </summary>
    public string? IpAddress { get; set; }

    public DateTime? IpUpdatedAt { get; set; }

    /// <summary>所屬 Sentinel 的名稱（路由/顯示屬性，非識別鍵；本機來源為 null）</summary>
    public string? NetiqServer { get; set; }

    /// <summary>伺服器角色描述，同一事件在 AD 網域控制站與檔案伺服器上的嚴重性不同</summary>
    public string RoleDesc { get; set; } = string.Empty;

    /// <summary>'local'（本機直讀）| 'netiq'（自 Sentinel 取得）</summary>
    public string Source { get; set; } = "local";

    public bool Active { get; set; } = true;

    /// <summary>
    /// 人工綁定新舊主機後的墓碑指標：這台已併入哪一台。
    /// 保留舊列而不刪除，歷史才追溯得到「這台曾經叫什麼」，綁錯也能反向修復。
    /// </summary>
    public long? MergedInto { get; set; }

    /// <summary>最近一次分析紀錄的寫入時間——儀表板「無回報主機」告警的依據</summary>
    public DateTime? LastReportAt { get; set; }

    /// <summary>所屬主機群組（↔ lf_host_group_members）</summary>
    public List<long> GroupIds { get; set; } = new();

    /// <summary>
    /// 負責人（↔ lf_host_owners，可多人）。
    /// **與授權是兩件事**：負責人不因此取得檢視權，他該透過部門群組拿到；
    /// 負責人的用途是處理人指派時的預設值與排序。
    /// </summary>
    public List<long> OwnerUserIds { get; set; } = new();
}

/// <summary>主機群組（↔ lf_host_groups）。維度不限於部門，也可以是 DMZ、DB 伺服器等分類</summary>
public class HostGroup
{
    public long GroupId { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public bool Active { get; set; } = true;
}

/// <summary>
/// 授權對應（↔ lf_group_access）：**使用者群組 → 主機群組**，多對多。
/// 一個主機群組可被多個使用者群組授權，一個使用者群組也可看多個主機群組。
/// </summary>
public class GroupAccess
{
    public long UserGroupId { get; set; }

    public long HostGroupId { get; set; }

    public DateTime GrantedAt { get; set; }
}
