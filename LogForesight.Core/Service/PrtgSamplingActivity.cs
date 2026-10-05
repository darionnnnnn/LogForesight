namespace LogForesight.Core.Service;

/// <summary>共用採樣優先 admission；採樣開始時取消尚未完成的背景歷史頁。</summary>
public sealed class PrtgSamplingActivity
{
    private static readonly PrtgSamplingActivity SharedInstance = new();
    private readonly object _gate = new();
    private readonly HashSet<BackgroundLease> _background = [];
    private int _samplingCount;

    public static PrtgSamplingActivity Shared => SharedInstance;

    /// <summary>宣告完整採樣區段；第一個採樣者會中止已准入的背景頁。</summary>
    public IDisposable BeginSampling()
    {
        BackgroundLease[] cancel;
        lock (_gate)
        {
            _samplingCount++;
            cancel = _background.ToArray();
        }
        foreach (var lease in cancel) lease.Preempt();
        return new SamplingLease(this);
    }

    /// <summary>無採樣進行時原子取得單一背景歷史頁名額。</summary>
    public bool TryBeginBackground(out BackgroundLease lease)
    {
        lock (_gate)
        {
            if (_samplingCount != 0)
            {
                lease = null!;
                return false;
            }
            lease = new BackgroundLease(this);
            _background.Add(lease);
            return true;
        }
    }

    private void EndSampling()
    {
        lock (_gate) _samplingCount = Math.Max(0, _samplingCount - 1);
    }

    private void EndBackground(BackgroundLease lease)
    {
        lock (_gate) _background.Remove(lease);
    }

    private sealed class SamplingLease(PrtgSamplingActivity owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.EndSampling();
        }
    }

    public sealed class BackgroundLease : IDisposable
    {
        private readonly PrtgSamplingActivity _owner;
        private readonly CancellationTokenSource _preempt = new();
        private int _disposed;

        internal BackgroundLease(PrtgSamplingActivity owner) => _owner = owner;
        public CancellationToken Token => _preempt.Token;
        public bool WasPreempted => _preempt.IsCancellationRequested;

        internal void Preempt()
        {
            try { _preempt.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException)
            {
                // 背景取消 callback 的錯誤不得阻止正式採樣取得優先權。
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.EndBackground(this);
            _preempt.Dispose();
        }
    }
}
