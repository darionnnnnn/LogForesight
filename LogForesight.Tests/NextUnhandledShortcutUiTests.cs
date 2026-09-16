using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋四十五輪 B2 C3 的前端守門：風險日詳情頁改打「下一筆未處理」專用端點，
/// 且該呼叫發生在主要內容載入之後、不阻塞主載入。
///
/// 斷言一律先把目標函式主體切出來（並確認切到非空）再比對——大型頁面檔案裡
/// 同一段字串到處都有，整檔斷言等於沒有斷言。唯一的例外是「零命中」這種否定斷言，
/// 那本來就該對整份檔案做。
/// </summary>
public class NextUnhandledShortcutUiTests
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

    private static string ReadRecordDetailJs()
    {
        var path = Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages", "record-detail.js");
        Assert.True(File.Exists(path), $"找不到檔案: {path}");
        return File.ReadAllText(path);
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

    /// <summary>C3／驗收 6：舊式捷徑查詢（把 200 筆清單拉回前端自己找）必須零命中</summary>
    [Fact]
    public void 風險日詳情頁_不再有舊式清單捷徑查詢()
    {
        var js = ReadRecordDetailJs();

        Assert.DoesNotContain("/api/records?statuses=", js);
        Assert.DoesNotContain("pageSize=200", js);
    }

    /// <summary>C1／C3：改打專用端點，且不再於前端自己找「下一筆」</summary>
    [Fact]
    public void 下一筆未處理_改打專用端點且不自己找下一筆()
    {
        var body = ExtractTopLevelFunction(ReadRecordDetailJs(), "async function setupNextUnhandled()");

        Assert.Contains("/api/records/next-unhandled", body);
        // 「下一筆是哪一筆」的判斷已移到後端：前端不再對清單做位置搜尋
        Assert.DoesNotContain("findIndex", body);
        Assert.DoesNotContain("items", body);
        // 失敗時維持現行行為：不顯示捷徑、不打斷詳情頁
        Assert.Contains("catch", body);
    }

    /// <summary>C3／驗收 6：捷徑的呼叫排在主要內容載入之後，而且不被 await 阻塞</summary>
    [Fact]
    public void 下一筆未處理_呼叫排在主載入之後且不阻塞()
    {
        var body = ExtractTopLevelFunction(ReadRecordDetailJs(), "async function load()");

        var detailIndex = body.IndexOf("/api/records/${hostId}/${date}", StringComparison.Ordinal);
        var reportsIndex = body.IndexOf("await loadReports()", StringComparison.Ordinal);
        var shortcutIndex = body.IndexOf("setupNextUnhandled()", StringComparison.Ordinal);

        Assert.True(detailIndex >= 0, "load() 主體裡找不到詳情本體的載入");
        Assert.True(reportsIndex >= 0, "load() 主體裡找不到報告載入");
        Assert.True(shortcutIndex >= 0, "load() 主體裡找不到捷徑的呼叫");

        Assert.True(shortcutIndex > detailIndex, "捷徑必須排在主要內容載入之後");
        Assert.True(shortcutIndex > reportsIndex, "捷徑必須排在報告載入之後");
        Assert.DoesNotContain("await setupNextUnhandled()", body);
    }
}
