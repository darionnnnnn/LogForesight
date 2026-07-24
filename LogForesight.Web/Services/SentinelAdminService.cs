using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// Sentinel 連線設定維護（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 1）。
/// 「新增即掃描」精靈與資料匯入頁整併留待 Phase 4；這裡先把 CRUD 本身建好。
/// </summary>
public interface ISentinelAdminService
{
    List<SentinelDto> GetSentinels();

    SentinelDto SaveSentinel(SaveSentinelRequest request);

    /// <summary>刪除＝觸發孤兒流程：轄下使用中的 NetIQ 主機停用並標記孤兒（可於 Web 重新綁定），主機列本身不刪</summary>
    void DeleteSentinel(long sentinelId);

    /// <summary>停用＝暫停輪巡，主機不動、不標記孤兒（過渡期用的溫和選項，與刪除刻意分開）</summary>
    SentinelDto SetActive(long sentinelId, bool active);
}

public class SentinelAdminService : ISentinelAdminService
{
    private readonly ISentinelStore _sentinels;
    private readonly IHostStore _hosts;
    private readonly IAuditService _audit;

    public SentinelAdminService(ISentinelStore sentinels, IHostStore hosts, IAuditService audit)
    {
        _sentinels = sentinels;
        _hosts = hosts;
        _audit = audit;
    }

    public List<SentinelDto> GetSentinels()
    {
        var hostCounts = _hosts.GetAll()
            .Where(h => h.Active && h.MergedInto == null && h.SentinelId.HasValue)
            .GroupBy(h => h.SentinelId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        return _sentinels.GetAll()
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => ToDto(s, hostCounts.GetValueOrDefault(s.SentinelId)))
            .ToList();
    }

    public SentinelDto SaveSentinel(SaveSentinelRequest request)
    {
        var name = request.Name.Trim();
        if (name.Length == 0)
            throw DomainException.Validation("請輸入 Sentinel 名稱。");

        var duplicate = _sentinels.FindByName(name);
        var isNew = request.SentinelId == 0;

        if (duplicate != null && duplicate.SentinelId != request.SentinelId)
            throw DomainException.Conflict($"已有名稱為「{name}」的 Sentinel。");

        Sentinel? existing = isNew ? null : _sentinels.Get(request.SentinelId);
        if (!isNew && existing == null)
            throw DomainException.NotFound("找不到這台 Sentinel。");

        // 先把改名前的名稱存成區域變數——existing 之後可能與 Upsert 內部操作的物件是同一份
        // （測試替身的常見實作方式），Upsert 之後才讀 existing.Name 會讀到已經被蓋掉的新值
        var previousName = existing?.Name;

        // 密碼 write-only：留空＝沿用既有密文（新增時留空＝這台不能主動掃描）
        var passwordEnc = string.IsNullOrEmpty(request.Password)
            ? existing?.PasswordEnc ?? ""
            : CryptoHelper.Encrypt(request.Password);

        var saved = _sentinels.Upsert(new Sentinel
        {
            SentinelId = request.SentinelId,
            Name = name,
            BaseUrl = request.BaseUrl?.Trim() ?? "",
            Username = request.Username?.Trim() ?? "",
            PasswordEnc = passwordEnc,
            Active = existing?.Active ?? true
        });

        // 改名時同步所有掛在這台 Sentinel 下的主機顯示快照——不然要等下次批次/人工編輯才會更新，
        // 畫面會有一段時間顯示改名前的名稱
        if (previousName != null && !string.Equals(previousName, name, StringComparison.Ordinal))
            SyncHostDisplaySnapshot(saved.SentinelId, name);

        _audit.Record(
            action: isNew ? AuditActions.SentinelCreate : AuditActions.SentinelUpdate,
            summary: isNew
                ? $"新增 Sentinel「{saved.Name}」"
                : $"更新 Sentinel「{saved.Name}」",
            targetKind: "sentinel",
            targetId: saved.SentinelId.ToString(),
            // 密碼欄位絕不進稽核——即使是「有沒有改」這個布林都不留，避免旁敲側擊
            detail: new { saved.Name, saved.BaseUrl, saved.Username });

        return ToDto(saved, HostCount(saved.SentinelId));
    }

    public void DeleteSentinel(long sentinelId)
    {
        var sentinel = _sentinels.Get(sentinelId) ?? throw DomainException.NotFound("找不到這台 Sentinel。");

        _sentinels.Delete(sentinelId);

        // 直接處理，不沿用 NetiqOrphanSweeper：那支是給批次啟動時的全庫掃描用，
        // 帶有「現存 Sentinel 名單整個是空的就安全跳過」的欄杆（防設定尚未匯入時誤判）；
        // 這裡是 admin 明確點了刪除這一台，就算它是最後一台，轄下主機也該照樣孤兒化，
        // 不能被那道防未知狀況的欄杆連坐擋下
        var affected = 0;
        foreach (var host in _hosts.GetAll().Where(h => h.SentinelId == sentinelId && h.Active && h.MergedInto == null))
        {
            host.Active = false;
            host.OrphanedFromSentinel = host.NetiqServer;
            _hosts.Upsert(host);
            affected++;
        }

        _audit.Record(
            action: AuditActions.SentinelDelete,
            summary: $"刪除 Sentinel「{sentinel.Name}」（轄下 {affected} 台主機已停用並標記孤兒，可於主機頁重新綁定）",
            targetKind: "sentinel",
            targetId: sentinelId.ToString(),
            detail: new { sentinel.Name, AffectedHostCount = affected });
    }

    public SentinelDto SetActive(long sentinelId, bool active)
    {
        var sentinel = _sentinels.Get(sentinelId) ?? throw DomainException.NotFound("找不到這台 Sentinel。");

        var saved = _sentinels.Upsert(new Sentinel
        {
            SentinelId = sentinel.SentinelId,
            Name = sentinel.Name,
            BaseUrl = sentinel.BaseUrl,
            Username = sentinel.Username,
            PasswordEnc = sentinel.PasswordEnc,
            Active = active
        });

        _audit.Record(
            action: AuditActions.SentinelSetActive,
            summary: active
                ? $"啟用 Sentinel「{saved.Name}」（恢復輪巡）"
                : $"停用 Sentinel「{saved.Name}」（暫停輪巡，轄下主機不動、不標記孤兒）",
            targetKind: "sentinel",
            targetId: sentinelId.ToString(),
            detail: new { saved.Name, Active = active });

        return ToDto(saved, HostCount(saved.SentinelId));
    }

    private int HostCount(long sentinelId) =>
        _hosts.GetAll().Count(h => h.Active && h.MergedInto == null && h.SentinelId == sentinelId);

    private void SyncHostDisplaySnapshot(long sentinelId, string newName)
    {
        foreach (var host in _hosts.GetAll().Where(h => h.SentinelId == sentinelId &&
                                                          !string.Equals(h.NetiqServer, newName, StringComparison.Ordinal)))
        {
            host.NetiqServer = newName;
            _hosts.Upsert(host);
        }
    }

    private static SentinelDto ToDto(Sentinel sentinel, int hostCount) => new()
    {
        SentinelId = sentinel.SentinelId,
        Name = sentinel.Name,
        BaseUrl = sentinel.BaseUrl,
        Username = sentinel.Username,
        HasPassword = !string.IsNullOrEmpty(sentinel.PasswordEnc),
        CanDiscover = sentinel.CanDiscover,
        Active = sentinel.Active,
        CreatedAt = sentinel.CreatedAt,
        UpdatedAt = sentinel.UpdatedAt,
        HostCount = hostCount
    };
}
