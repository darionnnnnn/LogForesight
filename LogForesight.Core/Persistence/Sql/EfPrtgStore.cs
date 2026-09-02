using LogForesight.Core.Models;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// PRTG 鏡像資料存取層（lf_prtg_* 五張表）：提供裝置與感測器結構鏡像 upsert、狀態變更寫入、
/// 每小時數值 upsert、按日主機映射寫入，以及依保留天數清理過期資料。
/// </summary>
public sealed class EfPrtgStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;

    /// <summary>單次 SaveChanges 的批次上限</summary>
    private const int UpsertBatchSize = 500;

    public EfPrtgStore(Func<LfDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// 切批寫入的私有輔助方法（每 <see cref="UpsertBatchSize"/> 筆存一次檔，每批一個獨立 DbContext）。
    /// 任一批失敗時例外照樣往外傳，不吞、不跳過剩餘批次。
    /// </summary>
    private int BatchWrite<T>(IReadOnlyList<T> items, Func<LfDbContext, List<T>, int> writeBatch)
    {
        if (items == null || items.Count == 0) return 0;

        var list = items as List<T> ?? items.ToList();
        var total = 0;

        for (var offset = 0; offset < list.Count; offset += UpsertBatchSize)
        {
            var count = Math.Min(UpsertBatchSize, list.Count - offset);
            var batch = list.GetRange(offset, count);
            using var ctx = _contextFactory();
            total += writeBatch(ctx, batch);
        }

        return total;
    }

    /// <summary>
    /// PRTG 裝置結構鏡像 upsert。自然鍵為 objid，已存在則就地更新描述性欄位，不存在則新增。
    /// 更新時 CreatedAt 保持原值不覆蓋（首次寫入時間）。
    /// </summary>
    public int UpsertDevices(IReadOnlyList<PrtgDeviceRow> devices, DateTime syncedAt)
    {
        return BatchWrite(devices, (ctx, batch) =>
        {
            var ids = batch.Select(d => d.Objid).ToList();
            var existing = ctx.PrtgDevices.Where(d => ids.Contains(d.Objid)).ToDictionary(d => d.Objid);

            foreach (var item in batch)
            {
                if (existing.TryGetValue(item.Objid, out var row))
                {
                    row.Name = item.Name;
                    row.GroupPath = item.GroupPath;
                    row.Ip = item.Ip;
                    row.Tags = item.Tags;
                    row.Status = item.Status;
                    row.DependencyObjid = item.DependencyObjid;
                    row.Paused = item.Paused;
                    row.SyncedAt = syncedAt;
                    // CreatedAt 保持原值不覆蓋（首次寫入時間）
                }
                else
                {
                    var newRow = new PrtgDeviceRow
                    {
                        Objid = item.Objid,
                        Name = item.Name,
                        GroupPath = item.GroupPath,
                        Ip = item.Ip,
                        Tags = item.Tags,
                        Status = item.Status,
                        DependencyObjid = item.DependencyObjid,
                        Paused = item.Paused,
                        SyncedAt = syncedAt,
                        CreatedAt = item.CreatedAt != default ? item.CreatedAt : syncedAt
                    };
                    ctx.PrtgDevices.Add(newRow);
                    existing[item.Objid] = newRow;
                }
            }

            ctx.SaveChanges();
            return batch.Count;
        });
    }

    /// <summary>
    /// PRTG 感測器結構鏡像 upsert。自然鍵為 objid，已存在則就地更新描述性欄位，不存在則新增。
    /// 更新時 CreatedAt 保持原值不覆蓋。
    /// </summary>
    public int UpsertSensors(IReadOnlyList<PrtgSensorRow> sensors, DateTime syncedAt)
    {
        return BatchWrite(sensors, (ctx, batch) =>
        {
            var ids = batch.Select(s => s.Objid).ToList();
            var existing = ctx.PrtgSensors.Where(s => ids.Contains(s.Objid)).ToDictionary(s => s.Objid);

            foreach (var item in batch)
            {
                if (existing.TryGetValue(item.Objid, out var row))
                {
                    row.DeviceObjid = item.DeviceObjid;
                    row.Name = item.Name;
                    row.SensorType = item.SensorType;
                    row.Tags = item.Tags;
                    row.Unit = item.Unit;
                    row.Status = item.Status;
                    row.ThresholdsJson = item.ThresholdsJson;
                    row.DependencyObjid = item.DependencyObjid;
                    row.Paused = item.Paused;
                    row.SyncedAt = syncedAt;
                    // Category 與 CategorySource 絕對不可覆蓋：這兩欄是 sensor 語意分類結果，未來會有人工指定的值，每日結構同步不得把人工結果洗掉。
                    // CreatedAt 保持原值不覆蓋（首次寫入時間）。
                }
                else
                {
                    var newRow = new PrtgSensorRow
                    {
                        Objid = item.Objid,
                        DeviceObjid = item.DeviceObjid,
                        Name = item.Name,
                        SensorType = item.SensorType,
                        Tags = item.Tags,
                        Unit = item.Unit,
                        Status = item.Status,
                        ThresholdsJson = item.ThresholdsJson,
                        DependencyObjid = item.DependencyObjid,
                        Paused = item.Paused,
                        Category = item.Category,
                        CategorySource = item.CategorySource,
                        SyncedAt = syncedAt,
                        CreatedAt = item.CreatedAt != default ? item.CreatedAt : syncedAt
                    };
                    ctx.PrtgSensors.Add(newRow);
                    existing[item.Objid] = newRow;
                }
            }

            ctx.SaveChanges();
            return batch.Count;
        });
    }

    /// <summary>
    /// 依 type 對照表自動填入尚未分類的 sensor（category 為 null 者）。
    /// 回傳實際填入的筆數。人工指定的分類（category 非 null）一律不動。
    /// </summary>
    public int ApplyAutoCategories()
    {
        List<(long Objid, string Category)> toUpdate;
        using (var ctx = _contextFactory())
        {
            var candidates = ctx.PrtgSensors
                .AsNoTracking()
                .Where(s => s.Category == null)
                .Select(s => new { s.Objid, s.SensorType })
                .ToList();

            toUpdate = new List<(long Objid, string Category)>();
            foreach (var item in candidates)
            {
                if (PrtgSensorTypeCategoryMap.Map.TryGetValue(item.SensorType, out var cat))
                {
                    toUpdate.Add((item.Objid, cat));
                }
            }
        }

        if (toUpdate.Count == 0) return 0;

        return BatchWrite(toUpdate, (ctx, batch) =>
        {
            var map = batch.ToDictionary(x => x.Objid, x => x.Category);
            var ids = batch.Select(x => x.Objid).ToList();
            var rows = ctx.PrtgSensors.Where(s => ids.Contains(s.Objid) && s.Category == null).ToList();

            foreach (var row in rows)
            {
                if (map.TryGetValue(row.Objid, out var category))
                {
                    row.Category = category;
                    row.CategorySource = PrtgCategorySources.Auto;
                }
            }

            ctx.SaveChanges();
            return rows.Count;
        });
    }

    /// <summary>
    /// PRTG 狀態變更寫入。以 (sensor_objid, changed_at) 為去重依據，已存在者跳過不新增，回傳實際新增列數。
    /// 查詢既有列時分批查（每批取 sensor_objid 集合與時間範圍比對），避免全表載入記憶體。
    /// </summary>
    public int AppendStateChanges(IReadOnlyList<PrtgStateChangeRow> changes)
    {
        var now = DateTime.Now;
        return BatchWrite(changes, (ctx, batch) =>
        {
            var sensorIds = batch.Select(c => c.SensorObjid).Distinct().ToList();
            var minTime = batch.Min(c => c.ChangedAt);
            var maxTime = batch.Max(c => c.ChangedAt);

            var existingKeys = ctx.PrtgStateChanges
                .Where(c => sensorIds.Contains(c.SensorObjid) && c.ChangedAt >= minTime && c.ChangedAt <= maxTime)
                .Select(c => new { c.SensorObjid, c.ChangedAt })
                .ToList()
                .Select(c => (c.SensorObjid, c.ChangedAt))
                .ToHashSet();

            var addedCount = 0;
            foreach (var item in batch)
            {
                var key = (item.SensorObjid, item.ChangedAt);
                if (existingKeys.Contains(key)) continue;

                existingKeys.Add(key);
                ctx.PrtgStateChanges.Add(new PrtgStateChangeRow
                {
                    SensorObjid = item.SensorObjid,
                    ChangedAt = item.ChangedAt,
                    Status = item.Status,
                    PrevStatus = item.PrevStatus,
                    Message = item.Message,
                    Quality = item.Quality,
                    CreatedAt = item.CreatedAt != default ? item.CreatedAt : now
                });
                addedCount++;
            }

            ctx.SaveChanges();
            return addedCount;
        });
    }

    /// <summary>
    /// PRTG 每小時聚合數值 upsert。自然鍵為 (sensor_objid, period_start)。已存在則更新量測欄位與 CreatedAt（供保留期清理），不存在則新增。
    /// </summary>
    public int UpsertValues(IReadOnlyList<PrtgValueRow> values)
    {
        var now = DateTime.Now;
        return BatchWrite(values, (ctx, batch) =>
        {
            var sensorIds = batch.Select(v => v.SensorObjid).Distinct().ToList();
            var minTime = batch.Min(v => v.PeriodStart);
            var maxTime = batch.Max(v => v.PeriodStart);

            var existing = ctx.PrtgValues
                .Where(v => sensorIds.Contains(v.SensorObjid) && v.PeriodStart >= minTime && v.PeriodStart <= maxTime)
                .ToList()
                .GroupBy(v => (v.SensorObjid, v.PeriodStart))
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var item in batch)
            {
                var key = (item.SensorObjid, item.PeriodStart);
                var writeTime = item.CreatedAt != default ? item.CreatedAt : now;

                if (existing.TryGetValue(key, out var row))
                {
                    row.AvgValue = item.AvgValue;
                    row.MinValue = item.MinValue;
                    row.MaxValue = item.MaxValue;
                    row.Coverage = item.Coverage;
                    row.Quality = item.Quality;
                    row.CreatedAt = writeTime; // 每次覆蓋都更新，保留期依它清理
                }
                else
                {
                    var newRow = new PrtgValueRow
                    {
                        SensorObjid = item.SensorObjid,
                        PeriodStart = item.PeriodStart,
                        AvgValue = item.AvgValue,
                        MinValue = item.MinValue,
                        MaxValue = item.MaxValue,
                        Coverage = item.Coverage,
                        Quality = item.Quality,
                        CreatedAt = writeTime
                    };
                    ctx.PrtgValues.Add(newRow);
                    existing[key] = newRow;
                }
            }

            ctx.SaveChanges();
            return batch.Count;
        });
    }

    /// <summary>
    /// PRTG 主機對應按日寫入。該日期的對應整份就地取代——先刪除該 map_date 的所有列，再寫入新的一批。
    /// 傳入空清單時仍然清空該日，回傳 0。
    /// </summary>
    public int ReplaceHostMapForDate(DateTime mapDate, IReadOnlyList<PrtgHostMapRow> rows)
    {
        var targetDate = mapDate.Date;
        using (var ctx = _contextFactory())
        {
            ctx.PrtgHostMaps.Where(m => m.MapDate == targetDate).ExecuteDelete();
        }

        if (rows == null || rows.Count == 0) return 0;

        var now = DateTime.Now;
        return BatchWrite(rows, (ctx, batch) =>
        {
            foreach (var item in batch)
            {
                ctx.PrtgHostMaps.Add(new PrtgHostMapRow
                {
                    MapDate = targetDate,
                    DeviceObjid = item.DeviceObjid,
                    Ip = item.Ip,
                    HostId = item.HostId,
                    HostName = item.HostName,
                    MapStatus = item.MapStatus,
                    Note = item.Note,
                    CreatedAt = item.CreatedAt != default ? item.CreatedAt : now
                });
            }

            ctx.SaveChanges();
            return batch.Count;
        });
    }

    /// <summary>
    /// 保留期清理：對象三張表（lf_prtg_values、lf_prtg_state_changes、lf_prtg_host_map），
    /// 皆依 created_at &lt; 今天減 retentionDays 刪除。
    /// lf_prtg_devices 與 lf_prtg_sensors 不清（結構鏡像永遠是現況全量）。
    /// </summary>
    public int Prune(int retentionDays) => Prune(retentionDays, BatchedPrune.MaxRowsPerRun, BatchedPrune.BatchSize);

    /// <summary>上限與批次可調的多載，供測試以小數字驗證分批與上限行為</summary>
    internal int Prune(int retentionDays, int maxRows, int batchSize)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);
        var total = 0;

        // lf_prtg_devices 與 lf_prtg_sensors 不清（結構鏡像永遠是現況全量）。
        if (total < maxRows)
        {
            total += BatchedPrune.Run<long>(
                _contextFactory,
                (ctx, take) => ctx.PrtgValues
                    .Where(v => v.CreatedAt < cutoff)
                    .OrderBy(v => v.Id)
                    .Select(v => v.Id)
                    .Take(take)
                    .ToList(),
                (ctx, ids) => ctx.PrtgValues.Where(v => ids.Contains(v.Id)).ExecuteDelete(),
                ctx => ctx.PrtgValues.Count(v => v.CreatedAt < cutoff),
                "PRTG 數值", maxRows - total, batchSize);
        }

        if (total < maxRows)
        {
            total += BatchedPrune.Run<long>(
                _contextFactory,
                (ctx, take) => ctx.PrtgStateChanges
                    .Where(c => c.CreatedAt < cutoff)
                    .OrderBy(c => c.Id)
                    .Select(c => c.Id)
                    .Take(take)
                    .ToList(),
                (ctx, ids) => ctx.PrtgStateChanges.Where(c => ids.Contains(c.Id)).ExecuteDelete(),
                ctx => ctx.PrtgStateChanges.Count(c => c.CreatedAt < cutoff),
                "PRTG 狀態變更", maxRows - total, batchSize);
        }

        if (total < maxRows)
        {
            total += BatchedPrune.Run<(DateTime MapDate, long DeviceObjid)>(
                _contextFactory,
                (ctx, take) => ctx.PrtgHostMaps
                    .Where(m => m.CreatedAt < cutoff)
                    .OrderBy(m => m.MapDate)
                    .ThenBy(m => m.DeviceObjid)
                    .Select(m => new { m.MapDate, m.DeviceObjid })
                    .Take(take)
                    .ToList()
                    .Select(m => (m.MapDate, m.DeviceObjid))
                    .ToList(),
                (ctx, keys) =>
                {
                    var deleted = 0;
                    foreach (var g in keys.GroupBy(k => k.MapDate))
                    {
                        var deviceIds = g.Select(k => k.DeviceObjid).ToList();
                        deleted += ctx.PrtgHostMaps
                            .Where(m => m.MapDate == g.Key && deviceIds.Contains(m.DeviceObjid))
                            .ExecuteDelete();
                    }
                    return deleted;
                },
                ctx => ctx.PrtgHostMaps.Count(m => m.CreatedAt < cutoff),
                "PRTG 主機對應", maxRows - total, batchSize);
        }

        return total;
    }

    /// <summary>
    /// 取得所有 PRTG 裝置鏡像清單（唯讀查詢）。
    /// </summary>
    public List<PrtgDeviceRow> GetAllDevices()
    {
        using var ctx = _contextFactory();
        return ctx.PrtgDevices.AsNoTracking().ToList();
    }

    /// <summary>
    /// 取得所有 PRTG 感測器鏡像清單（唯讀查詢）。
    /// </summary>
    public List<PrtgSensorRow> GetAllSensors()
    {
        using var ctx = _contextFactory();
        return ctx.PrtgSensors.AsNoTracking().ToList();
    }

    /// <summary>取得指定期間的 hourly 數值（依 sensor 與時間排序，匯出用）。</summary>
    public List<PrtgValueRow> GetValues(DateTime fromInclusive, DateTime toExclusive)
    {
        using var ctx = _contextFactory();
        return ctx.PrtgValues
            .AsNoTracking()
            .Where(v => v.PeriodStart >= fromInclusive && v.PeriodStart < toExclusive)
            .OrderBy(v => v.SensorObjid)
            .ThenBy(v => v.PeriodStart)
            .ToList();
    }

    /// <summary>
    /// 取得 sensor 的 objid 與暫停狀態（唯讀，只取這兩欄）。
    /// 供歷史回填使用：回填不重跑結構同步，sensor 清單改從鏡像讀。
    /// </summary>
    public List<(long Objid, bool Paused)> GetSensorTargets()
    {
        using var ctx = _contextFactory();
        return ctx.PrtgSensors.AsNoTracking()
            .Select(s => new { s.Objid, s.Paused })
            .ToList()
            .Select(s => (s.Objid, s.Paused))
            .ToList();
    }

    /// <summary>
    /// 取得指定期間的狀態變更（依 sensor 與時間排序）。判定「跨午夜持續 Down」時，
    /// 呼叫端可把起點往前一天以取得前導狀態。
    /// </summary>
    public List<PrtgStateChangeRow> GetStateChanges(DateTime fromInclusive, DateTime toExclusive)
    {
        using var ctx = _contextFactory();
        return ctx.PrtgStateChanges
            .AsNoTracking()
            .Where(r => r.ChangedAt >= fromInclusive && r.ChangedAt < toExclusive)
            .OrderBy(r => r.SensorObjid)
            .ThenBy(r => r.ChangedAt)
            .ToList();
    }

    /// <summary>取得未暫停 sensor 的現況狀態（判定沉默 device 用）：objid、device、status。</summary>
    public List<(long Objid, long DeviceObjid, string? Status)> GetSensorStatuses()
    {
        using var ctx = _contextFactory();
        return ctx.PrtgSensors
            .AsNoTracking()
            .Where(s => !s.Paused)
            .Select(s => new { s.Objid, s.DeviceObjid, s.Status })
            .ToList()
            .Select(s => (s.Objid, s.DeviceObjid, s.Status))
            .ToList();
    }

    /// <summary>
    /// 取得指定日期的 PRTG 主機對應清單（唯讀查詢）。
    /// </summary>
    public List<PrtgHostMapRow> GetHostMapForDate(DateTime mapDate)
    {
        using var ctx = _contextFactory();
        var targetDate = mapDate.Date;
        return ctx.PrtgHostMaps
            .AsNoTracking()
            .Where(m => m.MapDate == targetDate)
            .ToList();
    }

    /// <summary>取得指定 device 上的全部 sensor（主機明細顯示用）。</summary>
    public List<PrtgSensorRow> GetSensorsByDevice(long deviceObjid)
    {
        using var ctx = _contextFactory();
        return ctx.PrtgSensors
            .AsNoTracking()
            .Where(s => s.DeviceObjid == deviceObjid)
            .OrderBy(s => s.Name)
            .ToList();
    }

    /// <summary>
    /// 取得最近 30 天內最近一個有對應資料日期的 PRTG 主機對應清單（唯讀查詢）。
    /// 從今天往前逐日檢查 GetHostMapForDate，第一個有資料的日期即回傳；若 30 天內皆無資料則回傳空清單。
    /// </summary>
    public List<PrtgHostMapRow> GetLatestHostMap(int maxLookbackDays = 30)
    {
        var today = DateTime.Today;
        for (var i = 0; i < maxLookbackDays; i++)
        {
            var date = today.AddDays(-i);
            var rows = GetHostMapForDate(date);
            if (rows.Count > 0)
            {
                return rows;
            }
        }
        return new List<PrtgHostMapRow>();
    }

    /// <summary>讀取全部人工對應（device_objid → 列）</summary>
    public List<PrtgManualMapRow> GetManualMaps()
    {
        using var ctx = _contextFactory();
        return ctx.PrtgManualMaps.AsNoTracking().ToList();
    }

    /// <summary>新增或更新一筆人工對應。CreatedAt 首次建立時寫入，後續更新不覆蓋。</summary>
    public void UpsertManualMap(PrtgManualMapRow row)
    {
        if (row == null) return;
        using var ctx = _contextFactory();
        var existing = ctx.PrtgManualMaps.FirstOrDefault(m => m.DeviceObjid == row.DeviceObjid);
        if (existing != null)
        {
            existing.HostId = row.HostId;
            existing.CreatedBy = row.CreatedBy;
            existing.Note = row.Note;
            // CreatedAt 保持原值
        }
        else
        {
            var newRow = new PrtgManualMapRow
            {
                DeviceObjid = row.DeviceObjid,
                HostId = row.HostId,
                CreatedBy = row.CreatedBy,
                Note = row.Note,
                CreatedAt = row.CreatedAt != default ? row.CreatedAt : DateTime.Now
            };
            ctx.PrtgManualMaps.Add(newRow);
        }
        ctx.SaveChanges();
    }

    /// <summary>刪除一筆人工對應，回傳刪除筆數。</summary>
    public int DeleteManualMap(long deviceObjid)
    {
        using var ctx = _contextFactory();
        return ctx.PrtgManualMaps.Where(m => m.DeviceObjid == deviceObjid).ExecuteDelete();
    }

    /// <summary>
    /// 取得 PRTG 鏡像五表的概要統計（計數與最近時間點）。
    /// 透過 SQL 聚合查詢，不將整張表載入記憶體。
    /// </summary>
    public PrtgMirrorSummary GetMirrorSummary()
    {
        using var ctx = _contextFactory();

        var deviceCount = ctx.PrtgDevices.Count();
        var sensorCount = ctx.PrtgSensors.Count();

        var lastDeviceSync = ctx.PrtgDevices.Max(d => (DateTime?)d.SyncedAt);
        var lastSensorSync = ctx.PrtgSensors.Max(s => (DateTime?)s.SyncedAt);
        var lastValueAt = ctx.PrtgValues.Max(v => (DateTime?)v.PeriodStart);
        var lastStateChangeAt = ctx.PrtgStateChanges.Max(c => (DateTime?)c.ChangedAt);

        return new PrtgMirrorSummary(
            deviceCount,
            sensorCount,
            lastDeviceSync,
            lastSensorSync,
            lastValueAt,
            lastStateChangeAt);
    }

    /// <summary>
    /// 白名單覆蓋量級：命中白名單的未暫停 sensor 數，以及其中位於
    /// 當日已成功對應（ok）device 上的數量。白名單為空時兩個數字都回 0。
    /// </summary>
    public PrtgWhitelistCoverage GetWhitelistCoverage(IReadOnlyCollection<string>? whitelist, DateTime? mapDate)
    {
        if (whitelist == null || whitelist.Count == 0)
        {
            return new PrtgWhitelistCoverage(0, 0);
        }

        var whitelistSet = new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase);

        using var ctx = _contextFactory();

        var activeSensors = ctx.PrtgSensors
            .AsNoTracking()
            .Where(s => !s.Paused)
            .Select(s => new { s.Objid, s.DeviceObjid, s.SensorType })
            .ToList();

        var matchedSensors = activeSensors
            .Where(s => whitelistSet.Contains(s.SensorType))
            .ToList();

        var whitelistSensorCount = matchedSensors.Count;
        if (whitelistSensorCount == 0 || !mapDate.HasValue)
        {
            return new PrtgWhitelistCoverage(whitelistSensorCount, 0);
        }

        var targetDate = mapDate.Value.Date;
        var okDeviceIds = ctx.PrtgHostMaps
            .AsNoTracking()
            .Where(m => m.MapDate == targetDate && m.MapStatus == PrtgMapStatus.Ok)
            .Select(m => m.DeviceObjid)
            .ToHashSet();

        var onMappedDeviceCount = matchedSensors.Count(s => okDeviceIds.Contains(s.DeviceObjid));

        return new PrtgWhitelistCoverage(whitelistSensorCount, onMappedDeviceCount);
    }

    /// <summary>
    /// 選出要擷取數值的 sensor：位於指定 device 上、未暫停、且 type 命中白名單者。
    /// 白名單為 null 或空代表不限制 type（沿用設定語意）。device 集合為空時回空清單。
    /// </summary>
    public List<long> GetValueFetchTargets(IReadOnlyCollection<string>? whitelist, IReadOnlyCollection<long> deviceObjids)
    {
        if (deviceObjids == null || deviceObjids.Count == 0)
        {
            return new List<long>();
        }

        using var ctx = _contextFactory();

        var query = ctx.PrtgSensors
            .AsNoTracking()
            .Where(s => !s.Paused && deviceObjids.Contains(s.DeviceObjid))
            .Select(s => new { s.Objid, s.SensorType })
            .ToList();

        if (whitelist == null || whitelist.Count == 0)
        {
            return query.Select(s => s.Objid).ToList();
        }

        var whitelistSet = new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase);

        return query
            .Where(s => whitelistSet.Contains(s.SensorType))
            .Select(s => s.Objid)
            .ToList();
    }
}

/// <summary>
/// PRTG 鏡像表統計摘要
/// </summary>
public sealed record PrtgMirrorSummary(
    int DeviceCount,
    int SensorCount,
    DateTime? LastDeviceSync,
    DateTime? LastSensorSync,
    DateTime? LastValueAt,
    DateTime? LastStateChangeAt);

/// <summary>
/// PRTG 白名單覆蓋量級統計
/// </summary>
public sealed record PrtgWhitelistCoverage(int WhitelistSensorCount, int OnMappedDeviceCount);
