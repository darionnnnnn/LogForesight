using System.Diagnostics;

namespace LogForesight;

/// <summary>
/// <c>--netiq-probe</c>：對已設定的 Sentinel 逐一執行一組小規模驗證查詢，把原始回應與時間量測
/// 印成可直接複製貼回對話的報告（docs/NETIQ-API-PLAN.md §3.5）。
///
/// 這是欄位對應／IP 篩選批次大小／時區等未決事項（docs/NETIQ-API-PLAN.md §9）唯一的定案依據——
/// 公開文件沒有提供 event-search 結果頁的確切 JSON 結構範例，<see cref="SentinelClient"/> 的
/// <c>ParseEventsPage</c> 目前只能 best-effort 通用解析（docs/NETIQ-API-PLAN.md §3.3、§4 未決欄位），
/// 必須用真實環境的原始輸出核對後才能繼續實作 SentinelFieldMap／SentinelStatsSource
/// （docs/NETIQ-API-PLAN.md §8 步驟 3 起）。
///
/// 全程透過 <see cref="SentinelClient"/> 既有的單一佇列＋節流執行，總量約 15~20 個小查詢，
/// 對 server 負擔可忽略（docs/NETIQ-API-PLAN.md §5）。
/// </summary>
public static class NetiqProbeCli
{
    /// <summary>「查全部事件」的 filter。用嚴重度全範圍而不是 Lucene 的 <c>*:*</c>——
    /// <c>sev:[0 TO 5]</c> 是原廠文件實例（Aegis 整合範例的實際 payload）用過的語法，
    /// probe 的第一步就該用有文件背書的寫法，語法本身被拒的機率最低。</summary>
    private const string MatchAllFilter = "sev:[0 TO 5]";

    public static async Task<int> RunAsync(ISentinelStore sentinelStore, NetIqSettings settings)
    {
        var sentinels = sentinelStore.GetAll().Where(s => s.Active).ToList();
        if (sentinels.Count == 0)
        {
            Console.WriteLine("目前沒有已設定且啟用的 Sentinel（請先在 Web「資料匯入」頁新增），無法執行 probe。");
            return 1;
        }

        Console.WriteLine("══════════ NetIQ Sentinel API Probe ══════════");
        Console.WriteLine("以下輸出可直接複製貼回對話，用於定案欄位對應／批次大小／時區（docs/NETIQ-API-PLAN.md §9）。");
        Console.WriteLine();

        var allOk = true;
        foreach (var sentinel in sentinels)
        {
            if (string.IsNullOrWhiteSpace(sentinel.Username) || string.IsNullOrWhiteSpace(sentinel.PasswordEnc))
            {
                Console.WriteLine($"── {sentinel.Name}：帳密未設定（CanDiscover=false），略過 ──\n");
                continue;
            }

            var server = ToConnectable(sentinel);
            var ok = await ProbeOneAsync(server, settings);
            allOk &= ok;
        }

        Console.WriteLine("══════════ Probe 結束 ══════════");
        return allOk ? 0 : 1;
    }

    /// <summary>密碼在這裡解密，僅存在於本行程記憶體，不落地、不進 log（同 Web 端
    /// NetiqServerCatalog.ToProjection 的既有慣例）</summary>
    private static SentinelServer ToConnectable(Sentinel s) => new()
    {
        Id = s.SentinelId,
        Name = s.Name,
        BaseUrl = s.BaseUrl,
        Username = s.Username,
        Password = CryptoHelper.IsEncrypted(s.PasswordEnc) ? CryptoHelper.Decrypt(s.PasswordEnc) : s.PasswordEnc
    };

    private static async Task<bool> ProbeOneAsync(SentinelServer server, NetIqSettings settings)
    {
        Console.WriteLine($"── Sentinel「{server.Name}」（{server.BaseUrl}） ──");
        Console.WriteLine($"   [人工核對] apidoc（有無聚合端點可取代 §1.3 的本地聚合退回方案）：" +
                          $"{server.BaseUrl.TrimEnd('/')}/SentinelRESTServices/apidoc/en/index.html");

        var ok = true;
        var now = DateTimeOffset.UtcNow;

        await using (var client = new SentinelClient(server, settings))
        {
            ok &= await Step(1, "認證＋小範圍查詢（近 1 小時、3 筆、全欄位——用於核對欄位對應）", async () =>
            {
                var sw = Stopwatch.StartNew();
                var result = await client.SearchAsync(new SentinelSearchRequest(
                    MatchAllFilter, now.AddHours(-1), now, MaxResults: 3));
                sw.Stop();
                Console.WriteLine($"     耗時 {sw.ElapsedMilliseconds}ms，found={result.Found}，取回={result.Events.Count} 筆");

                var i = 0;
                foreach (var evt in result.Events)
                {
                    Console.WriteLine($"     事件 #{++i}：{string.Join("，", evt.Fields.Select(kv => $"{kv.Key}={Preview(kv.Value)}"))}");
                }
                if (result.Events.Count == 0)
                {
                    Console.WriteLine($"     ⚠ 近 1 小時查無事件——filter「{MatchAllFilter}」可能不合本環境語法（請看上面是否有例外訊息），" +
                                      "或這段時間真的沒有事件，可自行改用較長區間重跑本指令核對。");
                }
            });

            ok &= await Step(2, "dt 邊界（近 2 小時拆兩段，found 數請自行到 Sentinel Web UI 比對）", async () =>
            {
                var early = await client.SearchAsync(new SentinelSearchRequest(MatchAllFilter, now.AddHours(-2), now.AddHours(-1), MaxResults: 1));
                var late = await client.SearchAsync(new SentinelSearchRequest(MatchAllFilter, now.AddHours(-1), now, MaxResults: 1));
                Console.WriteLine($"     [now-2h, now-1h) found={early.Found}｜[now-1h, now) found={late.Found}");
                Console.WriteLine("     ⚠ 請自行到 Sentinel Web UI 搜尋同樣兩段區間，核對數字是否一致、" +
                                  "藉此確認 start 含／end 不含語意與時區基準符合預期。");
            });

            ok &= await Step(3, "分頁效能（pgsize 100／500／1000，近 24 小時）", async () =>
            {
                foreach (var pageSize in new[] { 100, 500, 1000 })
                {
                    var sw = Stopwatch.StartNew();
                    var result = await client.SearchAsync(new SentinelSearchRequest(
                        MatchAllFilter, now.AddHours(-24), now, PageSize: pageSize, MaxResults: pageSize));
                    sw.Stop();
                    Console.WriteLine($"     pgsize={pageSize}：耗時 {sw.ElapsedMilliseconds}ms，found={result.Found}，取回={result.Events.Count} 筆");
                }
            });

            ok &= await Step(4, "IP 篩選批次大小（10／50／100 個 IP 子句，語法是否被接受）", async () =>
            {
                foreach (var count in new[] { 10, 50, 100 })
                {
                    var filter = $"(sip:({string.Join(" OR ", Enumerable.Range(1, count).Select(i => $"10.0.0.{i}"))}))";
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        await client.SearchAsync(new SentinelSearchRequest(filter, now.AddMinutes(-5), now, MaxResults: 1));
                        sw.Stop();
                        Console.WriteLine($"     {count} 個 IP 子句：接受，耗時 {sw.ElapsedMilliseconds}ms");
                    }
                    catch (SentinelClientException ex)
                    {
                        Console.WriteLine($"     {count} 個 IP 子句：失敗（{ex.Message}）——" +
                                          "可能超出批次上限，也可能是欄位名稱「sip」不是本環境的正確欄位（待欄位對應定案後重試）");
                    }
                }
            });

            ok &= await Step(5, "失敗路徑：非法 filter 語法應被拒絕", async () =>
            {
                try
                {
                    await client.SearchAsync(new SentinelSearchRequest("((( 語法錯誤", now.AddMinutes(-5), now, MaxResults: 1));
                    Console.WriteLine("     ⚠ 非法 filter 卻沒有失敗——Sentinel 可能容忍此語法，或錯誤發生在非預期階段，請留意。");
                }
                catch (SentinelClientException ex)
                {
                    Console.WriteLine($"     ✓ 非法 filter 如預期被拒絕：{ex.Message}");
                }
            });
        }

        // 錯誤密碼獨立用一個 client 測試，且刻意放在最後——避免污染前面查詢步驟的 token 狀態
        ok &= await Step(6, "失敗路徑：錯誤密碼應回認證失敗（不影響上面已用正確密碼跑完的查詢）", async () =>
        {
            var badServer = new SentinelServer
            {
                Id = server.Id,
                Name = server.Name,
                BaseUrl = server.BaseUrl,
                Username = server.Username,
                Password = server.Password + "-wrong"
            };
            await using var badClient = new SentinelClient(badServer, settings);
            try
            {
                await badClient.SearchAsync(new SentinelSearchRequest(MatchAllFilter, now.AddMinutes(-5), now, MaxResults: 1));
                Console.WriteLine("     ⚠ 預期認證失敗，但查詢卻成功了——請確認 Sentinel 是否真的有驗證密碼。");
            }
            catch (SentinelClientException ex)
            {
                Console.WriteLine($"     ✓ 錯誤密碼如預期被拒絕：{ex.Message}");
            }
        });

        Console.WriteLine();
        return ok;
    }

    private static async Task<bool> Step(int index, string title, Func<Task> action)
    {
        Console.WriteLine($"   [{index}] {title}");
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"     ✗ 失敗：{ex.Message}");
            return false;
        }
    }

    private static string Preview(string value) => value.Length > 80 ? value[..80] + "…" : value;
}
