using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 守門一（docs/WEB-SPEC.md §7.2）：把例外的 <c>.Message</c> 放進回應、DTO、<c>Fail(</c>、<c>DomainException(</c>
/// 或維護主控台的寫法，只能出現在這份白名單；新增的寫法必須先說明理由進白名單，
/// 或改用 <c>UserFacingMessage.For(ex, 備用說明)</c>。純記錄檔的行（同行有 <c>Log.xxx(</c>）不列入。
/// 白名單每一項都必須還對得到程式碼，對不到＝過期，要刪掉。
/// </summary>
public class UserFacingMessageGuardTests
{
    private const string DomainOnly = "只捕捉專屬／已登錄型別，訊息是該型別自己寫好的中文";
    private const string SelfAuthored = "捕捉範圍僅限單一呼叫、該呼叫自擲的中文業務訊息（非框架例外）";

    private static readonly Regex MessageUse = new(
        @"\b(?:ex|exception|[A-Za-z]*Ex|workflowError|error|domain|conflict|busy|transfer|deidentification)\.Message\b",
        RegexOptions.Compiled);

    private static readonly Regex LogOnly = new(@"\b(?:Log|logger)\.[A-Za-z]+\(", RegexOptions.Compiled);

    /// <summary>（檔案結尾, 該行片段, 理由）</summary>
    private static readonly (string File, string Fragment, string Reason)[] Allowed =
    {
        ("Auth/Ldap/LdapCredentialVerifier.cs", "CredentialCheckResult.Fail($\"AD 驗證發生例外", "FailureReason 只在伺服器內判斷成敗，登入回應一律是「登入失敗」，沒有消費端回給使用者"),
        ("Auth/Ldap/LdapService.cs", "AdSubCodeRegex.Match(ex.Message", "只解析子代碼，不外傳"),
        ("Configuration/AppSettings.cs", "errors.Add(ex.Message)", "啟動前檢查 appsettings 的 InvalidOperationException（自擲中文），只寫啟動失敗輸出，不是 API 回應"),
        ("Controllers/Api/CalibrationController.cs", "exception.Message);", DomainOnly + "（CalibrationCapacityException）"),
        ("Controllers/Api/PrtgHostMappingAdminController.cs", "catch (PrtgScopeConflictException ex)", DomainOnly),
        ("Controllers/Api/PrtgHostMappingAdminController.cs", "ex is PrtgScopeConflictException or PrtgScopePurgeBusyException ? ex.Message", "先判型別為已登錄的專屬例外才用原訊息，其餘給固定說明"),
        ("Controllers/Api/PrtgMonitoringController.cs", "catch (InvalidOperationException ex)", "只包住 Store().Update 的交易，兩處自擲的中文衝突訊息；" + SelfAuthored),
        ("Controllers/Api/PrtgScopeEstimateController.cs", "throw DomainException.Validation(ex.Message)", "PrtgScopePurgeStore 自擲的中文預覽狀態訊息（不含 PrtgScopePurgeBusyException）；" + SelfAuthored),
        ("Controllers/Api/PrtgScopePurgeJobsController.cs", "throw DomainException.Validation(ex.Message)", "PrtgScopePurgeStore 自擲的中文作業狀態訊息；" + SelfAuthored),
        ("Controllers/Api/PrtgTransferController.cs", "FailureCode(ex.Message)", "只把訊息對成錯誤碼，回應用 SafeValidationMessage 的固定文字"),
        ("Controllers/Api/PrtgTransferController.cs", "exception.Code, exception.Message", DomainOnly + "（PrtgTransferStoreException）"),
        ("Controllers/Api/ScheduleController.cs", "throw DomainException.Validation(ex.Message)", "SentinelQueryBuilder.NormalizeSubnetPrefix 自擲的中文輸入格式訊息；" + SelfAuthored),
        ("Filters/ApiExceptionFilter.cs", "domain.Message", "例外過濾器本身：只對已登錄型別取訊息"),
        ("Filters/ApiExceptionFilter.cs", "conflict.Message", "例外過濾器本身：只對已登錄型別取訊息"),
        ("Filters/ApiExceptionFilter.cs", "busy.Message", "例外過濾器本身：只對已登錄型別取訊息"),
        ("Filters/ApiExceptionFilter.cs", "transfer.Message", "例外過濾器本身：只對已登錄型別取訊息"),
        ("Filters/ApiExceptionFilter.cs", "deidentification.Message", "例外過濾器本身：只對已登錄型別取訊息"),
        ("Models/UserFacingMessage.cs", "ex.Message", "規則小幫手本身：已先判斷型別"),
        ("Program.cs", "\"啟動失敗：\" + ex.Message", "啟動期寫到主控台給部署人員，站台尚未提供任何 API"),
        ("Services/SentinelAdminService.cs", "? ex.Message", "只有 SafeCode＝invalid-base-url（位址未設定或格式不正確）才給原文，訊息只含使用者自己填的位址與修正方式"),
        ("Services/Import/ImportService.cs", "CSV 解析失敗：{ex.Message}", "CsvParseException 的訊息是 CsvParser 自己寫的中文（行號與欄位名），不含路徑"),
        ("Services/IssueOwnerAdminService.cs", "ex is DomainException or LogForesight.Core.Persistence.WorkOrderRuleException ? ex.Message", "先判型別為領域例外或已登錄的 WorkOrderRuleException 才用原訊息，其餘給固定說明"),
        ("Services/NetiqDirectoryClient.cs", "throw new NetiqDiscoveryException(ex.Message)", "NormalizeSubnetPrefix 自擲的中文輸入格式訊息；" + SelfAuthored),
        ("Services/NetiqDiscoveryService.cs", "throw DomainException.Validation(ex.Message)", DomainOnly + "（NetiqDiscoveryException，內含的底層例外已改成固定說明）"),
        ("Services/PrtgBackfillService.cs", "error = ex.Message", "只捕捉 DomainException"),
        ("Services/PrtgDiagnosticExportActionResult.cs", "error is PrtgTransferStoreException ? error.Message", "先判型別為已登錄的專屬例外才用原訊息，其餘給固定說明"),
        ("Services/PrtgSnapshotHostedService.cs", "InvalidDataException or PrtgSnapshotJournalLockedException => ex.Message", "兩種例外都是 PrtgSnapshotJournal 自己寫的中文（不含路徑）；其餘型別給固定說明"),
        ("Services/WorkOrderCommandService.cs", "throw DomainException.Validation(ex.Message)", DomainOnly + "（只捕捉 WorkOrderRuleException；其餘例外記錄後往上拋）"),
        ("Services/WorkOrderReplyService.cs", "throw DomainException.Validation(ex.Message)", DomainOnly + "（只捕捉 WorkOrderRuleException；其餘例外記錄後往上拋）"),
        ("Services/WorkOrderReplyService.cs", "ex is DomainException ? ex.Message", "先判型別為領域例外才用原訊息，其餘給固定說明"),
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    /// <summary>多行的記錄呼叫：前面兩行內開了 Log.xxx( 而該行之前沒有以分號結束</summary>
    private static bool IsLogContinuation(string[] lines, int index)
    {
        for (var back = 1; back <= 2 && index - back >= 0; back++)
        {
            var previous = lines[index - back].Trim();
            if (previous.EndsWith(";") || previous.EndsWith("}") || previous.EndsWith("{")) return false;
            if (LogOnly.IsMatch(previous)) return true;
        }
        return false;
    }

    private static IEnumerable<(string File, int Line, string Text)> Scan(string file, string content)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i].Trim();
            if (text.StartsWith("//") || text.StartsWith("*")) continue;
            if (!MessageUse.IsMatch(text) || LogOnly.IsMatch(text) || IsLogContinuation(lines, i)) continue;
            yield return (file, i + 1, text);
        }
    }

    private static bool IsAllowed((string File, int Line, string Text) hit) =>
        Allowed.Any(a => hit.File.EndsWith(a.File, StringComparison.Ordinal) && hit.Text.Contains(a.Fragment, StringComparison.Ordinal));

    private static List<(string File, int Line, string Text)> RealHits()
    {
        var webRoot = Path.Combine(FindRepoRoot(), "LogForesight.Web");
        var hits = new List<(string File, int Line, string Text)>();
        foreach (var path in Directory.EnumerateFiles(webRoot, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(webRoot, path).Replace('\\', '/');
            if (rel.StartsWith("bin/") || rel.StartsWith("obj/")) continue;
            hits.AddRange(Scan(rel, File.ReadAllText(path)));
        }
        return hits;
    }

    [Fact]
    public void 例外訊息放進回應的寫法只出現在白名單()
    {
        var violations = RealHits().Where(h => !IsAllowed(h)).Select(h => $"{h.File}:{h.Line}  {h.Text}").ToList();
        Assert.True(violations.Count == 0,
            "以下寫法把例外原文放進回應／畫面；改用 UserFacingMessage.For(ex, 備用說明) 並把原文寫進 Log，或附理由加進白名單：\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void 白名單每一項都還對得到程式碼()
    {
        var hits = RealHits();
        var stale = Allowed.Where(a => !hits.Any(h => h.File.EndsWith(a.File, StringComparison.Ordinal)
            && h.Text.Contains(a.Fragment, StringComparison.Ordinal)))
            .Select(a => $"{a.File}  {a.Fragment}").ToList();
        Assert.True(stale.Count == 0, "白名單項目已對不到程式碼，請刪除：\n" + string.Join("\n", stale));
    }

    /// <summary>E6-a：暫存反例——白名單外把 ex.Message 放進回應 → 守門比得到；記錄檔寫法與小幫手不誤判</summary>
    [Fact]
    public void 反例_白名單外的寫法比得到()
    {
        const string bad = "return new ResultDto { Success = false, Message = $\"失敗：{ex.Message}\" };";
        Assert.False(IsAllowed(Assert.Single(Scan("Services/FooService.cs", bad))));

        const string failCall = "return BadRequest(ApiResponse.Fail(ApiErrorCodes.ValidationFailed, ex.Message));";
        Assert.False(IsAllowed(Assert.Single(Scan("Controllers/Api/FooController.cs", failCall))));

        const string domainCall = "throw DomainException.Validation($\"解析失敗：{liveEx.Message}\");";
        Assert.False(IsAllowed(Assert.Single(Scan("Services/FooService.cs", domainCall))));

        // 同一個檔案，白名單內的寫法仍放行（檔案與片段都要對得上）
        const string allowed = "throw DomainException.Validation(ex.Message);";
        Assert.True(IsAllowed(Assert.Single(Scan("Controllers/Api/ScheduleController.cs", allowed))));
        Assert.False(IsAllowed(Assert.Single(Scan("Controllers/Api/OtherController.cs", allowed))));

        // 純記錄檔的行不列入
        Assert.Empty(Scan("Services/FooService.cs", "Log.Warn(ex, \"失敗：{0}\", ex.Message);"));
        // 規則小幫手的寫法不是 .Message 取用
        Assert.Empty(Scan("Services/FooService.cs", "var m = UserFacingMessage.For(ex, \"請稍後再試。\");"));
    }

    // ───────── 守門二（批次 E-2）：給使用者的訊息字面不得以「英文識別字＋冒號」開頭 ─────────

    /// <summary>
    /// 掃描範圍：LogForesight.Core 與 LogForesight.Web 的 *.cs（排除 bin／obj）中，下列寫法的第一個字串字面——
    /// <c>DomainException(</c>／<c>DomainException.Xxx(</c>、<c>Fail(碼, "…")</c>、<c>new XxxException("…")</c>、<c>Message = "…"</c>。
    /// 命中條件：字面開頭是英文識別字加全形冒號，或半形冒號加空白（類別名前綴，例如「FooService：失敗」）。
    /// 設定鍵（<c>Storage:Xxx</c>）與格式標記（<c>enc:v1</c>）的半形冒號後沒有空白，不命中。
    /// </summary>
    private static readonly Regex MessageLiteral = new(
        @"(?:DomainException(?:\.\w+)?\(|\bFail\([^"");]*,\s*|new\s+\w*Exception\(|\bMessage\s*=\s*)\$?@?""([^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex ClassPrefix = new(@"^[A-Za-z][A-Za-z0-9_]*(?:：|: )", RegexOptions.Compiled);

    private static IEnumerable<string> PrefixedMessages(string content) =>
        MessageLiteral.Matches(content).Select(m => m.Groups[1].Value).Where(t => ClassPrefix.IsMatch(t));

    private static IEnumerable<(string Rel, string Content)> CoreAndWebSources()
    {
        var root = FindRepoRoot();
        foreach (var project in new[] { "LogForesight.Core", "LogForesight.Web" })
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
                yield return (rel, File.ReadAllText(path));
            }
    }

    [Fact]
    public void 守門二_Core與Web給使用者的訊息不以英文識別字加冒號開頭()
    {
        var violations = CoreAndWebSources()
            .SelectMany(f => PrefixedMessages(f.Content).Select(t => $"{f.Rel}  {t}")).ToList();
        Assert.True(violations.Count == 0, "訊息不要以類別名前綴開頭（給使用者的訊息只講事情）：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void 守門二_反例_類別名前綴命中_一般中文句不命中()
    {
        Assert.Single(PrefixedMessages("throw new InvalidOperationException(\"FooService：失敗\");"));
        Assert.Single(PrefixedMessages("throw DomainException.Validation($\"FooService：交辦單 {id} 已結案。\");"));
        Assert.Single(PrefixedMessages("return Fail(ApiErrorCodes.NotFound, \"BarStore: not found\");"));
        Assert.Single(PrefixedMessages("var r = new Dto { Message = \"Baz：壞了\" };"));
        Assert.Empty(PrefixedMessages("throw new InvalidOperationException(\"交辦單 3 已結案。\");"));
        Assert.Empty(PrefixedMessages("throw new InvalidOperationException($\"Storage:SqliteCacheSizeKb 必須大於 0\");"));
        Assert.Empty(PrefixedMessages("throw new CryptographicException(\"enc:v2 密文長度不足。\");"));
    }

    /// <summary>E4：給使用者的訊息不出現內部代碼（core／standard／test、conservative／aggressive、目錄版本權杖）</summary>
    [Fact]
    public void 守門二_訊息字面不含內部代碼()
    {
        var internalCode = new Regex(@"core、standard|standard 或 test|conservative 或 aggressive|目錄版本權杖", RegexOptions.Compiled);
        var violations = CoreAndWebSources()
            .SelectMany(f => MessageLiteral.Matches(f.Content).Select(m => (f.Rel, Text: m.Groups[1].Value)))
            .Where(x => internalCode.IsMatch(x.Text)).Select(x => $"{x.Rel}  {x.Text}").ToList();
        Assert.True(violations.Count == 0, "訊息直接寫了內部代碼，改成畫面上的顯示名稱：\n" + string.Join("\n", violations));
    }
}
