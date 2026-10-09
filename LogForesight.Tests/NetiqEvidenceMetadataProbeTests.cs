using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

public class NetiqEvidenceMetadataProbeTests
{
    private const string BaseUrl = "https://sentinel.example.invalid:8443";
    private const string JobUrl = BaseUrl + "/SentinelRESTServices/objects/event-search/one";
    private const string ResultUrl = BaseUrl + "/SentinelRESTServices/objects/event?query=x&page=1&pagesize=3";

    private static SentinelServer Server() => new()
    {
        Name = "test-sentinel",
        BaseUrl = BaseUrl,
        Username = "probe-account",
        Password = "probe-password"
    };

    private static NetiqOptions Options() => new() { RetryCount = 0, TimeoutSeconds = 120, QueryDelayMs = 0 };

    private static SentinelSearchRequest BoundedShapeRequest()
    {
        var end = DateTimeOffset.UtcNow;
        return new SentinelSearchRequest("sev:[0 TO 5]", end.AddHours(-1), end,
            PageSize: 3, MaxResults: 3, RawFields: true, ShapeOnly: true, MaxPages: 1, MaxShapeFieldKeys: 128);
    }

    [Fact]
    public async Task SuccessfulRunnerReportsBoundedUtcWindowVersionAndSafeSourceId()
    {
        var handler = new ProbeHandler(PageResponse("[{\"dt\":\"2026-10-08T10:00:00Z\",\"msg\":\"private-message\"}]"));
        var before = DateTimeOffset.UtcNow;
        var result = await NetiqEvidenceMetadataProbeRunner.RunAsync(Server(), Options(),
            (server, settings, limits) => new SentinelClient(server, settings, handler, limits),
            CancellationToken.None, sourceSentinelId: 53);
        var after = DateTimeOffset.UtcNow;
        Assert.True(result.Success, result.Report);
        Assert.Contains("sentinel-id=53", result.Report);
        Assert.Contains("schema-version=1; program-version=1.0.53.2", result.Report);
        var window = result.Report.Split('\n').Single(line => line.StartsWith("query-start-utc="));
        var parts = window.Trim().Split("; query-end-utc=");
        var start = DateTimeOffset.Parse(parts[0]["query-start-utc=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.FromHours(1), end - start);
        Assert.Equal(TimeSpan.Zero, start.Offset);
        Assert.Equal(TimeSpan.Zero, end.Offset);
        Assert.InRange(end, before, after);
        Assert.DoesNotContain("private-message", result.Report);
        Assert.DoesNotContain("probe-account", result.Report);
        Assert.DoesNotContain(BaseUrl, result.Report);
        Assert.InRange(Encoding.UTF8.GetByteCount(result.Report), 1, NetiqEvidenceMetadataProbeRunner.MaxReportBytes);
        Assert.InRange(handler.Requests.Count, 1, NetiqEvidenceMetadataProbeRunner.MaxHttpRequests);
    }

    [Fact]
    public async Task ShapeOnlyQueryRetainsOnlyFieldShapesAndReportOmitsRawValues()
    {
        var handler = new ProbeHandler(PageResponse(
            "[{\"dt\":\"2026-10-08T10:00:00Z\",\"repip\":\"203.0.113.7\",\"sun\":\"private-account\",\"msg\":\"private-message\",\"evt\":\"private-event\",\"opaqueCandidate\":\"private-volume-reference\",\"details\":{\"opaqueChild\":\"private-nested\"},\"bad\\nfield\":\"secret-invalid-key\"}]"));
        var client = new SentinelClient(Server(), Options(), handler,
            new SentinelClientReadLimits(NetiqEvidenceMetadataProbeRunner.MaxHttpRequests,
                NetiqEvidenceMetadataProbeRunner.MaxResponseBytes));

        try
        {
            var result = await client.SearchAsync(BoundedShapeRequest());
            var report = NetiqEvidenceMetadataProbeRunner.FormatReport(result);

            var evt = Assert.Single(result.Events);
            Assert.Empty(evt.Fields);
            Assert.Contains("dt", evt.Shapes.Keys);
            Assert.True(evt.Shapes["dt"].ParsesAsTimestamp);
            Assert.True(evt.Shapes["dt"].HasExplicitOffset);
            Assert.Contains("opaqueCandidate", evt.Shapes.Keys);
            Assert.Contains("details.opaqueChild", evt.Shapes.Keys);
            Assert.Contains("field=msg; json-kind=string; present=true", report);
            Assert.Contains("redacted-unsafe-field-name-count=1", report);
            Assert.Contains("Candidate shapes only; not semantic proof; does not authorize strong match.", report);
            Assert.Contains("schema-version=1; program-version=", report);
            Assert.Contains("query-start-utc=unknown; query-end-utc=unknown", report);
            Assert.DoesNotContain("203.0.113.7", report);
            Assert.DoesNotContain("private-account", report);
            Assert.DoesNotContain("private-message", report);
            Assert.DoesNotContain("private-event", report);
            Assert.DoesNotContain("private-volume-reference", report);
            Assert.DoesNotContain("private-nested", report);
            Assert.DoesNotContain("bad", report);
            Assert.DoesNotContain("secret-invalid-key", report);
            Assert.DoesNotContain(BaseUrl, report);
            Assert.Contains(handler.Requests, request => request.Url == ResultUrl);
            Assert.InRange(Encoding.UTF8.GetByteCount(report), 1, NetiqEvidenceMetadataProbeRunner.MaxReportBytes);
        }
        finally
        {
            await client.DisposeAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseByteLimitStopsStreamingAfterAtMostLimitPlusOneBytes(bool knownLength)
    {
        var tracked = new CountingStream(Encoding.UTF8.GetBytes(new string('x', 4096)));
        var page = new StreamContent(tracked);
        if (knownLength) page.Headers.ContentLength = 4096;
        var handler = new ProbeHandler(page);
        // 認證與工作狀態仍能通過，才會實際抵達要驗的 event response streaming 邊界。
        var client = new SentinelClient(Server(), Options(), handler, new SentinelClientReadLimits(12, 512));

        try
        {
            var exception = await Assert.ThrowsAsync<SentinelClientException>(() => client.SearchAsync(BoundedShapeRequest()));
            Assert.Equal("response-byte-limit", exception.SafeCode);
            Assert.Contains(handler.Requests, request => request.Url == ResultUrl);
            if (knownLength) Assert.Equal(0, tracked.BytesRead);
            else Assert.InRange(tracked.BytesRead, 1, 513);
        }
        finally
        {
            await client.DisposeAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AuthenticationResponseOverByteLimitRejectsBeforeReadingEvents()
    {
        var tracked = new CountingStream(Encoding.UTF8.GetBytes(new string('x', 512)));
        var handler = new ProbeHandler(new StreamContent(tracked));
        var client = new SentinelClient(Server(), Options(), handler, new SentinelClientReadLimits(12, 40));
        try
        {
            var exception = await Assert.ThrowsAsync<SentinelClientException>(() => client.SearchAsync(BoundedShapeRequest()));
            Assert.Equal("response-byte-limit", exception.SafeCode);
            Assert.Equal(0, tracked.BytesRead);
            Assert.DoesNotContain(handler.Requests, request => request.Url == ResultUrl);
        }
        finally { await client.DisposeAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task PageLimitRejectsSecondPageAndUsesSafeReason()
    {
        var handler = new ProbeHandler(PageResponse("[{\"a\":1},{\"b\":2}]"), found: 4, avail: 4);
        var client = new SentinelClient(Server(), Options(), handler, new SentinelClientReadLimits(12, 4096));

        try
        {
            var exception = await Assert.ThrowsAsync<SentinelClientException>(() => client.SearchAsync(new SentinelSearchRequest(
                "sev:[0 TO 5]", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow,
                PageSize: 2, MaxResults: 4, RawFields: true, MaxPages: 1)));
            Assert.Equal("page-limit", exception.SafeCode);
            Assert.Single(handler.Requests.Where(request => request.Url.Contains("page=1", StringComparison.Ordinal)));
            Assert.DoesNotContain(handler.Requests, request => request.Url.Contains("page=2", StringComparison.Ordinal));
        }
        finally
        {
            await client.DisposeAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeadlineCancellationReturnsOnlyAWhitelistedReason()
    {
        var handler = new ProbeHandler(null) { BlockAuthentication = true };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        var result = await NetiqEvidenceMetadataProbeRunner.RunAsync(Server(), Options(),
            (server, settings, limits) => new SentinelClient(server, settings, handler, limits), deadline.Token);

        Assert.False(result.Success);
        Assert.Contains("reason=deadline-reached", result.Report);
        Assert.DoesNotContain("private-account", result.Report);
        Assert.DoesNotContain(BaseUrl, result.Report);
    }

    [Fact]
    public async Task FieldKeyLimitFailsClosedWithoutReturningAnyResponseValue()
    {
        var body = new StringBuilder("[");
        for (var eventIndex = 0; eventIndex < 1; eventIndex++)
        {
            if (eventIndex > 0) body.Append(',');
            body.Append('{');
            for (var keyIndex = 0; keyIndex < 129; keyIndex++)
            {
                if (keyIndex > 0) body.Append(',');
                var key = "k" + keyIndex.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + new string('a', 60);
                body.Append('"').Append(key).Append("\":\"never-output-value\"");
            }
            body.Append('}');
        }
        body.Append(']');
        var handler = new ProbeHandler(PageResponse(body.ToString()), found: 1, avail: 1);

        var result = await NetiqEvidenceMetadataProbeRunner.RunAsync(Server(), Options(),
            (server, settings, limits) => new SentinelClient(server, settings, handler, limits), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("reason=field-key-limit", result.Report);
        Assert.DoesNotContain("never-output-value", result.Report);
        Assert.InRange(Encoding.UTF8.GetByteCount(result.Report), 1, NetiqEvidenceMetadataProbeRunner.MaxReportBytes);
    }

    [Theory]
    [InlineData("job-location", "https://outside.invalid/SentinelRESTServices/objects/event-search/job", "")]
    [InlineData("job-location", "http://sentinel.example.invalid:8443/SentinelRESTServices/objects/event-search/job", "")]
    [InlineData("results-href", "", "https://outside.invalid/SentinelRESTServices/objects/event?page=1")]
    public async Task BoundedProbeRejectsResponseProvidedCrossOriginOrDowngradeUris(
        string source, string jobLocation, string resultsHref)
    {
        var handler = new ProbeHandler(PageResponse("[]"),
            jobLocation: string.IsNullOrEmpty(jobLocation) ? null : new Uri(jobLocation),
            resultsHref: string.IsNullOrEmpty(resultsHref) ? null : resultsHref);

        var result = await NetiqEvidenceMetadataProbeRunner.RunAsync(Server(), Options(),
            (server, settings, limits) => new SentinelClient(server, settings, handler, limits), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("reason=source-endpoint-mismatch", result.Report);
        Assert.DoesNotContain("outside.invalid", result.Report);
        Assert.DoesNotContain("https://", result.Report);
        Assert.All(handler.Requests, request =>
        {
            var uri = new Uri(request.Url);
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("sentinel.example.invalid", uri.IdnHost);
            Assert.Equal(8443, uri.Port);
        });
        Assert.DoesNotContain(handler.Requests, request => request.Url.Contains("outside.invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhysicalHttpRequestBudgetCountsAuthenticationAndBlocksFurtherRequests()
    {
        var handler = new ProbeHandler(PageResponse("[]"));
        var client = new SentinelClient(Server(), Options(), handler, new SentinelClientReadLimits(2, 4096));
        try
        {
            var exception = await Assert.ThrowsAsync<SentinelClientException>(() => client.SearchAsync(BoundedShapeRequest()));
            Assert.Equal("request-limit", exception.SafeCode);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            await client.DisposeAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ProbeBoundsAreFixedAndLegacyAndMetadataShareOneBusyGate()
    {
        Assert.Equal(30, NetiqEvidenceMetadataProbeRunner.DeadlineSeconds);
        Assert.Equal(3, NetiqEvidenceMetadataProbeRunner.MaxEvents);
        Assert.Equal(128, NetiqEvidenceMetadataProbeRunner.MaxFieldKeys);
        Assert.Equal(512 * 1024, NetiqEvidenceMetadataProbeRunner.MaxResponseBytes);
        Assert.Equal(32 * 1024, NetiqEvidenceMetadataProbeRunner.MaxReportBytes);
        Assert.Equal(12, NetiqEvidenceMetadataProbeRunner.MaxHttpRequests);

        var state = new LogForesight.Web.Services.NetiqProbeRunState();
        Assert.True(state.TryBegin(1, "private server name"));
        Assert.False(state.TryBeginMetadata(1, "another name"));
        state.EndRun(false);
        Assert.True(state.TryBeginMetadata(1, "private server name"));
        Assert.Equal("metadata-shape", state.Snapshot().Mode);
        state.AppendLine(new string('x', NetiqEvidenceMetadataProbeRunner.MaxReportBytes + 1));
        var bounded = state.Snapshot();
        Assert.Contains("reason=report-byte-limit-exceeded", bounded.Output);
        Assert.DoesNotContain(new string('x', 64), bounded.Output);
        Assert.InRange(Encoding.UTF8.GetByteCount(bounded.Output), 1, NetiqEvidenceMetadataProbeRunner.MaxReportBytes);
        state.EndRun(true);
        Assert.False(state.Snapshot().Success);
        Assert.True(state.TryBeginMetadata(1, "private server name"));
        state.AppendLine("one complete bounded result");
        state.EndRun(true);
        Assert.True(state.Snapshot().Success);
    }

    [Fact]
    public void MetadataEndpointInheritsAdminMaintainPermissionAndUiHasRealConsumer()
    {
        var permission = Assert.Single(typeof(AdminController)
            .GetCustomAttributes(typeof(PermissionAttribute), inherit: true).Cast<PermissionAttribute>());
        Assert.Contains((Capability[])permission.Arguments![0], capability => capability == Capability.Maintain);

        var action = typeof(AdminController).GetMethod(nameof(AdminController.StartNetiqMetadataProbe));
        Assert.NotNull(action);
        Assert.Contains(action!.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).Cast<HttpPostAttribute>(),
            attribute => attribute.Template == "netiq/probe/metadata/start");

        var repoRoot = FindRepoRoot();
        var view = File.ReadAllText(Path.Combine(repoRoot, "LogForesight.Web", "Views", "Pages", "Netiq.cshtml"));
        var js = File.ReadAllText(Path.Combine(repoRoot, "LogForesight.Web", "wwwroot", "js", "pages", "netiq.js"));
        Assert.Contains("id=\"probe-metadata-start\"", view);
        Assert.Contains("id=\"probe-metadata-output\"", view);
        Assert.Contains("/api/admin/netiq/probe/metadata/start", js);
        Assert.Contains("status.mode === 'metadata-shape'", js);
    }

    private static HttpContent PageResponse(string json) => new StringContent(json, Encoding.UTF8, "application/json");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "LogForesight.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class ProbeHandler(HttpContent? pageContent, int found = 1, int avail = 1,
        Uri? jobLocation = null, string? resultsHref = null) : HttpMessageHandler
    {
        public bool BlockAuthentication { get; init; }
        public List<(HttpMethod Method, string Url)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add((request.Method, url));
            if (request.Method == HttpMethod.Post && url.EndsWith("/SentinelAuthServices/auth/tokens", StringComparison.Ordinal))
            {
                if (BlockAuthentication) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Json(HttpStatusCode.OK, "{\"Token\":\"test-token\"}");
            }
            if (request.Method == HttpMethod.Post && url.EndsWith("/SentinelRESTServices/objects/event-search", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, "{}", jobLocation ?? new Uri(JobUrl));
            if (request.Method == HttpMethod.Get && url == JobUrl)
                return Json(HttpStatusCode.OK, $"{{\"status\":2,\"found\":{found},\"avail\":{avail},\"results\":{{\"@href\":{JsonSerializer.Serialize(resultsHref ?? ResultUrl)}}}}}");
            if (request.Method == HttpMethod.Get && url.Contains("/SentinelRESTServices/objects/event?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = pageContent };
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.OK);
            throw new InvalidOperationException("Unexpected request in bounded probe test.");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body, Uri? location = null)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (location != null) response.Headers.Location = location;
            return response;
        }
    }

    private sealed class CountingStream(byte[] bytes) : Stream
    {
        private int _position;
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, bytes.Length - _position);
            Array.Copy(bytes, _position, buffer, offset, read);
            _position += read;
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
