using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SentinelEventMapper：SentinelEvent（Sentinel 原始投影）→ EventLogEntryData（既有五層偵測吃的模型）
/// 的映射。fixture 依三輪 --netiq-probe 真實環境輸出的欄位形狀建構（docs/NETIQ-API-REFERENCE.md §3.5），
/// **值已去識別化**（真實 IP／主機名／網域名換成範例用假值，欄位鍵與值的「形狀」保持真實）。
/// </summary>
public class SentinelEventMapperTests
{
    /// <summary>第二、三輪 probe 實證的登入成功事件（4624）形狀。</summary>
    private static SentinelEvent LoginSuccessFixture() => new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["dt"] = "2026-07-29T03:07:28.000Z",
        ["sev"] = "0",
        ["dhn"] = "SRV-DC02",
        ["sn"] = "SRV-DC02",
        ["repip"] = "10.1.2.12",
        ["sip"] = "10.5.6.7",
        ["obssvcname"] = "Microsoft-Windows-Security-Auditing",
        ["rv150"] = "Security",
        ["rv40"] = "4624",
        ["evt"] = "帳戶已順利登入。",
        ["msg"] = "帳戶已順利登入。    主體：安全性識別碼：S-1-5-18   帳戶名稱：svc-test.corp",
        ["sun"] = "svc-test.corp",
        ["xdasoutcome"] = "0"
    });

    /// <summary>第三輪 probe 實證的 Kerberos 預先驗證失敗（4771）形狀。</summary>
    private static SentinelEvent AuthFailureFixture() => new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["dt"] = "2026-07-29T03:07:27.000Z",
        ["sev"] = "4",
        ["dhn"] = "SRV-DC01",
        ["sn"] = "SRV-DC01",
        ["repip"] = "10.1.2.11",
        ["sip"] = "10.5.6.8",
        ["obssvcname"] = "Microsoft-Windows-Security-Auditing",
        ["rv150"] = "Security",
        ["rv40"] = "4771",
        ["evt"] = "Kerberos 預先驗證失敗。",
        ["msg"] = "Kerberos 預先驗證失敗。    帳戶資訊：安全性識別碼：S-1-5-21-...",
        ["sun"] = "y99999999$",
        ["xdasoutcome"] = "1"
    });

    /// <summary>第三輪 probe 實證的 System 頻道 Information 事件形狀（NTFS 磁碟區健康通知）。
    /// 刻意不含 xdasoutcome——真實樣本裡非稽核類事件就是沒有這個欄位。</summary>
    private static SentinelEvent SystemInformationFixture() => new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["dt"] = "2026-07-28T15:05:44.000Z",
        ["sev"] = "1",
        ["dhn"] = "SRV-DC03",
        ["sn"] = "SRV-DC03",
        ["repip"] = "10.1.2.13",
        ["obssvcname"] = "Microsoft-Windows-Ntfs",
        ["rv150"] = "System",
        ["rv40"] = "98",
        ["evt"] = "磁碟區健康情況良好。",
        ["msg"] = "磁碟區健康情況良好。不需要採取任何動作。"
    });

    [Fact]
    public void 登入成功事件_映射出正確的EventId與Source與頻道()
    {
        var mapped = SentinelEventMapper.Map(LoginSuccessFixture());

        Assert.NotNull(mapped);
        Assert.Equal(4624, mapped!.EventId);
        Assert.Equal("Microsoft-Windows-Security-Auditing", mapped.Source);
        Assert.Equal("Security", mapped.LogName);
        Assert.Equal(EventLogEntryType.SuccessAudit, mapped.EntryType);
        Assert.Contains("svc-test.corp", mapped.Message);
    }

    [Fact]
    public void 驗證失敗事件_映射為FailureAudit()
    {
        var mapped = SentinelEventMapper.Map(AuthFailureFixture());

        Assert.NotNull(mapped);
        Assert.Equal(4771, mapped!.EventId);
        Assert.Equal(EventLogEntryType.FailureAudit, mapped.EntryType);
    }

    [Fact]
    public void System頻道Information事件_映射為Information()
    {
        var mapped = SentinelEventMapper.Map(SystemInformationFixture());

        Assert.NotNull(mapped);
        Assert.Equal("System", mapped!.LogName);
        Assert.Equal("Microsoft-Windows-Ntfs", mapped.Source);
        Assert.Equal(EventLogEntryType.Information, mapped.EntryType);
    }

    [Fact]
    public void 時間欄位由UTC轉為本機時區_與既有EventRecordMapper的TimeGenerated語意一致()
    {
        var mapped = SentinelEventMapper.Map(LoginSuccessFixture());

        // dt 是 UTC；本機（EventRecordMapper 讀 Event Log）的 TimeGenerated 是機器當地時間，
        // Program.cs 的日切界判定依賴這個前提一致——用 DateTimeOffset.ToLocalTime() 算期望值，
        // 不寫死時區位移，才不會綁死在某個特定 CI 機器的時區設定上。
        var expected = DateTimeOffset.Parse("2026-07-29T03:07:28.000Z").ToLocalTime().DateTime;
        Assert.Equal(expected, mapped!.TimeGenerated);
    }

    [Fact]
    public void msg缺席時退回evt當訊息()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dt"] = "2026-07-29T03:07:28.000Z",
            ["sev"] = "0",
            ["rv150"] = "System",
            ["rv40"] = "1",
            ["obssvcname"] = "Test-Provider",
            ["evt"] = "測試事件名"
        };

        var mapped = SentinelEventMapper.Map(new SentinelEvent(fields));

        Assert.Equal("測試事件名", mapped!.Message);
    }

    [Fact]
    public void dt欄位缺席時回傳null()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sev"] = "0" };

        var mapped = SentinelEventMapper.Map(new SentinelEvent(fields));

        Assert.Null(mapped);
    }

    [Fact]
    public void dt格式無法解析時回傳null_不擲例外()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["dt"] = "not-a-date" };

        var mapped = SentinelEventMapper.Map(new SentinelEvent(fields));

        Assert.Null(mapped);
    }

    [Fact]
    public void MapAll_略過解析失敗的筆數並回報數量()
    {
        var events = new List<SentinelEvent>
        {
            LoginSuccessFixture(),
            new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),  // 缺 dt，解析失敗
            AuthFailureFixture()
        };

        var (mapped, skipped) = SentinelEventMapper.MapAll(events);

        Assert.Equal(2, mapped.Count);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void 數字欄位格式異常時當0處理_不擲例外()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dt"] = "2026-07-29T03:07:28.000Z",
            ["sev"] = "not-a-number",
            ["rv40"] = "also-not-a-number"
        };

        var mapped = SentinelEventMapper.Map(new SentinelEvent(fields));

        Assert.NotNull(mapped);
        Assert.Equal(0, mapped!.EventId);
    }
}
