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

    private const string EventText = "事件／日誌列的 Message 屬性（日誌內文），不是例外訊息";
    private const string WarnText = "PrtgSyncWarning／警示列的 Message，是系統自寫並經 PrtgSyncWarning.Create 截斷的文字，不是例外訊息";
    private const string FieldKey = "SentinelFieldMap.Message 是欄位名稱常數，不是例外訊息";
    private const string Pending = "已知待檢討：原文流進執行紀錄／狀態，不在第 80 輪批次 D 範圍，另案收斂；放行僅為讓守門先立起來";

    // 偵測的是「任何成員存取鏈結尾是 .Message」（ex.Message、e.Message、inner.Message、
    // ex.InnerException.Message、ex.GetBaseException().Message…），不再只認固定的變數名稱——
    // 換個變數名就繞得過的守門等於沒有。寫入端（`x.Message = …`）不是取用，排除。
    // 非例外的 Message 屬性（事件列、輸出列等）逐條放進 Allowed 並附理由。
    //
    // 為什麼不做編譯期禁用（BannedSymbols）：Exception.Message 寫進記錄檔與主控台是合法用法、
    // 全站上百處，逐處放行的成本遠高於這條測試；要攔的只有「流進回應、DTO 或會回給畫面的狀態」這個出口。
    private static readonly Regex MessageUse = new(
        @"[\w\)\]]\??\.Message\b(?!\s*=[^=>])", RegexOptions.Compiled);

    /// <summary>Core 只看「把 .Message 賦值給屬性、欄位或區域變數」的行：這些值之後會流進狀態物件、回給畫面。</summary>
    private static readonly Regex CoreAssignment = new(
        @"(?<![=!<>])=(?![=>]).*[\w\)\]]\??\.Message\b(?!\s*=[^=>])", RegexOptions.Compiled);

    private const string CorePrefix = "LogForesight.Core/";

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
        ("Controllers/Api/PrtgScopeEstimateController.cs", "throw DomainException.Validation(ex.Message)", "PrtgScopePurgeStore 自擲的中文預覽狀態訊息（不含 PrtgScopePurgeBusyException）；" + SelfAuthored),
        ("Controllers/Api/PrtgScopePurgeJobsController.cs", "throw DomainException.Validation(ex.Message)", "PrtgScopePurgeStore 自擲的中文作業狀態訊息；" + SelfAuthored),
        ("Controllers/Api/PrtgTransferController.cs", "exception.Code, exception.Message", DomainOnly + "（PrtgTransferStoreException）"),
        ("Controllers/Api/ScheduleController.cs", "throw DomainException.Validation(ex.Message)", "SentinelQueryBuilder.NormalizeSubnetPrefix 自擲的中文輸入格式訊息；" + SelfAuthored),
        ("Filters/ApiExceptionMapping.cs", "domain.Message", "例外過濾器本身：只對已登錄型別取訊息"),
        ("Filters/ApiExceptionMapping.cs", "conflict.Message", "例外過濾器本身：只對已登錄型別取訊息"),
        ("Filters/ApiExceptionMapping.cs", "busy.Message", "例外過濾器本身：只對已登錄型別取訊息"),
        ("Filters/ApiExceptionMapping.cs", "transfer.Message", "例外過濾器本身：只對已登錄型別取訊息"),
        ("Filters/ApiExceptionMapping.cs", "deidentification.Message", "例外過濾器本身：只對已登錄型別取訊息"),
        ("Filters/ApiExceptionMapping.cs", "rule.Message", "例外過濾器本身：只對已登錄型別取訊息"),
        ("Filters/ApiExceptionMapping.cs", "scopeConflict.Message", "例外過濾器本身：只對已登錄型別取訊息"),
        ("Filters/ApiExceptionMapping.cs", "discovery.Message", "例外過濾器本身：只對已登錄型別取訊息"),
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
        ("LogForesight.Core/PrtgClient.cs", "var sanitized = StripSecrets(ex.Message);", "經 StripSecrets 遮蔽憑證後才進 PrtgClientException（其訊息保證不含 token／密碼，是管理者判斷帳密與網路要用的資訊）"),
        ("LogForesight.Core/Analysis/AIService.cs", "choice?.Message.Content", "模型回應的 choice.Message（模型輸出內容），不是例外訊息"),
        ("LogForesight.Core/Analysis/SentinelEventMapper.cs", "SentinelFieldMap.Message", FieldKey),
        ("LogForesight.Core/Analysis/LogAggregator.cs", "g.Select(e => e.Message)", EventText),
        ("LogForesight.Core/Analysis/LogAggregator.cs", "FindLinuxRule(l.Source, eventName: null, l.Message)", EventText),
        ("LogForesight.Core/Analysis/RiskyEventSelector.cs", "Message = TextTruncation.Truncate(e.Message, MaxMessageChars)", EventText),
        ("LogForesight.Core/Models/PrtgStructureSyncStatus.cs", "Message = w.Message", WarnText),
        ("LogForesight.Core/Service/LogAnalysisService.cs", "Message = e.Message,", EventText),
        ("LogForesight.Core/Service/LogAnalysisService.cs", "new[] { e.Message }", EventText),
        ("LogForesight.Core/Service/LogAnalysisService.cs", "Select(l => l.Message)", EventText),
        ("LogForesight.Core/Service/LogAnalysisService.cs", "new[] { entry.Message }", EventText),
        ("LogForesight.Core/Service/PrtgDailyPipeline.cs", "PrtgSyncWarning.Create(w.Code, w.Message)", WarnText),
        ("LogForesight.Core/Service/PrtgStructureSyncRunner.cs", "PrtgSyncWarning.Create(w.Code, w.Message)", WarnText),
        ("LogForesight.Core/Service/PrtgBackfillRunner.cs", "stateChangesError = ex.Message;", Pending),
        ("LogForesight.Core/Service/AccountDisplayFormatter.cs", "語法錯誤：{ex.Message}", "使用者自填正則的語法檢查（Regex 的 ArgumentException），訊息只描述該樣式的語法問題，回給輸入者本人"),
        ("LogForesight.Core/Service/AnalysisOrchestrator.cs", "$\"呼叫外部服務逾時：{ex.Message}\" : RunFailureReasons", Pending),
        ("LogForesight.Core/Service/AnalysisOrchestrator.cs", "$\"NetIQ 分析失敗：{ex.Message}\"", Pending),
        ("LogForesight.Core/Service/HostDayPostProcessor.cs", "evt.Message ?? string.Empty", EventText),
        ("LogForesight.Core/Service/HostDayPostProcessor.cs", "evt.Message == null", EventText),
        ("LogForesight.Core/Service/EventLogService.cs", "Message = entry.Message,", EventText),
        ("LogForesight.Core/Service/RiskReportService.cs", "log.Message.Split", EventText),
        ("LogForesight.Core/Persistence/Sql/EfPrtgStore.cs", "Message = item.Message,", WarnText),
        ("LogForesight.Core/Persistence/Sql/EfRiskyEventStore.cs", "Message = e.Message,", EventText),
        ("LogForesight.Core/Persistence/Sql/EfRiskyEventStore.cs", "Message = row.Message,", EventText),
        ("Filters/ApiExceptionHandler.cs", "envelope.Message", "ApiStatusEnvelope 依狀態碼固定的說明文字，不是例外訊息"),
        ("Filters/ApiExceptionMapping.cs", "keyUnavailable.Message", DomainOnly + "（CryptoKeyUnavailableException，訊息是處理方式）"),
        ("Auth/CookieSessionEvents.cs", "Value.Message", "ApiStatusEnvelope 依狀態碼固定的說明文字，不是例外訊息"),
        ("Services/RunExportService.cs", "var message = l.Message ?? string.Empty;", EventText),
        ("Services/RunExportService.cs", "TryRead(l.Message)", EventText),
        ("Services/SentinelEventFetchService.cs", "SentinelFieldMap.Message", FieldKey),
        ("Services/RunMonitorService.cs", "Message = l.Message,", EventText),
        ("Services/RunMonitorService.cs", "Normalize(e.Message)", EventText),
        ("Services/RunMonitorService.cs", "Message = group.First().Message,", EventText),
        ("Services/WorkOrderBoardService.cs", "g.Hosts, g.Message }", "失敗群組的 Message 是系統自寫的失敗說明欄位，不是例外訊息"),
        ("Services/PrtgBackfillService.cs", "error = preview.Message;", "預覽結果的 Message 是 PrtgBackfill 預覽自寫的中文說明，不是例外訊息"),
        ("Services/RiskyEventLookupService.cs", "stored.Select(e => e.Message)", EventText),
        ("Services/HealthService.cs", "prtgSnapshot?.Message", "PRTG 快照診斷的 Message 是 HealthService 自寫的固定中文說明，不是例外訊息"),
        ("Services/HealthService.cs", "prtgSyncWarnings.Select(w => w.Message)", WarnText),
        ("Services/PrtgStructureSyncService.cs", "Warnings.Select(w => w.Message)", WarnText),
        ("Controllers/Api/ScheduleController.cs", "LastRunMessage = lastOutcome?.Message,", "排程上次結果的 Message 是排程狀態自寫的說明，不是例外訊息"),
        ("Controllers/Api/PrtgSnapshotDiagnosticsController.cs", "p.StopCode, p.Message,", "快照日誌預覽的 Message 是系統自寫的停止說明，不是例外訊息"),
        ("Services/HealthAttention.cs", "PrtgSyncWarning.Truncate(warning.Message", WarnText),
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
            // Core：主控台輸出合法；只有賦值才會把原文帶進狀態
            if (file.StartsWith(CorePrefix, StringComparison.Ordinal)
                && (text.Contains("console.WriteLine", StringComparison.Ordinal) || !CoreAssignment.IsMatch(text))) continue;
            yield return (file, i + 1, text);
        }
    }

    private static bool IsAllowed((string File, int Line, string Text) hit) =>
        Allowed.Any(a => hit.File.EndsWith(a.File, StringComparison.Ordinal) && hit.Text.Contains(a.Fragment, StringComparison.Ordinal));

    private static List<(string File, int Line, string Text)> RealHits()
    {
        var webRoot = Path.Combine(FindRepoRoot(), "LogForesight.Web");
        var hits = new List<(string File, int Line, string Text)>();
        var coreRoot = Path.Combine(FindRepoRoot(), "LogForesight.Core");
        foreach (var path in Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
        {
            var rel = CorePrefix + Path.GetRelativePath(coreRoot, path).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
            hits.AddRange(Scan(rel, File.ReadAllText(path)));
        }
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

    [Fact]
    public void 反例_任何成員鏈結尾的Message都命中_記錄呼叫與寫入端不算()
    {
        Assert.Single(Scan("Services/FooService.cs", "return Fail(\"x\", e.Message);"));
        Assert.Single(Scan("Services/FooService.cs", "dto.Error = inner.Message;"));
        Assert.Single(Scan("Services/FooService.cs", "dto.Error = ex.InnerException.Message;"));
        Assert.Single(Scan("Services/FooService.cs", "dto.Error = ex.GetBaseException().Message;"));
        Assert.Single(Scan("Services/FooService.cs", "dto.Error = ex.InnerException?.Message;"));
        Assert.Empty(Scan("Services/FooService.cs", "Log.Warn(e, e.Message);"));
        Assert.Empty(Scan("Services/FooService.cs", "// 不要用 ex.Message 回給使用者"));
        Assert.Empty(Scan("Services/FooService.cs", "output.Message = \"固定說明\";"));
    }

    [Fact]
    public void 反例_Core只看賦值_主控台與記錄呼叫不算()
    {
        const string core = "LogForesight.Core/Service/Foo.cs";
        Assert.Single(Scan(core, "mapError = ex.Message;"));
        Assert.Single(Scan(core, "status.ErrorMessage = $\"主機對應失敗：{ex.Message}\";"));
        Assert.Single(Scan(core, "status.ErrorMessage = ex.InnerException.Message;"));
        Assert.Empty(Scan(core, "console.WriteLine($\"✗ 結構同步失敗：{ex.Message}\");"));
        Assert.Empty(Scan(core, "Fail($\"[範圍] ✗ 失敗：{ex.Message}\");"));
        Assert.Empty(Scan(core, "return list.Select(w => w.Message);"));
        Assert.Empty(Scan(core, "Log.Warn(ex, \"失敗：{0}\", ex.Message); var m = 1;"));
    }

    [Fact]
    public void Core賦值守門_目前程式綠()
    {
        var violations = RealHits().Where(h => h.File.StartsWith(CorePrefix, StringComparison.Ordinal) && !IsAllowed(h))
            .Select(h => $"{h.File}:{h.Line}  {h.Text}").ToList();
        Assert.True(violations.Count == 0,
            "Core 把 .Message 賦值給會回給畫面的狀態；改用固定說明（如 PrtgFailureText.ForStatus）或附理由加進白名單：\n"
            + string.Join("\n", violations));
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
