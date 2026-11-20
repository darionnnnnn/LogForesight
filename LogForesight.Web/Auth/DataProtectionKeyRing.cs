using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;

namespace LogForesight.Web.Auth;

/// <summary>
/// Data Protection 金鑰環的存放（docs/WEB-SPEC.md §6.1）。登入 cookie 與 PRTG 分頁游標都靠它加解密／簽章。
///
/// 不設定存放位置時，框架把金鑰放在目前帳號的設定檔目錄（服務帳號常常沒有）或記憶體——
/// 重啟、換帳號或換部署目錄後金鑰環消失，全員被登出、分頁游標失效。這裡固定存到資料目錄下的
/// <c>dataprotection</c> 子資料夾，並固定 <see cref="ApplicationName"/>（框架預設以內容根目錄路徑當應用名稱，
/// 部署路徑一變金鑰就對不上）。
///
/// 金鑰環遺失的後果只是全員重新登入與舊的分頁游標失效，不影響資料，不必備份。
/// 目錄權限限縮：Unix 目錄 700、既有檔案 600；Windows 移除繼承，只留執行帳號與 SYSTEM。
/// </summary>
public static class DataProtectionKeyRing
{
    public const string ApplicationName = "LogForesight";

    public const string DirectoryName = "dataprotection";

    public static string KeyDirectory(string dataRoot) => Path.Combine(dataRoot, DirectoryName);

    public static IServiceCollection AddLogForesightDataProtection(this IServiceCollection services, string dataRoot)
    {
        services.AddDataProtection().SetApplicationName(ApplicationName);

        // 不用 PersistKeysToFileSystem：它在註冊當下就建立目錄，而權限要在建立時就限縮、且測試組裝 DI 時不該落地檔案。
        // 這裡等第一次真的用到金鑰環才準備目錄（Program 啟動時另會提早準備一次，失敗就停止啟動）。
        services.AddOptions<KeyManagementOptions>().Configure<ILoggerFactory>((options, loggerFactory) =>
            options.XmlRepository = new FileSystemXmlRepository(Prepare(KeyDirectory(dataRoot)), loggerFactory));
        return services;
    }

    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 建立（或收緊）金鑰環目錄。冪等：目錄已存在就只重設權限。
    /// 失敗時記錄與 <see cref="Console.Error"/> 都帶目錄路徑與目前執行身分（診斷 ACL 問題最需要這兩項），再把原例外擲出。
    /// </summary>
    public static DirectoryInfo Prepare(string path)
    {
        try
        {
            return PrepareCore(path);
        }
        catch (Exception ex)
        {
            var message = FailureMessage(path, CurrentIdentityName());
            Log.Error(ex, message);
            Console.Error.WriteLine(message + "（" + ex.GetType().Name + "）");
            throw;
        }
    }

    /// <summary>準備失敗的說明（含目錄與執行身分）；獨立成函式供測試驗證內容</summary>
    internal static string FailureMessage(string path, string identity) =>
        $"Data Protection 金鑰環目錄準備失敗：目錄 {path}、執行身分 {identity}。請確認該身分對此目錄有建立與寫入權限。";

    private static string CurrentIdentityName()
    {
        try
        {
            return OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().Name : Environment.UserName;
        }
        catch (Exception)
        {
            return Environment.UserName;
        }
    }

    private static DirectoryInfo PrepareCore(string path)
    {
        var directory = new DirectoryInfo(path);
        if (OperatingSystem.IsWindows())
        {
            PrepareWindows(directory);
        }
        else
        {
            const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (!directory.Exists) Directory.CreateDirectory(path, DirectoryMode);
            File.SetUnixFileMode(path, DirectoryMode);
            foreach (var file in directory.EnumerateFiles())
                File.SetUnixFileMode(file.FullName, FileMode);
        }

        return directory;
    }

    [SupportedOSPlatform("windows")]
    private static void PrepareWindows(DirectoryInfo directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

        if (!directory.Exists) directory.Create(security);
        else directory.SetAccessControl(security);
    }
}
