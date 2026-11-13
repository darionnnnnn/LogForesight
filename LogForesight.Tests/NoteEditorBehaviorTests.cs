using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 66 輪 E：說明欄編輯器尾區結構（[說明欄, 尾區, 錯誤訊息]）與處理面板 chips／動作區的 production 行為，
/// 由 JsBehavior/note-editor.behavior.mjs 逐案經 <see cref="NodeRuntime.RunOrFail"/> 執行。
/// </summary>
public sealed class NoteEditorBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("note-editor.behavior.mjs", "暫存_自動還原後進批次不預選")]
    [InlineData("note-editor.behavior.mjs", "暫存_批次選的狀態不帶回日層級")]
    [InlineData("note-editor.behavior.mjs", "暫存_沒點過狀態時重載取新的推導狀態")]
    [InlineData("note-editor.behavior.mjs", "暫存_親自點過的狀態同模式重載保留")]
    [InlineData("note-editor.behavior.mjs", "暫存_批次打的說明回日層級保留並提示套用對象")]
    [InlineData("note-editor.behavior.mjs", "暫存_批次說明為空回日層級不提示")]
    [InlineData("note-editor.behavior.mjs", "篩選取消勾選的提示只出現一次")]
    [InlineData("note-editor.behavior.mjs", "AI整理回應到達時欄位已卸下就丟棄")]
    [InlineData("note-editor.behavior.mjs", "AI整理沒有重建時照常填入")]
    [InlineData("note-editor.behavior.mjs", "有errorEl時順序為說明欄_尾區_錯誤")]
    [InlineData("note-editor.behavior.mjs", "尾區含字數計數與AI整理按鈕")]
    [InlineData("note-editor.behavior.mjs", "不傳errorEl時說明欄後面仍是尾區")]
    [InlineData("note-editor.behavior.mjs", "日層級七顆chip點選不重建節點")]
    [InlineData("note-editor.behavior.mjs", "批次八顆chip點選不重建節點")]
    [InlineData("note-editor.behavior.mjs", "動作區只含送出鈕且說明欄結構正確")]
    [InlineData("note-editor.behavior.mjs", "chips公開函式只建立一次節點")]
    [InlineData("note-editor.behavior.mjs", "處理面板以renderChips單選帶短標籤與完整名稱")]
    public void 說明欄編輯器與處理面板的production行為(string scriptName, string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, scriptName);
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
