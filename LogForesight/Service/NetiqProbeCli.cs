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
/// 全程透過 <see cref="SentinelClient"/> 既有的單一佇列＋節流執行，對 server 負擔可忽略
/// （docs/NETIQ-API-PLAN.md §5）。
///
/// **第二輪（2026-07-29）**：第一輪真實輸出（Sentinel「162」）量級遠超原估計
/// （近 24h found≈2470 萬筆），推翻了「探索走近 24h 全事件投影＋本地 distinct」的原設計，
/// 也把「主機歸屬鍵是哪個欄位」升格為最關鍵未決項。**步驟 6～12** 就是為了收斂這些新問題
/// （步驟 13 是原本就有的錯誤密碼失敗路徑，只是順序後移）。其中步驟 8／9／11 需要
/// <c>--sample-linux-ip</c>／<c>--sample-ip</c> 指定樣本主機才能執行，未提供時明確標示略過，
/// 不是靜默跳過；其餘步驟不需要任何參數。
/// </summary>
public static class NetiqProbeCli
{
    /// <summary>「查全部事件」的 filter。用嚴重度全範圍而不是 Lucene 的 <c>*:*</c>——
    /// <c>sev:[0 TO 5]</c> 是原廠文件實例（Aegis 整合範例的實際 payload）用過的語法，
    /// probe 的第一步就該用有文件背書的寫法，語法本身被拒的機率最低。</summary>
    private const string MatchAllFilter = "sev:[0 TO 5]";

    /// <param name="sampleIp">一台已知的 Windows 主機 IP（非網域控制站，用於核對跨主機事件的
    /// 主機歸屬鍵、頻道覆蓋、dt 邊界）。省略時對應步驟標示略過。</param>
    /// <param name="sampleLinuxIp">一台已知的 Linux 主機 IP，用於核對 Linux 事件的欄位形狀
    /// （program／sev↔syslog priority／OS 判別候選值）。省略時對應步驟標示略過。</param>
    public static async Task<int> RunAsync(
        ISentinelStore sentinelStore, NetiqOptions settings, string? sampleIp = null, string? sampleLinuxIp = null)
    {
        var sentinels = sentinelStore.GetAll().Where(s => s.Active).ToList();
        if (sentinels.Count == 0)
        {
            Console.WriteLine("目前沒有已設定且啟用的 Sentinel（請先在 Web「資料匯入」頁新增），無法執行 probe。");
            return 1;
        }

        Console.WriteLine("══════════ NetIQ Sentinel API Probe ══════════");
        Console.WriteLine("以下輸出可直接複製貼回對話，用於定案欄位對應／批次大小／時區（docs/NETIQ-API-PLAN.md §9）。");
        if (string.IsNullOrWhiteSpace(sampleIp))
        {
            Console.WriteLine("（未提供 --sample-ip：加這個參數可多跑第二輪的主機歸屬鍵／頻道覆蓋／dt 邊界核對，" +
                              "例：--netiq-probe --sample-ip 10.1.2.34 --sample-linux-ip 10.1.2.56）");
        }
        Console.WriteLine();

        var allOk = true;
        foreach (var sentinel in sentinels)
        {
            if (string.IsNullOrWhiteSpace(sentinel.Username) || string.IsNullOrWhiteSpace(sentinel.PasswordEnc))
            {
                Console.WriteLine($"── {sentinel.Name}：帳密未設定（CanDiscover=false），略過 ──\n");
                continue;
            }

            var server = SentinelConnectionFactory.ToConnectable(sentinel);
            try
            {
                allOk &= await ProbeOneAsync(server, settings, sampleIp, sampleLinuxIp);
            }
            catch (Exception ex)
            {
                // 失敗隔離：一台的設定有問題（例如連線位址格式不正確，在建立 client 時就擲出）
                // 不該讓其餘 Sentinel 完全沒被 probe 到——那正是這支工具要收集的資訊
                Console.WriteLine($"   ✗ 這台 Sentinel 無法 probe：{ex.Message}\n");
                allOk = false;
            }
        }

        Console.WriteLine("══════════ Probe 結束 ══════════");
        return allOk ? 0 : 1;
    }

    private static async Task<bool> ProbeOneAsync(
        SentinelServer server, NetiqOptions settings, string? sampleIp, string? sampleLinuxIp)
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
                Console.WriteLine($"     前半段 {Window(now.AddHours(-2), now.AddHours(-1))} found={early.Found}");
                Console.WriteLine($"     後半段 {Window(now.AddHours(-1), now)} found={late.Found}");
                Console.WriteLine("     ⚠ 請自行到 Sentinel Web UI 搜尋上列兩段區間，核對數字是否一致、" +
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

            ok &= await Step(4, "IP 篩選批次大小（10／50／100 個 IP 子句，用 repip——第一輪已實證的回報者 IP 欄位）", async () =>
            {
                foreach (var count in new[] { 10, 50, 100 })
                {
                    var filter = $"(repip:({string.Join(" OR ", Enumerable.Range(1, count).Select(i => $"10.0.0.{i}"))}))";
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        await client.SearchAsync(new SentinelSearchRequest(filter, now.AddMinutes(-5), now, MaxResults: 1));
                        sw.Stop();
                        Console.WriteLine($"     {count} 個 IP 子句：接受，耗時 {sw.ElapsedMilliseconds}ms");
                    }
                    catch (SentinelClientException ex)
                    {
                        Console.WriteLine($"     {count} 個 IP 子句：失敗（{ex.Message}）——可能超出批次上限");
                    }
                }
            });

            // filter 內容刻意全用 ASCII：2026-07-29 第二輪實測發現 Sentinel 的 JSON 解析器
            // 不吃 \uXXXX 轉義（已於 SentinelClient.JobBodyJsonOptions 修正），原本用中文的
            // 「((( 語法錯誤」是在 **JSON 解析階段**就被拒，根本沒走到 Lucene 語法檢查——
            // 這個步驟等於一直為了錯的理由通過。改成純 ASCII 的未閉合括號才真的測到 Lucene。
            ok &= await Step(5, "失敗路徑：非法 Lucene 語法應被拒絕（錯誤訊息應指向查詢語法，不是 JSON 格式）", async () =>
            {
                try
                {
                    await client.SearchAsync(new SentinelSearchRequest("((( unclosed", now.AddMinutes(-5), now, MaxResults: 1));
                    Console.WriteLine("     ⚠ 非法 filter 卻沒有失敗——Sentinel 可能容忍此語法，或錯誤發生在非預期階段，請留意。");
                }
                catch (SentinelClientException ex)
                {
                    Console.WriteLine($"     ✓ 非法 filter 如預期被拒絕：{ex.Message}");
                    if (ex.Message.Contains("invalid JSON value", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("     ⚠ 但錯誤是「invalid JSON value」＝卡在 JSON 解析、沒測到 Lucene 語法檢查，" +
                                          "請回報這行（代表請求本文的轉義仍有問題）。");
                    }
                }
            });

            // ── 第二輪（2026-07-29）：第一輪真實輸出把「探索走近 24h 全事件 distinct」的原設計
            // 推翻（此環境單一 Sentinel 近 24h found=2470 萬筆），且「主機歸屬鍵是哪個欄位」
            // 變成最關鍵未決項。步驟 6～12 收斂這些新問題，全部沿用同一 client（單一佇列＋節流）。

            ok &= await Step(6, "ESM eventsource 清單（能否取代「投影事件 distinct」當主機目錄／OS 判別來源）", async () =>
            {
                try
                {
                    var body = await client.RawGetAsync("/SentinelRESTServices/objects/eventsource");
                    Console.WriteLine($"     原始回應（前 2000 字元）：{Preview(body, 2000)}");
                }
                catch (SentinelClientException ex)
                {
                    Console.WriteLine($"     ⚠ eventsource 端點無法存取：{ex.Message}" +
                                      "（若持續失敗，探索退回窄時間窗事件投影 distinct 的備案，並誠實申報涵蓋範圍有限）");
                }
            });

            ok &= await Step(7, "登入事件取樣（rv40:4624/4625，近 24h、3 筆、全欄位）——核對跨主機時 dhn/sn/repip 分工＋sun 帳號欄位語意", async () =>
            {
                var result = await client.SearchAsync(new SentinelSearchRequest(
                    "rv40:(4624 OR 4625)", now.AddHours(-24), now, MaxResults: 3));
                Console.WriteLine($"     found={result.Found}，取回={result.Events.Count} 筆");

                var i = 0;
                foreach (var evt in result.Events)
                {
                    Console.WriteLine($"     事件 #{++i}：{string.Join("，", evt.Fields.Select(kv => $"{kv.Key}={Preview(kv.Value)}"))}");
                }
                if (result.Events.Count == 0)
                {
                    Console.WriteLine("     ⚠ 近 24h 查無登入事件，可自行放大時間範圍重跑本指令核對。");
                }
            });

            if (string.IsNullOrWhiteSpace(sampleLinuxIp))
            {
                Console.WriteLine("   [8] Linux 主機樣本：略過（未提供 --sample-linux-ip）");
            }
            else
            {
                ok &= await Step(8, $"Linux 主機樣本（repip:{sampleLinuxIp}，近 24h、3 筆、全欄位）——program 欄位／sev↔syslog priority／OS 判別候選值", async () =>
                {
                    var result = await client.SearchAsync(new SentinelSearchRequest(
                        $"repip:{sampleLinuxIp}", now.AddHours(-24), now, MaxResults: 3));
                    Console.WriteLine($"     found={result.Found}，取回={result.Events.Count} 筆");

                    var i = 0;
                    foreach (var evt in result.Events)
                    {
                        Console.WriteLine($"     事件 #{++i}：{string.Join("，", evt.Fields.Select(kv => $"{kv.Key}={Preview(kv.Value)}"))}");
                    }
                    if (result.Events.Count == 0)
                    {
                        Console.WriteLine($"     ⚠ 近 24h 查無事件——請確認「{sampleLinuxIp}」的 repip 值是否正確、" +
                                          "或這台主機的 log 是否確實有轉送到本 Sentinel。");
                    }
                });
            }

            if (string.IsNullOrWhiteSpace(sampleIp))
            {
                Console.WriteLine("   [9] 頻道覆蓋（System/Application）：略過（未提供 --sample-ip）");
            }
            else
            {
                // 傾印實際事件而不只印 found 數：磁碟／服務／硬體類規則比對的就是 System 頻道，
                // 但到目前為止一筆 System/Application 樣本都沒有（前兩輪抓到的全是 Security），
                // SentinelFieldMap 對這兩個頻道等於零依據。順帶也核實 dhn/sn 是否確實等於
                // 這台 sampleIp 的主機名（確認 repip 是主機自身而不是代收多台的 collector）。
                ok &= await Step(9, $"頻道覆蓋＋樣本（repip:{sampleIp} 的 System/Application 近 24h）", async () =>
                {
                    foreach (var channel in new[] { "System", "Application" })
                    {
                        var result = await client.SearchAsync(new SentinelSearchRequest(
                            $"(repip:{sampleIp}) AND (rv150:{channel})", now.AddHours(-24), now, MaxResults: 2));
                        Console.WriteLine($"     {channel} found={result.Found}，取樣={result.Events.Count} 筆");

                        foreach (var evt in result.Events)
                        {
                            Console.WriteLine($"       · {string.Join("，", evt.Fields.Select(kv => $"{kv.Key}={Preview(kv.Value)}"))}");
                        }
                        if (result.Found == 0)
                        {
                            Console.WriteLine($"     ⚠ {channel} 頻道近 24h 無事件——若這台主機平常確實有 {channel} log，" +
                                              "代表該頻道未轉送到 Sentinel，Windows 面此規則類別將全數不適用（需誠實申報，不是「沒告警」）。");
                        }
                    }
                });
            }

            ok &= await Step(10, "generic 錯誤等級量級（決定 sev>=err 收集是否可行的量級依據）", async () =>
            {
                var errAll = await client.SearchAsync(new SentinelSearchRequest(
                    "sev:[3 TO 5]", now.AddHours(-24), now, MaxResults: 1));
                Console.WriteLine($"     全站 sev:[3 TO 5] 近 24h found={errAll.Found}");

                var sampleIps = new[] { sampleIp, sampleLinuxIp }.Where(ip => !string.IsNullOrWhiteSpace(ip)).ToList();
                if (sampleIps.Count > 0)
                {
                    var ipFilter = $"({string.Join(" OR ", sampleIps.Select(ip => $"repip:{ip}"))})";
                    var sw = Stopwatch.StartNew();
                    var scoped = await client.SearchAsync(new SentinelSearchRequest(
                        $"({ipFilter}) AND (sev:[3 TO 5])", now.AddHours(-24), now, MaxResults: 1));
                    sw.Stop();
                    Console.WriteLine($"     {sampleIps.Count} 台樣本 IP＋sev:[3 TO 5]：耗時 {sw.ElapsedMilliseconds}ms，found={scoped.Found}" +
                                      "（只是示範用小批次，真實 50 台批次的耗時需等主機清單就緒後另測）");
                }
            });

            if (string.IsNullOrWhiteSpace(sampleIp))
            {
                Console.WriteLine("   [11] dt 邊界精確核對：略過（未提供 --sample-ip）");
            }
            else
            {
                // 窗口取單台主機的 1 小時拆兩個 30 分鐘：全站查詢的 found 是百萬級（第一輪實測）
                // 無法人工核對，鎖單台主機才壓得到可數的量級；30 分鐘則是為了不要小到經常查無事件
                // ——兩段都 0 的話這個步驟等於沒做。
                ok &= await Step(11, $"dt 邊界精確核對（repip:{sampleIp}，近 1 小時拆兩個 30 分鐘，found 數應可人工比對）", async () =>
                {
                    var early = await client.SearchAsync(new SentinelSearchRequest(
                        $"repip:{sampleIp}", now.AddMinutes(-60), now.AddMinutes(-30), MaxResults: 1));
                    var late = await client.SearchAsync(new SentinelSearchRequest(
                        $"repip:{sampleIp}", now.AddMinutes(-30), now, MaxResults: 1));
                    Console.WriteLine($"     前半段 {Window(now.AddMinutes(-60), now.AddMinutes(-30))} found={early.Found}");
                    Console.WriteLine($"     後半段 {Window(now.AddMinutes(-30), now)} found={late.Found}");
                    Console.WriteLine("     ⚠ 請自行到 Sentinel Web UI 用同一台主機與上列兩段絕對時間核對這兩個數字，確認含/不含語意與時區基準。");
                    if (early.Found == 0 && late.Found == 0)
                    {
                        Console.WriteLine($"     ⚠ 兩段皆無事件，無法據此核對邊界——請確認「{sampleIp}」的 repip 值正確，" +
                                          "或改挑一台事件較頻繁的主機重跑。");
                    }
                });
            }

            ok &= await Step(12, "obssvcname 欄位查詢行為（完整片語 vs 部分詞，決定規則來源能否下推 Lucene）", async () =>
            {
                var exact = await client.SearchAsync(new SentinelSearchRequest(
                    "obssvcname:\"Microsoft-Windows-Security-Auditing\"", now.AddMinutes(-5), now, MaxResults: 1));
                var partial = await client.SearchAsync(new SentinelSearchRequest(
                    "obssvcname:Security-Auditing", now.AddMinutes(-5), now, MaxResults: 1));
                Console.WriteLine($"     完整片語比對 found={exact.Found}｜部分詞 found={partial.Found}" +
                                  "（兩者相同＝analyzer 對此欄位做全文斷詞，可用子字串比對；不同則須用完整片語）");
            });
        }

        // 錯誤密碼獨立用一個 client 測試，且刻意放在最後——避免污染前面查詢步驟的 token 狀態
        ok &= await Step(13, "失敗路徑：錯誤密碼應回認證失敗（不影響上面已用正確密碼跑完的查詢）", async () =>
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

    private static string Preview(string value, int maxLen = 80) => value.Length > maxLen ? value[..maxLen] + "…" : value;

    /// <summary>
    /// 把查詢區間印成人工可在 Sentinel Web UI 重現的**絕對**時間。
    /// 「請自行核對同樣區間」的步驟少了這個就無法執行——只說「近 10 分鐘」，
    /// 操作者不知道那是哪 10 分鐘（而且 probe 跑完時 now 早就過去了）。
    /// UTC 與本機時間並列：查詢送出的是 UTC，Web UI 通常顯示本機時區，核對時兩邊都要看得到。
    /// </summary>
    private static string Window(DateTimeOffset start, DateTimeOffset end) =>
        $"{start.UtcDateTime:yyyy-MM-dd HH:mm:ss}~{end.UtcDateTime:HH:mm:ss} UTC" +
        $"（本機 {start.LocalDateTime:yyyy-MM-dd HH:mm:ss}~{end.LocalDateTime:HH:mm:ss}）";
}
