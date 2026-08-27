
namespace LogForesight.Core.Persistence;

/// <summary><see cref="IUserStore"/> 的實作（blob key=users，整份型）</summary>
public class UserStore : JsonBlobCollection<WebUser>, IUserStore
{
    public UserStore(EfJsonBlobStore blob) : base(blob) { }

    public List<WebUser> GetAll() => Read();

    // 單筆查找走不複製的快照（回饋三十四輪 A5 同型）：FindByAccount 在權限異動清單的
    // 逐列迴圈裡被呼叫，每列複製整份使用者清單純粹是 GC 壓力
    public WebUser? Get(long userId) => ReadSnapshot().FirstOrDefault(u => u.UserId == userId);

    public WebUser? FindByAccount(string account) =>
        ReadSnapshot().FirstOrDefault(u => string.Equals(u.Account, account, StringComparison.OrdinalIgnoreCase));

    public WebUser Upsert(WebUser user)
    {
        return Mutate(users =>
        {
            var existing = users.FirstOrDefault(u =>
                string.Equals(u.Account, user.Account, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                user.UserId = NextId(users.Select(u => u.UserId));
                users.Add(user);
                return user;
            }

            // 逐欄複製：**新增模型欄位時這裡要跟著加**（漏抄的症狀是「新增存得進去、
            // 編輯被靜默還原」，BlobStoreRoundTripTests 守住這件事）。
            //
            // LastLoginAt 刻意不覆寫：唯一寫入點是 TouchLogin。Upsert 的呼叫端
            // （admin 編輯、負責人匯入）建的是不帶登入時間的物件，照抄會把真實登入時間
            // 清成 null，讓使用者清單的「最近登入」每編輯一次就歸零。
            existing.Account = user.Account;
            existing.DisplayName = user.DisplayName;
            existing.Email = user.Email;
            existing.Active = user.Active;
            existing.GroupIds = user.GroupIds;
            return existing;
        });
    }

    public void SetGroups(long userId, IEnumerable<long> groupIds)
    {
        Mutate(users =>
        {
            var user = users.FirstOrDefault(u => u.UserId == userId);
            if (user == null) return;
            user.GroupIds = groupIds.Distinct().ToList();
        });
    }

    public void TouchLogin(long userId, DateTime at)
    {
        Mutate(users =>
        {
            var user = users.FirstOrDefault(u => u.UserId == userId);
            if (user == null) return;
            user.LastLoginAt = at;
        });
    }
}

/// <summary><see cref="IUserGroupStore"/> 的實作（blob key=user_groups，整份型）</summary>
public class UserGroupStore : JsonBlobCollection<UserGroup>, IUserGroupStore
{
    public UserGroupStore(EfJsonBlobStore blob) : base(blob) { }

    public List<UserGroup> GetAll() => Read();

    public UserGroup? Get(long groupId) => Read().FirstOrDefault(g => g.GroupId == groupId);

    public UserGroup? FindByName(string groupName) =>
        Read().FirstOrDefault(g => string.Equals(g.GroupName, groupName, StringComparison.OrdinalIgnoreCase));

    public UserGroup Upsert(UserGroup group)
    {
        return Mutate(groups =>
        {
            var existing = group.GroupId == 0
                ? null
                : groups.FirstOrDefault(g => g.GroupId == group.GroupId);

            if (existing == null)
            {
                group.GroupId = NextId(groups.Select(g => g.GroupId));
                groups.Add(group);
                return group;
            }

            existing.GroupName = group.GroupName;
            existing.Role = group.Role;
            existing.Builtin = group.Builtin;
            existing.Active = group.Active;
            return existing;
        });
    }

    public void Delete(long groupId) => Mutate(groups => groups.RemoveAll(g => g.GroupId == groupId));
}
