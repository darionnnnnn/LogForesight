namespace LogForesight;

/// <summary><see cref="IUserStore"/> 的實作（blob key=users，整份型）</summary>
public class JsonUserStore : JsonBlobCollection<WebUser>, IUserStore
{
    public JsonUserStore(IJsonBlobStore blob) : base(blob) { }

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

/// <summary><see cref="IUserGroupStore"/> 的實作（blob key=user_groups，整份型）</summary>
public class JsonUserGroupStore : JsonBlobCollection<UserGroup>, IUserGroupStore
{
    public JsonUserGroupStore(IJsonBlobStore blob) : base(blob) { }

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
