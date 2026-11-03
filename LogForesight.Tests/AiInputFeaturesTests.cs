using Xunit;

namespace LogForesight.Tests;

/// <summary>第 63 輪 F-4：輸入特徵（<see cref="AiInputFeatures"/>）只算次數與長度，不含原文。</summary>
public sealed class AiInputFeaturesTests
{
    private static LogIssueSignature Issue(string source = "", string? ruleId = null, params string[] samples) => new()
    {
        LogName = "System", Source = source, EventId = 1, Count = 1, RuleId = ruleId,
        SampleMessages = samples.ToList()
    };

    [Fact]
    public void 總字數為0_比例為null_不是0()
    {
        var features = AiInputFeatures.From(new DailyAnalysisRecord());

        Assert.Equal(0, features.InputChars);
        Assert.Equal(0, features.LongestItemChars);
        Assert.Null(features.NonAsciiRatio);
        Assert.Equal(0, features.IssueCount);
        Assert.Equal(0, features.QuoteCount);
    }

    [Fact]
    public void 只有空字串項目_仍視為沒有文字_比例null()
    {
        var record = new DailyAnalysisRecord
        {
            TopIssues = { Issue("", null, "", "") },
            TrendAlerts = { "" }
        };

        var features = AiInputFeatures.From(record);

        Assert.Equal(1, features.IssueCount);
        Assert.Equal(0, features.InputChars);
        Assert.Null(features.NonAsciiRatio);
    }

    [Fact]
    public void 計數欄位_問題_規則命中_錯誤警告稽核_提示_不完整()
    {
        var record = new DailyAnalysisRecord
        {
            ErrorCount = 4, WarningCount = 5, AuditEventCount = 6, DataIncomplete = true,
            TopIssues = { Issue("a", "rule-1"), Issue("b", null), Issue("c", ""), Issue("d", "rule-2") },
            TrendAlerts = { "t1", "t2" },
            CorrelationAlerts = { "c1" }
        };

        var f = AiInputFeatures.From(record);

        Assert.Equal(4, f.IssueCount);
        Assert.Equal(2, f.RuleHitCount); // null 與空字串都不算命中
        Assert.Equal(4, f.ErrorCount);
        Assert.Equal(5, f.WarningCount);
        Assert.Equal(6, f.AuditCount);
        Assert.Equal(2, f.TrendAlertCount);
        Assert.Equal(1, f.CorrelationAlertCount);
        Assert.True(f.DataIncomplete);
    }

    [Fact]
    public void 文字範圍_來源_範例訊息_趨勢_關聯_未涵蓋_總字數與最長單則()
    {
        var record = new DailyAnalysisRecord
        {
            TopIssues = { Issue("Src12", null, "12345", "1234567890") },   // 5 + 5 + 10
            TrendAlerts = { "ab" },                                          // 2
            CorrelationAlerts = { "cdefg" },                                 // 5
            UncoveredChecks = { new string('x', 20) },                       // 20
            // 不在範圍內：這些欄位不得計入
            Summary = new string('z', 500), Headline = new string('h', 500), Action = "動作動作"
        };
        record.TopIssues[0].KeyDetails = new string('k', 300);

        var f = AiInputFeatures.From(record);

        Assert.Equal(5 + 5 + 10 + 2 + 5 + 20, f.InputChars);
        Assert.Equal(20, f.LongestItemChars);
    }

    [Fact]
    public void 特殊字元計數_引號_反斜線_大括號_中括號_小於_控制字元_非BMP()
    {
        // "x" 兩個引號；\ 一個；{ 兩個；[ 一個；< 兩個；控制字元：\u0001 \u007f（\r \n \t 不算）；emoji 一個字元（兩個 UTF-16 單元）
        var text = "\"x\" \\ {{ [ << \u0001\u007f\r\n\t 😀";
        var record = new DailyAnalysisRecord { TopIssues = { Issue("", null, text) } };

        var f = AiInputFeatures.From(record);

        Assert.Equal(2, f.QuoteCount);
        Assert.Equal(1, f.BackslashCount);
        Assert.Equal(2, f.OpenBraceCount);
        Assert.Equal(1, f.OpenBracketCount);
        Assert.Equal(2, f.LessThanCount);
        Assert.Equal(2, f.ControlCharCount);
        Assert.Equal(1, f.NonBmpCharCount);
        Assert.Equal(text.Length, f.InputChars);
    }

    [Fact]
    public void 路徑JSONXML樣式片段()
    {
        var messages = new[]
        {
            @"檔案 C:\Windows\System32\drivers\x.sys 損毀 與 \\fileserver\share\a.txt", // 2 條 Windows 路徑
            "log at /var/log/syslog and /etc/ssh/sshd_config",                           // 2 條 Unix 路徑
            "https://example.com/a/b 與 10:30:00",                                        // URL 與時間不算路徑
            "{\"name\":\"x\",\"count\":3}",                                              // 2 個 "鍵": 片段
            "<Event xmlns=\"x\"><System/></Event>"                                        // <Event ...> <System/> </Event>
        };
        var record = new DailyAnalysisRecord { TopIssues = { Issue("", null, messages) } };

        var f = AiInputFeatures.From(record);

        Assert.Equal(4, f.PathLikeCount);
        Assert.Equal(2, f.JsonLikeCount);
        Assert.Equal(3, f.XmlLikeCount);
    }

    [Fact]
    public void 非ASCII比例_以UTF16字元為單位()
    {
        var record = new DailyAnalysisRecord { TrendAlerts = { "ab中文", "cdef" } }; // 8 字元，2 個非 ASCII

        Assert.Equal(0.25, AiInputFeatures.From(record).NonAsciiRatio);
    }

    [Fact]
    public void 純ASCII_比例為0而不是null()
    {
        var record = new DailyAnalysisRecord { TrendAlerts = { "abc" } };

        Assert.Equal(0.0, AiInputFeatures.From(record).NonAsciiRatio);
    }

    [Fact]
    public void 不改動輸入紀錄()
    {
        var record = new DailyAnalysisRecord { TopIssues = { Issue("s", "r", "m") }, TrendAlerts = { "t" } };
        var before = System.Text.Json.JsonSerializer.Serialize(record);

        _ = AiInputFeatures.From(record);

        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(record));
    }
}

/// <summary>第 63 輪 F-4：回覆類別判定（案例只保留回覆前 N 字，只能從開頭判斷）。</summary>
public sealed class AiReplyShapeTests
{
    [Theory]
    [InlineData("{\"risk_level\":", "object")]
    [InlineData("  \n\t{\"a\":1}", "object")]              // 略過開頭空白
    [InlineData("```json\n{\"a\":1}\n```", "fence")]
    [InlineData("~~~\n{}", "fence")]
    [InlineData("[1,2]", "array")]
    [InlineData("[{\"a\":1}]", "array")]
    [InlineData("好的，以下是分析：{\"a\":1}", "text")]    // 前言後才有 JSON 仍是 text
    [InlineData("Sorry, I cannot", "text")]
    [InlineData("``", "text")]                              // 圍欄要三個
    [InlineData("", "blank")]
    [InlineData("   \r\n\t ", "blank")]
    [InlineData(null, "blank")]
    public void 形狀(string? reply, string expected) =>
        Assert.Equal(expected, AiReplyShape.Classify(reply, 600).Shape);

    [Theory]
    [InlineData(599, false)]
    [InlineData(600, true)]    // 剛好上限：與被截斷無法區分，保守標 cut_off
    [InlineData(601, true)]
    [InlineData(603, true)]    // TextTruncation.Truncate 截斷後會接「...」，長度 600+3
    public void 達上限標cut_off(int length, bool expected)
    {
        var reply = "{" + new string('a', length - 1);

        var result = AiReplyShape.Classify(reply, 600);

        Assert.Equal(length, reply.Length);
        Assert.Equal(expected, result.CutOff);
        Assert.Equal("object", result.Shape);
    }

    [Fact]
    public void blank不標cut_off_即使上限是0()
    {
        Assert.False(AiReplyShape.Classify("", 600).CutOff);
        Assert.False(AiReplyShape.Classify("   ", 0).CutOff);
    }

    [Fact]
    public void 上限不為正_不標cut_off()
    {
        Assert.False(AiReplyShape.Classify("{abc", 0).CutOff);
        Assert.False(AiReplyShape.Classify("{abc", -1).CutOff);
    }

    [Fact]
    public void 常數值固定_是匯出契約()
    {
        Assert.Equal(new[] { "object", "fence", "array", "text", "blank" },
            new[] { AiReplyShape.Object, AiReplyShape.Fence, AiReplyShape.Array, AiReplyShape.Text, AiReplyShape.Blank });
    }
}
