using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// AiOutputSanitizer（docs/FEEDBACK-3-PLAN.md #7）：channel 標記清洗＋OpenCC 簡轉繁。
/// </summary>
public class AiOutputSanitizerTests
{
    [Fact]
    public void 整段皆思考無final段_視為空回應()
    {
        // 使用者實際回報樣本的縮影：<|channel>thought 開頭、通篇思考、被 max_tokens 截斷，
        // 從未生成到 final 段
        const string content = "<|channel>thought\n" +
            "User Question: \"依据什么判断是暴力破解而不是用户忘记密码?\"\n" +
            "Context: A maintenance analysis report...\n" +
            "*Self-Correction/Refinement based on strict instructions";

        Assert.Null(AiOutputSanitizer.Sanitize(content));
    }

    [Fact]
    public void thought與final混合_只留final段()
    {
        const string content = "<|channel|>thought<|message|>這是內部思考，不該被看到。" +
            "<|channel|>final<|message|>這是使用者該看到的答案。";

        Assert.Equal("這是使用者該看到的答案。", AiOutputSanitizer.Sanitize(content));
    }

    [Fact]
    public void 缺結尾豎線的channel標記變體也能解析()
    {
        // <|channel>final（第二個豎線缺席）與正常 <|channel|>final 都要接得住
        const string content = "<|channel>thought<|message>思考內容。" +
            "<|channel>final<|message>答案內容。";

        Assert.Equal("答案內容。", AiOutputSanitizer.Sanitize(content));
    }

    [Fact]
    public void 多個final段只取最後一個()
    {
        const string content = "<|channel|>final<|message|>第一版答案（不該被採用）。" +
            "<|channel|>final<|message|>修正後的最終答案。";

        Assert.Equal("修正後的最終答案。", AiOutputSanitizer.Sanitize(content));
    }

    [Fact]
    public void 無任何channel標記時原樣通過清洗()
    {
        const string content = "這是一段正常的回覆，沒有任何 channel 標記。";

        Assert.Equal(content, AiOutputSanitizer.Sanitize(content));
    }

    [Fact]
    public void final段之外殘留的token一併剝除()
    {
        const string content = "<|start|><|channel|>final<|message|>答案內容。<|end|>";

        Assert.Equal("答案內容。", AiOutputSanitizer.Sanitize(content));
    }

    [Theory]
    [InlineData("内存不足", "記憶體不足")]
    [InlineData("网络异常", "網路異常")]
    [InlineData("数据遗失", "資料遺失")]
    [InlineData("默认设定", "預設設定")]
    [InlineData("登录失败", "登入失敗")]
    [InlineData("该硬盘导致系统故障", "該硬碟導致系統故障")]
    [InlineData("用户忘记密码", "使用者忘記密碼")]
    public void 簡體字轉為台灣繁體用詞(string input, string expected)
    {
        Assert.Equal(expected, AiOutputSanitizer.Sanitize(input));
    }

    [Fact]
    public void JSON內容清洗轉換後仍可反序列化且鍵名不變()
    {
        const string content = """{"riskLevel":"高","summary":"检测到网络异常，建议检查该主机的默认设定"}""";

        var sanitized = AiOutputSanitizer.Sanitize(content);
        Assert.NotNull(sanitized);

        using var doc = JsonDocument.Parse(sanitized!);
        Assert.Equal("高", doc.RootElement.GetProperty("riskLevel").GetString());
        Assert.Equal("檢測到網路異常，建議檢查該主機的預設設定", doc.RootElement.GetProperty("summary").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<|start|><|end|>")]
    public void 空白或純token輸入回null(string content)
    {
        Assert.Null(AiOutputSanitizer.Sanitize(content));
    }
}
