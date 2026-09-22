using System.Text.Json;

namespace LogForesight.Core.Persistence;

/// <summary>
/// <see cref="IUserStore"/> 的實作（blob key=users，整份型；最後登入時間另存 blob key=user_last_login）。
///
/// 開快取：清單頁逐列、<c>ActiveUserMiddleware</c> 每個請求都會查使用者，不快取就是每次重讀＋反序列化整份清單。
/// 最後登入時間因此搬出使用者清單——每次登入都改寫整份清單會讓版本前進、快取在登入尖峰反覆失效。
/// 快取回傳的是共用物件：呼叫端不得就地修改（要改先複製）。
/// </summary>
public class UserStore : JsonBlobCollection<WebUser>, IUserStore
{
    private readonly EfJsonBlobStore _lastLogin;

    public UserStore(EfJsonBlobStore blob, EfJsonBlobStore lastLogin) : base(blob, cached: true)
    {
        _lastLogin = lastLogin;
    }

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

    /// <summary>寫到 user_last_login（原子讀改寫），**不動使用者清單**——使用者清單的版本因此不前進、快取不失效</summary>
    public void TouchLogin(long userId, DateTime at)
    {
        _lastLogin.Mutate(raw =>
        {
            var map = DeserializeLastLogins(raw);
            map[userId] = at;
            return (JsonSerializer.Serialize(map, LfJsonOptions.Compact), 0);
        });
    }

    public IReadOnlyDictionary<long, DateTime> GetLastLogins() => DeserializeLastLogins(_lastLogin.Read());

    private static Dictionary<long, DateTime> DeserializeLastLogins(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new Dictionary<long, DateTime>()
            : JsonSerializer.Deserialize<Dictionary<long, DateTime>>(json, LfJsonOptions.Compact) ?? new Dictionary<long, DateTime>();

    /// <summary>只改暫停接單旗標：Upsert 是逐欄複製且刻意不含這個欄位（見該處註解），
    /// 這裡是它的唯一寫入點</summary>
    public void SetDispatchPaused(long userId, bool paused)
    {
        Mutate(users =>
        {
            var user = users.FirstOrDefault(u => u.UserId == userId);
            if (user == null) return;
            user.DispatchPaused = paused;
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

    /// <summary>只改派工池旗標：Upsert 是逐欄複製且刻意不含這個欄位（見該處註解），
    /// 這裡是它的唯一寫入點</summary>
    public void SetDispatchPool(long groupId, bool inPool)
    {
        Mutate(groups =>
        {
            var group = groups.FirstOrDefault(g => g.GroupId == groupId);
            if (group == null) return;
            group.DispatchPool = inPool;
        });
    }

    public void Delete(long groupId) => Mutate(groups => groups.RemoveAll(g => g.GroupId == groupId));
}
