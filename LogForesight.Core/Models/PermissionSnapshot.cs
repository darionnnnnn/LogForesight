namespace LogForesight.Core.Models;

internal class PermissionSnapshot
{
    public DateTime CapturedAt { get; set; }
    public List<string>? AdministratorsMembers { get; set; }
    public Dictionary<string, FolderAclSnapshot> Folders { get; set; } = new();
}

internal class FolderAclSnapshot
{
    public bool Accessible { get; set; }
    public string? Owner { get; set; }
    public List<string> Rules { get; set; } = new();
}
