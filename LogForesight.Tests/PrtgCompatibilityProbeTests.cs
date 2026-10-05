using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PrtgCompatibilityProbe 的單元測試：
/// 驗證 3 種目標感測器最小採樣、別名隔離、去識別化、快照/頻道/歷史解析、逾時、錯誤容錯與 64KiB 邊界。
/// </summary>
public class PrtgCompatibilityProbeTests
{
    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    private static List<PrtgProbeRunner.SensorTypeSample> CreateSamples(params (long Objid, string Type, string Status)[] items)
    {
        return items.Select(x => new PrtgProbeRunner.SensorTypeSample(
            x.Type,
            null,
            100,
            x.Objid,
            x.Status
        )).ToList();
    }

    [Fact]
    public async Task ExecuteCoreAsync_三種目標均存在且正常_產出標準Target與別名()
    {
        var samples = CreateSamples(
            (1001, "SNMP CPU Load", "Up"),
            (1002, "SNMP Memory", "Up"),
            (1003, "SNMP Disk Free", "Up"));

        var console = new TestConsole();
        var context = new PrtgProbeEvidenceContext
        {
            BuildVersion = "2.0.0",
            SourceFingerprint = "fingerprint-123",
            StorageProvider = "sqlite"
        };

        var requestedUrls = new List<string>();

        Func<string, CancellationToken, Task<string>> getJson = (url, _) =>
        {
            requestedUrls.Add(url);
            if (url.Contains("content=sensors") && url.Contains("filter_objid="))
            {
                var id = url.Split("filter_objid=")[1];
                return Task.FromResult($@"{{
                    ""sensors"": [
                        {{""objid"": ""{id}"", ""type"": ""test"", ""status"": ""Up"", ""status_raw"": 3, ""lastvalue_raw"": 42.5, ""lastcheck"": ""2026-10-01 12:00:00"", ""interval"": ""60""}}
                    ]
                }}");
            }
            if (url.Contains("content=channels"))
            {
                return Task.FromResult(@"{
                    ""channels"": [
                        {""channel"": ""Total Memory"", ""lastvalue"": ""16 GB""},
                        {""channel"": ""Customer Secret Host"", ""lastvalue"": ""99%""}
                    ]
                }");
            }
            if (url.Contains("/api/historicdata.json"))
            {
                return Task.FromResult(@"{
                    ""histdata"": [
                        {""datetime"": ""2026-10-01 00:00:00"", ""value_raw"": 10.0},
                        {""datetime"": ""2026-10-01 01:00:00"", ""value_raw"": 12.0}
                    ]
                }");
            }
            return Task.FromResult("{}");
        };

        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, console, samples, context);

        Assert.NotNull(evidence);
        Assert.Equal("1.0.0", evidence.SchemaVersion);
        Assert.Equal("partial", evidence.Status); // 探測結果永遠不可宣稱完全就緒
        Assert.False(evidence.EvidenceReady);
        Assert.Equal(3, evidence.Targets.Count);

        // 別名必須為 s1, s2, s3 且不外洩原始 objid
        Assert.Equal("s1", evidence.Targets[0].Alias);
        Assert.Equal("cpu", evidence.Targets[0].Category);
        Assert.Equal("s2", evidence.Targets[1].Alias);
        Assert.Equal("memory", evidence.Targets[1].Category);
        Assert.Equal("s3", evidence.Targets[2].Alias);
        Assert.Equal("disk", evidence.Targets[2].Category);

        var json = PrtgCompatibilityProbe.SerializeEvidence(evidence);
        Assert.DoesNotContain("1001", json);
        Assert.DoesNotContain("1002", json);
        Assert.DoesNotContain("1003", json);
        Assert.DoesNotContain("Customer Secret Host", json);
        Assert.Contains("total memory", json);
    }

    [Fact]
    public async Task ExecuteCoreAsync_缺少部分目標_標記為missing且不猜測()
    {
        // 只有 CPU，缺少 Memory 和 Disk
        var samples = CreateSamples((1001, "SNMP CPU Load", "Up"));

        Func<string, CancellationToken, Task<string>> getJson = (url, _) =>
        {
            if (url.Contains("content=sensors"))
                return Task.FromResult(@"{""sensors"": [{""objid"": ""1001"", ""status"": ""Up"", ""type"": ""SNMP CPU Load""}]}");
            return Task.FromResult(@"{""channels"": [], ""histdata"": []}");
        };

        var console = new TestConsole();
        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, console, samples, null);

        Assert.Equal("partial", evidence.Status);
        Assert.Equal(3, evidence.Targets.Count);

        var cpu = evidence.Targets.First(t => t.Category == "cpu");
        Assert.NotEqual("missing", cpu.Status);

        var memory = evidence.Targets.First(t => t.Category == "memory");
        Assert.Equal("missing", memory.Status);
        Assert.Null(memory.Snapshot);
        Assert.Null(memory.Channels);
        Assert.Null(memory.History);

        var disk = evidence.Targets.First(t => t.Category == "disk");
        Assert.Equal("missing", disk.Status);
    }

    [Fact]
    public async Task ExecuteCoreAsync_目標處於Paused狀態_標記為paused且非ready()
    {
        var samples = CreateSamples((1001, "SNMP CPU Load", "Paused"));

        var console = new TestConsole();
        Func<string, CancellationToken, Task<string>> getJson = (_, _) => Task.FromResult(@"{
            ""sensors"": [{""objid"": ""1001"", ""status"": ""Paused""}],
            ""channels"": [],
            ""histdata"": []
        }");

        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, console, samples, null);

        var cpu = evidence.Targets.First(t => t.Category == "cpu");
        Assert.Equal("paused", cpu.Status);
        Assert.False(evidence.EvidenceReady);
    }

    [Fact]
    public async Task ExecuteCoreAsync_快照未濾除物件_設定has_unfiltered_objects且不保留其他感測器欄位()
    {
        var samples = CreateSamples((1001, "SNMP CPU Load", "Up"));

        var console = new TestConsole();
        // 模擬 PRTG 忽略 filter_objid 參數，回傳了多個物件
        Func<string, CancellationToken, Task<string>> getJson = (url, _) =>
        {
            if (url.Contains("content=sensors"))
            {
                return Task.FromResult(@"{
                    ""sensors"": [
                        {""objid"": ""999"", ""type"": ""other"", ""status"": ""Up""},
                        {""objid"": ""1001"", ""type"": ""SNMP CPU Load"", ""status"": ""Up"", ""lastvalue_raw"": ""50""},
                        {""objid"": ""1002"", ""type"": ""other2"", ""status"": ""Up""}
                    ]
                }");
            }
            return Task.FromResult(@"{""channels"": [], ""histdata"": []}");
        };

        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, console, samples, null);

        var cpu = evidence.Targets.First(t => t.Category == "cpu");
        Assert.NotNull(cpu.Snapshot);
        Assert.True(cpu.Snapshot.HasUnfilteredObjects);
        // 不應洩漏 objid 999 或 1002 的資料
        var json = PrtgCompatibilityProbe.SerializeEvidence(evidence);
        Assert.DoesNotContain("999", json);
        Assert.DoesNotContain("1002", json);
    }

    [Fact]
    public async Task ExecuteCoreAsync_快照與頻道缺少部分欄位_returned_fields與missing_fields分開記錄()
    {
        var samples = CreateSamples((1001, "SNMP CPU Load", "Up"));

        var console = new TestConsole();
        // 只回傳 objid 與 status，缺少 type, lastvalue_raw, lastcheck, interval
        Func<string, CancellationToken, Task<string>> getJson = (url, _) =>
        {
            if (url.Contains("content=sensors"))
            {
                return Task.FromResult(@"{
                    ""sensors"": [
                        {""objid"": ""1001"", ""status"": ""Up""}
                    ]
                }");
            }
            return Task.FromResult(@"{""channels"": [], ""histdata"": []}");
        };

        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, console, samples, null);

        var cpu = evidence.Targets.First(t => t.Category == "cpu");
        Assert.NotNull(cpu.Snapshot);
        Assert.Contains("objid", cpu.Snapshot.ReturnedFields);
        Assert.Contains("status", cpu.Snapshot.ReturnedFields);
        Assert.Contains("type", cpu.Snapshot.MissingFields);
        Assert.Contains("lastvalue_raw", cpu.Snapshot.MissingFields);
        Assert.Contains("interval", cpu.Snapshot.MissingFields);
    }

    [Fact]
    public async Task ExecuteCoreAsync_回傳HTML或無效JSON_記錄malformed_json且不崩潰()
    {
        var samples = CreateSamples((1001, "SNMP CPU Load", "Up"));

        var console = new TestConsole();
        Func<string, CancellationToken, Task<string>> getJson = (_, _) =>
            Task.FromResult(@"<html><body>502 Bad Gateway</body></html>");

        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, console, samples, null);

        Assert.NotNull(evidence);
        var cpu = evidence.Targets.First(t => t.Category == "cpu");
        Assert.NotNull(cpu.Snapshot);
        Assert.Equal("malformed_json", cpu.Snapshot.Status);
    }

    [Fact]
    public async Task ExecuteCoreAsync_單次請求例外_記錄錯誤狀態並繼續後續步驟()
    {
        var samples = CreateSamples((1001, "SNMP CPU Load", "Up"));

        var console = new TestConsole();
        Func<string, CancellationToken, Task<string>> getJson = (url, _) =>
        {
            if (url.Contains("/api/historicdata.json"))
            {
                throw new HttpRequestException("SENTINEL_EXCEPTION_BODY", null, HttpStatusCode.InternalServerError);
            }
            return Task.FromResult(@"{""sensors"": [{""objid"": ""1001"", ""status"": ""Up""}], ""channels"": []}");
        };

        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, console, samples, null);

        var cpu = evidence.Targets.First(t => t.Category == "cpu");
        Assert.NotNull(cpu.History);
        Assert.Equal("error", cpu.History.Status);
        Assert.Contains("500", cpu.History.Error);
        Assert.DoesNotContain("SENTINEL_EXCEPTION_BODY", cpu.History.Error);
    }

    [Fact]
    public async Task ExecuteCoreAsync_歷史數值重複欄位與順序保全()
    {
        var samples = CreateSamples((1001, "SNMP CPU Load", "Up"));

        var console = new TestConsole();
        // PRTG historicdata 常常一列有多個 value_raw (多 channel)
        var rawJson = @"{
            ""histdata"": [
                {""datetime"": ""2026-10-01 00:00:00"", ""value_raw"": 10.5, ""value_raw"": 20.5, ""coverage"": ""100%""},
                {""datetime"": ""2026-10-01 01:00:00"", ""value_raw"": 30.5}
            ]
        }";

        Func<string, CancellationToken, Task<string>> getJson = (url, _) =>
        {
            if (url.Contains("/api/historicdata.json"))
                return Task.FromResult(rawJson);
            return Task.FromResult(@"{""sensors"": [{""objid"": ""1001"", ""status"": ""Up""}], ""channels"": []}");
        };

        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, console, samples, null);

        var cpu = evidence.Targets.First(t => t.Category == "cpu");
        Assert.NotNull(cpu.History);
        Assert.Equal(2, cpu.History.TotalRows);
        var firstRow = cpu.History.Rows[0];
        Assert.Contains(firstRow.Entries, e => e.Name == "datetime" && (string?)e.Value == "2026-10-01T00:00:00.0000000");
        Assert.NotEmpty(firstRow.Entries);
    }

    [Fact]
    public async Task ExecuteCoreAsync_頻道數量超過上限16_截斷並標記truncated()
    {
        var samples = CreateSamples((1001, "SNMP CPU Load", "Up"));

        var console = new TestConsole();
        // 產生 25 個 channels
        var channelRows = Enumerable.Range(1, 25).Select(i => $"{{\"channel\": \"Total Disk {i}\", \"lastvalue\": \"{i} GB\"}}");
        var channelsJson = "{\"channels\": [" + string.Join(",", channelRows) + "]}";

        Func<string, CancellationToken, Task<string>> getJson = (url, _) =>
        {
            if (url.Contains("content=channels"))
                return Task.FromResult(channelsJson);
            return Task.FromResult(@"{""sensors"": [{""objid"": ""1001"", ""status"": ""Up""}], ""histdata"": []}");
        };

        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, console, samples, null);

        var cpu = evidence.Targets.First(t => t.Category == "cpu");
        Assert.NotNull(cpu.Channels);
        Assert.Equal(16, cpu.Channels.Rows.Count);
        Assert.True(cpu.Channels.Truncated);
        Assert.Equal(25, cpu.Channels.TotalRows);
    }

    [Fact]
    public async Task ExecuteCoreAsync_歷史列數超過上限3_截斷並標記truncated()
    {
        var samples = CreateSamples((1001, "SNMP CPU Load", "Up"));

        var console = new TestConsole();
        var histRows = Enumerable.Range(1, 10).Select(i => $"{{\"datetime\": \"2026-10-01 {i:D2}:00:00\", \"value_raw\": {i}}}");
        var histJson = "{\"histdata\": [" + string.Join(",", histRows) + "]}";

        Func<string, CancellationToken, Task<string>> getJson = (url, _) =>
        {
            if (url.Contains("/api/historicdata.json"))
                return Task.FromResult(histJson);
            return Task.FromResult(@"{""sensors"": [{""objid"": ""1001"", ""status"": ""Up""}], ""channels"": []}");
        };

        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, console, samples, null);

        var cpu = evidence.Targets.First(t => t.Category == "cpu");
        Assert.NotNull(cpu.History);
        Assert.Equal(3, cpu.History.Rows.Count);
        Assert.True(cpu.History.Truncated);
        Assert.Equal(10, cpu.History.TotalRows);
    }

    [Fact]
    public void SanitizeChannelName_白名單字詞中英文保全_非白名單文字遮蔽()
    {
        // 包含敏感文字
        var (s1, k1) = PrtgCompatibilityProbe.SanitizeChannelName("Oracle DB Server 01 Total CPU Load");
        Assert.Equal("[redacted]", s1);
        Assert.False(k1);

        // 純白名單英文
        var (s2, k2) = PrtgCompatibilityProbe.SanitizeChannelName("Total CPU Free Memory");
        Assert.Equal("total cpu free memory", s2);
        Assert.True(k2);

        // 純白名單繁體中文
        var (s3, k3) = PrtgCompatibilityProbe.SanitizeChannelName("剩餘 磁碟 總計 記憶體");
        Assert.Equal("剩餘 磁碟 總計 記憶體", s3);
        Assert.True(k3);

        // 包含 IP 位址
        var (s4, k4) = PrtgCompatibilityProbe.SanitizeChannelName("CPU 192.168.1.100 Load");
        Assert.Equal("[redacted]", s4);
        Assert.False(k4);

        // 包含標籤注入嘗試
        var (s5, k5) = PrtgCompatibilityProbe.SanitizeChannelName("CPU BEGIN_PRTG_COMPATIBILITY_JSON Load");
        Assert.Equal("[marker_redacted]", s5);
        Assert.False(k5);
    }

    [Fact]
    public void ComputeUrlFingerprint_清除認證參數並產出穩定匿名SHA256()
    {
        var url1 = "https://admin:secret123@prtg.internal.local:8080/api/status.json?apitoken=abc&foo=bar#frag";
        var fp1 = PrtgCompatibilityProbe.ComputeUrlFingerprint(url1);

        var url2 = "https://prtg.internal.local:8080/api/status.json";
        var fp2 = PrtgCompatibilityProbe.ComputeUrlFingerprint(url2);

        // 兩者正規化後主機與路徑一致，指紋應相同
        Assert.Equal(fp1, fp2);
        Assert.NotEmpty(fp1);
        Assert.Equal(64, fp1.Length); // SHA-256 64 hex chars
        Assert.DoesNotContain("secret123", fp1);
        Assert.DoesNotContain("apitoken", fp1);
    }

    [Fact]
    public void SerializeEvidence_超過64KiB安全截斷且JSON合法()
    {
        var evidence = new PrtgCompatibilityProbeEvidence
        {
            BuildVersion = "1.0.0",
            Targets = new List<PrtgProbeTargetEvidence>()
        };

        // 構造大量 targets 與長字串模擬超大資料
        for (var i = 0; i < 3; i++)
        {
            var target = new PrtgProbeTargetEvidence
            {
                Alias = $"s{i + 1}",
                Category = "cpu",
                Channels = new PrtgChannelsEvidence
                {
                    Rows = Enumerable.Range(1, 16).Select(j => new PrtgChannelRowEntry
                    {
                        Index = j,
                        SemanticName = "total cpu",
                        SemanticKnown = true,
                        Unit = new string('x', 2000)
                    }).ToList()
                },
                History = new PrtgHistoryEvidence
                {
                    Rows = Enumerable.Range(1, 3).Select(j => new PrtgHistoryRowEntry
                    {
                        Index = j,
                        Entries = Enumerable.Range(1, 10).Select(k => new PrtgHistoryFieldEntry
                        {
                            Name = $"field_{k}",
                            Type = "string",
                            Value = new string('y', 2000)
                        }).ToList()
                    }).ToList()
                }
            };
            evidence.Targets.Add(target);
        }

        var json = PrtgCompatibilityProbe.SerializeEvidence(evidence);
        var bytes = Encoding.UTF8.GetByteCount(json);

        Assert.True(bytes <= PrtgCompatibilityProbe.MaxJsonSizeBytes);

        // 必須可正確 parse
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task ExecuteCoreAsync_外層CancellationToken取消_拋出OperationCanceledException()
    {
        var samples = CreateSamples((1001, "SNMP CPU Load", "Up"));
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 立即取消

        var console = new TestConsole();
        Func<string, CancellationToken, Task<string>> getJson = (_, _) => Task.FromResult("{}");

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, console, samples, null, cts.Token);
        });
    }

    [Fact]
    public async Task ExecuteCoreAsync_未知欄與metadata密碼主機及巢狀秘密一律不進EvidenceJson()
    {
        var samples = CreateSamples((1001, "SNMP CPU Load", "Up"));
        var requests = new List<string>();
        var context = new PrtgProbeEvidenceContext
        {
            BuildVersion = "customer-prod-17",
            SourcePrtgVersion = "customer-prod-17",
            SettingsRevision = "SENTINEL_SECRET_9",
            HostUtcOffset = "SENTINEL_OFFSET",
            SourceFingerprint = "customer-prod-17",
            StorageProvider = "SENTINEL_PROVIDER",
            EfCoreProvider = "SENTINEL_PROVIDER",
            ScopeSummary = "SENTINEL_SCOPE customer-prod-17",
            ReadinessSummary = "SENTINEL_READINESS"
        };

        Func<string, CancellationToken, Task<string>> getJson = (url, _) =>
        {
            requests.Add(url);
            if (url.Contains("content=sensors"))
                return Task.FromResult("""
                    {"sensors":[{"objid":1001,"type":"SNMP CPU Load","status":"Up","status_raw":3,"lastvalue_raw":42.5,"lastcheck":"arbitraryHost","lastcheck_raw":10,"interval":60,"SENTINEL_FIELD_NAME_7":"SENTINEL_SECRET_9","password":"SENTINEL_SECRET_9","host":"customer-prod-17"}]}
                    """);
            if (url.Contains("content=channels"))
                return Task.FromResult("""
                    {"channels":[{"objid":0,"channel":"Total CPU","unit":"SENTINEL_UNIT","scaling":1,"primary":true,"password":"SENTINEL_SECRET_9"}]}
                    """);
            return Task.FromResult("""
                {"histdata":[{"datetime":"2026-10-01 00:00:00","value_raw":["SENTINEL_HISTORY_ARRAY"],"password":"SENTINEL_SECRET_9"}]}
                """);
        };

        string? independentEvidence = null;
        var result = await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, new TestConsole(), samples, context,
            onEvidenceJsonProduced: value => independentEvidence = value);
        var json = Assert.IsType<string>(independentEvidence);

        Assert.Equal(3, requests.Count);
        Assert.Equal("partial", result.Status);
        Assert.False(result.EvidenceReady);
        Assert.DoesNotContain("SENTINEL_SECRET_9", json);
        Assert.DoesNotContain("SENTINEL_FIELD_NAME_7", json);
        Assert.DoesNotContain("SENTINEL_UNIT", json);
        Assert.DoesNotContain("SENTINEL_HISTORY_ARRAY", json);
        Assert.DoesNotContain("customer-prod-17", json);
        Assert.DoesNotContain("arbitraryHost", json);
        Assert.Contains("parent sensor id is unavailable", json);
        Assert.Contains("\"unit\":\"[redacted]\"", json);
        Assert.Equal("unknown (filtered request; channel objid identifies a channel, response parent sensor id is unavailable)", result.Targets[0].Channels!.ParentIdentity);
        Assert.Equal("number", result.Targets[0].Channels!.Rows[0].ChannelIdType);
        Assert.Contains("\"unrecognized_fields_count\":3", json);
        Assert.Contains("\"api_operation_attempts\":3", json);
        Assert.Contains("\"api_operation_responses\":3", json);
        Assert.Contains("\"requested_at_utc\"", json);
        Assert.Contains("\"received_at_utc\"", json);
        using var parsed = JsonDocument.Parse(json);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= PrtgCompatibilityProbe.MaxJsonSizeBytes);
    }

    [Theory]
    [InlineData("23.4-customerprod17", "unknown")]
    [InlineData("23.4.92.1234", "23.4.92.1234")]
    [InlineData("23.4", "23.4")]
    public async Task ExecuteCoreAsync_SourcePrtgVersion只接受numeric廠商版本(string sourceVersion, string expected)
    {
        var context = new PrtgProbeEvidenceContext { SourcePrtgVersion = sourceVersion };
        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(
            (_, _) => throw new InvalidOperationException("No request expected for empty samples"),
            new TestConsole(), Array.Empty<PrtgProbeRunner.SensorTypeSample>(), context);

        var json = PrtgCompatibilityProbe.SerializeEvidence(evidence);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, doc.RootElement.GetProperty("source_prtg_version").GetString());
        Assert.DoesNotContain("customerprod17", json);
    }
    [Theory]
    [InlineData("60 s", true)]
    [InlineData("1 min", true)]
    [InlineData("0.5 HOURS", true)]
    [InlineData("60 s customer-secret", false)]
    [InlineData("<script>customer-secret</script>", false)]
    public async Task SnapshotDurationFieldPreservesOnlyBoundedDurationSyntax(string interval, bool allowed)
    {
        var json = JsonSerializer.Serialize(new { sensors = new[] { new {
            objid = 1001, status_raw = 3, lastvalue_raw = 42, lastcheck_raw = 46000.5,
            interval_raw = 60, interval
        } } });
        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(
            (url, _) => Task.FromResult(url.Contains("content=sensors") ? json : "{}"),
            new TestConsole(), CreateSamples((1001, "SNMP CPU Load", "Up")), context: null);
        var field = evidence.Targets.Single(x => x.Category == "cpu").Snapshot.Fields.Single(x => x.Field == "interval");
        Assert.Equal(allowed ? "duration_string" : "invalid_number", field.ValueType);
        if (allowed) Assert.Equal(interval, field.Value);
        else Assert.Null(field.Value);
        Assert.DoesNotContain("customer-secret", PrtgCompatibilityProbe.SerializeEvidence(evidence));
        Assert.False(evidence.EvidenceReady);
    }

}
