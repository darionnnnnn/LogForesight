using LogForesight.Core.Analysis;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 強制格式（response_format）啟用狀態的跨呼叫記憶（第 70 輪 C-1）。
/// 一律以固定順序餵入事件驗證計數，不用壓力測試。
/// </summary>
public class AiGrammarMemoryTests
{
    private const string Key = "http://ai/v1/chat/completions\u0001m\u0001AiAnalysisResult";

    private static void Empty(AiGrammarMemory memory, string key = Key) =>
        memory.ReportFirstAttempt(key, usedGrammar: true, FirstAttemptOutcome.Empty);

    private static AiGrammarMemory Disabled(string key = Key)
    {
        var memory = new AiGrammarMemory();
        for (var i = 0; i < AiGrammarMemory.DisableThreshold; i++) Empty(memory, key);
        return memory;
    }

    [Fact]
    public void 常數維持暫定值()
    {
        Assert.Equal(3, AiGrammarMemory.DisableThreshold);
        Assert.Equal(20, AiGrammarMemory.ProbeInterval);
    }

    [Fact]
    public void 從未回報_預設帶強制格式()
    {
        var memory = new AiGrammarMemory();
        for (var i = 0; i < 50; i++) Assert.True(memory.UseGrammarFirst(Key));
    }

    [Fact]
    public void 連續兩次空回覆仍帶格式_第三次後停用()
    {
        var memory = new AiGrammarMemory();

        Empty(memory);
        Assert.True(memory.UseGrammarFirst(Key));
        Empty(memory);
        Assert.True(memory.UseGrammarFirst(Key));
        Empty(memory);

        Assert.False(memory.UseGrammarFirst(Key));
        Assert.True(memory.IsDisabled(Key));
    }

    [Fact]
    public void 帶格式的非空回覆_連續計數歸零()
    {
        var memory = new AiGrammarMemory();
        Empty(memory);
        Empty(memory);
        memory.ReportFirstAttempt(Key, usedGrammar: true, FirstAttemptOutcome.OtherFailure);
        Empty(memory);
        Empty(memory);

        // 中間被打斷過：總共四次空回覆但從未連續三次
        Assert.True(memory.UseGrammarFirst(Key));
        Assert.False(memory.IsDisabled(Key));

        memory.ReportFirstAttempt(Key, usedGrammar: true, FirstAttemptOutcome.Success);
        Empty(memory);
        Empty(memory);
        Assert.False(memory.IsDisabled(Key));
    }

    [Fact]
    public void 停用後_每第二十次呼叫探測一次_其餘不帶()
    {
        var memory = Disabled();

        for (var call = 1; call <= AiGrammarMemory.ProbeInterval * 2; call++)
        {
            var expected = call % AiGrammarMemory.ProbeInterval == 0;
            Assert.Equal(expected, memory.UseGrammarFirst(Key));
        }
    }

    [Fact]
    public void 探測回非空_恢復啟用_之後每次都帶()
    {
        var memory = Disabled();
        for (var call = 1; call < AiGrammarMemory.ProbeInterval; call++) Assert.False(memory.UseGrammarFirst(Key));
        Assert.True(memory.UseGrammarFirst(Key)); // 第 20 次：探測

        memory.ReportFirstAttempt(Key, usedGrammar: true, FirstAttemptOutcome.Success);

        Assert.False(memory.IsDisabled(Key));
        for (var i = 0; i < 30; i++) Assert.True(memory.UseGrammarFirst(Key));
    }

    [Fact]
    public void 探測回其他失敗_同樣恢復啟用()
    {
        var memory = Disabled();
        for (var call = 1; call <= AiGrammarMemory.ProbeInterval; call++) memory.UseGrammarFirst(Key);

        memory.ReportFirstAttempt(Key, usedGrammar: true, FirstAttemptOutcome.OtherFailure);

        Assert.False(memory.IsDisabled(Key));
        Assert.True(memory.UseGrammarFirst(Key));
    }

    [Fact]
    public void 探測又空_維持停用_並重新數到下一個探測點()
    {
        var memory = Disabled();
        for (var call = 1; call < AiGrammarMemory.ProbeInterval; call++) Assert.False(memory.UseGrammarFirst(Key));
        Assert.True(memory.UseGrammarFirst(Key)); // 探測

        Empty(memory); // 探測又空

        Assert.True(memory.IsDisabled(Key));
        for (var call = 1; call < AiGrammarMemory.ProbeInterval; call++) Assert.False(memory.UseGrammarFirst(Key));
        Assert.True(memory.UseGrammarFirst(Key)); // 下一個探測點
    }

    [Theory]
    [InlineData(FirstAttemptOutcome.Empty)]
    [InlineData(FirstAttemptOutcome.OtherFailure)]
    public void 不帶格式時非成功_恢復啟用並歸零(FirstAttemptOutcome outcome)
    {
        var memory = Disabled();
        Assert.False(memory.UseGrammarFirst(Key));

        memory.ReportFirstAttempt(Key, usedGrammar: false, outcome);

        Assert.False(memory.IsDisabled(Key));
        Assert.True(memory.UseGrammarFirst(Key));
        // 計數已歸零：要再連續三次才會停用
        Empty(memory);
        Empty(memory);
        Assert.True(memory.UseGrammarFirst(Key));
    }

    [Fact]
    public void 不帶格式且成功_狀態不變()
    {
        var memory = Disabled();

        for (var i = 0; i < 5; i++) memory.ReportFirstAttempt(Key, usedGrammar: false, FirstAttemptOutcome.Success);

        Assert.True(memory.IsDisabled(Key));
        Assert.False(memory.UseGrammarFirst(Key));
    }

    [Fact]
    public void 不帶格式且成功_不會清掉啟用中的連續計數()
    {
        var memory = new AiGrammarMemory();
        Empty(memory);
        Empty(memory);

        memory.ReportFirstAttempt(Key, usedGrammar: false, FirstAttemptOutcome.Success);
        Empty(memory);

        Assert.True(memory.IsDisabled(Key));
    }

    [Fact]
    public void 不同鍵互不影響()
    {
        var memory = Disabled(Key);
        const string other = "http://ai/v1/chat/completions\u0001m2\u0001AiAnalysisResult";

        Assert.False(memory.UseGrammarFirst(Key));
        Assert.True(memory.UseGrammarFirst(other));
        Empty(memory, other);
        Empty(memory, other);
        Assert.True(memory.UseGrammarFirst(other)); // 兩次還不到門檻
        Assert.False(memory.UseGrammarFirst(Key)); // 原本那把仍停用
    }

    [Fact]
    public void BuildKey_端點_模型_型別任一不同就是另一把鍵()
    {
        var baseKey = AiGrammarMemory.BuildKey("http://a", "m", "T");

        Assert.NotEqual(baseKey, AiGrammarMemory.BuildKey("http://b", "m", "T"));
        Assert.NotEqual(baseKey, AiGrammarMemory.BuildKey("http://a", "m2", "T"));
        Assert.NotEqual(baseKey, AiGrammarMemory.BuildKey("http://a", "m", "T2"));
        Assert.Equal(baseKey, AiGrammarMemory.BuildKey("http://a", "m", "T"));
    }

    [Fact]
    public void 閘道實例各自持有記憶_Null閘道每次存取都是新實例()
    {
        var gate = new AiRequestGate(() => 1);
        Assert.Same(gate.GrammarMemory, gate.GrammarMemory);
        Assert.NotSame(gate.GrammarMemory, new AiRequestGate(() => 1).GrammarMemory);

        Assert.NotSame(NullAiRequestGate.Instance.GrammarMemory, NullAiRequestGate.Instance.GrammarMemory);
    }
}
