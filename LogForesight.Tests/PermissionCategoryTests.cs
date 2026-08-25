using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 權限異動紀錄結構化欄位與類別推導測試（task-PC-A1）。
/// </summary>
public class PermissionCategoryTests
{
    [Theory]
    [InlineData("成員新增", PermissionCategory.GroupMember)]
    [InlineData("成員移除", PermissionCategory.GroupMember)]
    [InlineData("權限新增（ACL 規則）", PermissionCategory.FolderAcl)]
    [InlineData("權限移除（ACL 規則）", PermissionCategory.FolderAcl)]
    [InlineData("權限變更", PermissionCategory.FolderAcl)]
    [InlineData("擁有者變更", PermissionCategory.OwnerChange)]
    [InlineData("無法存取", PermissionCategory.FolderAccess)]
    [InlineData("恢復可存取", PermissionCategory.FolderAccess)]
    [InlineData("稽核政策變更", PermissionCategory.AuditPolicy)]
    [InlineData("權限異動（彙總）", PermissionCategory.Summary)]
    [InlineData("例行同步（彙總）", PermissionCategory.Summary)]
    public void 十個已知異動類型皆正確推導至指定類別且不落入其他(string changeType, string expectedCategory)
    {
        var category = PermissionCategory.Resolve(changeType);

        Assert.Equal(expectedCategory, category);
        Assert.NotEqual(PermissionCategory.Other, category);
    }

    [Theory]
    [InlineData("自訂未知異動")]
    [InlineData("未知類型XYZ")]
    [InlineData("invalid_change_type")]
    public void 未知的異動類型推導至其他且不拋例外(string unknownType)
    {
        var category = PermissionCategory.Resolve(unknownType);

        Assert.Equal(PermissionCategory.Other, category);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 異動類型為空字串或Null時推導至其他且不拋例外(string? emptyOrNullType)
    {
        var category = PermissionCategory.Resolve(emptyOrNullType);

        Assert.Equal(PermissionCategory.Other, category);
    }

    [Fact]
    public void 特權目標判定_本機Administrators群組且成員新增_回傳True()
    {
        var isPrivileged = PermissionCategory.IsPrivilegedTarget("本機 Administrators 群組", "成員新增");

        Assert.True(isPrivileged);
    }

    [Fact]
    public void 特權目標判定_本機Administrators群組但成員移除_回傳False()
    {
        var isPrivileged = PermissionCategory.IsPrivilegedTarget("本機 Administrators 群組", "成員移除");

        Assert.False(isPrivileged);
    }

    [Fact]
    public void 特權目標判定_一般共用資料夾路徑且成員新增_回傳False()
    {
        var isPrivileged = PermissionCategory.IsPrivilegedTarget(@"D:\共用資料夾\業務", "成員新增");

        Assert.False(isPrivileged);
    }

    [Theory]
    [InlineData("DOMAIN ADMINS")]
    [InlineData("builtin\\administrators")]
    [InlineData("CN=ENTERPRISE ADMINS,CN=Users,DC=corp,DC=local")]
    [InlineData("SCHEMA ADMINS")]
    [InlineData("account operators")]
    [InlineData("BACKUP OPERATORS")]
    public void 特權目標判定_特權群組關鍵字大小寫不同時仍為True(string target)
    {
        var isPrivileged = PermissionCategory.IsPrivilegedTarget(target, "成員新增");

        Assert.True(isPrivileged);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 特權目標判定_目標為Null或空白字串_回傳False(string? target)
    {
        var isPrivileged = PermissionCategory.IsPrivilegedTarget(target, "成員新增");

        Assert.False(isPrivileged);
    }

    [Theory]
    [InlineData("權限變更")]
    [InlineData("擁有者變更")]
    [InlineData("無法存取")]
    [InlineData("稽核政策變更")]
    public void 特權目標判定_非成員新增異動類型_回傳False(string changeType)
    {
        var isPrivileged = PermissionCategory.IsPrivilegedTarget("Administrators", changeType);

        Assert.False(isPrivileged);
    }

    [Theory]
    [InlineData(PermissionCategory.GroupMember, "群組成員異動")]
    [InlineData(PermissionCategory.FolderAcl, "資料夾權限異動")]
    [InlineData(PermissionCategory.OwnerChange, "擁有者變更")]
    [InlineData(PermissionCategory.FolderAccess, "資料夾存取狀態")]
    [InlineData(PermissionCategory.AuditPolicy, "稽核政策變更")]
    [InlineData(PermissionCategory.Summary, "例行同步彙總")]
    [InlineData(PermissionCategory.Other, "其他")]
    public void 類別標籤查詢_已知類別回傳對應中文標籤(string category, string expectedLabel)
    {
        var label = PermissionCategory.GetLabel(category);

        Assert.Equal(expectedLabel, label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown_category")]
    [InlineData("non_existent_key")]
    public void 類別標籤查詢_未知或Null類別回傳其他標籤而非Key本身(string? unknownCategory)
    {
        var label = PermissionCategory.GetLabel(unknownCategory);

        Assert.Equal("其他", label);
        if (unknownCategory != null)
        {
            Assert.NotEqual(unknownCategory, label);
        }
    }

    [Fact]
    public void PermissionChangeRecord新建立實例時Category非空且預設為other()
    {
        var record = new PermissionChangeRecord();

        Assert.False(string.IsNullOrEmpty(record.Category));
        Assert.Equal(PermissionCategory.Other, record.Category);
        Assert.False(record.IsPrivilegedTarget);
        Assert.Null(record.InitiatorAccount);
        Assert.Null(record.TargetAccount);
    }

    [Fact]
    public void 寫入端的彙總字串與Resolve往返一致()
    {
        // 驗證的是「寫入端用的字串確實被 Resolve 認得」這件事本身，
        // 而不是把標籤字面值再抄一遍（那種斷言永遠綠，抓不到兩邊不一致）
        Assert.Equal(PermissionCategory.Summary, PermissionCategory.Resolve(HostDayPostProcessor.RoutineSyncChangeType));
    }
}
