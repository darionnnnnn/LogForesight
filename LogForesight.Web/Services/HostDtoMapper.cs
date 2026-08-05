using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// WebHost → HostDto 的欄位對映（純函式）。原本 HostAdminService 與 NetiqHostService
/// 各自寫一份逐字相同的版本，收斂到這裡；兩邊各自取得 groupsById／usersById 的策略不動
/// （NetiqHostService 目前每次呼叫都重查 store，屬既有效能議題，不在此次一併處理）。
/// </summary>
internal static class HostDtoMapper
{
    public static HostDto ToDto(
        WebHost host,
        IReadOnlyDictionary<long, HostGroup> groupsById,
        IReadOnlyDictionary<long, WebUser> usersById) => new()
    {
        HostId = host.HostId,
        HostName = host.HostName,
        DisplayName = host.DisplayName,
        IpAddress = host.IpAddress,
        NetiqServer = host.NetiqServer,
        RoleDesc = host.RoleDesc,
        Source = host.Source,
        Os = host.Os,
        Active = host.Active,
        MergedInto = host.MergedInto,
        LastReportAt = host.LastReportAt,
        CreatedAt = host.CreatedAt,
        GroupIds = host.GroupIds,
        GroupNames = NameFormat.ResolveNames(host.GroupIds, groupsById, g => g.GroupName),
        OwnerUserIds = host.OwnerUserIds,
        // §9：負責人欄顯示「顯示名稱(帳號)」。此清單為顯示用途（主機頁 badges），
        // 與 HandlingDto.OwnerNames（另兼作指派下拉的置頂比對鍵）刻意不同源，故可在此組帳號
        OwnerNames = NameFormat.ResolveNames(host.OwnerUserIds, usersById, u => NameFormat.WithAccount(u.DisplayName, u.Account))
    };
}
