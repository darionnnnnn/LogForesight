using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;

namespace LogForesight.Web.Services;

public sealed record PrtgScopePurgeAuthorization(
    bool Allowed, long ActorId, long PermissionRevision, string? AuthFingerprint, string? DenialReason,
    long UsersRevision = 0, long GroupsRevision = 0)
{
    public PrtgScopePurgeAuthorizationFence ToBatchFence() =>
        new(PermissionRevision, ActorId == 0 ? null : UsersRevision,
            ActorId == 0 ? null : GroupsRevision, AuthFingerprint);
}

/// <summary>Live purge authorization. It reloads user/group data for every decision and never trusts JWT capability claims.</summary>
public sealed class PrtgScopePurgeAuthorizationService
{
    private readonly IUserStore _users;
    private readonly UserCapabilityResolver _capabilities;
    private readonly ICurrentUser _currentUser;
    private readonly WebAppSettings _settings;
    private readonly EfJsonBlobStore _permissionVersion;
    private readonly EfJsonBlobStore _usersBlob;
    private readonly EfJsonBlobStore _groupsBlob;

    public PrtgScopePurgeAuthorizationService(IUserStore users, UserCapabilityResolver capabilities,
        ICurrentUser currentUser, WebAppSettings settings, StorageBackend backend)
    {
        _users = users;
        _capabilities = capabilities;
        _currentUser = currentUser;
        _settings = settings;
        _permissionVersion = backend.Blob(PermissionVersionStamp.BlobKey);
        _usersBlob = backend.Blob("users");
        _groupsBlob = backend.Blob("user_groups");
    }

    public IReadOnlySet<Capability> CurrentCapabilities()
    {
        if (_currentUser.IsServerAdmin) return RoleCapabilityMap.ForServerAdmin();
        var user = _users.Get(_currentUser.UserId);
        return user is { Active: true } ? _capabilities.Resolve(user) : new HashSet<Capability>();
    }

    public PrtgScopePurgeAuthorization Check(long expectedActorId, string? expectedAuthFingerprint = null)
    {
        var revision = _permissionVersion.ReadVersion(); // Direct durable read; PermissionVersionStamp.Current is process-cached.
        var usersRevision = _usersBlob.ReadVersion();
        var groupsRevision = _groupsBlob.ReadVersion();
        if (!_currentUser.IsAuthenticated || _currentUser.UserId != expectedActorId)
            return new(false, expectedActorId, revision, null, "登入身分已變更，請重新預覽。", usersRevision, groupsRevision);

        if (_currentUser.IsServerAdmin)
        {
            var admin = _settings.Auth.ServerAdmin;
            var fingerprint = Fingerprint($"{admin.Account}\n{admin.PasswordHash}");
            var configured = !string.IsNullOrWhiteSpace(admin.Account) && !string.IsNullOrWhiteSpace(admin.PasswordHash)
                && string.Equals(admin.Account, _currentUser.Account, StringComparison.OrdinalIgnoreCase);
            if (!configured || expectedActorId != 0 ||
                (expectedAuthFingerprint != null && !string.Equals(expectedAuthFingerprint, fingerprint, StringComparison.Ordinal)))
                return new(false, expectedActorId, revision, fingerprint, "救援管理者設定已變更，請重新登入並預覽。", usersRevision, groupsRevision);
            return new(RoleCapabilityMap.ForServerAdmin().Contains(Capability.Maintain), 0, revision, fingerprint,
                RoleCapabilityMap.ForServerAdmin().Contains(Capability.Maintain) ? null : "目前帳號沒有維護權限。", usersRevision, groupsRevision);
        }

        var user = _users.Get(expectedActorId);
        if (user == null || !user.Active)
            return new(false, expectedActorId, revision, null, "操作者帳號已停用或不存在。", usersRevision, groupsRevision);
        var allowed = _capabilities.Resolve(user).Contains(Capability.Maintain);
        return new(allowed, expectedActorId, revision, null, allowed ? null : "目前帳號沒有維護權限。", usersRevision, groupsRevision);
    }

    /// <summary>Read-only access to an actor's own progress may remain available after Maintain is revoked.</summary>
    public PrtgScopePurgeAuthorization CheckRead(long expectedActorId, string? expectedAuthFingerprint = null)
    {
        var revision = _permissionVersion.ReadVersion();
        if (!_currentUser.IsAuthenticated || _currentUser.UserId != expectedActorId)
            return new(false, expectedActorId, revision, null, "登入身分已變更，無法讀取此作業。");
        if (_currentUser.IsServerAdmin)
        {
            var admin = _settings.Auth.ServerAdmin;
            var fingerprint = Fingerprint($"{admin.Account}\n{admin.PasswordHash}");
            var configured = !string.IsNullOrWhiteSpace(admin.Account) && !string.IsNullOrWhiteSpace(admin.PasswordHash)
                && string.Equals(admin.Account, _currentUser.Account, StringComparison.OrdinalIgnoreCase);
            var allowed = configured && expectedActorId == 0
                && (expectedAuthFingerprint == null || string.Equals(expectedAuthFingerprint, fingerprint, StringComparison.Ordinal));
            return new(allowed, expectedActorId, revision, fingerprint,
                allowed ? null : "救援管理者設定已變更，無法讀取舊作業。");
        }
        var user = _users.Get(expectedActorId);
        if (user is not { Active: true })
            return new(false, expectedActorId, revision, null, "操作者帳號已停用或不存在。");
        var capabilities = _capabilities.Resolve(user);
        var canRead = capabilities.Contains(Capability.Maintain) || capabilities.Contains(Capability.ViewAll);
        return new(canRead, expectedActorId, revision, null, canRead ? null : "目前帳號沒有讀取此作業的權限。");
    }

    public static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
