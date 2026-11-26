using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 79 輪 D4／D5 的原始碼守門：截斷字串的出口只有一個、落庫前的手動截斷只出現在核准的幾處。
/// 掃描器是純函式（輸入檔名與內容），並各以反例證明「新增違規寫法就會紅」。
///
/// 三道守門：
/// 1. 方法定義：簽章是「字串＋整數長度」的截斷／夾制方法，只能是核准清單內的包裝
///    （<c>TextTruncation</c> 本體、<c>ColumnClamp.Clamp</c>、<c>AiInsightService</c> 的 Truncate 擴充、各處轉呼叫 <c>SafeCut</c> 的薄包裝）。
/// 2. 落庫截斷呼叫：<c>ColumnClamp.Clamp(</c>／<c>ClampFor</c> 只能出現在核准檔案與次數；不得以數字字面值當長度。
/// 3. 切片形狀：<c>Substring(0, …)</c> 與長度／上限／數字終點的範圍切片（<c>[..N]</c>）是「疑似截斷」。
///    既有的 56 處由「檔案 → 次數」基準表記錄（非截斷用途的解析與雜湊前綴、翻譯成 SQL 的前綴投影、尚未收斂的顯示用截斷），
///    新增一處或新檔案出現即紅；日後收斂時把基準數字調小。
/// </summary>
public class TruncationSourceGuardTests
{
    // ── 掃描器 ────────────────────────────────────────────────────────────────

    /// <summary>去掉行尾的 <c>//</c> 註解（字串內含 // 的行極少；寧可少算也不誤抓註解文字）。</summary>
    private static string Code(string line)
    {
        var at = line.IndexOf("//", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    private static readonly Regex MethodDefinition = new(
        @"\bstatic\s+string\??\s+(?<name>Clamp|Cut|Trunc(?:ate)?|TextCut|Shorten\w*|Ellipsi[sz]e\w*|LimitLength|SafeCut\w*)\s*(?:<[^>]*>)?\(\s*(?:this\s+)?string\??\s+\w+\s*,\s*int\s",
        RegexOptions.Compiled);

    /// <summary>核准的「字串＋長度」截斷方法：檔名 → 方法名集合。</summary>
    private static readonly Dictionary<string, string[]> ApprovedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LogForesight.Core/TextTruncation.cs"] = ["SafeCut", "SafeCutEllipsis", "Truncate"],
        ["LogForesight.Core/Persistence/Sql/ColumnClamp.cs"] = ["Clamp"],
        ["LogForesight.Core/Models/PrtgStructureSyncStatus.cs"] = ["Truncate"],          // 轉呼叫 SafeCut 的薄包裝
        ["LogForesight.Core/SentinelClient.cs"] = ["Truncate"],                          // 轉呼叫 SafeCut（後綴「…(截斷)」）
        ["LogForesight.Web/Services/AiInsightService.cs"] = ["Truncate"],                // string 擴充，轉呼叫 SafeCut
        ["LogForesight.Web/Services/RunExportService.cs"] = ["TextCut"],                 // 轉呼叫 SafeCutEllipsis
    };

    internal static List<string> MethodDefinitionOffenders(IEnumerable<(string Path, string Text)> files)
    {
        var offenders = new List<string>();
        foreach (var (path, text) in files)
        {
            var approved = ApprovedMethods.GetValueOrDefault(path) ?? [];
            foreach (var line in text.Split('\n'))
            {
                var m = MethodDefinition.Match(Code(line));
                if (m.Success && !approved.Contains(m.Groups["name"].Value))
                    offenders.Add($"{path}：{m.Groups["name"].Value}");
            }
        }
        return offenders;
    }

    private static readonly Regex ClampCall = new(@"ColumnClamp\.(?<kind>ClampFor|Clamp)\b", RegexOptions.Compiled);
    private static readonly Regex ClampWithLiteral = new(@"ColumnClamp\.Clamp\s*\([^;]*?,\s*\d+\s*\)", RegexOptions.Compiled);

    /// <summary>核准呼叫 <c>ColumnClamp.ClampFor</c> 的檔案與次數（D3 的 ExecuteUpdate／原生 SQL 與四個索引欄）；ColumnClamp.cs 本體不計。</summary>
    private static readonly Dictionary<string, int> ApprovedClampFor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LogForesight.Core/Persistence/PermissionChangeStore.cs"] = 1,          // 確認人帳號（ExecuteUpdate）
        ["LogForesight.Core/Persistence/Sql/EfWorkOrderStore.cs"] = 2,           // 續掛停止原因（ExecuteUpdate）、背景整併案件來源名稱（ExecuteUpdate）
        ["LogForesight.Core/Persistence/Sql/EfAnalysisRecordStore.cs"] = 2,      // 首見日原生 INSERT、問題列來源名稱（索引欄）
        ["LogForesight.Core/Persistence/Sql/EfRiskyEventStore.cs"] = 1,          // 風險事件來源（索引欄）
        ["LogForesight.Core/Service/PrtgFetchService.cs"] = 2,                   // PRTG 裝置 Ip、感測器 SensorType（索引欄）
    };

    internal static List<string> ClampCallOffenders(IEnumerable<(string Path, string Text)> files)
    {
        var offenders = new List<string>();
        foreach (var (path, text) in files)
        {
            if (path.EndsWith("Persistence/Sql/ColumnClamp.cs", StringComparison.OrdinalIgnoreCase)) continue;
            var code = string.Join('\n', text.Split('\n').Select(Code));
            var forCount = 0;
            foreach (Match m in ClampCall.Matches(code))
            {
                if (m.Groups["kind"].Value == "Clamp") offenders.Add($"{path}：呼叫 ColumnClamp.Clamp（只能經 ClampFor 取模型上限）");
                else forCount++;
            }
            if (ClampWithLiteral.IsMatch(code)) offenders.Add($"{path}：以數字字面值當截斷長度");
            var allowed = ApprovedClampFor.GetValueOrDefault(path);
            if (forCount != allowed) offenders.Add($"{path}：ClampFor 呼叫 {forCount} 處，核准 {allowed} 處");
        }
        return offenders;
    }

    private static readonly Regex SliceShape = new(
        @"\.Substring\(\s*0\s*,|\[\.\.(?:\(?\s*(?:\d+\b|Math\.Min|[A-Za-z_.]*(?:[Mm]ax|[Ll]imit|Chars|Length|Len)[A-Za-z_.]*))",
        RegexOptions.Compiled);

    /// <summary>
    /// 既有疑似截斷切片的基準（檔案 → 次數）。分類：
    /// 非截斷＝CryptoHelper／StorageBackend／WebAiService／SettingsAuditProjection／BuildVersion／PrtgTransferController（雜湊或版本前綴）、EfReportStore（檔名取日期）；
    /// 翻譯成 SQL 的前綴投影＝EfPrtgStore／CalibrationService／NetiqSourceAttempt／EfAnalysisRecordStore／EfIssueAggregateQuery 的 Substring；
    /// 其餘為顯示、診斷輸出或不落庫的長度限制（尚未收斂進 TextTruncation，日後收斂時調小次數）。
    /// </summary>
    private static readonly Dictionary<string, int> SliceBaseline = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LogForesight.Core/Analysis/AIService.cs"] = 3,
        ["LogForesight.Core/Analysis/LogAggregator.cs"] = 1,
        ["LogForesight.Core/CryptoHelper.cs"] = 3,
        ["LogForesight.Core/Models/WorkOrder.cs"] = 1,
        ["LogForesight.Core/Models/WorkOrderOutbox.cs"] = 2,
        ["LogForesight.Core/Persistence/IssueCaseCoordinator.cs"] = 1,
        ["LogForesight.Core/Persistence/Sql/EfAnalysisRecordStore.cs"] = 4,
        ["LogForesight.Core/Persistence/Sql/EfIssueAggregateQuery.cs"] = 1,
        ["LogForesight.Core/Persistence/Sql/EfIssueCaseStore.cs"] = 1,
        ["LogForesight.Core/Persistence/Sql/EfIssueHandlingStore.cs"] = 1,
        ["LogForesight.Core/Persistence/Sql/EfPrtgStore.cs"] = 6,
        ["LogForesight.Core/Persistence/Sql/EfReportStore.cs"] = 1,
        ["LogForesight.Core/Persistence/StorageBackend.cs"] = 1,
        ["LogForesight.Core/Persistence/UserPreferenceStore.cs"] = 1,
        ["LogForesight.Core/Persistence/WorkOrderCoordinator.cs"] = 2,
        ["LogForesight.Core/PrtgFailureText.cs"] = 1,
        ["LogForesight.Core/Service/BuildVersion.cs"] = 1,
        ["LogForesight.Core/Service/CalibrationService.cs"] = 7,
        ["LogForesight.Core/Service/HostDayWorkflowState.cs"] = 1,
        ["LogForesight.Core/Service/NetiqProbeRunner.cs"] = 1,
        ["LogForesight.Core/Service/NetiqSourceAttempt.cs"] = 2,
        ["LogForesight.Core/Service/PrtgDailyPipeline.cs"] = 3,
        ["LogForesight.Core/Service/PrtgHostMapper.cs"] = 1,
        ["LogForesight.Core/Service/PrtgProbeRunner.cs"] = 1,
        ["LogForesight.Core/Service/RiskReportService.cs"] = 1,
        ["LogForesight.Web/Controllers/Api/CspReportController.cs"] = 1,
        ["LogForesight.Web/Controllers/Api/PrtgTransferController.cs"] = 1,   // 稽核明細的封包雜湊前 12 碼
        ["LogForesight.Web/Controllers/Api/QueryStringParsing.cs"] = 1,
        ["LogForesight.Web/Services/AiInsightService.cs"] = 1,
        ["LogForesight.Web/Services/HandlingNoteAiService.cs"] = 1,
        ["LogForesight.Web/Services/SentinelEventFetchService.cs"] = 1,
        ["LogForesight.Web/Services/SettingsAuditProjection.cs"] = 1,
        ["LogForesight.Web/Services/WebAiService.cs"] = 1,
    };

    internal static List<string> SliceOffenders(IEnumerable<(string Path, string Text)> files)
    {
        var offenders = new List<string>();
        foreach (var (path, text) in files)
        {
            // TextTruncation 本體是核准的唯一切字串處
            if (path.EndsWith("LogForesight.Core/TextTruncation.cs", StringComparison.OrdinalIgnoreCase)) continue;
            var count = text.Split('\n').Sum(line => SliceShape.Matches(Code(line)).Count);
            var allowed = SliceBaseline.GetValueOrDefault(path);
            if (count > allowed) offenders.Add($"{path}：疑似截斷切片 {count} 處，基準 {allowed} 處");
        }
        return offenders;
    }

    // ── 讀原始碼 ──────────────────────────────────────────────────────────────

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static List<(string Path, string Text)> ProductionFiles()
    {
        var root = FindRepoRoot();
        var files = new List<(string, string)>();
        foreach (var project in new[] { "LogForesight.Core", "LogForesight.Web" })
        {
            var projectRoot = Path.Combine(root, project);
            foreach (var file in Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                var inside = Path.GetRelativePath(projectRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                if (inside.StartsWith("bin/") || inside.StartsWith("obj/")) continue;
                files.Add(($"{project}/{inside}", File.ReadAllText(file)));
            }
        }
        Assert.True(files.Count > 100, $"掃描檔數異常偏少（{files.Count}），守門可能已失效");
        return files;
    }

    // ── 守門（掃真實原始碼） ─────────────────────────────────────────────────────

    [Fact]
    public void 正式碼的字串加長度截斷方法只剩核准的包裝()
    {
        var offenders = MethodDefinitionOffenders(ProductionFiles());
        Assert.True(offenders.Count == 0, "出現核准清單外的截斷方法（請改用 TextTruncation.SafeCut）：" + string.Join("；", offenders));
    }

    [Fact]
    public void 落庫前的手動截斷只出現在核准的檔案與次數()
    {
        var offenders = ClampCallOffenders(ProductionFiles());
        Assert.True(offenders.Count == 0, "ColumnClamp 的使用超出 D3 核准範圍：" + string.Join("；", offenders));
    }

    [Fact]
    public void 疑似截斷的切片不得超出基準()
    {
        var files = ProductionFiles();
        var offenders = SliceOffenders(files);
        Assert.True(offenders.Count == 0,
            "新增了疑似手動截斷的 Substring／範圍切片（請改走 TextTruncation.SafeCut；若是解析或雜湊前綴，更新基準表並註明分類）：" + string.Join("；", offenders));
        // 基準表不得有過期的條目（檔案已不存在或次數已降低卻沒調小）
        var stale = SliceBaseline.Where(kv =>
        {
            var text = files.FirstOrDefault(f => f.Path.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)).Text;
            return text == null || text.Split('\n').Sum(l => SliceShape.Matches(Code(l)).Count) != kv.Value;
        }).Select(kv => kv.Key).ToList();
        Assert.True(stale.Count == 0, "基準表與實際次數不符（請同步調整）：" + string.Join("、", stale));
    }

    // ── 反例：掃描器對違規寫法必須變紅，對合法寫法不誤報 ─────────────────────────

    [Fact]
    public void 反例_自訂截斷方法會被抓到_核准檔案內的同名包裝不會()
    {
        const string rogue = "static string Clamp(string v, int max) => v.Length > max ? v[..max] : v;";
        Assert.Single(MethodDefinitionOffenders([("LogForesight.Core/Service/Rogue.cs", rogue)]));
        Assert.Single(MethodDefinitionOffenders([("LogForesight.Web/Services/X.cs", "private static string Cut(string s, int max) => s;")]));
        Assert.Single(MethodDefinitionOffenders([("LogForesight.Web/Services/X.cs", "public static string Truncate(this string s, int max) => s;")]));
        Assert.Single(MethodDefinitionOffenders([("LogForesight.Core/Service/Y.cs", "static string? Shorten(string? s, int n) => s;")]));
        // 對照：核准檔案的核准方法名不報；沒有「字串＋整數」簽章的不報
        Assert.Empty(MethodDefinitionOffenders([("LogForesight.Core/Persistence/Sql/ColumnClamp.cs", rogue)]));
        Assert.Empty(MethodDefinitionOffenders([("LogForesight.Core/Service/Z.cs", "private static string Clamp(string s) => s;")]));
        // 核准檔案內新增了核准清單外的名稱（Cut）仍會被抓
        Assert.Single(MethodDefinitionOffenders([("LogForesight.Core/TextTruncation.cs", "static string Cut(string s, int max) => s;")]));
    }

    [Fact]
    public void 反例_ColumnClamp的違規用法會被抓到()
    {
        Assert.Single(ClampCallOffenders([("LogForesight.Core/Service/New.cs", "var x = ColumnClamp.ClampFor<PrtgDeviceRow>(v, nameof(PrtgDeviceRow.Ip));")]));   // 未核准的檔案
        Assert.NotEmpty(ClampCallOffenders([("LogForesight.Core/Service/New.cs", "var x = ColumnClamp.Clamp(v, 255);")]));                                       // 未經 ClampFor、且字面值長度
        Assert.Contains(ClampCallOffenders([("LogForesight.Core/Persistence/PermissionChangeStore.cs", "var x = ColumnClamp.Clamp(v, 64);")]),
            o => o.Contains("字面值"));
        // 核准檔案多出一處呼叫、或少一處都會紅（次數必須吻合）
        const string call = "a = ColumnClamp.ClampFor<PermissionChangeRow>(v, nameof(PermissionChangeRow.HostName));\n";
        Assert.Single(ClampCallOffenders([("LogForesight.Core/Persistence/PermissionChangeStore.cs", call + call)]));
        Assert.Single(ClampCallOffenders([("LogForesight.Core/Persistence/PermissionChangeStore.cs", "")]));
        // 對照：次數吻合、ColumnClamp 本體、註解內的字樣都不報
        Assert.Empty(ClampCallOffenders([("LogForesight.Core/Persistence/PermissionChangeStore.cs", call)]));
        Assert.Empty(ClampCallOffenders([("LogForesight.Core/Persistence/Sql/ColumnClamp.cs", "ColumnClamp.Clamp(v, 1)")]));
        Assert.Empty(ClampCallOffenders([("LogForesight.Core/Service/Doc.cs", "// 請用 ColumnClamp.ClampFor 截斷")]));
    }

    [Theory]
    [InlineData("var s = text.Substring(0, 80);")]
    [InlineData("var s = text.Length > 80 ? text[..80] : text;")]
    [InlineData("var s = text[..MaxChars];")]
    [InlineData("var s = text[..Math.Min(text.Length, limit)];")]
    [InlineData("var s = value[..maxLen] + \"…\";")]
    public void 反例_新增的疑似截斷切片會超出基準(string line)
    {
        Assert.Single(SliceOffenders([("LogForesight.Core/Service/BrandNew.cs", line)]));
        // 既有檔案也一樣：基準以外多一處就紅（PrtgFailureText 的基準是 1 處）
        Assert.Single(SliceOffenders([("LogForesight.Core/PrtgFailureText.cs", line + "\n" + line)]));
    }

    [Theory]
    [InlineData("var head = line[..colonIndex];")]
    [InlineData("var tail = s[(end + 1)..];")]
    [InlineData("var s = text.Substring(start, 3);")]
    [InlineData("// var s = text.Substring(0, 80);")]
    public void 反例的對照組_解析用切片與註解不計入(string line) =>
        Assert.Empty(SliceOffenders([("LogForesight.Core/Service/BrandNew.cs", line)]));
}
