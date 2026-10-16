using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 監看政策：可選的主機／sensor 縮圈（docs/PRTG-SPEC.md §7）。兩份清單都空＝全部已對應主機與其 sensor；
/// 主機清單有值時每日評估只算清單內的啟用主機，sensor 清單有值時狀態型與數值規則只評估清單內的 sensor
/// （silent 是裝置層判定，不受 sensor 清單限制）。
/// </summary>
public sealed class PrtgMonitoringPolicy
{
    /// <summary>每次儲存遞增的版次字串，供編輯畫面做樂觀並行比對。</summary>
    public string Revision { get; set; } = string.Empty;
    public List<long> HostIds { get; set; } = [];
    public List<long> SensorIds { get; set; } = [];
}

/// <summary>舊版政策文件裡多出的欄位（Core 身分、時區、文化、時間授權…）在反序列化時直接略過。</summary>
public sealed class PrtgMonitoringPolicyStore(EfJsonBlobStore blob) : JsonBlobSingleton<PrtgMonitoringPolicy>(blob)
{
    public const string BlobKey = "prtg_monitoring_policy";
}
