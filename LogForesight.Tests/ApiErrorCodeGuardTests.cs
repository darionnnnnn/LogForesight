using System.Reflection;
using System.Text.RegularExpressions;
using LogForesight.Web.Models;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 錯誤碼登錄守門：錯誤碼 → 狀態碼只有 <see cref="ApiErrorCodes.Table"/> 一個事實來源。
/// 程式裡傳給 Fail／DomainException 的碼一律用 const，不寫字面；Core 的傳輸儲存層因不能參照 Web，
/// 其字面碼則必須在 ApiErrorCodes 登錄（讓表與實際回應對得上）。
/// </summary>
public class ApiErrorCodeGuardTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles(params string[] projects)
    {
        var root = FindRepoRoot();
        foreach (var project in projects)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = file[root.Length..];
                if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;
                yield return file;
            }
        }
    }

    private static Dictionary<string, string> RegisteredCodes() =>
        typeof(ApiErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void FailAndDomainException_NeverTakeStringLiteralCode()
    {
        var pattern = new Regex(@"(?:ApiResponse(?:<[^()]*>)?\.Fail|new\s+DomainException)\(\s*""", RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var file in SourceFiles("LogForesight.Core", "LogForesight.Web"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in pattern.Matches(text))
            {
                var line = text.AsSpan(0, m.Index).Count('\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0, "錯誤碼不可用字串字面（請改用 ApiErrorCodes 常數）：" + string.Join("、", offenders));
    }

    [Fact]
    public void EveryErrorCodeConst_IsRegisteredInTable()
    {
        // validation_failed 在表外＝400 是刻意的預設值（StatusOf 對未登錄碼一律回 400），不登錄
        var missing = RegisteredCodes()
            .Where(kv => kv.Value != ApiErrorCodes.ValidationFailed && !ApiErrorCodes.Table.ContainsKey(kv.Value))
            .Select(kv => kv.Key)
            .ToList();

        Assert.True(missing.Count == 0, "以下錯誤碼常數未登錄於 ApiErrorCodes.Table：" + string.Join("、", missing));
        Assert.Equal(400, ApiErrorCodes.StatusOf(ApiErrorCodes.ValidationFailed));
        Assert.DoesNotContain(ApiErrorCodes.ValidationFailed, ApiErrorCodes.Table.Keys);
    }

    [Fact]
    public void NewGenericCodes_MapToTheirStatus()
    {
        Assert.Equal(429, ApiErrorCodes.StatusOf(ApiErrorCodes.RateLimited));
        Assert.Equal(503, ApiErrorCodes.StatusOf(ApiErrorCodes.ServiceUnavailable));
        Assert.Equal(401, ApiErrorCodes.StatusOf(ApiErrorCodes.AuthFailed));
        Assert.Equal(503, ApiErrorCodes.StatusOf(ApiErrorCodes.CaptureBusy));
    }

    [Fact]
    public void TransferStoreLiteralCodes_AreRegistered()
    {
        // Core 不能參照 Web 的 ApiErrorCodes，傳輸儲存層與其呼叫端的字面碼必須在此登錄
        var registered = RegisteredCodes().Values.ToHashSet();
        var thrown = new Regex(@"PrtgTransferStoreException\(\s*""([a-z0-9_]+)""", RegexOptions.Compiled);
        var helper = new Regex(@"(?:BadRequest|Conflict)\(""([a-z0-9_]+)""", RegexOptions.Compiled);
        var capacity = new Regex(@"new\(""([a-z0-9_]+)"",\s*message,", RegexOptions.Compiled);
        var offenders = new SortedSet<string>();
        foreach (var file in SourceFiles("LogForesight.Core", "LogForesight.Web"))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            foreach (Match m in thrown.Matches(text))
                if (!registered.Contains(m.Groups[1].Value)) offenders.Add($"{name}:{m.Groups[1].Value}");
            if (name == "EfPrtgTransferStore.cs")
            {
                foreach (Match m in helper.Matches(text))
                    if (!registered.Contains(m.Groups[1].Value)) offenders.Add($"{name}:{m.Groups[1].Value}");
                foreach (Match m in capacity.Matches(text))
                    if (!registered.Contains(m.Groups[1].Value)) offenders.Add($"{name}:{m.Groups[1].Value}");
            }
        }

        Assert.True(offenders.Count == 0, "傳輸儲存層字面碼未登錄：" + string.Join("、", offenders));
    }
}
