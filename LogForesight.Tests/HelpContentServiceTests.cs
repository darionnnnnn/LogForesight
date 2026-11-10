using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 操作說明書內容載入與精靈導引卡整合（回饋十八輪批次H，終檢輪補上 H-5 規劃的這組測試）：
/// manifest 與各章節都是內嵌資源，直接驗證真實內容——這裡釘住的是「manifest 擴充 type/href
/// 欄位後既有 14 章 DTO 向後相容」與「Hidden 過濾」兩條批次H 的核心語意。
/// </summary>
public class HelpContentServiceTests
{
    private readonly HelpContentService _service = new();
    private static readonly HashSet<LogForesight.Web.Auth.Capability> AdminCapabilities = new() { LogForesight.Web.Auth.Capability.Maintain };

    [Fact]
    public void 精靈導引卡在第一項且type與href正確()
    {
        var manual = _service.GetManual(AdminCapabilities, hideSetupWizard: false);

        var first = manual.Chapters[0];
        Assert.Equal("setup-wizard", first.Id);
        Assert.Equal("link", first.Type);
        Assert.Equal("/setup", first.Href);
        Assert.Equal("", first.Content);   // link 章節沒有 Markdown 內容
    }

    [Fact]
    public void Hidden時精靈導引卡被濾掉_其餘章節不受影響()
    {
        var visible = _service.GetManual(AdminCapabilities, hideSetupWizard: false);
        var hidden = _service.GetManual(AdminCapabilities, hideSetupWizard: true);

        Assert.Equal(visible.Chapters.Count - 1, hidden.Chapters.Count);
        Assert.DoesNotContain(hidden.Chapters, c => c.Id == "setup-wizard");
        // 只濾精靈那一項，其餘章節（id 集合）完全一致
        Assert.Equal(
            visible.Chapters.Where(c => c.Id != "setup-wizard").Select(c => c.Id),
            hidden.Chapters.Select(c => c.Id));
    }

    /// <summary>既有 Markdown 章節向後相容：manifest 沒填 type/href 的章節預設 markdown、
    /// Href 為 null、內容非空——加欄位不能改變既有 14 章的行為。</summary>
    [Fact]
    public void 既有Markdown章節_Type預設markdown且內容非空()
    {
        var markdownChapters = _service.GetManual(AdminCapabilities).Chapters.Where(c => c.Id != "setup-wizard").ToList();

        Assert.NotEmpty(markdownChapters);
        Assert.All(markdownChapters, c =>
        {
            Assert.Equal("markdown", c.Type);
            Assert.Null(c.Href);
            Assert.False(string.IsNullOrWhiteSpace(c.Content));
        });
    }

    // ── 回饋二十七輪作業 G：使用者版與 AI 版雙內容 ──────────────────────────
    //
    // 使用者手冊要好讀、AI 問答要夠細，兩者對「同一章該寫多長」的要求相反。
    // 章節可另附 AI 版（manifest 的 aiFile），沒有的章節 fallback 回使用者版。

    /// <summary>有 AI 版時：餵給 AI 的是詳細版，使用者版原封不動。</summary>
    [Fact]
    public void 章節有AI版時_ContentForAi取詳細版且Content不受影響()
    {
        var chapter = new HelpChapter(
            "demo", "示範", "使用者看的簡明內容", new List<string>(), new List<string>(), "info-circle",
            AiContent: "AI 看的詳細內容");

        Assert.Equal("AI 看的詳細內容", chapter.ContentForAi);
        Assert.Equal("使用者看的簡明內容", chapter.Content);
    }

    /// <summary>沒有 AI 版時 fallback 回使用者版——fallback 寫在型別上而不是載入端，
    /// 任何直接建構章節的地方（測試、未來其他來源）都不會拿到空內容。</summary>
    [Fact]
    public void 章節沒有AI版時_ContentForAi退回使用者版()
    {
        var chapter = new HelpChapter(
            "demo", "示範", "只有一份內容", new List<string>(), new List<string>(), "info-circle");

        Assert.Equal("只有一份內容", chapter.ContentForAi);
    }

    /// <summary>GetManual 是使用者手冊的出口，不得夾帶 AI 版內容；
    /// DTO 本身也不該有這個欄位（AI 版不外洩到前端）。</summary>
    [Fact]
    public void GetManual回傳的章節只帶使用者版內容()
    {
        var manual = _service.GetManual(AdminCapabilities);

        foreach (var dto in manual.Chapters.Where(c => c.Id != "setup-wizard"))
        {
            var source = _service.Chapters.Single(c => c.Id == dto.Id);
            Assert.Equal(source.Content, dto.Content);
        }

        Assert.DoesNotContain(
            typeof(LogForesight.Web.Models.Dto.HelpChapterDto).GetProperties(),
            p => p.Name.Contains("Ai", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// manifest 宣告了 aiFile 的章節，實際一定要讀得到那個檔。
    /// 沒有這條的話，檔名打錯／檔案沒被編進組件都只會靜默 fallback 成使用者版——
    /// 畫面一切正常，只有 AI 答得比預期差，而且沒有任何人會發現。
    /// </summary>
    [Fact]
    public void 宣告了aiFile的章節都要真的讀到AI版內容()
    {
        var assembly = typeof(HelpContentService).Assembly;
        var manifestName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("HelpContent.manifest.json", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(manifestName)!;
        using var reader = new StreamReader(stream);
        using var doc = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());

        var idsWithAiFile = doc.RootElement.GetProperty("chapters").EnumerateArray()
            .Where(c => c.TryGetProperty("aiFile", out _))
            .Select(c => c.GetProperty("id").GetString()!)
            .ToList();

        Assert.NotEmpty(idsWithAiFile);   // 一章都沒有的話這條測試等於沒測到東西

        foreach (var id in idsWithAiFile)
        {
            var chapter = _service.Chapters.Single(c => c.Id == id);
            Assert.False(string.IsNullOrWhiteSpace(chapter.AiContent),
                $"章節 {id} 的 manifest 有 aiFile，卻沒讀到 AI 版內容（檔名打錯或沒被編進組件）");
            Assert.NotEqual(chapter.Content, chapter.AiContent);
        }
    }

    /// <summary>
    /// 權限異動檢核的讀者是只有確認權限的一般使用者（User 角色＝Handle＋ConfirmPermission）。
    /// 這章不設 requires，讓他們讀得到；授權設定章仍只給維護者。
    /// </summary>
    [Fact]
    public void 權限異動檢核章對只有確認權限的使用者可見_授權設定章不可見()
    {
        var user = new HashSet<LogForesight.Web.Auth.Capability>
            { LogForesight.Web.Auth.Capability.Handle, LogForesight.Web.Auth.Capability.ConfirmPermission };
        var ids = _service.GetManual(user).Chapters.Select(c => c.Id).ToList();

        Assert.Contains("permission-changes", ids);
        Assert.DoesNotContain("permissions", ids);
        var chapter = _service.Chapters.Single(c => c.Id == "permission-changes");
        Assert.Contains("全選符合目前篩選的權限異動", chapter.Content);
        Assert.Contains("PermissionChangesController", chapter.ContentForAi);
    }

    [Fact]
    public void PRTG說明區分來源佐證_期間提示與案件授與範圍()
    {
        var detail = _service.Chapters.Single(c => c.Id == "record-detail").Content;
        var handling = _service.Chapters.Single(c => c.Id == "handling").Content;
        var myWork = _service.Chapters.Single(c => c.Id == "my-work").Content;

        Assert.Contains("來源觀測", detail);
        Assert.Contains("PRTG 監控對應", detail);
        // CPU／記憶體期間觀察、正式模式、試算啟用已整套移除（docs/PRTG-SPEC.md §9b 只剩數值三條件），說明書不得再描述
        foreach (var removed in new[] { "期間觀察", "正式模式", "等待可信資料", "撤回／重評" })
            Assert.DoesNotContain(removed, detail);
        Assert.Contains("交辦授與不會附帶主機級", handling);
        Assert.Contains("不含主機整體的 PRTG 資訊", myWork);
    }

    /// <summary>
    /// 篩選矩陣、PRTG 部分完成與 AI 跟隨取數是說明書必須交代的行為：
    /// 程式改了（WEB-SPEC §10、PRTG-SPEC §9b、排程觸發文字）說明書沒跟上，使用者會照舊說明操作而找不到對應畫面。
    /// </summary>
    [Fact]
    public void 說明書交代篩選矩陣_PRTG部分完成與AI跟隨取數()
    {
        var issues = _service.Chapters.Single(c => c.Id == "issues");
        var prtg = _service.Chapters.Single(c => c.Id == "prtg");
        var scheduler = _service.Chapters.Single(c => c.Id == "scheduler");

        Assert.Contains("不支援", issues.Content);
        Assert.Contains("不支援的角度", issues.ContentForAi);
        Assert.Contains("部分完成", prtg.Content);
        Assert.Contains("部分完成", prtg.ContentForAi);
        Assert.Contains("AI 跟隨取數", scheduler.Content);
    }

    /// <summary>
    /// 使用者版與 AI 版是同一章的兩種寫法，章名必須一致：只改一邊會讓 AI 檢索到的章名與使用者畫面對不上。
    /// 只比 H1（H2 兩版刻意不同結構）；AI 版 H1 結尾的「（AI 參考指引）」「（AI 檢索版）」標註先去掉再比。
    /// </summary>
    [Fact]
    public void 說明書每章的使用者版與AI版H1一致()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        var helpDir = Path.Combine(dir!.FullName, "LogForesight.Web", "HelpContent");

        string FirstH1(string path)
        {
            var line = File.ReadAllLines(path).Select(l => l.TrimStart('\uFEFF'))
                .FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal));
            Assert.True(line != null, $"{Path.GetFileName(path)} 找不到 H1 標題");
            return line!.Substring(2).Trim();
        }

        var userFiles = Directory.GetFiles(helpDir, "*.md")
            .Where(f => !f.EndsWith(".ai.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(userFiles);   // 一章都沒掃到等於沒測到東西

        var problems = new List<string>();
        foreach (var userFile in userFiles)
        {
            var aiFile = Path.ChangeExtension(userFile, ".ai.md");
            var name = Path.GetFileName(userFile);
            if (!File.Exists(aiFile))
            {
                problems.Add($"{name} 缺少對應的 AI 版 {Path.GetFileName(aiFile)}");
                continue;
            }

            var userH1 = FirstH1(userFile);
            var aiH1 = System.Text.RegularExpressions.Regex.Replace(
                FirstH1(aiFile), @"（AI (參考指引|檢索版)）$", "").Trim();
            if (!string.Equals(userH1, aiH1, StringComparison.Ordinal))
            {
                problems.Add($"{name} H1「{userH1}」≠ {Path.GetFileName(aiFile)} H1（去標註後）「{aiH1}」");
            }
        }

        Assert.True(problems.Count == 0, "說明書使用者版與 AI 版 H1 不同步：\n" + string.Join("\n", problems));
    }
}
