using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 資源守門自動偵測記錄文件。
/// </summary>
public sealed class PrtgResourceGuardAutoDetectDoc
{
    public DateTime? DetectedAt { get; set; }
    public string[] Objids { get; set; } = Array.Empty<string>();
}

/// <summary>
/// PRTG 資源守門自動偵測記錄存取區。
/// </summary>
public sealed class PrtgResourceGuardAutoDetectStore(EfJsonBlobStore blob) : JsonBlobSingleton<PrtgResourceGuardAutoDetectDoc>(blob)
{
    public const string BlobKey = PrtgResourceGuardAutoDetect.BlobKey;
}

/// <summary>
/// PRTG 資源守門目標自動偵測與註記服務。
/// </summary>
public static class PrtgResourceGuardAutoDetect
{
    public const string BlobKey = "prtg_resource_guard_autodetect";

    /// <summary>偵測逾時時間（可由測試調整，不開可選建構子參數）。</summary>
    internal static TimeSpan DetectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 確保夜間取數前 PRTG 資源守門目標已完成自動偵測並填入清單。
    /// </summary>
    public static async Task EnsureAsync(
        StorageBackend backend,
        SystemSettings settings,
        IReadOnlyList<Sentinel> sentinels,
        IRunConsole console,
        Func<PrtgClient> clientFactory,
        CancellationToken ct)
    {
        await Task.Yield();

        // 1. settings.PrtgResourceGuardEnabled 為 false → 直接 return（不偵測、不出聲）。
        if (!settings.PrtgResourceGuardEnabled)
        {
            return;
        }

        // 2. 讀 blob。清單非空且不等於 blob 的 Objids（順序無關比較）→ 視為人工清單，return。
        var detectStore = new PrtgResourceGuardAutoDetectStore(backend.Blob(BlobKey));
        var doc = detectStore.Get();
        var currentObjids = settings.PrtgResourceGuardSensorObjids ?? (IReadOnlyList<string>)Array.Empty<string>();
        bool hasCurrent = currentObjids.Any(s => !string.IsNullOrWhiteSpace(s));
        bool equalsBlob = doc.Objids != null && doc.Objids.Length > 0 && SetEquals(currentObjids, doc.Objids);

        if (hasCurrent && !equalsBlob)
        {
            return;
        }

        // 3. 需要偵測的條件：清單為空；或清單等於 blob（自動偵測的結果）且其中有任何 objid 已不在 sensor 鏡像（backend.PrtgStore() 的 sensor 清單）中。都不成立 → return。
        bool needDetect = false;
        if (!hasCurrent)
        {
            needDetect = true;
        }
        else if (equalsBlob)
        {
            var mirrorSensors = backend.PrtgStore().GetAllSensors();
            var mirrorObjidSet = new HashSet<long>(mirrorSensors.Select(s => s.Objid));
            bool anyMissing = doc.Objids != null && doc.Objids.Any(idStr => !long.TryParse(idStr?.Trim(), out var id) || !mirrorObjidSet.Contains(id));
            if (anyMissing)
            {
                needDetect = true;
            }
        }

        if (!needDetect)
        {
            return;
        }

        // 4. 建 client（clientFactory()，using），以 CancellationTokenSource.CreateLinkedTokenSource(ct) 並 CancelAfter(15s) 呼叫
        //    PrtgResourceGuardTargets.Resolve(new PrtgLiveGuardSource(client, token, console), settings, sentinels, console, new PrtgAddressResolver(), ignoreOverride: true)。
        //    15 秒逾時或任何例外（父 ct 取消除外，要重拋）→ console.WriteLine("[PRTG資源守門] 自動偵測未完成（{原因}），本趟不守門，取數照常進行。") 後 return，不改清單。
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(DetectTimeout);

        PrtgResourceGuardTargetResult result;
        try
        {
            using var client = clientFactory();
            result = PrtgResourceGuardTargets.Resolve(
                new PrtgLiveGuardSource(client, linkedCts.Token, console),
                settings,
                sentinels,
                console,
                new PrtgAddressResolver(),
                ignoreOverride: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            console.WriteLine($"[PRTG資源守門] 自動偵測未完成（{ex.Message}），本趟不守門，取數照常進行。");
            return;
        }

        // 5. 偵測結果為空 → 印一行「自動偵測沒有找到守門目標」，不改清單。
        if (result.SensorObjids == null || result.SensorObjids.Count == 0)
        {
            console.WriteLine("[PRTG資源守門] 自動偵測沒有找到守門目標");
            return;
        }

        // 6. 否則：以 SystemSettingsStore 更新 PrtgResourceGuardSensorObjids 設為結果（objid 字串、遞增排序）；
        //    寫 blob { DetectedAt = DateTime.Now, Objids = 同一份 }；印一行「[PRTG資源守門] 自動偵測並填入 N 顆守門目標（{日期}）」。
        var sortedObjids = result.SensorObjids
            .OrderBy(id => id)
            .Select(id => id.ToString())
            .ToList();

        new SystemSettingsStore(backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgResourceGuardSensorObjids = sortedObjids;
        });

        var now = DateTime.Now;
        detectStore.Update(d =>
        {
            d.DetectedAt = now;
            d.Objids = sortedObjids.ToArray();
        });

        console.WriteLine($"[PRTG資源守門] 自動偵測並填入 {sortedObjids.Count} 顆守門目標（{now:yyyy-MM-dd}）");
    }

    /// <summary>
    /// 描述目前的守門目標清單是否為自動偵測結果及自動偵測時間。
    /// 清單非空且等於 blob → (true, DetectedAt)，否則 (false, null)。
    /// </summary>
    public static (bool AutoDetected, DateTime? DetectedAt) Describe(StorageBackend backend, SystemSettings settings)
    {
        var current = settings.PrtgResourceGuardSensorObjids;
        if (current == null || current.Count == 0 || !current.Any(s => !string.IsNullOrWhiteSpace(s)))
        {
            return (false, null);
        }

        var doc = new PrtgResourceGuardAutoDetectStore(backend.Blob(BlobKey)).Get();
        if (doc.DetectedAt == null || doc.Objids == null || doc.Objids.Length == 0)
        {
            return (false, null);
        }

        if (SetEquals(current, doc.Objids))
        {
            return (true, doc.DetectedAt);
        }

        return (false, null);
    }

    /// <summary>
    /// 順序無關的比對兩個字串清單。
    /// </summary>
    internal static bool SetEquals(IEnumerable<string>? a, IEnumerable<string>? b)
    {
        var setA = new HashSet<string>(a?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var setB = new HashSet<string>(b?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        return setA.SetEquals(setB);
    }
}
