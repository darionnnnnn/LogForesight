using System.Diagnostics;
using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// EventLogReader（新式 Operational 頻道）的核心契約：EventRecord 的 Level/Keywords → legacy
/// EventLogEntryType 的映射決定 Defender/RDP 事件在聚合鍵與錯誤/警告/稽核計數中的分類，並保留
/// classic 的「Critical → 0」慣例讓兩條讀取路徑計數一致。這裡鎖住 MapEntryType 的每個分支
/// （Map 需真實 EventRecord，無法在單元測試中建構，其讀取行為由真實主機的實測比對驗證）。
/// </summary>
public class EventRecordMapperTests
{
    // 標準稽核 Keywords（winnt.h）
    private const long AuditSuccess = 0x20000000000000L;
    private const long AuditFailure = 0x10000000000000L;

    [Fact]
    public void Level1的Critical映射為0保留classic慣例()
    {
        // EventLogEntryType 沒有 Critical 值，classic 讀 Critical 事件（如 Kernel-Power 41）時 EntryType 就是 0
        Assert.Equal(0, (int)EventRecordMapper.MapEntryType(1, null, isAuditChannel: false));
    }

    [Theory]
    [InlineData((byte)2, EventLogEntryType.Error)]
    [InlineData((byte)3, EventLogEntryType.Warning)]
    [InlineData((byte)4, EventLogEntryType.Information)]
    [InlineData((byte)5, EventLogEntryType.Information)]
    [InlineData((byte)0, EventLogEntryType.Information)]
    public void 非稽核頻道依Level映射(byte level, EventLogEntryType expected)
    {
        Assert.Equal(expected, EventRecordMapper.MapEntryType(level, null, isAuditChannel: false));
    }

    [Fact]
    public void Level為null時映射為Information()
    {
        Assert.Equal(EventLogEntryType.Information, EventRecordMapper.MapEntryType(null, null, isAuditChannel: false));
    }

    [Fact]
    public void 稽核頻道依Keywords判成功失敗稽核而非Level()
    {
        // Security 事件 Level 常為 0/4，必須看 Keywords 才分得出成功/失敗稽核
        Assert.Equal(EventLogEntryType.FailureAudit, EventRecordMapper.MapEntryType(0, AuditFailure, isAuditChannel: true));
        Assert.Equal(EventLogEntryType.SuccessAudit, EventRecordMapper.MapEntryType(0, AuditSuccess, isAuditChannel: true));
    }

    [Fact]
    public void 稽核頻道失敗優先於成功()
    {
        // 同時帶兩個 keyword（理論上罕見）時，失敗稽核優先——寧可當成失敗也不漏看
        Assert.Equal(EventLogEntryType.FailureAudit,
            EventRecordMapper.MapEntryType(0, AuditFailure | AuditSuccess, isAuditChannel: true));
    }

    [Fact]
    public void 稽核頻道無稽核Keyword時退回Level映射()
    {
        Assert.Equal(EventLogEntryType.Error, EventRecordMapper.MapEntryType(2, 0, isAuditChannel: true));
        Assert.Equal(EventLogEntryType.Information, EventRecordMapper.MapEntryType(4, null, isAuditChannel: true));
    }

    [Fact]
    public void 非稽核頻道忽略稽核Keywords()
    {
        // Operational/System 頻道即使 Keywords 帶了稽核位元也不該被判成稽核事件——用 Level 走
        Assert.Equal(EventLogEntryType.Error, EventRecordMapper.MapEntryType(2, AuditFailure, isAuditChannel: false));
        Assert.Equal(EventLogEntryType.Information, EventRecordMapper.MapEntryType(4, AuditSuccess, isAuditChannel: false));
    }
}
