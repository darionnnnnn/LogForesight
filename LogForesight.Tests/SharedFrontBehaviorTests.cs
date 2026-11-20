using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 前端共用出口（第 79 輪 I 段）的行為：剪貼簿（安全／非安全環境與兩者皆失敗）、欄位錯誤的無障礙屬性、
/// 數字欄位空值不當成 0、網址 hash 進選擇器前跳脫、文字語系排序、對話框關閉方法。
/// 直接載入 production 模組逐案執行（node 腳本見 JsBehavior/shared-front.behavior.mjs）。
/// </summary>
public class SharedFrontBehaviorTests
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

    [NodeTheory]
    [InlineData("安全環境走clipboard複製成功")]
    [InlineData("非安全環境無clipboard走退路複製成功")]
    [InlineData("焦點在modal內時退路的textarea掛進該modal")]
    [InlineData("clipboard被拒絕時退回execCommand")]
    [InlineData("兩者都失敗時回報原因_安全環境")]
    [InlineData("兩者都失敗時回報原因_非安全環境")]
    [InlineData("欄位錯誤帶aria屬性與訊息id")]
    [InlineData("欄位錯誤清除後還原屬性")]
    [InlineData("欄位錯誤重複設定只留一個訊息元素")]
    [InlineData("欄位沒有id時配一個不重複的id")]
    [InlineData("空欄位必填時回報欄位錯誤且不給數字")]
    [InlineData("空白字元視同空欄位")]
    [InlineData("填0時回0且不標錯")]
    [InlineData("選填空欄位回null且不標錯")]
    [InlineData("整數限制兩側")]
    [InlineData("欄位集合有空欄位時不通過並聚焦第一個錯誤")]
    [InlineData("欄位集合填0時整組通過並送0")]
    [InlineData("欄位集合選填欄位空時仍通過")]
    [InlineData("hash含引號與方括號時不擲例外")]
    [InlineData("hash一般字串仍找得到元素")]
    [InlineData("hash百分比編碼壞掉時回原字串")]
    [InlineData("escapeSelectorValue跳脫特殊字元")]
    [InlineData("數字字串依數值排序")]
    [InlineData("中文名稱依語系排序而非碼位")]
    [InlineData("null與undefined視為空字串排最前")]
    [InlineData("sortRows字串欄位用語系比較")]
    [InlineData("sortRows數字欄位維持數值比較")]
    [InlineData("對話框回傳關閉方法並呼叫hide")]
    [InlineData("整列連結放在第一個沒有互動元素的儲存格")]
    [InlineData("整列連結每格都有互動元素時放空連結並帶名稱")]
    [InlineData("沒有rowHref時不產生整列連結")]
    [InlineData("頁籤配上外掛屬性並建立外掛實例")]
    [InlineData("切換頁籤時同步面板與標籤與回呼與hash")]
    [InlineData("網址hash指到頁籤時開啟對應頁籤")]
    [InlineData("網址hash指到目前頁籤時仍呼叫一次回呼")]
    [InlineData("網址hash含特殊字元時不擲例外且不切換")]
    [InlineData("hashchange時跟著切頁籤")]
    [InlineData("多顆頁籤共用同一面板時標籤跟著目前頁籤")]
    [InlineData("只認配得到按鈕的面板")]
    public void 前端共用出口行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "shared-front.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(30), label: caseName);

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
