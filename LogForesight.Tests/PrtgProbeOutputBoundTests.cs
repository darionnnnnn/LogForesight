using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 探測／回填／結構同步的執行狀態是 singleton，輸出每次輪詢都整份複製；
/// 逐裝置輸出隨範圍成長，必須有界（第 77 輪 M-7）。
/// </summary>
public sealed class PrtgProbeOutputBoundTests
{
    [Fact]
    public void 未超過上限時輸出原樣保留()
    {
        var state = new PrtgProbeRunState();
        Assert.True(state.TryBeginRun(out _));
        for (var i = 0; i < 10; i++) state.AppendLine($"line {i}");

        var output = state.Snapshot().Output;
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"line {i}"), output);
    }

    [Fact]
    public void 超過上限時保留開頭與最新尾段並標出略過行數()
    {
        var state = new PrtgProbeRunState();
        Assert.True(state.TryBeginRun(out _));
        const int total = 100_000;
        for (var i = 0; i < total; i++) state.AppendLine($"line {i}");

        var output = state.Snapshot().Output;
        var head = PrtgProbeRunState.OutputHeadLimit;
        var tail = PrtgProbeRunState.OutputTailCapacity;
        Assert.Equal(head + 1 + tail, output.Count);
        Assert.Equal("line 0", output[0]);
        Assert.Equal($"line {head - 1}", output[head - 1]);
        Assert.Contains($"{total - head - tail:N0}", output[head]);
        Assert.Equal($"line {total - tail}", output[head + 1]);
        Assert.Equal($"line {total - 1}", output[^1]);
        Assert.Equal($"line {total - 1}", state.Snapshot().LatestMessage);
    }

    [Fact]
    public void 結束後保留同一份有界內容_新一趟開始時清空()
    {
        var state = new PrtgProbeRunState();
        Assert.True(state.TryBeginRun(out _));
        for (var i = 0; i < 5_000; i++) state.AppendLine($"line {i}");
        state.FinishRun(success: true, cancelled: false);

        var finished = state.Snapshot().Output;
        Assert.Equal(PrtgProbeRunState.OutputHeadLimit + 1 + PrtgProbeRunState.OutputTailCapacity, finished.Count);

        Assert.True(state.TryBeginRun(out _));
        state.AppendLine("new");
        Assert.Equal(new[] { "new" }, state.Snapshot().Output);
    }
}
