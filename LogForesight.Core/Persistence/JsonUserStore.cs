namespace LogForesight;

/// <summary>
/// <see cref="IUserStore"/> 的 JSONL 後端實作：webdata\users.json（整檔型，原子替換）。
/// 前期單機測試用；SQL 就緒後新增 SqlUserStore 並在 StorageFactory 切換，呼叫端不動。
/// </summary>
public class JsonUserStore : JsonCollectionFile<WebUser>, IUserStore
{
    public JsonUserStore(string filePath) : base(filePath) { }

    public List<WebUser> GetAll() => Read();

    public WebUser? Get(long userId) => Read().FirstOrDefault(u => u.UserId == userId);

    public WebUser? FindByAccount(string account) =>
        Read().FirstOrDefault(u => string.Equals(u.Account, account, StringComparison.OrdinalIgnoreCase));

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
}

/// <summary>
/// <see cref="IUserGroupStore"/> 的 JSONL 後端實作：webdata\groups.json（整檔型，原子替換）。
/// </summary>
public class JsonUserGroupStore : JsonCollectionFile<UserGroup>, IUserGroupStore
{
    public JsonUserGroupStore(string filePath) : base(filePath) { }

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
