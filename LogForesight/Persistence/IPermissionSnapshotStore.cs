namespace LogForesight;

/// <summary>權限/角色異動監控的快照存取，與分析紀錄分開（不同的生命週期與存取模式：這裡只需要「最新一份」）</summary>
public interface IPermissionSnapshotStore
{
    /// <summary>無快照（首次執行）回傳 null</summary>
    PermissionSnapshot? Load();

    void Save(PermissionSnapshot snapshot);
}
