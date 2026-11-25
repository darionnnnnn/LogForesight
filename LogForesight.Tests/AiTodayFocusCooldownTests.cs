using LogForesight.Core.Analysis;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Services;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// CU-4：AI 冷卻中，今日焦點不先算儀表板摘要。儀表板服務刻意給 null——
/// 走到摘要就擲例外，所以「不擲、回 null、AI 零呼叫」證明冷卻判斷在摘要之前。
/// </summary>
public sealed class AiTodayFocusCooldownTests
{
    private static AiController Controller(FakeWebAi ai, IAiRequestGate gate) => new(
        new AiInsightService(ai), null!, null!, null!, null!, new FakeHostStore(),
        new HandlingNoteAiService(ai), new ManualAiThrottle(), new AutoAiThrottle(),
        FakeCurrentUser.ForUser(1), new RecordingAuditService(), new FakeSystemSettingsStore(), new AiAnalysisRunState(),
        gate);

    [Fact]
    public async Task 冷卻中_不算摘要_直接回null()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = new AiRequestGate(() => 1, timeProvider: clock);
        gate.ReportNetworkFailure(AiLane.Interactive);
        var ai = new FakeWebAi { Response = "{\"items\":[{\"text\":\"x\"}]}" };

        var res = await Controller(ai, gate).TodayFocus(7);

        Assert.True(res.Success);
        Assert.Null(res.Data);
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task 冷卻結束_照常走摘要()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = new AiRequestGate(() => 1, timeProvider: clock);
        gate.ReportNetworkFailure(AiLane.Interactive);
        clock.Advance(AiRequestGate.NetworkFailureCooldown + TimeSpan.FromSeconds(1));

        // 冷卻已過：會去取摘要（此處儀表板服務為 null，擲例外即代表有走到摘要）
        await Assert.ThrowsAsync<NullReferenceException>(() => Controller(new FakeWebAi(), gate).TodayFocus(7));
    }
}
