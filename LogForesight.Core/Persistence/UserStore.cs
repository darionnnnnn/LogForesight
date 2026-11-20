using System.Text.Json;

namespace LogForesight.Core.Persistence;

/// <summary>
/// <see cref="IUserStore"/> 的實作（blob key=users，整份型；最後登入時間另存 blob key=user_last_login）。
///
/// 開快取：清單頁逐列、Cookie 驗證（<c>CookieSessionEvents</c>）每個請求都會查使用者，不快取就是每次重讀＋反序列化整份清單。
/// 最後登入時間因此搬出使用者清單——每次登入都改寫整份清單會讓版本前進、快取在登入尖峰反覆失效。
/// 快取回傳的是共用物件：呼叫端不得就地修改（要改先複製）。
/// </summary>
public class UserStore : JsonBlobCollection<WebUser>, IUserStore
{
    private readonly EfJsonBlobStore _lastLogin;
    private readonly EfJsonBlobStore? _sessionVersions;
    private SessionVersionSnapshot? _sessionSnapshot;

    /// <param name="sessionVersions">
    /// 工作階段版號的增量（userId → 登出次數）獨立存在這個 blob：登出是日常操作，若寫進 users blob，
    /// 版本前進會讓以 users 版本當授權柵欄的 PRTG 範圍清除作業被標成 authorization-changed 而暫停。
    /// 有效版號＝<see cref="WebUser.SessionVersion"/>（舊資料與停用遞增）＋這個 blob 的增量。
    /// 不提供時退回舊行為（登出改寫 users blob），只留給不關心柵欄的單元測試簡化建構。
    /// </param>
    public UserStore(EfJsonBlobStore blob, EfJsonBlobStore lastLogin, EfJsonBlobStore? sessionVersions = null) : base(blob, cached: true)
    {
        _lastLogin = lastLogin;
        _sessionVersions = sessionVersions;
    }

    /// <summary>某版本的增量表；整份以不可變物件替換，讀取端不加鎖</summary>
    private sealed record SessionVersionSnapshot(long BlobVersion, IReadOnlyDictionary<long, long> Increments);

    private static Dictionary<long, long> ParseIncrements(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new Dictionary<long, long>()
            : JsonSerializer.Deserialize<Dictionary<long, long>>(json, LfJsonOptions.Compact) ?? new Dictionary<long, long>();

    /// <summary>每次先探測 blob 版本（Cookie 驗證每個請求都會呼叫，成本同使用者清單快取）；版本沒變就用快取</summary>
    private IReadOnlyDictionary<long, long> SessionIncrements()
    {
        if (_sessionVersions == null) return new Dictionary<long, long>();
        var version = _sessionVersions.ReadVersion();
        var cached = Volatile.Read(ref _sessionSnapshot);
        if (cached != null && cached.BlobVersion == version) return cached.Increments;

        var (content, readVersion) = _sessionVersions.ReadWithVersion();
        var fresh = new SessionVersionSnapshot(readVersion, ParseIncrements(content));
        Volatile.Write(ref _sessionSnapshot, fresh);
        return fresh.Increments;
    }

    public long GetSessionVersion(WebUser user) =>
        user.SessionVersion + (SessionIncrements().TryGetValue(user.UserId, out var extra) ? extra : 0);

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
            //
            // SessionVersion 同樣不抄：唯一的遞增點是 BumpSessionVersion 與下面「啟用轉停用」。
            // 停用當下遞增，之後即使重新啟用，停用前簽發的 cookie 也不會復活。
            // 停用遞增留在 users blob：停用本身（Active 改變）就是影響授權的寫入、users 版本本來就要前進，
            // 與使用者清單同一個原子寫入還能避免「已停用但版號尚未遞增」的空窗。登出才是日常操作，走獨立 blob。
            if (existing.Active && !user.Active) existing.SessionVersion++;
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

    public long BumpSessionVersion(long userId)
    {
        if (_sessionVersions != null)
        {
            var user = Get(userId);
            if (user == null) return 0;
            // 只寫獨立 blob，不動 users blob（見建構子說明）；原子讀改寫避免兩個裝置同時登出漏加
            var increment = _sessionVersions.Mutate(raw =>
            {
                var map = ParseIncrements(raw);
                map[userId] = (map.TryGetValue(userId, out var current) ? current : 0) + 1;
                return (JsonSerializer.Serialize(map, LfJsonOptions.Compact), map[userId]);
            });
            return user.SessionVersion + increment;
        }
        return Mutate(users =>
        {
            var user = users.FirstOrDefault(u => u.UserId == userId);
            return user == null ? 0 : ++user.SessionVersion;
        });
    }

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
