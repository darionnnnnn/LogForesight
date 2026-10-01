using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>只追蹤影響 PRTG 範圍的主機欄位；NetIQ 更新回報時間不應中止 PRTG。</summary>
public sealed class PrtgScopeRevisionReader(StorageBackend backend, IHostStore hosts)
{
    private readonly object _gate = new();
    private long _hostVersion = -1;
    private string _hostFingerprint = "";
    private long _sentinelVersion = -1;
    private string _sentinelFingerprint = "";

    public string Read()
    {
        lock (_gate)
        {
            var version = backend.Blob("hosts").ReadVersion();
            if (version != _hostVersion)
            {
                var projection = hosts.GetAll().OrderBy(h => h.HostId)
                    .Select(h => new { h.HostId, h.HostName, h.IpAddress, h.Active, h.MergedInto, h.SentinelId, h.Source });
                _hostFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(projection))));
                // 版本在讀取內容前取得；有競爭時至多下個安全點重算，不把舊內容標成新版本。
                _hostVersion = version;
            }
            var sentinelVersion = backend.Blob("sentinels").ReadVersion();
            if (sentinelVersion != _sentinelVersion)
            {
                _sentinelFingerprint = JsonSerializer.Serialize(new SentinelStore(backend.Blob("sentinels")).GetAll()
                    .OrderBy(s => s.SentinelId).Select(s => new { s.SentinelId, s.Active, s.BaseUrl }));
                _sentinelVersion = sentinelVersion;
            }
            return $"{backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion()}:{backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadVersion()}:{backend.Blob("rules").ReadVersion()}:{_sentinelFingerprint}:{_hostFingerprint}";
        }
    }
}
