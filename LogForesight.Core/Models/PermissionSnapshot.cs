namespace LogForesight.Core.Models;

internal class PermissionSnapshot
{
    public DateTime CapturedAt { get; set; }
    public List<string>? AdministratorsMembers { get; set; }

    /// <summary>
    /// 成員的 SID → 顯示名稱。身分以 SID 比對：名稱（SamAccountName）分不出本機 bob 與網域 bob，
    /// 換成同名的另一個帳號不會被偵測。舊版快照沒有這一欄（null），第一次比對以名稱過渡。
    /// </summary>
    public Dictionary<string, string>? AdministratorsMemberSids { get; set; }
    public Dictionary<string, FolderAclSnapshot> Folders { get; set; } = new();
}

internal class FolderAclSnapshot
{
    public bool Accessible { get; set; }
    public string? Owner { get; set; }
    public List<string> Rules { get; set; } = new();
}
