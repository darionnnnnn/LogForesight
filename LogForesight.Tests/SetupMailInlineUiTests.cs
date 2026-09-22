using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

public class SetupMailInlineUiTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()));

    [Fact]
    public void Mail表單含收件人觸發與測試寄送且密碼不預填()
    {
        var view = Read("LogForesight.Web", "Views", "Pages", "Setup.cshtml");
        var start = view.IndexOf("id=\"setup-mail-template\"", StringComparison.Ordinal);
        var end = view.IndexOf("</template>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var template = view[start..end];
        foreach (var id in new[] { "setup-mail-enabled", "setup-smtp-server", "setup-smtp-port",
                     "setup-smtp-password", "setup-mail-from", "setup-mail-recipients",
                     "setup-mail-on-run-completed", "setup-mail-test-btn", "setup-mail-save-btn" })
            Assert.Contains($"id=\"{id}\"", template);
        Assert.Contains("autocomplete=\"off\"", template);
        foreach (Match button in Regex.Matches(template, @"<button\b[^>]*>"))
            Assert.Contains("type=\"button\"", button.Value);
    }

    [Fact]
    public void 儲存沿用最新設定並限制有效Port與觸發條件()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");
        var start = js.IndexOf("function renderMailInlineForm(", StringComparison.Ordinal);
        var end = js.IndexOf("function stepState(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = js[start..end];
        Assert.Contains("parseRecipients(mailRecipientsInput.value)", body);
        Assert.Contains("api.post('/api/admin/settings/mail-test'", body);
        Assert.Contains("const latest = await api.get('/api/admin/settings')", body);
        Assert.Contains("...latest,", body);
        Assert.Contains("mailOnRunCompleted || Boolean(latest?.mailDailyEnabled", body);
        Assert.Contains("Number.isInteger(smtpPort)", body);
        Assert.Contains("smtpPort < 1 || smtpPort > 65535", body);
        Assert.Contains("smtpPassword: smtpPassword || null", body);
        Assert.Contains("await api.put('/api/admin/settings', payload)", body);
        Assert.Contains("await load()", body);
    }

    [Fact]
    public void 收件人解析會去空白空行與重複()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");
        var match = Regex.Match(js, @"export function parseRecipients\(raw\)\s*(\{[^}]+\})");
        Assert.True(match.Success);
        var psi = new ProcessStartInfo("node", "--experimental-default-type=module --input-type=module")
        {
            WorkingDirectory = Root(), RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        Assert.NotNull(process);
        process!.StandardInput.WriteLine($"function parseRecipients(raw) {match.Groups[1].Value}");
        process.StandardInput.WriteLine("const a = parseRecipients(' a@x.test \\n b@x.test\\n a@x.test\\n '); if (a.length !== 2 || a[0] !== 'a@x.test' || a[1] !== 'b@x.test' || parseRecipients(null).length) throw new Error('invalid recipients');");
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }
}
