using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>金鑰檔建立當下就是限縮權限；互斥鎖名稱跨工作階段（第 79 輪批次 G6、G7）</summary>
public class CryptoKeyFileCreationTests
{
    [Fact]
    public void Unix_建立當下模式即為600_不經過較寬的狀態()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "lf-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (CryptoKeyBootstrapper.CreateRestrictedKeyFile(path))
            {
                // 檔案還開著、尚未寫入任何內容時就檢查：模式必須已是 600
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 已存在時不覆寫_擲IOException交給呼叫端沿用()
    {
        var path = Path.Combine(Path.GetTempPath(), "lf-key-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "existing");
        try
        {
            Assert.ThrowsAny<IOException>(() => CryptoKeyBootstrapper.CreateRestrictedKeyFile(path));
            Assert.Equal("existing", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [WindowsFact]
    public void Windows_建立當下即不繼承且只授權目前身分與系統管理員()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "lf-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (CryptoKeyBootstrapper.CreateRestrictedKeyFile(path)) { }
            var acl = new FileInfo(path).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            Assert.Equal(2, acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 金鑰啟動互斥鎖名稱帶Global前綴() =>
        Assert.StartsWith(@"Global\", CryptoKeyBootstrapper.MutexName, StringComparison.Ordinal);
}
