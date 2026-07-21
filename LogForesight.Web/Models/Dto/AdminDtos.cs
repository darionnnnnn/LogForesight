using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

public class UserDto
{
    public long UserId { get; set; }
    public string Account { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool Active { get; set; }
    public List<long> GroupIds { get; set; } = new();

    /// <summary>群組名稱（清單畫面直接顯示，前端不必自己 join）</summary>
    public List<string> GroupNames { get; set; } = new();
}

public class SaveUserRequest
{
    [Required(ErrorMessage = "請輸入帳號")]
    [StringLength(255, ErrorMessage = "帳號長度不可超過 255 字元")]
    public string Account { get; set; } = string.Empty;

    [StringLength(255)]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(255)]
    public string? Email { get; set; }

    public bool Active { get; set; } = true;
}

public class SetUserGroupsRequest
{
    public List<long> GroupIds { get; set; } = new();
}

public class UserGroupDto
{
    public long GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool Builtin { get; set; }
    public bool Active { get; set; }
    public int MemberCount { get; set; }
}

public class SaveUserGroupRequest
{
    public long GroupId { get; set; }

    [Required(ErrorMessage = "請輸入群組名稱")]
    [StringLength(100, ErrorMessage = "群組名稱長度不可超過 100 字元")]
    public string GroupName { get; set; } = string.Empty;

    /// <summary>User | Dev | Manager | Admin。builtin 群組不可變更</summary>
    public string Role { get; set; } = "User";

    public bool Active { get; set; } = true;
}

public class HostGroupDto
{
    public long GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public bool Active { get; set; }
    public int HostCount { get; set; }
}

public class SaveHostGroupRequest
{
    public long GroupId { get; set; }

    [Required(ErrorMessage = "請輸入群組名稱")]
    [StringLength(100, ErrorMessage = "群組名稱長度不可超過 100 字元")]
    public string GroupName { get; set; } = string.Empty;

    public bool Active { get; set; } = true;
}

/// <summary>授權矩陣：列＝使用者群組、欄＝主機群組</summary>
public class AccessMatrixDto
{
    public List<AccessMatrixRowDto> UserGroups { get; set; } = new();
    public List<HostGroupDto> HostGroups { get; set; } = new();
}

public class AccessMatrixRowDto
{
    public long UserGroupId { get; set; }
    public string UserGroupName { get; set; } = string.Empty;
    public bool Active { get; set; }
    public List<long> GrantedHostGroupIds { get; set; } = new();
}

public class SetAccessRequest
{
    public List<long> HostGroupIds { get; set; } = new();
}

public class HostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? NetiqServer { get; set; }
    public string RoleDesc { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool Active { get; set; }
    public long? MergedInto { get; set; }
    public DateTime? LastReportAt { get; set; }
    public List<long> GroupIds { get; set; } = new();
    public List<string> GroupNames { get; set; } = new();
    public List<long> OwnerUserIds { get; set; } = new();
    public List<string> OwnerNames { get; set; } = new();
}

public class SaveHostRequest
{
    [Required(ErrorMessage = "請輸入主機名稱")]
    [StringLength(255, ErrorMessage = "主機名稱長度不可超過 255 字元")]
    public string HostName { get; set; } = string.Empty;

    [StringLength(45)]
    public string? IpAddress { get; set; }

    [StringLength(50)]
    public string? NetiqServer { get; set; }

    [StringLength(500)]
    public string? RoleDesc { get; set; }

    public bool Active { get; set; } = true;
}

public class SetIdsRequest
{
    public List<long> Ids { get; set; } = new();
}

public class MergeHostRequest
{
    public long SourceHostId { get; set; }
    public long TargetHostId { get; set; }
}

/// <summary>主篩選列的主機選單項目（已依授權過濾）</summary>
public class VisibleHostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string RoleDesc { get; set; } = string.Empty;
    public DateTime? LastReportAt { get; set; }
}
