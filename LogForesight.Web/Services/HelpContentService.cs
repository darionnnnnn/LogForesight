using System.Reflection;
using System.Text.Json;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>操作說明書單一章節的完整內容（回饋十五輪批次E）。Keywords 只供 AI 選節計分使用，
/// 不外洩到 <see cref="HelpChapterDto"/>——前端不需要知道計分用的關鍵字清單。
/// <paramref name="Type"/>／<paramref name="Href"/>（回饋十八輪批次H）：type=link 的章節
/// （目前只有啟動精靈）沒有 Markdown 內容，Content 恆為空字串。
/// <paramref name="AiContent"/>（回饋二十七輪作業 G）：AI 問答專用的詳細版原文，
/// manifest 有填 aiFile 時才有值；要餵給 AI 的內容一律走 <see cref="ContentForAi"/>，
/// 不要直接讀這個欄位。</summary>
public record HelpChapter(string Id, string Title, string Content, List<string> Keywords, List<string> Related, string Icon,
    string Type = "markdown", string? Href = null, string AiContent = "")
{
    /// <summary>實際餵給 AI 的內容：沒有 AI 版時 fallback 成使用者版。
    /// **fallback 只寫在這裡一處**——寫在載入端的話，任何直接建構 HelpChapter 的地方
    /// （測試、未來的其他來源）都會拿到空內容，而且空得無聲無息。</summary>
    public string ContentForAi => string.IsNullOrEmpty(AiContent) ? Content : AiContent;
}

/// <summary>
/// 操作說明書內容載入（docs/archive/FEEDBACK-15-PLAN.md 批次E-1）：manifest.json＋各章節 Markdown
/// 皆以內嵌資源編進組件（見 csproj 的 EmbeddedResource），部署零額外檔案。Singleton 生命週期——
/// 內容編譯進組件，執行期間不會變，只需要載入一次；用 <see cref="Lazy{T}"/> 延後到第一次真的用到
/// 才載入，一般請求（不進手冊頁的人）不必付這個成本。
/// </summary>
public class HelpContentService
{
    private const string ResourceSuffixPrefix = "HelpContent.";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Lazy<List<HelpChapter>> _chapters = new(Load);

    public IReadOnlyList<HelpChapter> Chapters => _chapters.Value;

    /// <summary>
    /// <paramref name="hideSetupWizard"/>（回饋十八輪批次H）：精靈全部步驟完成後使用者選擇隱藏時，
    /// 濾掉 id=setup-wizard 的導引卡章節。過濾放在這裡而不是 <see cref="Load"/>——章節內容
    /// 編譯進組件、Lazy 只載入一次，不該讓「隱藏與否」這種會變動的狀態滲進那份快取；
    /// 每次呼叫依當下狀態現算，快取本身維持與狀態無關。
    /// </summary>
    public HelpManualDto GetManual(bool hideSetupWizard = false) => new()
    {
        Chapters = Chapters
            .Where(c => !(hideSetupWizard && c.Id == "setup-wizard"))
            .Select(c => new HelpChapterDto
            {
                Id = c.Id,
                Title = c.Title,
                Content = c.Content,
                Related = c.Related,
                Icon = c.Icon,
                Type = c.Type,
                Href = c.Href
            }).ToList()
    };

    private static List<HelpChapter> Load()
    {
        var assembly = typeof(HelpContentService).Assembly;

        using var manifestStream = OpenResource(assembly, "manifest.json");
        using var manifestReader = new StreamReader(manifestStream);
        var manifest = JsonSerializer.Deserialize<ManifestRoot>(manifestReader.ReadToEnd(), JsonOptions)
            ?? throw new InvalidOperationException("操作說明書 manifest 解析失敗（內嵌資源格式不正確）。");

        var chapters = new List<HelpChapter>();
        foreach (var entry in manifest.Chapters)
        {
            // type=link 章節（回饋十八輪批次H，目前只有啟動精靈）沒有對應的 Markdown 檔——
            // File 欄位在 manifest 裡就沒填，載入時直接跳過開檔，Content 留空；
            // AiContent 同 Content 維持空字串（沒有可塞進 prompt 的說明文字）。
            if (entry.Type == "link")
            {
                chapters.Add(new HelpChapter(entry.Id, entry.Title, "", entry.Keywords, entry.Related, entry.Icon,
                    entry.Type, entry.Href, AiContent: ""));
                continue;
            }

            using var contentStream = OpenResource(assembly, entry.File);
            using var contentReader = new StreamReader(contentStream);
            var content = contentReader.ReadToEnd().TrimEnd();

            // aiFile（回饋二十七輪作業 G）：有填且對應內嵌資源存在時讀 AI 版；讀不到就留空字串，
            // 由 HelpChapter.ContentForAi 統一 fallback 回使用者版（fallback 不在這裡重寫一份）
            var aiContent = string.Empty;
            if (!string.IsNullOrEmpty(entry.AiFile))
            {
                var aiSuffix = ResourceSuffixPrefix + entry.AiFile;
                var aiName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(aiSuffix, StringComparison.Ordinal));
                if (aiName != null)
                {
                    using var aiStream = assembly.GetManifestResourceStream(aiName)!;
                    using var aiReader = new StreamReader(aiStream);
                    aiContent = aiReader.ReadToEnd().TrimEnd();
                }
            }

            chapters.Add(new HelpChapter(entry.Id, entry.Title, content,
                entry.Keywords, entry.Related, entry.Icon, entry.Type, entry.Href, AiContent: aiContent));
        }
        return chapters;
    }

    /// <summary>用尾碼比對找內嵌資源，不寫死組件的根命名空間前綴——MSBuild 產生的資源名稱
    /// 前綴取決於 RootNamespace，尾碼（HelpContent.檔名）才是穩定不受組態影響的部分。</summary>
    private static Stream OpenResource(Assembly assembly, string fileName)
    {
        var suffix = ResourceSuffixPrefix + fileName;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
        if (name == null)
            throw new InvalidOperationException($"找不到操作說明書內嵌資源：{fileName}（預期尾碼 {suffix}）。");
        return assembly.GetManifestResourceStream(name)!;
    }

    private class ManifestChapter
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string File { get; set; } = "";
        public List<string> Keywords { get; set; } = new();
        public List<string> Related { get; set; } = new();
        public string Icon { get; set; } = "";

        /// <summary>"markdown"（省略時的預設值，既有章節不用改）｜"link"（回饋十八輪批次H）</summary>
        public string Type { get; set; } = "markdown";

        public string? Href { get; set; }

        /// <summary>選填：AI 問答用的詳細版內容檔（回饋二十七輪作業 G）。沒填就沿用
        /// <see cref="File"/> 的內容——使用者版與 AI 版分離是加值，不是每章的必要條件。</summary>
        public string? AiFile { get; set; }
    }

    private class ManifestRoot
    {
        public List<ManifestChapter> Chapters { get; set; } = new();
    }
}
