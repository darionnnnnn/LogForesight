using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// 「同步結構與對應」的執行本體（docs/PRTG-SPEC.md §5a）。
///
/// 夜間排程的 PRTG 路徑會做結構同步，但那是每天一次的事：PRTG 剛啟用、或剛新增一批裝置時，
/// 鏡像表要等到隔天才有內容，而主機對應、資源守門的自動偵測、觸發式取數全都建立在鏡像之上。
/// 這條路徑讓管理者當場把鏡像補齊並立刻重算今天的對應。
///
/// 兩步：① 結構與狀態變更同步（不取數值）；② 對今天做主機對應。
/// 結構同步失敗就不做對應——對著半套或空的鏡像算對應，只會把既有結果洗成一堆 unmatched。
/// </summary>
public static class PrtgStructureSyncRunner
{
    /// <summary>
    /// 執行一次同步與對應。回傳結果狀態物件（成功或失敗都回，呼叫端負責持久化）。
    /// </summary>
    public static async Task<PrtgStructureSyncStatus> RunAsync(
        PrtgFetchService fetchService,
        EfPrtgStore prtgStore,
        IHostStore hostStore,
        IPrtgAddressResolver resolver,
        int concurrency,
        IRunConsole console,
        CancellationToken ct,
        Action<string, int, int>? progress = null,
        DateTime? today = null)
    {
        var mapDate = (today ?? DateTime.Today).Date;
        var stopwatch = Stopwatch.StartNew();
        var status = new PrtgStructureSyncStatus { MapDate = mapDate };
        var syncFailures = 0;

        try
        {
            // 1. 結構與狀態變更同步（fetchValues:false——數值取數是夜間批次與歷史回填的事）
            progress?.Invoke(RunPhases.PrtgSync, 0, 0);
            var fetchResult = await fetchService.FetchDayAsync(
                mapDate, concurrency, ct, syncStructure: true, fetchValues: false,
                (stage, done, total) => progress?.Invoke(stage, done, total));

            status.Devices = fetchResult.Devices;
            status.Sensors = fetchResult.Sensors;
            syncFailures = fetchResult.Failures;

            console.WriteLine($"結構同步完成：裝置 {fetchResult.Devices}、感測器 {fetchResult.Sensors}、" +
                              $"狀態變更 {fetchResult.StateChanges}" +
                              (fetchResult.Failures > 0 ? $"、失敗階段 {fetchResult.Failures}" : ""));

            // 分頁階段的失敗不會擲例外，只累加 Failures——沒有這道判斷的話，
            // 「PRTG 整台連不上」會被當成一次成功的同步（裝置 0、感測器 0），
            // 接著把整份對應洗成空的。
            if (fetchResult.Failures > 0 && fetchResult.Devices == 0)
            {
                stopwatch.Stop();
                status.Success = false;
                status.ErrorMessage = $"結構同步有 {fetchResult.Failures} 個階段失敗，且沒有取得任何裝置。";
                status.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                status.CompletedAt = DateTime.Now;
                console.WriteLine("✗ 結構同步沒有取得任何裝置（未進行主機對應，既有對應結果保持不變）。" +
                                  "請確認 PRTG 連線位址與認證是否正確。");
                return status;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 結構同步失敗就不做對應：對著空的或半套的鏡像算對應，會把原本正確的對應
            // 洗成一整批 unmatched，比不算還糟。
            stopwatch.Stop();
            status.Success = false;
            status.ErrorMessage = ex.Message;
            status.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            status.CompletedAt = DateTime.Now;
            console.WriteLine($"✗ 結構同步失敗：{ex.Message}（未進行主機對應，既有對應結果保持不變）");
            return status;
        }

        try
        {
            // 2. 主機對應（對今天）
            var mapper = new PrtgHostMapper(prtgStore, hostStore, console, resolver);
            var mapResult = mapper.MapForDate(mapDate);

            status.MapOk = mapResult.Ok;
            status.MapManual = mapResult.Manual;
            status.MapConflict = mapResult.Conflict;
            status.MapUnmatched = mapResult.Unmatched;
            status.MapSkippedNoIp = mapResult.SkippedNoIp;
            status.MapSkippedExcluded = mapResult.SkippedExcluded;
            status.MapSkippedManualSibling = mapResult.SkippedManualSibling;

            console.WriteLine($"主機對應完成（{mapDate:yyyy-MM-dd}）：成功 {mapResult.Ok}、人工 {mapResult.Manual}、" +
                              $"衝突 {mapResult.Conflict}、查無主機 {mapResult.Unmatched}、" +
                              $"略過 {mapResult.SkippedNoIp + mapResult.SkippedExcluded + mapResult.SkippedManualSibling}");

            if (mapResult.Ok + mapResult.Manual == 0)
            {
                console.WriteLine("⚠ 沒有任何 PRTG 裝置對應到主機主檔。請確認 PRTG 裝置的位址欄位與主機清單的 IP 是否一致；" +
                                  "裝置以 DNS 名稱建立時，名稱必須解析得到 IP 才對得上。");
            }

            // 有階段失敗但裝置有拿到：對應照做（那部分鏡像是有效的），但整體不報成功，
            // 讓畫面說得出「這次同步不完整」。
            status.Success = syncFailures == 0;
            if (syncFailures > 0)
            {
                status.ErrorMessage = $"結構同步有 {syncFailures} 個階段失敗，鏡像可能不完整。";
                console.WriteLine($"⚠ 結構同步有 {syncFailures} 個階段失敗，主機對應已依現有鏡像完成，但結果可能不完整。");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 對應失敗不抹掉已完成的結構同步成果——鏡像已經更新了，那部分是有效的。
            status.Success = false;
            status.ErrorMessage = $"主機對應失敗：{ex.Message}";
            console.WriteLine($"✗ 主機對應失敗：{ex.Message}（結構同步結果已保留）");
        }

        stopwatch.Stop();
        status.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        status.CompletedAt = DateTime.Now;
        return status;
    }
}
