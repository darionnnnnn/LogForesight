using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 執行紀錄寫入佇列：以可閘控的寫入替身證明「入佇列時尚未寫庫」、上限邊界兩側與丟棄說明、結束排空。
/// </summary>
public class RunLogQueueTests
{
    private static BatchRunLog Row(int i) => new() { RunId = 7, Level = "Warn", Logger = "T", Message = "m" + i, LoggedAt = DateTime.Now };

    private sealed class Rig
    {
        public readonly ManualResetEventSlim Gate = new(false);
        public readonly ManualResetEventSlim FirstWriteEntered = new(false);
        public readonly List<BatchRunLog> Written = new();
        public readonly List<long> Notes = new();
        public int WriterThreadId;
        public RunLogQueue Queue = null!;

        public static Rig Create(int capacity, bool gated)
        {
            var rig = new Rig();
            if (!gated) rig.Gate.Set();
            rig.Queue = new RunLogQueue(7, row =>
            {
                rig.FirstWriteEntered.Set();
                rig.Gate.Wait();
                lock (rig.Written) rig.Written.Add(row);
                rig.WriterThreadId = Environment.CurrentManagedThreadId;
            }, n => { lock (rig.Notes) rig.Notes.Add(n); }, capacity, inline: false);
            return rig;
        }
    }

    [Fact]
    public void 入佇列返回時尚未寫庫_寫入只發生在背景執行緒且完成時排空()
    {
        var rig = Rig.Create(10, gated: true);
        var callerThread = Environment.CurrentManagedThreadId;

        for (var i = 0; i < 5; i++) Assert.True(rig.Queue.TryEnqueueCaptured(Row(i)));
        // 寫入端被閘卡住：呼叫端已全部返回，卻沒有任何一筆落庫
        Assert.True(rig.FirstWriteEntered.Wait(TimeSpan.FromSeconds(10)));
        lock (rig.Written) Assert.Empty(rig.Written);

        rig.Gate.Set();
        Assert.True(rig.Queue.Complete(TimeSpan.FromSeconds(10)));
        lock (rig.Written)
        {
            Assert.Equal(5, rig.Written.Count);
            Assert.Equal(Enumerable.Range(0, 5).Select(i => "m" + i), rig.Written.Select(r => r.Message));
        }
        Assert.NotEqual(callerThread, rig.WriterThreadId);
    }

    [Fact]
    public void 上限邊界_剛好等於上限全收_多一筆丟棄並計數且補一筆說明()
    {
        var rig = Rig.Create(3, gated: true);

        Assert.True(rig.Queue.TryEnqueueCaptured(Row(0)));
        Assert.True(rig.FirstWriteEntered.Wait(TimeSpan.FromSeconds(10))); // 第 0 筆在寫入中，仍算未寫出
        Assert.True(rig.Queue.TryEnqueueCaptured(Row(1)));
        Assert.True(rig.Queue.TryEnqueueCaptured(Row(2)));     // 剛好 3 筆：全收
        Assert.Equal(0, rig.Queue.DroppedTotal);

        Assert.False(rig.Queue.TryEnqueueCaptured(Row(3)));    // 第 4 筆：丟棄
        Assert.False(rig.Queue.TryEnqueueCaptured(Row(4)));
        Assert.Equal(2, rig.Queue.DroppedTotal);

        rig.Gate.Set();
        Assert.True(rig.Queue.Complete(TimeSpan.FromSeconds(10)));
        lock (rig.Written)
        {
            Assert.Equal(new[] { "m0", "m1", "m2" }, rig.Written.Take(3).Select(r => r.Message));
            var note = Assert.Single(rig.Written, r => r.Logger == RunLogQueue.DropNoteLogger);
            Assert.Equal("Warn", note.Level);
            Assert.Contains("2 筆", note.Message);
            Assert.Contains("3 筆", note.Message);
            Assert.Equal(4, rig.Written.Count);
        }
        lock (rig.Notes) Assert.Equal(new long[] { 2 }, rig.Notes);
    }

    [Fact]
    public void 寫出後騰出空位_上限是未寫出的筆數而非累計筆數()
    {
        var rig = Rig.Create(2, gated: false);
        for (var i = 0; i < 20; i++)
        {
            Assert.True(rig.Queue.TryEnqueueCaptured(Row(i)));
            Assert.True(rig.Queue.Flush(TimeSpan.FromSeconds(10)));
        }
        Assert.Equal(0, rig.Queue.DroppedTotal);
        Assert.True(rig.Queue.Complete(TimeSpan.FromSeconds(10)));
        lock (rig.Written) Assert.Equal(20, rig.Written.Count);
    }

    [Fact]
    public void 親自寫的行不受上限約束且與擷取事件保持順序()
    {
        var rig = Rig.Create(1, gated: true);
        Assert.True(rig.Queue.TryEnqueueCaptured(Row(0)));
        Assert.False(rig.Queue.TryEnqueueCaptured(Row(1)));    // 擷取事件已滿
        Assert.True(rig.Queue.TryEnqueueForced(Row(2)));       // 里程碑類照收
        Assert.True(rig.Queue.TryEnqueueForced(Row(3)));
        rig.Gate.Set();
        Assert.True(rig.Queue.Complete(TimeSpan.FromSeconds(10)));
        lock (rig.Written)
            Assert.Equal(new[] { "m0", "m2", "m3" }, rig.Written.Where(r => r.Logger == "T").Select(r => r.Message));
    }

    [Fact]
    public void 結束後不再收件_forced回傳false由呼叫端直接寫()
    {
        var rig = Rig.Create(5, gated: false);
        Assert.True(rig.Queue.Complete(TimeSpan.FromSeconds(10)));
        Assert.False(rig.Queue.TryEnqueueCaptured(Row(0)));
        Assert.False(rig.Queue.TryEnqueueForced(Row(1)));
    }

    [Fact]
    public void 寫入端擲例外交由write自行處理_佇列不被拖垮()
    {
        var written = new List<string>();
        var queue = new RunLogQueue(7, row =>
        {
            if (row.Message == "壞") return; // write 委派自行吞例外的等價行為
            lock (written) written.Add(row.Message);
        }, _ => { }, 5, inline: false);
        queue.TryEnqueueCaptured(Row(0));
        queue.TryEnqueueForced(new BatchRunLog { RunId = 7, Level = "Info", Logger = "x", Message = "壞" });
        queue.TryEnqueueCaptured(Row(1));
        Assert.True(queue.Complete(TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { "m0", "m1" }, written);
    }
}
