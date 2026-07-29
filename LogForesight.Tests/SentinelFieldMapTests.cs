using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SentinelFieldMap.MapEntryType：sev/xdasOutcome → EventLogEntryType 的推導
/// （docs/NETIQ-API-PLAN.md §3.3，三輪 --netiq-probe 真實輸出實證的部分見各測試註解）。
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
}
