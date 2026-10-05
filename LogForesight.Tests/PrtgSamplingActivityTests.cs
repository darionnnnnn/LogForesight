using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSamplingActivityTests
{
    [Fact]
    public void NestedSamplingIsIdempotentAndBlocksAdmissionUntilAllScopesEnd()
    {
        var activity = new PrtgSamplingActivity();
        var first = activity.BeginSampling();
        var second = activity.BeginSampling();
        first.Dispose(); first.Dispose();
        Assert.False(activity.TryBeginBackground(out _));
        second.Dispose(); second.Dispose();
        Assert.True(activity.TryBeginBackground(out var lease));
        lease.Dispose();
    }

    [Fact]
    public void CallbackFailureDoesNotInterruptSamplingOrLeakAdmission()
    {
        var activity = new PrtgSamplingActivity();
        Assert.True(activity.TryBeginBackground(out var background));
        using var registration = background.Token.Register(() => throw new InvalidOperationException("fixture"));
        using (activity.BeginSampling())
        {
            Assert.True(background.WasPreempted);
            Assert.False(activity.TryBeginBackground(out _));
        }
        background.Dispose();
        Assert.True(activity.TryBeginBackground(out var next));
        next.Dispose();
    }

    [Fact]
    public async Task ConcurrentSamplingAndAdmissionNeverLeaveAnUncancelledLease()
    {
        for (var i = 0; i < 100; i++)
        {
            var activity = new PrtgSamplingActivity();
            PrtgSamplingActivity.BackgroundLease? background = null;
            using var start = new ManualResetEventSlim(false);
            var admit = Task.Run(() =>
            {
                start.Wait();
                if (activity.TryBeginBackground(out var lease)) background = lease;
            });
            var sample = Task.Run(() =>
            {
                start.Wait();
                return activity.BeginSampling();
            });
            start.Set();
            await Task.WhenAll(admit, sample);
            using (sample.Result)
            {
                Assert.False(activity.TryBeginBackground(out _));
                if (background != null) Assert.True(background.WasPreempted);
            }
            background?.Dispose();
            Assert.True(activity.TryBeginBackground(out var next));
            next.Dispose();
        }
    }
}
