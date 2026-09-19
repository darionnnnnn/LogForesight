using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// 監看裝置集合的計算結果。四個計數是各項各自的不重複裝置數（重疊照算），只供輸出用。
/// </summary>
/// <param name="DeviceObjids">監看裝置：感測器與狀態變更同步、快照都只處理這些裝置</param>
public sealed record PrtgScopeResult(IReadOnlySet<long> DeviceObjids, int Mapped, int Conflict, int Manual, int Guard)
{
    /// <summary>
    /// 不在監看範圍、但清除時必須保留的裝置（資源守門未啟用時的守門裝置與 corehealth 裝置）。
    /// 守門目標是從感測器鏡像反推的——清掉之後就再也偵測不到，日後無法重新啟用守門。
    /// </summary>
    public IReadOnlySet<long> PreserveDeviceObjids { get; init; } = new HashSet<long>();

    /// <summary>本次只涵蓋部分主機（指定主機更新）。為 true 時任何「沒刷新到就清」的動作都不得執行。</summary>
    public bool IsPartial { get; init; }

    /// <summary>指向已停用、已合併或不存在主機而未納入的人工對應筆數</summary>
    public int ManualExcluded { get; init; }
}

/// <summary>
/// PRTG「監看裝置」集合的唯一出口：ok 對應、與主機有關的 conflict、人工對應、守門裝置四項聯集。
/// <list type="bullet">
/// <item>conflict 只收「mapper 已指到主機」或「IP 屬於某台啟用中主機」的列——同 IP 多裝置但與主機無關的
/// conflict 在大型 PRTG 可能有數百台，整批收進來等於沒縮圈。</item>
/// <item>人工對應只收目標主機存在、啟用中且未被合併的列；其餘計入 <see cref="PrtgScopeResult.ManualExcluded"/>。</item>
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
        IReadOnlyCollection<long>? hostIds)
    {
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

        var liveHostIds = hostStore.GetAll()
            .Where(h => h.Active && h.MergedInto == null)
            .Select(h => h.HostId)
            .ToHashSet();
        var manual = new HashSet<long>();
        var manualExcluded = 0;
        foreach (var m in store.GetManualMaps())
        {
            if (!liveHostIds.Contains(m.HostId))
            {
                manualExcluded++;
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

        var guardInScope = settings.PrtgResourceGuardEnabled && wanted == null;
        var preserve = new HashSet<long>();
        if (guardInScope)
            all.UnionWith(guard);
        else
            preserve.UnionWith(guard);

        return new PrtgScopeResult(all, mapped.Count, conflict.Count, manual.Count, guardInScope ? guard.Count : 0)
        {
            PreserveDeviceObjids = preserve,
            IsPartial = wanted != null,
            ManualExcluded = manualExcluded
        };
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
    public static bool RunAfterStructureSync(EfPrtgStore store, PrtgScopeResult? scope, bool devicesRefreshed, IRunConsole console)
    {
        var reason = CheckScope(scope, devicesRefreshed);
        if (reason != null)
        {
            console.WriteLine($"[範圍外清除] {reason}，本次不清除範圍外的數值與狀態變更。");
            return false;
        }

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

            var (values, stateChanges) = store.DeleteOutOfScopeData(KeepSet(scope));
            RecordBaseline(store, scope.DeviceObjids.Count);
            console.WriteLine($"[範圍外清除] 已清除監看範圍外的數值 {values} 筆、狀態變更 {stateChanges} 筆（監看裝置 {scope.DeviceObjids.Count} 台）。");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            console.WriteLine($"[範圍外清除] ✗ 清除失敗：{ex.Message}");
            return false;
        }
    }

    /// <summary>成功清除後更新基準並清掉擋下提示（自動與人工確認共用）</summary>
    public static void RecordBaseline(EfPrtgStore store, int deviceCount) =>
        store.ScopeBaseline().Update(b =>
        {
            b.DeviceCount = deviceCount;
            b.At = DateTime.Now;
            b.BlockedReason = null;
            b.BlockedAt = null;
        });
}
