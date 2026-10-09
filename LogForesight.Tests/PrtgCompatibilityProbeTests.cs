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
    [Fact]
    public async Task IdentityPreservingXmlReachesSafeProbeArtifactWithoutAuthorizingUnknownClockOrPrimary()
    {
        var samples = CreateSamples((1003, "SNMP Disk Free", "Up"));
        var xmlCalls = 0;
        var result = await PrtgCompatibilityProbe.ExecuteCoreAsync((url, _) => Task.FromResult(
            url.Contains("content=sensors") ? "{\"sensors\":[{\"objid\":1003,\"lastvalue_raw\":24}]}" :
            url.Contains("content=channels") ? "{\"channels\":[{\"objid\":3,\"name\":\"Free Space\"}]}" :
            "{\"histdata\":[{\"datetime_raw\":46288.5,\"value_raw\":24}]}"),
            new TestConsole(), samples, null, getHistoricXml: (url, _) =>
            {
                xmlCalls++;
                Assert.Contains("avg=0", url);
                Assert.Contains("historicdata.xml", url);
                const string xml = "<histdata totalcount=\"1\"><prtg-version>24.1.92.1554+</prtg-version><item><datetime_raw>46288.5</datetime_raw><value channel=\"Free Space\" channelid=\"3\">24 %</value><value_raw channel=\"Free Space\" channelid=\"3\">24</value_raw><value_raw channel=\"Private Host Label\" channelid=\"9\">11</value_raw></item></histdata>";
                return Task.FromResult(new PrtgSourceResponse(xml, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            });
        Assert.Equal(1, xmlCalls);
        var raw = Assert.Single(result.Targets.Where(t => t.Category == "disk")).RawChannelIdentity!;
        Assert.Equal("ok", raw.Status);
        Assert.Equal(1, raw.SampleCount);
        Assert.Equal(46288.5, raw.Samples[0].RawOaDate);
        Assert.Equal("3", raw.Samples[0].Channels[0].ChannelId);
        Assert.Equal(24, raw.Samples[0].Channels[0].RawValue);
        Assert.True(raw.Samples[0].Channels[0].ExplicitPercentDisplay);
        Assert.False(raw.Samples[0].Channels[1].SemanticKnown);
        Assert.False(raw.AuthorizesFormalProfile);
        Assert.Equal("unknown", raw.RawTimestampBasis);
        Assert.False(result.EvidenceReady);
        var safe = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("Private Host Label", safe);
        Assert.Equal(4, result.Summary.RequestsAttempted);
    }

    [Fact]
    public async Task FullCompatibilityArtifactIncludesSafeDeploymentAndProviderMatchedStorageMetadata()
    {
        var context = new PrtgProbeEvidenceContext
        {
            BuildVersion = "1.0.53.2+0123456789abcdef0123456789abcdef01234567",
            StorageProvider = "Sqlite",
            EfCoreProvider = "Microsoft.EntityFrameworkCore.Sqlite",
            StorageEnvironment = new PrtgStorageEnvironmentFacts
            {
                Status = "measured",
                Provider = "Sqlite",
                EngineVersion = "3.45.0",
                Edition = "unknown",
                EngineEdition = "unknown",
                FileStatus = "measured",
                VolumeStatus = "measured",
                LocalFileStatus = "measured",
                LogStatus = "unknown",
                QueriesAttempted = 2,
                QueriesSucceeded = 2,
                FileCapacities = [
                    new PrtgStorageCapacityRow("database-pages", 4096, 2048, 8192, "bounded", null, null),
                    new PrtgStorageCapacityRow("write-ahead-log", 1024, 512, null, "unknown", null, null)
                ],
                VolumeCapacities = [new PrtgStorageVolumeRow("owned-data-root", 100_000, 40_000)]
            }
        };

        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync((_, _) => Task.FromResult("{}"),
            new TestConsole(), [], context);
        var json = PrtgCompatibilityProbe.SerializeEvidence(evidence);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var deployment = root.GetProperty("deployment_resources");
        var storage = root.GetProperty("storage_environment");

        Assert.Equal(JsonValueKind.Number, deployment.GetProperty("total_available_memory_bytes").ValueKind);
        Assert.Equal(JsonValueKind.Number, deployment.GetProperty("working_set_bytes").ValueKind);
        Assert.Equal("unknown (remote database host resources not observable from app process)",
            deployment.GetProperty("sql_host_resources").GetString());
        Assert.Equal("Sqlite", root.GetProperty("storage_provider").GetString());
        Assert.Equal("Sqlite", storage.GetProperty("provider").GetString());
        Assert.Equal("owned-data-root", storage.GetProperty("volume_capacities")[0].GetProperty("role").GetString());
        Assert.DoesNotContain("physical_memory", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Data Source=", json, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, PrtgCompatibilityProbe.MaxJsonSizeBytes);
    }

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
        // Object identities must be aliases. Numeric substrings in timestamps are unrelated.
        using var document = JsonDocument.Parse(json);
        foreach (var target in document.RootElement.GetProperty("targets").EnumerateArray())
        {
            var objectId = target.GetProperty("snapshot").GetProperty("fields")
                .EnumerateArray().Single(field => field.GetProperty("field").GetString() == "objid");
            Assert.Equal("alias", objectId.GetProperty("value_type").GetString());
            Assert.Equal(target.GetProperty("alias").GetString(), objectId.GetProperty("value").GetString());
        }
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
        Assert.Equal(new[] { "objid", "type", "status", "lastvalue_raw", "lastcheck", "interval", "primarychannel" }, cpu.Snapshot!.RequestedFields);
        Assert.Contains("lastvalue_raw", cpu.Snapshot.MissingRequestedFields);
        Assert.Equal("s1", cpu.Channels!.RequestSensorAlias);
        Assert.Contains("filtered request used the selected sensor objid", cpu.Channels.RequestSensorIdProvenance);
        Assert.Equal(new[] { "objid", "name", "lastvalue", "unit", "scaling", "primary" }, cpu.Channels.RequestedFields);
        Assert.Contains(cpu.Channels.FieldPresence, field => field.Field == "unit" && field.PresentRows == 0 && field.MissingRows == 0);

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
        using var document = JsonDocument.Parse(json);
        var snapshotFields = document.RootElement.GetProperty("targets")[0]
            .GetProperty("snapshot").GetProperty("fields").EnumerateArray().ToArray();
        var objectId = Assert.Single(snapshotFields, field => field.GetProperty("field").GetString() == "objid");
        Assert.Equal("alias", objectId.GetProperty("value_type").GetString());
        Assert.Equal("s1", objectId.GetProperty("value").GetString());
        // Only the selected sensor contributes fields, even when the endpoint ignored its filter.
        Assert.DoesNotContain(snapshotFields, field => field.GetProperty("value").ValueKind == JsonValueKind.String &&
            field.GetProperty("value").GetString() is "999" or "1002" or "other" or "other2");
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
        Assert.Equal(new[] { "datetime", "value_raw", "value_raw", "coverage" },
            firstRow.Entries.Select(entry => entry.Name));
        var timestamp = Assert.Single(firstRow.Entries, entry => entry.Name == "datetime");
        Assert.Equal("datetime_string", timestamp.Type);
        Assert.Equal("unknown", timestamp.TimestampBasis);
        Assert.Equal("2026-10-01T00:00:00.000", timestamp.TimestampComponents);
        Assert.Equal(new object?[] { 10.5, 20.5 },
            firstRow.Entries.Where(entry => entry.Name == "value_raw").Select(entry => entry.Value));
    }

    [Theory]
    [InlineData("+02:00")]
    [InlineData("-05:00")]
    [InlineData("+00:00")]
    [InlineData("+05:30")]
    public async Task ExecuteCoreAsync_ISO明確時區偏移保留原始壁鐘與正負零及半小時偏移(string offset)
    {
        var samples = CreateSamples((1001, "SNMP CPU Load", "Up"));
        var historyJson = System.Text.Json.JsonSerializer.Serialize(new {
            histdata = new[] { new { datetime = "2026-10-04T06:02:03" + offset, value_raw = 1 } }
        });

        Func<string, CancellationToken, Task<string>> getJson = (url, _) =>
        {
            if (url.Contains("/api/historicdata.json")) return Task.FromResult(historyJson);
            if (url.Contains("content=sensors") && url.Contains("filter_objid="))
                return Task.FromResult(@"{""sensors"": [{""objid"": ""1001"", ""type"": ""SNMP CPU Load"", ""status"": ""Up""}]}");
            if (url.Contains("content=channels")) return Task.FromResult(@"{""channels"": []}");
            return Task.FromResult("{}");
        };

        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(getJson, new TestConsole(), samples, null);
        var history = Assert.IsType<PrtgHistoryEvidence>(evidence.Targets.Single(t => t.Category == "cpu").History);
        var timestamp = Assert.Single(Assert.Single(history.Rows).Entries, entry => entry.Name == "datetime");
        var candidate = Assert.Single(timestamp.TimestampCandidates!);
        Assert.Equal("unknown", timestamp.TimestampBasis);
        Assert.Equal(offset, candidate.ReportedOffset);
        Assert.Equal("2026-10-04T06:02:03.000", candidate.StartComponents);
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

        // 僅完整且明確的標籤保留；相鄰的多個詞不能被誤讀為一個已知 channel。
        var (s2, k2) = PrtgCompatibilityProbe.SanitizeChannelName("Total CPU Free Memory");
        Assert.Equal("[redacted]", s2);
        Assert.False(k2);

        var (s2a, k2a) = PrtgCompatibilityProbe.SanitizeChannelName("Free Memory");
        Assert.Equal("free memory", s2a);
        Assert.True(k2a);
        var (s2b, k2b) = PrtgCompatibilityProbe.SanitizeChannelName("Used Memory");
        Assert.Equal("used memory", s2b);
        Assert.True(k2b);

        // 純白名單繁體中文
        var (s3, k3) = PrtgCompatibilityProbe.SanitizeChannelName("剩餘 記憶體");
        Assert.Equal("剩餘 記憶體", s3);
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
        Assert.Contains("request_sensor_id_provenance", json);
        Assert.Contains("requested_fields", json);
        Assert.Contains("missing_requested_fields", json);
        Assert.Contains("\"unit\":\"[redacted]\"", json);
        Assert.Equal("unknown (filtered request; channel objid identifies a channel, response parent sensor id is unavailable)", result.Targets[0].Channels!.ParentIdentity);
        Assert.Equal("s1", result.Targets[0].Channels.RequestSensorAlias);
        Assert.Equal("number", result.Targets[0].Channels!.Rows[0].ChannelIdType);
        Assert.Contains("name", result.Targets[0].Channels.Rows[0].MissingFields);
        Assert.Contains(result.Targets[0].Channels.FieldPresence,
            field => field.Field == "name" && field.PresentRows == 0 && field.MissingRows == 1);
        Assert.Contains(result.Targets[0].Channels.FieldPresence,
            field => field.Field == "unit" && field.PresentRows == 1 && field.MissingRows == 0);
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
    [InlineData("24.1.92.1554+", "24.1.92.1554+")]
    [InlineData("24.1.92.1554+private-build", "unknown")]
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
