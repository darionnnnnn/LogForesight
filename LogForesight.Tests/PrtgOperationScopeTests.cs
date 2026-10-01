using System.Net;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgOperationScopeTests
{
    [Fact]
    public void 執行版本顯示期望差異與安全取消_不暴露憑證或來源網址()
    {
        var initial = new SystemSettings { PrtgEnabled = true, Revision = "before", PrtgUrl = "https://private.example", PrtgApiTokenEnc = "PRIVATE-TOKEN" };
        var current = new SystemSettings { PrtgEnabled = true, Revision = "before", PrtgUrl = initial.PrtgUrl, PrtgApiTokenEnc = initial.PrtgApiTokenEnc };
        var scopeRevision = "old-scope"; var kind = Guid.NewGuid().ToString("N");
        using var scope = new PrtgOperationScope(initial, () => current, default, () => scopeRevision, kind);
        scope.Checkpoint(); scope.CompletedStage("第一批提交完成");
        scopeRevision = "new-scope"; current.Revision = "after";
        var version = Assert.Single(PrtgOperationScope.ReadVersions().Where(v => v.Kind == kind));
        Assert.Equal("before", version.AdoptedSettingsRevision); Assert.Equal("after", version.ExpectedSettingsRevision);
        Assert.NotEqual(version.AdoptedScopeRevision, version.ExpectedScopeRevision);
        Assert.Equal("等待安全取消點", version.State); Assert.Equal("第一批提交完成", version.LastCompletedStage);
        var json = System.Text.Json.JsonSerializer.Serialize(version);
        Assert.DoesNotContain("PRIVATE-TOKEN", json); Assert.DoesNotContain("private.example", json);
        Assert.Throws<OperationCanceledException>(scope.Checkpoint);
        scope.Dispose();
        version = Assert.Single(PrtgOperationScope.ReadVersions().Where(v => v.Kind == kind));
        Assert.NotNull(version.EndedAtUtc); Assert.Contains("已取消", version.State);
    }
    [Fact]
    public void 設定停用_只取消自己的作業_即使版本時間相同()
    {
        using var parent = new CancellationTokenSource();
        var initial = new SystemSettings { PrtgEnabled = true };
        var current = new SystemSettings { PrtgEnabled = false, UpdatedAt = initial.UpdatedAt };
        using var scope = new PrtgOperationScope(initial, () => current, parent.Token);
        Assert.Throws<OperationCanceledException>(scope.Checkpoint);
        Assert.True(scope.SettingsChanged);
        Assert.True(scope.Token.IsCancellationRequested);
        Assert.False(parent.IsCancellationRequested);
    }

    [Fact]
    public void 郵件設定改變_不停止取數_父層取消仍有效()
    {
        using var parent = new CancellationTokenSource();
        var initial = new SystemSettings { PrtgEnabled = true, MailEnabled = false };
        var current = new SystemSettings { PrtgEnabled = true, MailEnabled = true };
        using var scope = new PrtgOperationScope(initial, () => current, parent.Token);
        scope.Checkpoint();
        Assert.False(scope.Token.IsCancellationRequested);
        parent.Cancel();
        Assert.Throws<OperationCanceledException>(scope.Checkpoint);
        Assert.False(scope.SettingsChanged);
    }

    [Theory]
    [InlineData(PrtgAuthModes.Token)]
    [InlineData(PrtgAuthModes.Password)]
    public async Task 回應中停用_不發布回應也不發下一個請求(string authMode)
    {
        var initial = new SystemSettings { PrtgEnabled = true };
        var current = new SystemSettings { PrtgEnabled = true };
        using var parent = new CancellationTokenSource();
        using var scope = new PrtgOperationScope(initial, () => current, parent.Token);
        using var handler = new StopHandler(() => current.PrtgEnabled = false);
        using var client = new PrtgClient("https://prtg.example.test", "token", 5, false,
            handler, authMode, "user", "password", "");
        client.OperationCheckpoint = scope.Checkpoint;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetJsonAsync("/api/table.json", scope.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetJsonAsync("/api/table.json", scope.Token));
        Assert.Equal(1, handler.Requests);
        Assert.False(parent.IsCancellationRequested);
    }

    private sealed class StopHandler(Action stop) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            stop();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("123456") });
        }
    }
}
