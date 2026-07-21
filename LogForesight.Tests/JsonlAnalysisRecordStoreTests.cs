using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// JSONL 後端**特有**的行為（跨後端的共同語意在 <see cref="AnalysisRecordStoreContractTests"/>）：
/// 壞行容錯與整檔重寫的併發安全。這些都是「檔案就是資料庫」才會有的問題，
/// SQL 後端不適用，所以不放進合約基底。
/// </summary>
public class JsonlAnalysisRecordStoreTests : IDisposable
{
    private readonly string _tempFile =
        Path.Combine(Path.GetTempPath(), $"lf_test_{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
        GC.SuppressFinalize(this);
    }

    private JsonlAnalysisRecordStore Store() => new(_tempFile);

    private static DailyAnalysisRecord Record(DateTime date, string risk = "低") =>
        new() { Date = date, RiskLevel = risk, Headline = $"{date:MM-dd}" };

    /// <summary>
    /// 逐行獨立是 JSONL 的核心優點：一行壞掉不影響其餘。
    /// （整檔型的 JSON 壞了就是壞了——見 JsonCollectionFile 的相反取捨。）
    /// </summary>
    [Fact]
    public void 壞行_略過且其餘紀錄照常讀回()
    {
        var store = Store();
        store.Append(Record(DateTime.Today.AddDays(-2)));
        File.AppendAllText(_tempFile, "{ 這行不是合法的 JSON" + Environment.NewLine);
        store.Append(Record(DateTime.Today.AddDays(-1)));

        Assert.Equal(2, store.ReadRecent(DateTime.Today, 7).Count);
    }

    /// <summary>
    /// **A1 的核心迴歸測試**：重寫期間，先前開啟的讀取 handle 必須仍看到**完整的舊內容**。
    ///
    /// 這正是原子替換與直接覆寫的分水嶺。`File.Replace` 換掉的是目錄項目，已開啟的 handle
    /// continues 指向原本的檔案資料；`File.WriteAllLines` 則是就地截斷同一個檔案，
    /// 同一個 handle 會讀到被砍半的內容——而解析失敗的行是靜默略過的，
    /// 結果就是 Web 少了幾天資料卻沒有任何跡象。
    ///
    /// 用「重寫後才從舊 handle 讀」來斷言，是因為真正的競態（讀到一半被截斷）無法穩定重現；
    /// 這個性質與它同源，而且是決定性的。
    /// </summary>
    [Fact]
    public void 重寫期間_先前開啟的讀取handle仍看到完整舊內容()
    {
        var store = Store();
        store.Append(Record(DateTime.Today.AddDays(-100)));
        store.Append(Record(DateTime.Today));

        // 模擬 Web 已經開啟檔案準備讀取（共用模式與實作的讀取路徑一致）
        using var reader = new StreamReader(new FileStream(
            _tempFile, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete));

        Assert.Equal(1, store.Prune(90));   // 重寫發生在 handle 開啟之後

        var linesSeenByReader = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Length > 0) linesSeenByReader++;
        }

        // 舊 handle 看到的是替換前的完整兩行；直接覆寫的話這裡會是 1 行（被截斷後的新內容）
        Assert.Equal(2, linesSeenByReader);
    }

    /// <summary>替換後再讀取（新的 handle）看到的是新內容——舊內容只對舊 handle 有效</summary>
    [Fact]
    public void 重寫後_新的讀取取得新內容()
    {
        var store = Store();
        store.Append(Record(DateTime.Today.AddDays(-100)));
        store.Append(Record(DateTime.Today));

        store.Prune(90);

        Assert.Single(store.ReadRecent(DateTime.Today, 365));
    }

    /// <summary>讀者持檔期間，附掛週體檢（另一條重寫路徑）同樣要成功</summary>
    [Fact]
    public void 有讀者持檔時_附掛週體檢仍成功()
    {
        var store = Store();
        var date = DateTime.Today;
        store.Append(Record(date));

        using var reader = new FileStream(
            _tempFile, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        store.AttachWeeklyCheckup(date, new WeeklyCheckupResult
        {
            CheckupDate = date,
            HasFindings = true,
            Conclusion = "重寫期間有讀者持檔"
        });

        Assert.Equal(date.Date, store.LastWeeklyCheckupDate());
    }

    /// <summary>重寫不得留下暫存檔（資料目錄要乾淨，也避免下次重寫誤用殘檔）</summary>
    [Fact]
    public void 重寫後_不留下暫存檔()
    {
        var store = Store();
        store.Append(Record(DateTime.Today.AddDays(-100)));
        store.Append(Record(DateTime.Today));

        store.Prune(90);

        Assert.False(File.Exists(_tempFile + ".tmp"));
    }

    /// <summary>
    /// **批次重寫 × Web 查詢的併發驗收**：規劃階段列為「手動重整頁面確認」，
    /// 改寫成可重複執行的測試——人工重整只能碰運氣撞上那幾毫秒的重寫窗口，
    /// 這裡則是持續重寫並持續讀取，torn read 一旦發生必定被抓到。
    ///
    /// 斷言是「讀到的筆數永遠等於實際筆數」：直接覆寫的實作下，
    /// 讀者會在截斷的瞬間讀到較少的行（而壞行是靜默略過的，所以表現為「少了幾天資料」）。
    /// </summary>
    [Fact]
    public async Task 持續重寫期間_併發讀取不會少資料()
    {
        var store = Store();
        const int recordCount = 20;
        var anchor = DateTime.Today;

        for (int i = 0; i < recordCount; i++) store.Append(Record(anchor.AddDays(-i)));

        using var done = new CancellationTokenSource();
        var observedCounts = new System.Collections.Concurrent.ConcurrentBag<int>();

        // 保險絲：writer 若中途擲例外，reader 的迴圈沒有東西叫它停——
        // 沒有這個上限，一個失敗的測試會變成掛住整個測試回合
        done.CancelAfter(TimeSpan.FromSeconds(30));

        var reader = Task.Run(() =>
        {
            while (!done.Token.IsCancellationRequested)
            {
                observedCounts.Add(store.ReadRecent(anchor, recordCount).Count);
            }
        });

        var writer = Task.Run(() =>
        {
            try
            {
                // AttachWeeklyCheckup 每次都重寫整個檔案，是最頻繁的整檔改寫路徑
                for (int i = 0; i < 60; i++)
                {
                    store.AttachWeeklyCheckup(anchor, new WeeklyCheckupResult
                    {
                        CheckupDate = anchor,
                        HasFindings = true,
                        Conclusion = $"第 {i} 次重寫"
                    });
                }
            }
            finally
            {
                done.Cancel();
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.NotEmpty(observedCounts);

        var wrong = observedCounts.Where(c => c != recordCount).ToList();
        Assert.True(wrong.Count == 0,
            $"共 {observedCounts.Count} 次讀取，其中 {wrong.Count} 次筆數不對；" +
            $"觀察到的錯誤筆數：{string.Join(", ", wrong.Distinct().OrderBy(c => c))}");
    }

    [Fact]
    public void 歷史檔不存在_附掛安靜略過不擲例外()
    {
        Store().AttachWeeklyCheckup(DateTime.Today, new WeeklyCheckupResult { CheckupDate = DateTime.Today });

        Assert.False(File.Exists(_tempFile));
    }
}
