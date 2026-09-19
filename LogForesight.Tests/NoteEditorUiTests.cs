using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 處理說明輸入不再丟字的前端守門：處理表單重建不重設說明、必填只走手動驗證一套、
/// 交辦單回覆彈窗有關閉保護、草稿存取不因本機儲存區不可用而擲例外、
/// 「確認不處理」不再送出沒有理由的標記。
///
/// 斷言先把目標函式主體切出來（並確認切到非空）再比對；「零命中」的否定斷言才對整份檔案做。
/// </summary>
public class NoteEditorUiTests
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

    private static string ReadJs(params string[] relative)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "LogForesight.Web", "wwwroot", "js" }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), $"找不到檔案: {path}");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    /// <summary>取出頂層函式主體：自宣告起算到第一個位於行首的 '}'（頂層函式的收尾）為止</summary>
    private static string ExtractTopLevelFunction(string js, string declaration)
    {
        var start = js.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到函式宣告: {declaration}");

        var end = js.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, $"找不到函式收尾: {declaration}");

        var body = js.Substring(start, end - start);
        Assert.False(string.IsNullOrWhiteSpace(body), $"切出來的主體是空的: {declaration}");
        return body;
    }

    [Fact]
    public void 處理表單_重建時不依模式重設說明且以表單暫存為初值()
    {
        var form = ExtractTopLevelFunction(ReadJs("pages", "handling-panel.js"), "function handlingForm()");

        Assert.DoesNotContain("noteInput.value = batchMode ? ''", form);
        Assert.Contains("state.formDraft", form);
        Assert.Matches(new Regex(@"noteInput\.value = draft \? draft\.note"), form);
        Assert.Contains("attachNoteEditor(noteInput", form);
    }

    [Fact]
    public void 處理表單_不設定原生必填且驗證錯誤有文字並聚焦()
    {
        var js = ReadJs("pages", "handling-panel.js");
        Assert.DoesNotContain("noteInput.required", js);

        var form = ExtractTopLevelFunction(js, "function handlingForm()");
        Assert.Contains("invalid-feedback", form);
        Assert.Contains("noteInput.focus()", form);
    }

    [Fact]
    public void 處理面板_換主機或日期才清空表單暫存()
    {
        var init = ExtractTopLevelFunction(ReadJs("pages", "handling-panel.js"), "export async function initHandlingPanel(");

        Assert.Matches(new Regex(@"state\?\.hostId === hostId && state\?\.date === date \? state\.formDraft : null"), init);
    }

    [Fact]
    public void 交辦單回覆彈窗_監聽關閉事件並套用說明增強()
    {
        var modal = ExtractTopLevelFunction(ReadJs("pages", "issue-status-reply.js"), "export function openWorkOrderReplyModal(");

        Assert.Contains("'hide.bs.modal'", modal);
        Assert.Contains("event.preventDefault()", modal);
        Assert.Contains("attachNoteEditor(noteInput", modal);
        Assert.Contains("clearDraft()", modal);
    }

    [Fact]
    public void 交辦單回覆彈窗_全部呼叫端都傳入草稿識別()
    {
        var jsRoot = Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js");
        var calls = 0;
        foreach (var file in Directory.GetFiles(jsRoot, "*.js", SearchOption.AllDirectories))
        {
            var js = File.ReadAllText(file).Replace("\r\n", "\n");
            foreach (Match match in Regex.Matches(js, @"(?<!function )openWorkOrderReplyModal\(\{"))
            {
                calls++;
                // 呼叫的參數物件以 onApplied 收尾（三個呼叫端皆同），切到那裡為止
                var close = js.IndexOf("onApplied", match.Index, StringComparison.Ordinal);
                Assert.True(close > match.Index, $"{Path.GetFileName(file)} 的 openWorkOrderReplyModal 呼叫切不出參數物件");
                var call = js.Substring(match.Index, close - match.Index);
                Assert.True(call.Contains("draftKey:"), $"{Path.GetFileName(file)} 的 openWorkOrderReplyModal 呼叫沒有傳 draftKey");
            }
        }

        Assert.True(calls >= 3, $"呼叫端數量不如預期（{calls}）——守門可能沒掃到");
    }

    [Fact]
    public void 說明增強模組_本機儲存區存取都在try區塊內()
    {
        var js = ReadJs("core", "note-editor.js");
        var matches = Regex.Matches(js, @"localStorage\.");
        Assert.NotEmpty(matches);

        foreach (Match match in matches)
        {
            var before = js.Substring(0, match.Index);
            var tryIndex = before.LastIndexOf("try {", StringComparison.Ordinal);
            Assert.True(tryIndex >= 0, $"第 {match.Index} 字元的 localStorage 存取前找不到 try 區塊");
            Assert.DoesNotContain("catch", before.Substring(tryIndex));
        }

        Assert.DoesNotContain("innerHTML", js);
    }

    [Fact]
    public void 風險日詳情_確認不處理不再送出沒有理由的標記()
    {
        var js = ReadJs("pages", "record-detail.js");

        Assert.DoesNotMatch(new Regex(@"'wont_fix'[^\n]*note: null"), js);
        Assert.Contains("不處理的理由（必填）", js);
    }

    [Fact]
    public void 登出流程_呼叫登出成功後清除草稿()
    {
        var logout = ExtractTopLevelFunction(ReadJs("core", "layout.js"), "function bindLogout(");

        var post = logout.IndexOf("api.post('/api/auth/logout')", StringComparison.Ordinal);
        var clear = logout.IndexOf("clearAllDraftsForUser(", StringComparison.Ordinal);
        Assert.True(post >= 0 && clear > post, "草稿要在登出 API 成功之後才清");
    }
}
