using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SentinelFieldMap.MapEntryType：sev/xdasOutcome → EventLogEntryType 的推導
/// （docs/NETIQ-API-REFERENCE.md §3.3，三輪 --netiq-probe 真實輸出實證的部分見各測試註解）。
/// </summary>
public class SentinelFieldMapTests
{
    [Fact]
    public void Security頻道_xdasOutcome為0時判定為成功稽核()
    {
        // 第二、三輪 probe 實證：4624/4776 等成功事件 xdasoutcome=0、XDAS_OUT_SUCCESS
        var type = SentinelFieldMap.MapEntryType("Security", severity: 0, xdasOutcome: 0);

        Assert.Equal(EventLogEntryType.SuccessAudit, type);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Security頻道_xdasOutcome非0時判定為失敗稽核(int xdasOutcome)
    {
        // 第三輪 probe 實證：4771 Kerberos 預先驗證失敗 xdasoutcome=1、XDAS_OUT_INVALID_INPUT
        var type = SentinelFieldMap.MapEntryType("Security", severity: 4, xdasOutcome: xdasOutcome);

        Assert.Equal(EventLogEntryType.FailureAudit, type);
    }

    [Fact]
    public void Security頻道_xdasOutcome缺席時保守判定為失敗稽核()
    {
        var type = SentinelFieldMap.MapEntryType("Security", severity: 0, xdasOutcome: null);

        Assert.Equal(EventLogEntryType.FailureAudit, type);
    }

    [Fact]
    public void 非Security頻道_sev1判定為Information()
    {
        // 第三輪 probe 實證：System 頻道 NTFS「磁碟區健康情況良好」sev=1；
        // Application 頻道 Security-SPP 通知 sev=1
        var type = SentinelFieldMap.MapEntryType("System", severity: 1, xdasOutcome: null);

        Assert.Equal(EventLogEntryType.Information, type);
    }

    [Fact]
    public void 非Security頻道_sev4判定為Error()
    {
        var type = SentinelFieldMap.MapEntryType("Application", severity: 4, xdasOutcome: null);

        Assert.Equal(EventLogEntryType.Error, type);
    }

    [Fact]
    public void 頻道名比對不分大小寫_小寫system走非Security分支()
    {
        var type = SentinelFieldMap.MapEntryType("system", severity: 0, xdasOutcome: 0);

        Assert.Equal(EventLogEntryType.Information, type);
    }

    [Fact]
    public void 頻道名比對不分大小寫_全大寫SECURITY走Security分支()
    {
        var type = SentinelFieldMap.MapEntryType("SECURITY", severity: 0, xdasOutcome: 0);

        Assert.Equal(EventLogEntryType.SuccessAudit, type);
    }

    // ── Linux（docs/archive/FEEDBACK-12-PLAN.md §4.4，輪 B 第 3/4 項定案）──────────────

    [Theory]
    [InlineData(0, EventLogEntryType.Information)]
    [InlineData(1, EventLogEntryType.Information)]
    [InlineData(2, EventLogEntryType.Warning)]
    [InlineData(3, EventLogEntryType.Error)]
    [InlineData(4, EventLogEntryType.Error)]
    [InlineData(5, EventLogEntryType.Error)]
    public void MapEntryTypeLinux_sev對應EntryType(int severity, EventLogEntryType expected)
    {
        // 輪 B 實測分佈：sev0=1.87M/sev1=7.67M/sev2=8/sev3~5=2.4k（全站/24h）——
        // 這個對應是計數用途的務實選擇，不是 syslog priority 語意還原（NetworkManager 的
        // <warn> 與 dockerd 的 level=error 皆落在 sev=1，見 SentinelFieldMap 的文件註解）
        Assert.Equal(expected, SentinelFieldMap.MapEntryTypeLinux(severity));
    }

    [Fact]
    public void MapEntryTypeLinux_不依賴頻道名參數_純以sev判定()
    {
        // 與 Windows 版 MapEntryType 的關鍵差異：Linux 沒有 Security 特例（xdasoutcome 不存在），
        // 純粹依 sev 分段，函數簽章上也刻意不收頻道名參數
        Assert.Equal(EventLogEntryType.Error, SentinelFieldMap.MapEntryTypeLinux(5));
    }
}
