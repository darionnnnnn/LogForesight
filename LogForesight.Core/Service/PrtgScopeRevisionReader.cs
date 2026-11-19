using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// 只追蹤影響 PRTG 範圍的主機欄位；NetIQ 更新回報時間不應中止 PRTG。
/// <para>
/// 內部快取（主機／Sentinel／監看政策縮圈指紋）都以資料庫版本為鍵、內容是純鏡像結構，與使用者無關，
/// 可跨週期、跨請求共用同一個實例；所有讀取都在 <c>_gate</c> 內進行，多執行緒呼叫安全。
/// 可變欄位只有字串與版本號，沒有集合。
/// </para>
/// </summary>
public sealed class PrtgScopeRevisionReader(StorageBackend backend, IHostStore hosts)
{
    private readonly object _gate = new();
    private long _hostVersion = -1;
    private string _hostFingerprint = "";
    private long _sentinelVersion = -1;
    private string _sentinelFingerprint = "";
    private long _policyVersion = -1;
    private string _policyRawFingerprint = "";

    private const string HostsBlobKey = "hosts";
    private const string SentinelsBlobKey = "sentinels";
    private const string RulesBlobKey = "rules";

    /// <summary>
    /// 完整柵欄（含政策與規則版本）。輸出字串格式是郵件冪等鍵與快照佇列雜湊的持久鍵，不得更動。
    /// </summary>
    public string Read() => ReadCore(includePolicyRevision: true, includeRules: true);

    /// <summary>Structural fence for consumers that independently revalidate each policy-dependent issue.</summary>
    public string ReadStructural() => ReadCore(includePolicyRevision: false, includeRules: false);

    /// <summary>
    /// 長時間取值工作（快照、範圍補抓）的範圍柵欄：結構範圍＋監看政策的主機／sensor 縮圈。
    /// 不含整份政策的 blob 版本，政策裡與縮圈無關的編輯不會打斷進行中的取值。
    /// </summary>
    public string ReadRawSemantic() => ReadCore(includePolicyRevision: false, includeRules: false, includeRawPolicy: true);

    // includeRules=false 時規則版本欄位固定填 0：範圍計算與回填都不讀規則，存規則不應中止進行中的工作。
    private string ReadCore(bool includePolicyRevision, bool includeRules, bool includeRawPolicy = false)
    {
        lock (_gate)
        {
            // These five keys jointly define the active PRTG scope. Read only their versions,
            // in one bounded query, while preserving a fresh database read for every checkpoint.
            // Explicit comparisons keep this fixed-key query provider-neutral (no collection
            // parameter translation such as SQL Server OPENJSON is needed).
            Dictionary<string, long> versions;
            using (backend.Performance.Measure("prtg:ScopeRevision:ReadVersions"))
            using (var context = backend.CreateContext())
            {
                versions = context.Blobs.AsNoTracking()
                    .Where(row => row.BlobKey == HostsBlobKey ||
                                  row.BlobKey == SentinelsBlobKey ||
                                  row.BlobKey == EfPrtgStore.ScopeRevisionBlobKey ||
                                  row.BlobKey == PrtgMonitoringPolicyStore.BlobKey ||
                                  row.BlobKey == RulesBlobKey)
                    // Canonicalize in SQL with the same equality semantics as the filter.
                    // A SQL Server collation can match historical casing/trailing spaces;
                    // returning the physical key would lose that match in an ordinal dictionary.
                    .Select(row => new
                    {
                        BlobKey = row.BlobKey == HostsBlobKey ? HostsBlobKey :
                                  row.BlobKey == SentinelsBlobKey ? SentinelsBlobKey :
                                  row.BlobKey == EfPrtgStore.ScopeRevisionBlobKey ? EfPrtgStore.ScopeRevisionBlobKey :
                                  row.BlobKey == PrtgMonitoringPolicyStore.BlobKey ? PrtgMonitoringPolicyStore.BlobKey : RulesBlobKey,
                        row.Version
                    })
                    .ToDictionary(row => row.BlobKey, row => row.Version, StringComparer.Ordinal);
            }

            var version = versions.GetValueOrDefault(HostsBlobKey);
            if (version != _hostVersion)
            {
                var projection = hosts.GetAll().OrderBy(h => h.HostId)
                    .Select(h => new { h.HostId, h.HostName, h.IpAddress, h.Active, h.MergedInto, h.SentinelId, h.Source });
                _hostFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(projection))));
                // 版本在讀取內容前取得；有競爭時至多下個安全點重算，不把舊內容標成新版本。
                _hostVersion = version;
            }
            var sentinelVersion = versions.GetValueOrDefault(SentinelsBlobKey);
            if (sentinelVersion != _sentinelVersion)
            {
                _sentinelFingerprint = JsonSerializer.Serialize(new SentinelStore(backend.Blob("sentinels")).GetAll()
                    .OrderBy(s => s.SentinelId).Select(s => new { s.SentinelId, s.Active, s.BaseUrl }));
                _sentinelVersion = sentinelVersion;
            }
            var rawSuffix = "";
            if (includeRawPolicy)
            {
                // 政策縮圈指紋以政策 blob 版本快取：版本沒變沿用，變了才重讀整份政策。
                // 版本在讀內容前取得（同主機指紋）；有競爭時至多下個安全點重算。
                var rawVersion = versions.GetValueOrDefault(PrtgMonitoringPolicyStore.BlobKey);
                if (rawVersion != _policyVersion)
                {
                    var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                    var rawScope = JsonSerializer.Serialize(new
                    {
                        HostIds = policy.HostIds.Order().ToArray(),
                        SensorIds = policy.SensorIds.Order().ToArray()
                    });
                    _policyRawFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawScope)));
                    _policyVersion = rawVersion;
                }
                rawSuffix = $":{_policyRawFingerprint}";
            }
            var policyVersion = includePolicyRevision ? versions.GetValueOrDefault(PrtgMonitoringPolicyStore.BlobKey) : 0;
            var rulesVersion = includeRules ? versions.GetValueOrDefault(RulesBlobKey) : 0;
            return $"{versions.GetValueOrDefault(EfPrtgStore.ScopeRevisionBlobKey)}:{policyVersion}:{rulesVersion}:{_sentinelFingerprint}:{_hostFingerprint}{rawSuffix}";
        }
    }
}
