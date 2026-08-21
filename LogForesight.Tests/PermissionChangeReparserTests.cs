using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 既有 NetIQ 權限異動列的重剖回填（作業 D）：舊解析器在單行訊息上把欄位切到行尾、
/// 也抓不到群組區段內的群組名，留下一批髒值；解析器修好後用原始訊息重剖補回。
/// </summary>
public sealed class PermissionChangeReparserTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private PermissionChangeReparser NewReparser() =>
        new(_fixture.NewContext,
            new PermissionChangeReparseStateStore(new EfJsonBlobStore(_fixture.NewContext, PermissionChangeReparser.StateBlobKey)));

    /// <summary>舊版寫進 DB 的髒列：對象是訊息尾巴、目標帳號吞到行尾</summary>
    private const string SingleLineMessage =
        "已新增成員到已啟用安全性的萬用群組。 主體: 安全性識別碼: S-1-5-21-1-2-3-17749 帳戶名稱: OP_ACCT " +
        "帳戶網域: DOM1 成員: 安全性識別碼: S-1-5-21-1-2-3-278464 帳戶名稱: CN=User One,OU=Dept,DC=example,DC=com " +
        "群組: 安全性識別碼: S-1-5-21-1-2-3-40793 帳戶名稱: GROUP_A 帳戶網域: DOM1";

    private PermissionChangeRecord DirtyRecord(string changeId = "dirty-1", string? alertText = null) => new()
    {
        ChangeId = changeId,
        HostName = "SRV-OLD",
        DetectedAt = new DateTime(2026, 8, 18, 17, 40, 0),
        Target = string.Empty,                                   // 舊解析器剖不出群組名
        ChangeType = "成員新增",
        Category = PermissionCategory.GroupMember,
        TargetAccount = "OP_ACCT 帳戶網域: DOM1 成員: ...",       // 舊解析器吞到行尾的髒值
        Before = "（不在群組中）",
        After = string.Empty,
        AlertText = alertText ?? SingleLineMessage,
        Source = PermissionChangeSources.Netiq,
        EventId = 4756
    };

    [Fact]
    public void 重剖回填_舊髒列的對象與目標帳號被修正()
    {
        var store = new PermissionChangeStore(_fixture.NewContext);
        store.AppendChanges(new[] { DirtyRecord() });

        NewReparser().Run();

        var row = Assert.Single(store.Query(null, null, 100));
        Assert.Equal("GROUP_A", row.Target);
        Assert.Equal("CN=User One,OU=Dept,DC=example,DC=com", row.TargetAccount);
        Assert.Equal("OP_ACCT", row.InitiatorAccount);
    }

    /// <summary>alert_text 只有原訊息前 500 字，被截掉群組段的列剖不回來——維持原值不洗成空。</summary>
    [Fact]
    public void 重剖回填_剖不出的欄位維持原值()
    {
        var store = new PermissionChangeStore(_fixture.NewContext);
        store.AppendChanges(new[] { DirtyRecord("truncated", alertText: "已新增成員到已啟用安全性的萬用群組。 主體: 帳戶名稱: OP_ACCT") });

        NewReparser().Run();

        var row = Assert.Single(store.Query(null, null, 100));
        Assert.Equal(string.Empty, row.Target);                       // 剖不出就維持原本的空
        Assert.Equal("OP_ACCT 帳戶網域: DOM1 成員: ...", row.TargetAccount);   // 髒值保留，不被洗掉
        Assert.Equal("OP_ACCT", row.InitiatorAccount);                // 剖得出的仍補上
    }

    [Fact]
    public void 重剖回填_彙總列跳過不處理()
    {
        var store = new PermissionChangeStore(_fixture.NewContext);
        store.AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = "summary-1",
                HostName = "SRV-OLD",
                DetectedAt = new DateTime(2026, 8, 18),
                Target = "SRV-OLD（彙總）",
                ChangeType = "權限異動（彙總）",
                Category = PermissionCategory.Summary,
                AlertText = SingleLineMessage,        // 就算內容剖得出東西也不該動它
                Source = PermissionChangeSources.Netiq
            }
        });

        NewReparser().Run();

        var row = Assert.Single(store.Query(null, null, 100));
        Assert.Equal("SRV-OLD（彙總）", row.Target);
    }

    [Fact]
    public void 重剖回填_跑兩次結果相同且第二次不再掃描()
    {
        var store = new PermissionChangeStore(_fixture.NewContext);
        store.AppendChanges(new[] { DirtyRecord() });

        var reparser = NewReparser();
        reparser.Run();
        Assert.True(reparser.IsCompleted);

        reparser.Run();   // 已完成：直接返回

        var row = Assert.Single(store.Query(null, null, 100));
        Assert.Equal("GROUP_A", row.Target);
    }

    /// <summary>對象修對之後特權判定才成立——舊列的對象是訊息尾巴，永遠命中不了關鍵字。</summary>
    [Fact]
    public void 重剖回填_特權旗標依修正後的對象重算()
    {
        var store = new PermissionChangeStore(_fixture.NewContext);
        var privMessage = SingleLineMessage.Replace("GROUP_A", "Domain Admins");
        var dirty = DirtyRecord("priv-1", privMessage);
        dirty.IsPrivilegedTarget = false;                 // 舊列因為對象是髒值而沒被標
        store.AppendChanges(new[] { dirty });

        NewReparser().Run();

        var row = Assert.Single(store.Query(null, null, 100));
        Assert.Equal("Domain Admins", row.Target);
        Assert.True(row.IsPrivilegedTarget);
    }

    /// <summary>重剖也要吃欄位對應設定：用非標準欄位名的站台，沒帶對應等於白跑一趟，
    /// 而重剖是一次性工作、跑完不會自動重來（作業 B 與 D 之間的接線）。</summary>
    [Fact]
    public void 重剖回填_套用自訂欄位對應()
    {
        var store = new PermissionChangeStore(_fixture.NewContext);
        store.AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = "custom-1",
                HostName = "SRV-OLD",
                DetectedAt = new DateTime(2026, 8, 18),
                Target = string.Empty,
                ChangeType = "成員新增",
                Category = PermissionCategory.GroupMember,
                AlertText = "CustomGrp: GROUP_X CustomMem: DOM1" + Sep + "userx",
                Source = PermissionChangeSources.Netiq,
                EventId = 4728
            }
        });

        var mappings = new PermissionFieldMappings(
            operatorFields: null,
            memberFields: new[] { "CustomMem" },
            groupFields: new[] { "CustomGrp" },
            objectFields: null);

        NewReparser().Run(default, mappings);

        var row = Assert.Single(store.Query(null, null, 100));
        Assert.Equal("GROUP_X", row.Target);
        Assert.Equal("DOM1" + Sep + "userx", row.TargetAccount);
    }

    private const string Sep = @"\";

    /// <summary>舊列的操作者本身就是髒值（被吞了行尾）——回填不能把它當成「事件自帶的操作者」
    /// 餵回解析器，否則那個欄位永遠修不好。</summary>
    [Fact]
    public void 重剖回填_操作者是髒值時也會被修正()
    {
        var store = new PermissionChangeStore(_fixture.NewContext);
        var dirty = DirtyRecord("dirty-initiator");
        dirty.InitiatorAccount = "OP_ACCT 帳戶網域: DOM1 成員: ...";   // 舊解析器吞到行尾的操作者
        store.AppendChanges(new[] { dirty });

        NewReparser().Run();

        var row = Assert.Single(store.Query(null, null, 100));
        Assert.Equal("OP_ACCT", row.InitiatorAccount);
    }
}
