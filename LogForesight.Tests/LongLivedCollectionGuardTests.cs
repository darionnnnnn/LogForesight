using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using LogForesight.Web.Configuration;
using LogForesight.Web.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 長駐集合守門（第 77 輪 M-8）：站台以月計不重啟，singleton 與 static 上的可變集合只要沒有上限或清除時機，
/// 就是慢速洩漏（R67 排程三天吃光 24 GB 的教訓）。每個這類欄位都要登錄在 <see cref="Registry"/>，
/// 寫明上限或何時清；新增欄位沒登錄就紅，逼新增者回答「它會不會一直長」。
/// 登錄不是執行期機制，只是讓這個問題有人回答；數量上限的行為由各自的單元測試守。
/// </summary>
public sealed class LongLivedCollectionGuardTests(ITestOutputHelper output)
{
    /// <summary>欄位（型別完整名稱.欄位名）→ 上限或清除時機。</summary>
    private static readonly Dictionary<string, string> Registry = new(StringComparer.Ordinal)
    {
        ["LogForesight.Web.Filters.DefaultInputLimitsFilter.UnboundedProperties"] = "以請求 DTO 型別為鍵的反射快取；型別數量固定（程式碼內定義），不隨請求成長",
        ["LogForesight.Core.Analysis.LinuxAuthParser.LoginFailureRuleIds"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Core.Analysis.SentinelQueryBuilder.LinuxNoisyPrograms"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Core.CryptoHelper.ReportedFailures"] = "每種解密失敗只報一次的標記；鍵是憑證名稱（數量有限），重新載入金鑰時清空",
        ["LogForesight.Core.Models.PrtgVolumeSensorTypes.Types"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Core.Persistence.PerRunIssueOwnerStore._cached"] = "夜間協調器每趟新建一個包裝、趟結束即可回收；只留整份問題負責人清單一份，Update／Delete 後丟棄",
        ["LogForesight.Core.Persistence.Sql.SchemaUpgrader.UpgraderIndexNames"] = "結構升級建立的索引名稱；數量固定（程式碼內的索引定義），不隨資料成長",
        ["LogForesight.Core.Persistence.Sql.SqlPerformanceMonitor._topSlow"] = "最慢操作只留前 10 筆（TopSlowCapacity）",
        ["LogForesight.Core.Service.EventLogService.ClassicApiLogs"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Core.Service.HostDayPostProcessor.PermissionChangeEventTypes"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Core.Service.HostDayWorkflowService._touched"] = "分析主流程每趟新建一個實例、趟結束即可回收；站台常駐實例只在還原路徑寫入且不追蹤（trackTouched: false）",
        ["LogForesight.Core.Service.PermissionChangeExtractor.KnownMultiWordKeys"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Core.Service.PrtgAddressResolver._cache"] = "位址解析快取上限 1 萬筆，正快取 5 分鐘、負快取 60 秒",
        ["LogForesight.Core.Service.PrtgLegacyJsonTransferValidator+StreamingParser.ArrayProperties"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Core.Service.PrtgOperationScope.Active"] = "進行中的 PRTG 操作範圍；每個範圍以 using／finally 結束時移除",
        ["LogForesight.Core.Service.PrtgOperationScope.Versions"] = "已結束的範圍版本只留最近 32 筆",
        ["LogForesight.Core.Service.PrtgRuleCatalog.Rules"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Core.Service.PrtgValueSemantics.CodeToSemantic"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Core.Service.RunTimingPhases.SubLabels"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Core.Service.RunTimingPhases.TopLevelLabels"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Auth.LoginThrottle._accounts"] = "上限 1 萬個鍵（MaxKeys），每次呼叫清過期",
        ["LogForesight.Web.Auth.LoginThrottle._ips"] = "上限 1 萬個鍵（MaxKeys），每次呼叫清過期",
        ["LogForesight.Web.Auth.RevokedTokens._revoked"] = "每次撤銷清掉全部已過期項；不設筆數上限——上限會讓被撤銷的 token 重新可用。量＝token 效期內的登出次數",
        ["LogForesight.Web.Configuration.WebAppSettings.KnownDevSecrets"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.ActionableSnapshotCache._entries"] = "256 條目＋20 萬元素上限；讀取時每個 TTL 週期釋放過期與舊版本",
        ["LogForesight.Web.Services.VisibleHostKeyPart.Memo"] = "ConditionalWeakTable：鍵（可見主機集合物件）被回收時條目自動消失；只記憶 IReadOnlySet",
        ["LogForesight.Web.Services.AiInsightService.KnownCategories"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.AiInsightService.KnownRisks"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.AuditQueryService.ActionNames"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.HostDayWorkflowRecoveryHostedService._oversized"] = "列被讀到後保留 2 天（InMemoryRetention），走完一次深層巡掃才丟；巡掃預算見第 78 輪批次 O",
        ["LogForesight.Web.Services.HostDayWorkflowRecoveryHostedService._waiting"] = "同 _oversized：保留 2 天並等一次深層巡掃",
        ["LogForesight.Web.Services.Import.ImportService.Pending"] = "預覽暫存 30 分鐘，新預覽或套用時清過期；單筆受 ImportMaxRows 限制，只有管理者可上傳",
        ["LogForesight.Web.Services.IssueOwnedHostIdsCache._entries"] = "256 條目；版本推進時整批清空",
        ["LogForesight.Web.Services.IssueRankingCache._entries"] = "256 條目＋20 萬元素上限；讀取時每個 TTL 週期釋放過期與舊版本",
        ["LogForesight.Web.Services.Mail.MailNotificationService._ledgerFullWarnedAtUtc"] = "每種帳本已滿只警告一次的時間戳；寫入時移除過期項",
        ["LogForesight.Web.Services.NextUnhandledSequenceCache._entries"] = "256 條目；讀取時每個 TTL 週期釋放過期與舊版本",
        ["LogForesight.Web.Services.PrtgBackfillService._fullPreviews"] = "上限 128 筆，建立時清過期",
        ["LogForesight.Web.Services.PrtgDeviceIndexCache._entries"] = "條目數上限（MaxEntries），滿了整批清空；TTL 短",
        ["LogForesight.Web.Services.PrtgProbeRunState._output"] = "開頭最多 1,500 行（OutputHeadLimit），新一趟開始時清空",
        ["LogForesight.Web.Services.PrtgProbeRunState._outputTail"] = "最新 500 行環形保留（OutputTailCapacity），新一趟開始時清空",
        ["LogForesight.Web.Services.PrtgSnapshotHostedService._confirmedEmptyDevices"] = "已確認沒有感測器的裝置；上限是取數範圍內的裝置數，結構同步寫過鏡像後清空",
        ["LogForesight.Web.Services.PrtgSnapshotHostedService._overrideObjidsNotFound"] = "人工指定但鏡像找不到的 objid；上限是人工指定數，結構同步寫過鏡像後清空",
        ["LogForesight.Web.Services.PrtgSnapshotHostedService._pendingWrite"] = "待寫入的快照批次；由待寫檔（PrtgSnapshotJournal）的 20 萬列／64 MiB 上限約束",
        ["LogForesight.Web.Services.RunExportDeidentifier.FileExtensions"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.RunExportDeidentifier.KeepDomains"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.RunExportDeidentifier.KeepPrincipals"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.RunExportDeidentifier.KeepProfiles"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.RunExportDeidentifier.NotFqdn"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.RunExportDeidentifier.Tlds"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.RunExportService.KnownCategories"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.RunExportService.KnownEmptyShapes"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.RunExportService.KnownEndedBy"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.RunExportService.KnownPhaseOutcomes"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.RunExportService.KnownPrtgOutcomes"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.SchedulerRunState._recentScheduleAttempts"] = "只留最近 10 筆",
        ["LogForesight.Web.Services.SlidingWindowThrottle`1._calls"] = "上限 maxKeys（預設 1 萬），新鍵滿了先清過期再拒絕",
        ["LogForesight.Web.Services.SentinelEventFetchService.Cache"] = "上限 512 筆、每筆最多 50 則訊息、TTL 10 分鐘，滿了整批清",
        ["LogForesight.Web.Services.SetupReadinessService.OldSevenStepIds"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.SetupReadinessService.StepDefinitionsById"] = "常數查找表：型別初始化時建好，之後只讀",
        ["LogForesight.Web.Services.SummaryCache._entries"] = "256 條目；讀取時每個 TTL 週期釋放過期與舊版本",
    };

    private static readonly Type[] MutableCollectionDefinitions =
    [
        typeof(List<>), typeof(Dictionary<,>), typeof(HashSet<>), typeof(Queue<>), typeof(Stack<>),
        typeof(SortedDictionary<,>), typeof(SortedSet<>), typeof(LinkedList<>), typeof(SortedList<,>),
        typeof(System.Collections.Concurrent.ConcurrentDictionary<,>), typeof(System.Collections.Concurrent.ConcurrentQueue<>),
        typeof(System.Collections.Concurrent.ConcurrentBag<>), typeof(System.Collections.Concurrent.ConcurrentStack<>),
        typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>),
    ];

    private static bool IsMutableCollection(Type t) =>
        t.IsGenericType && MutableCollectionDefinitions.Contains(t.GetGenericTypeDefinition());

    private static IEnumerable<Assembly> ProductAssemblies() =>
        [typeof(ServiceCollectionExtensions).Assembly, typeof(LogForesight.Core.Persistence.StorageBackend).Assembly];

    /// <summary>DI 裡生命週期為 singleton 的實作型別（含背景服務）；以工廠註冊、只知道介面的，取組件內所有實作該介面的類別。</summary>
    private static HashSet<Type> SingletonTypes()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new WebAppSettings());
        services.AddStorage(new WebAppSettings());
        services.AddLogForesightAuth(new WebAppSettings());
        services.AddLogForesightServices();

        var concrete = ProductAssemblies().SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && !t.IsDefined(typeof(CompilerGeneratedAttribute))).ToList();
        var result = new HashSet<Type>();
        foreach (var d in services.Where(d => d.Lifetime == ServiceLifetime.Singleton))
        {
            if (d.ImplementationType != null) { result.Add(d.ImplementationType); continue; }
            if (d.ImplementationInstance != null) { result.Add(d.ImplementationInstance.GetType()); continue; }
            if (d.ServiceType.IsClass && !d.ServiceType.IsAbstract) { result.Add(d.ServiceType); continue; }
            foreach (var t in concrete.Where(t => d.ServiceType.IsAssignableFrom(t))) result.Add(t);
        }
        return result.Where(t => ProductAssemblies().Contains(t.Assembly)).ToHashSet();
    }

    private static List<string> LongLivedCollectionFields()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var type in ProductAssemblies().SelectMany(a => a.GetTypes()))
            foreach (var f in type.GetFields(all | BindingFlags.Static))
                if (!f.IsLiteral && IsMutableCollection(f.FieldType) && !f.IsDefined(typeof(CompilerGeneratedAttribute)))
                    found.Add($"{type.FullName}.{f.Name}");

        foreach (var type in SingletonTypes())
            for (var t = type; t != null && ProductAssemblies().Contains(t.Assembly); t = t.BaseType)
                foreach (var f in t.GetFields(all | BindingFlags.Instance))
                    if (IsMutableCollection(f.FieldType))
                        // 泛型類別以定義名登錄（SlidingWindowThrottle`1），不隨型別引數各列一次
                        found.Add($"{(t.IsGenericType ? t.GetGenericTypeDefinition() : t).FullName}.{f.Name}");

        return found.ToList();
    }

    [Fact]
    public void 長駐物件的可變集合都已登錄上限或清除時機()
    {
        var fields = LongLivedCollectionFields();
        var missing = fields.Where(f => !Registry.ContainsKey(f)).ToList();
        foreach (var f in missing) output.WriteLine(f);
        Assert.True(missing.Count == 0,
            $"下列 {missing.Count} 個長駐集合沒有登錄上限或清除時機（見 LongLivedCollectionGuardTests.Registry）：{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
    }

    [Fact]
    public void 登錄清單沒有已不存在的欄位()
    {
        var fields = LongLivedCollectionFields().ToHashSet(StringComparer.Ordinal);
        var stale = Registry.Keys.Where(k => !fields.Contains(k)).ToList();
        Assert.True(stale.Count == 0, $"登錄清單有已不存在的欄位：{string.Join("、", stale)}");
    }

    [Fact]
    public void 每筆登錄都寫了上限或清除時機()
    {
        Assert.All(Registry, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
    }
}
