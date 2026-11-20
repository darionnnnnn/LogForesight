using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 數字欄位的讀取守門：<c>Number('')</c> 是 0，直接 <c>Number(x.value)</c> 會把清空的欄位當成「輸入 0」送出。
/// 讀表單欄位一律用 <c>core/number-field.js</c>；<c>Number(x) || 預設</c> 形式（空值或 0 都沿用預設）不在此限。
/// 其餘保留處列在 <see cref="Allowed"/>，每一筆都註明為什麼安全。
/// </summary>
public class NumberFieldGuardUiTests
{
    private static readonly Regex Direct = new(@"\b(Number|parseInt|parseFloat)\(\s*[^;]*?\.value\b", RegexOptions.CultureInvariant);

    /// <summary>（檔名, 該行須含的片段, 保留理由）</summary>
    private static readonly (string File, string Snippet, string Reason)[] Allowed =
    {
        ("ui.js", "Number(select.value)", "每頁筆數下拉，選項固定為數字"),
        ("issue-owners.js", "Number(i.value)", "勾選框的 id 值"),
        ("groups.js", "Number(input.value)", "勾選框的 id 值"),
        ("users.js", "Number(input.value)", "勾選框的 id 值"),
        ("hosts.js", "Number(input.value)", "勾選框的 id 值"),
        ("prtg-admin.js", "Number(check.value)", "勾選框的 id 值"),
        ("netiq.js", "Number(probeSentinelSelect.value)", "選擇器的 id 值，空選項由後續 id 檢查擋下"),
        ("handling-panel.js", "Number(select.value)", "選擇器的 id 值，已先判斷 select.value 非空"),
        ("issue-mute-modal.js", "Number(customInput.value)", "緊接 Number.isInteger 與天數範圍檢查，空字串 0 不在範圍內"),
        ("runs.js", "parseInt(concurrencyInput?.value", "緊接 isNaN 與 1～8 範圍檢查"),
        ("rules.js", "const value = Number(input.value)", "同一行先以 !input.value 擋空欄位"),
        ("rules.js", "rule-prtg-trend-window').value) <", "上一段迴圈已逐欄檢查非空與範圍"),
        ("rules.js", "rule-prtg-vtrend-", "missingThresholdMessage 已先擋掉空欄位"),
    };

    internal static List<string> Offenders(IEnumerable<(string File, string Text)> files)
    {
        var offenders = new List<string>();
        foreach (var (file, text) in files)
        {
            var name = Path.GetFileName(file);
            var lineNo = 0;
            foreach (var raw in text.Split('\n'))
            {
                lineNo++;
                var line = raw.Trim();
                if (line.StartsWith("//") || line.StartsWith("*") || line.StartsWith("/*")) continue;
                if (!Direct.IsMatch(line) || line.Contains("||")) continue;
                if (Allowed.Any(a => a.File == name && line.Contains(a.Snippet))) continue;
                offenders.Add($"{name}:{lineNo}  {line}");
            }
        }
        return offenders;
    }

    [Fact]
    public void 前端不直接把表單欄位值轉數字送出_改用number_field()
    {
        var jsRoot = Path.Combine(ConvergenceScan.FindRepoRoot(), "LogForesight.Web", "wwwroot", "js");
        var files = Directory.EnumerateFiles(jsRoot, "*.js", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "lib" + Path.DirectorySeparatorChar))
            .Select(f => (f, File.ReadAllText(f)));
        var offenders = Offenders(files);
        Assert.True(offenders.Count == 0,
            "這些行直接 Number(x.value)，空欄位會變 0；請改用 core/number-field.js（或 `Number(x) || 預設`）：\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void 守門抓得到反例_且放行有保留理由與預設寫法()
    {
        Assert.Single(Offenders(new[] { ("pages/foo.js", "const n = Number(document.getElementById('x').value);") }));
        Assert.Single(Offenders(new[] { ("pages/foo.js", "const n = parseInt(input.value, 10);") }));
        // 預設寫法與註解不算
        Assert.Empty(Offenders(new[] { ("pages/foo.js", "const n = Number(input.value) || 7;\n// Number(input.value)") }));
        // 允許清單只對指定檔案生效
        Assert.Empty(Offenders(new[] { ("pages/groups.js", "const id = Number(input.value);") }));
        Assert.Single(Offenders(new[] { ("pages/foo.js", "const id = Number(input.value);") }));
    }
}
