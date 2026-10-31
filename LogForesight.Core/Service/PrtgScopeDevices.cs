using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogForesight.Core.Service;

/// <summary>
/// 監看裝置集合的計算結果。四個計數是各項各自的不重複裝置數（重疊照算），只供輸出用。
/// </summary>
/// <param name="DeviceObjids">監看裝置：感測器與狀態變更同步、快照都只處理這些裝置</param>
public sealed record PrtgScopeResult(IReadOnlySet<long> DeviceObjids, int Mapped, int Conflict, int Manual, int Guard)
{
    /// <summary>Fingerprint of the source/mapping inputs used to form this exact scope.</summary>
    public string SourceGeneration { get; init; } = string.Empty;
    /// <summary>Version snapshot bracketing the source reads used to compute this scope.</summary>
    public string ScopeFenceJson { get; init; } = string.Empty;
    /// <summary>
    /// 不在監看範圍、但清除時必須保留的裝置（資源守門未啟用時的守門裝置與 corehealth 裝置）。
    /// 守門目標是從感測器鏡像反推的——清掉之後就再也偵測不到，日後無法重新啟用守門。
    /// </summary>
    public IReadOnlySet<long> PreserveDeviceObjids { get; init; } = new HashSet<long>();

    /// <summary>本次只涵蓋部分主機（指定主機更新）。為 true 時任何「沒刷新到就清」的動作都不得執行。</summary>
    public bool IsPartial { get; init; }

}

/// <summary>
/// PRTG「監看裝置」集合的唯一出口：ok 對應、與主機有關的 conflict、人工對應、守門裝置四項聯集。
/// <list type="bullet">
/// <item>conflict 只收「mapper 已指到主機」（一 IP 多主機）或「IP 屬於某台啟用中主機」的列——名稱命中多台主機（hostname-multi）
/// 沒有 IP 可判斷時不收，等人工指派後才進範圍（docs/PRTG-SPEC.md §4）。</item>
/// <item>人工對應只收目標主機存在、啟用中且未被合併、且 device 仍在裝置鏡像的列；
/// 其餘不入範圍並寫進執行輸出。</item>
/// <item>守門裝置與 corehealth 所在裝置只在資源守門啟用時入範圍；未啟用（或只算部分主機）時改放進
/// <see cref="PrtgScopeResult.PreserveDeviceObjids"/>：不取數，但清除時保留它們的感測器鏡像，守門自動偵測才找得到。</item>
/// <item><c>hostIds</c> 非 null（指定主機更新）時三類對應只收屬於這些主機的列、守門不入範圍，結果標為 partial。</item>
/// </list>
/// </summary>
public static class PrtgScopeDevices
{
    /// <param name="hostIds">null＝全站主機；非 null＝只算這些主機（結果 <see cref="PrtgScopeResult.IsPartial"/> 為 true）。必填，呼叫端要明講。</param>
    public static PrtgScopeResult Compute(
        EfPrtgStore store, IHostStore hostStore, IPrtgResourceGuardSource guardSource,
        SystemSettings settings, IReadOnlyList<Sentinel> sentinels,
        IRunConsole console, IPrtgAddressResolver resolver,
        IReadOnlyCollection<long>? hostIds, bool includeGuardInPartial = false)
    {
        var purgeStore = store.ScopePurgeJobs();
        var scopeFenceBefore = purgeStore.CaptureScopeFenceJson();
        var wanted = hostIds?.ToHashSet();
        bool HostWanted(long hostId) => wanted == null || wanted.Contains(hostId);

        var mapRows = store.GetLatestHostMap();

        var mapped = mapRows
            .Where(r => r.MapStatus == PrtgMapStatus.Ok && (wanted == null || (r.HostId.HasValue && wanted.Contains(r.HostId.Value))))
            .Select(r => r.DeviceObjid)
            .ToHashSet();

        var hostIpLookup = PrtgHostMapper.BuildActiveHostIpLookup(hostStore, resolver);
        var conflict = new HashSet<long>();
        foreach (var row in mapRows.Where(r => r.MapStatus == PrtgMapStatus.Conflict))
        {
            if (row.HostId.HasValue)
            {
                if (HostWanted(row.HostId.Value))
                    conflict.Add(row.DeviceObjid);
                continue;
            }
            var ip = resolver.Resolve(row.Ip);
            if (ip != null && hostIpLookup.TryGetValue(ip, out var ipHosts) && ipHosts.Any(h => HostWanted(h.HostId)))
                conflict.Add(row.DeviceObjid);
        }

        var hostRows = hostStore.GetAll();
        var liveHostIds = hostRows
            .Where(h => h.Active && h.MergedInto == null)
            .Select(h => h.HostId)
            .ToHashSet();
        var manual = new HashSet<long>();
        var manualExcluded = 0;
        var manualRows = store.GetManualMaps();
        // 人工對應指向的 device 若已從 PRTG 刪除，objid 留在範圍內會讓每晚查詢失敗、範圍外清除永不執行
        var mirrorObjids = store.GetDeviceNamesByObjids(manualRows.Select(m => m.DeviceObjid).Distinct().ToList()).Keys.ToHashSet();
        foreach (var m in manualRows)
        {
            if (!liveHostIds.Contains(m.HostId))
            {
                manualExcluded++;
                continue;
            }
            if (!mirrorObjids.Contains(m.DeviceObjid))
            {
                console.WriteLine($"人工對應的裝置 #{m.DeviceObjid} 已不在 PRTG 鏡像，未納入取數範圍");
                continue;
            }
            if (HostWanted(m.HostId))
                manual.Add(m.DeviceObjid);
        }
        if (manualExcluded > 0)
        {
            console.WriteLine($"人工對應有 {manualExcluded} 筆指向已停用或不存在的主機，未納入監看。");
        }

        var guard = new HashSet<long>(PrtgResourceGuardTargets.ResolveDeviceObjids(guardSource, settings, sentinels, console, resolver));
        foreach (var sensor in guardSource.GetSensors())
        {
            if (PrtgResourceGuardTargets.IsCoreHealthSensor(sensor))
                guard.Add(sensor.DeviceObjid);
        }

        var all = new HashSet<long>(mapped);
        all.UnionWith(conflict);
        all.UnionWith(manual);

        var guardInScope = settings.PrtgResourceGuardEnabled && (wanted == null || includeGuardInPartial);
        var preserve = new HashSet<long>();
        if (guardInScope)
            all.UnionWith(guard);
        else
            preserve.UnionWith(guard);

        var scopeFenceAfter = purgeStore.CaptureScopeFenceJson();
        if (!string.Equals(scopeFenceBefore, scopeFenceAfter, StringComparison.Ordinal))
            throw new InvalidOperationException("計算監看範圍期間來源或對應已變更，請重新執行。");
        return new PrtgScopeResult(all, mapped.Count, conflict.Count, manual.Count, guardInScope ? guard.Count : 0)
        {
            PreserveDeviceObjids = preserve,
            IsPartial = wanted != null,
            ScopeFenceJson = scopeFenceAfter,
            SourceGeneration = SourceGenerationFingerprint(mapRows, manualRows, hostRows, guard,
                settings.PrtgResourceGuardEnabled, wanted)
        };
    }

    private static string SourceGenerationFingerprint(IReadOnlyList<PrtgHostMapRow> maps,
        IReadOnlyList<PrtgManualMapRow> manual, IReadOnlyList<WebHost> hosts, IReadOnlySet<long> guard,
        bool guardEnabled, IReadOnlySet<long>? requestedHosts)
    {
        var material = JsonSerializer.Serialize(new
        {
            guardEnabled,
            requestedHosts = requestedHosts?.OrderBy(x => x),
            maps = maps.OrderBy(x => x.DeviceObjid).Select(x => new { x.DeviceObjid, x.HostId, x.Ip, x.MapStatus }),
            manual = manual.OrderBy(x => x.DeviceObjid).Select(x => new { x.DeviceObjid, x.HostId, x.Note }),
            hosts = hosts.OrderBy(x => x.HostId).Select(x => new { x.HostId, x.Active, x.MergedInto, x.IpAddress }),
            guard = guard.OrderBy(x => x)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

/// <summary>範圍外清除的基準（blob <see cref="PrtgScopeBaselineStore.BlobKey"/>）。</summary>
public sealed class PrtgScopeBaseline
{
    /// <summary>上一次成功執行範圍外清除時的監看裝置數</summary>
    public int DeviceCount { get; set; }

    /// <summary>上一次成功執行範圍外清除的時間；default＝尚無基準</summary>
    public DateTime At { get; set; }

    /// <summary>最近一次自動清除被縮小保護／無基準擋下的原因（成功清除後清空），鏡像頁「監看範圍外資料」區塊顯示</summary>
    public string? BlockedReason { get; set; }

    public DateTime? BlockedAt { get; set; }

    public bool HasBaseline => At != default;
}

/// <summary>範圍外清除基準的存放（blob key <see cref="BlobKey"/>）。</summary>
public sealed class PrtgScopeBaselineStore : JsonBlobSingleton<PrtgScopeBaseline>
{
    public const string BlobKey = "prtg_scope_baseline";

    public PrtgScopeBaselineStore(EfJsonBlobStore blob) : base(blob) { }
}

/// <summary>
/// 監看範圍外的數值與狀態變更清除（docs/PRTG-SPEC.md §3c）。
/// 自動清除只在結構同步全部成功後由 <see cref="PrtgStructureSyncRunner"/>／<see cref="PrtgDailyPipeline"/> 呼叫；
/// 人工確認走 PRTG 維護頁的預覽＋確認。
/// </summary>
public static class PrtgScopePurge
{
    /// <summary>縮小保護的比例門檻（暫定）：比基準少超過基準的 30% 就不自動清</summary>
    internal const double ShrinkRatio = 0.3;

    /// <summary>縮小保護的絕對門檻下限（暫定）：小環境 30% 可能只有幾台，少於 50 台的變動不擋</summary>
    internal const int ShrinkFloor = 50;

    /// <summary>
    /// 規則 1：範圍可信與否。回傳擋下的原因；null＝可清。
    /// </summary>
    /// <param name="scope">null＝範圍計算擲例外</param>
    /// <param name="devicesRefreshed">裝置鏡像本趟成功更新（人工確認時＝裝置鏡像非空）</param>
    public static string? CheckScope(PrtgScopeResult? scope, bool devicesRefreshed)
    {
        if (scope == null) return "監看裝置計算失敗";
        if (scope.IsPartial) return "本趟只處理部分主機";
        if (scope.DeviceObjids.Count == 0) return "監看裝置是空的";
        if (!devicesRefreshed) return "裝置鏡像本趟未成功更新";
        return null;
    }

    /// <summary>規則 2、3：縮小保護與無基準。回傳擋下的原因；null＝可清。</summary>
    public static string? CheckShrink(int deviceCount, PrtgScopeBaseline baseline)
    {
        const string confirmHint = "確認無誤請到 PRTG 維護頁執行預覽並確認";
        if (!baseline.HasBaseline)
            return $"尚無監看裝置數基準（第一次），本次不自動清除範圍外資料；{confirmHint}。";
        var threshold = Math.Max(baseline.DeviceCount * ShrinkRatio, ShrinkFloor);
        if (baseline.DeviceCount - deviceCount > threshold)
            return $"監看裝置數由 {baseline.DeviceCount} 降為 {deviceCount}，疑似主機清單或 DNS 異常，本次不清除範圍外資料；{confirmHint}。";
        return null;
    }

    /// <summary>清除時要留下的裝置＝監看裝置 ∪ 保留裝置</summary>
    public static IReadOnlySet<long> KeepSet(PrtgScopeResult scope)
    {
        var keep = new HashSet<long>(scope.DeviceObjids);
        keep.UnionWith(scope.PreserveDeviceObjids);
        return keep;
    }

    /// <summary>
    /// 結構同步全部成功後的自動清除。呼叫端只在同步全部成功時呼叫；這裡再檢查三道保護。
    /// 清除失敗不擲出（只輸出），不影響同步結果。回傳是否實際執行了清除。
    /// </summary>
    public static bool RunAfterStructureSync(EfPrtgStore store, PrtgScopeResult? scope, bool devicesRefreshed,
        string verifiedParentJobId, IRunConsole console, CancellationToken cancellationToken = default,
        Func<bool>? parentOwnsRun = null)
    {
        var reason = CheckScope(scope, devicesRefreshed);
        if (reason != null)
        {
            console.WriteLine($"[範圍外清除] {reason}，本次不清除範圍外的數值與狀態變更。");
            return false;
        }

        PrtgScopePurgeStore? jobs = null;
        PrtgScopePurgeJobRow? job = null;
        try
        {
            var baselineStore = store.ScopeBaseline();
            var shrink = CheckShrink(scope!.DeviceObjids.Count, baselineStore.Get());
            if (shrink != null)
            {
                console.WriteLine($"[範圍外清除] ⚠ {shrink}");
                baselineStore.Update(b =>
                {
                    b.BlockedReason = shrink;
                    b.BlockedAt = DateTime.Now;
                });
                return false;
            }

            if (string.IsNullOrWhiteSpace(verifiedParentJobId))
                throw new InvalidOperationException("缺少成功同步父作業識別，拒絕自動清除。");
            if (parentOwnsRun == null || !parentOwnsRun())
                throw new InvalidOperationException("同步父作業已失去所有權，拒絕自動清除。");
            jobs = store.ScopePurgeJobs();
            var fingerprint = ComputeFingerprint(store, scope);
            job = jobs.CreateAutomatic(verifiedParentJobId, fingerprint, KeepSet(scope), scope.DeviceObjids.Count,
                DateTimeOffset.UtcNow, scope.ScopeFenceJson);
            while (job.Status is "accepted" or "running" or "paused")
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!parentOwnsRun())
                {
                    job = jobs.Stop(job.JobId, "parent-ownership-lost", DateTimeOffset.UtcNow);
                    break;
                }
                job = jobs.RunBatch(job.JobId, job.Revision, fingerprint, DateTimeOffset.UtcNow,
                    cancellationToken, ownershipCheck: parentOwnsRun);
            }
            if (job.Status == "complete")
            {
                console.WriteLine($"[範圍外清除] 已清除本作業數值 {job.DeletedValues} 筆、狀態變更 {job.DeletedStateChanges} 筆；其他清理已移除 {job.OtherwiseRemovedValues}／{job.OtherwiseRemovedStateChanges} 筆（作業 {job.JobId}）。");
                return true;
            }
            console.WriteLine(job.CountsReconciled
                ? $"[範圍外清除] 未完成，作業 {job.JobId}，狀態 {job.Status}，已核對剩餘 {job.RemainingValues}／{job.RemainingStateChanges} 筆。"
                : $"[範圍外清除] 處理中，作業 {job.JobId}，本作業已刪 {job.DeletedValues}／{job.DeletedStateChanges} 筆；預估尚餘 {job.RemainingValues}／{job.RemainingStateChanges} 筆，暫未核對其他清理。");
            return false;
        }
        catch (OperationCanceledException)
        {
            if (jobs != null && job != null)
            {
                try { jobs.Stop(job.JobId, "parent-cancelled", DateTimeOffset.UtcNow); }
                catch { /* The committed batch remains authoritative if storage is unavailable. */ }
            }
            throw;
        }
        catch (Exception ex)
        {
            if (jobs != null && job != null)
            {
                try { jobs.Stop(job.JobId, "batch-failed", DateTimeOffset.UtcNow); }
                catch { /* Keep the last durable progress if storage itself failed. */ }
            }
            console.WriteLine($"[範圍外清除] ✗ 清除失敗：{ex.Message}");
            return false;
        }
    }

    /// <summary>Fingerprint of the exact purge keep set and current mapping generation.</summary>
    public static string ComputeFingerprint(EfPrtgStore store, PrtgScopeResult scope)
    {
        var maps = store.GetLatestHostMap().OrderBy(x => x.DeviceObjid)
            .Select(x => new { x.DeviceObjid, x.HostId, x.MapStatus }).ToArray();
        var manual = store.GetManualMaps().OrderBy(x => x.DeviceObjid)
            .Select(x => new { x.DeviceObjid, x.HostId }).ToArray();
        var summary = store.GetMirrorSummary();
        var material = JsonSerializer.Serialize(new
        {
            keep = KeepSet(scope).OrderBy(x => x), scope.DeviceObjids.Count,
            scope.SourceGeneration,
            summary.LastDeviceSync, summary.LastSensorSync, maps, manual
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
