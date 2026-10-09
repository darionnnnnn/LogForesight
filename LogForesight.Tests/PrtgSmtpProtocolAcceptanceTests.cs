using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Net.Sockets;
using System.Text;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Bounded loopback SMTP protocol acceptance. This exercises the production sender and the
/// persisted notification outbox against an inbox owned by the test process; it makes no external
/// network request and does not claim delivery to a real mailbox.
/// </summary>
public sealed class PrtgSmtpProtocolAcceptanceTests
{
    private static async Task WithDiagnosticsAsync(Func<string> diagnostics, Func<Task> assertions)
    {
        try
        {
            await assertions().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new Xunit.Sdk.XunitException($"{error}\n\nSMTP acceptance diagnostics:\n{diagnostics()}");
        }
    }

    private static string ExtractBody(string rawMessage)
    {
        var separator = rawMessage.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator < 0) return rawMessage;

        var headers = ParseHeaders(rawMessage[..separator]);
        var encodedBody = rawMessage[(separator + 4)..];
        var transferEncoding = headers.GetValueOrDefault("content-transfer-encoding", "7bit").Trim();
        byte[] bodyBytes = transferEncoding.ToLowerInvariant() switch
        {
            "base64" => Convert.FromBase64String(encodedBody),
            "quoted-printable" => DecodeQuotedPrintable(encodedBody),
            _ => Encoding.Latin1.GetBytes(encodedBody)
        };
        var contentType = headers.GetValueOrDefault("content-type", "text/plain; charset=utf-8");
        var charset = new ContentType(contentType).CharSet;
        return Encoding.GetEncoding(string.IsNullOrWhiteSpace(charset) ? "utf-8" : charset)
            .GetString(bodyBytes).TrimEnd('\r', '\n');
    }

    private static string GetHeader(string rawMessage, string name) =>
        ParseHeaders(rawMessage[..rawMessage.IndexOf("\r\n\r\n", StringComparison.Ordinal)])
            .GetValueOrDefault(name.ToLowerInvariant(), "");

    private static Dictionary<string, string> ParseHeaders(string rawHeaders)
    {
        var unfolded = rawHeaders.Replace("\r\n\t", " ", StringComparison.Ordinal)
            .Replace("\r\n ", " ", StringComparison.Ordinal);
        return unfolded.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (Line: line, Colon: line.IndexOf(':')))
            .Where(item => item.Colon > 0)
            .ToDictionary(item => item.Line[..item.Colon].Trim().ToLowerInvariant(),
                item => item.Line[(item.Colon + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static byte[] DecodeQuotedPrintable(string body)
    {
        var source = Encoding.Latin1.GetBytes(body);
        using var decoded = new MemoryStream(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != '=')
            {
                decoded.WriteByte(source[i]);
                continue;
            }

            if (i + 2 < source.Length && source[i + 1] == '\r' && source[i + 2] == '\n')
            {
                i += 2;
                continue;
            }

            if (i + 2 < source.Length && byte.TryParse(
                    Encoding.ASCII.GetString(source, i + 1, 2),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                decoded.WriteByte(value);
                i += 2;
            }
            else
            {
                decoded.WriteByte(source[i]);
            }
        }
        return decoded.ToArray();
    }

    [Fact]
    public async Task DirectSystemNetSenderReturnsAfterLoopbackInboxAcknowledgesData()
    {
        await using var inbox = new LoopbackSmtpInbox();
        var sender = new SystemNetSmtpMailSender();
        var message = new MailMessageSpec("logforesight@loopback.invalid", ["ops@loopback.invalid"],
            "loopback acknowledgement", "direct sender acceptance baseline");

        await WithDiagnosticsAsync(() => inbox.Diagnostics, async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            await sender.SendAsync(new SmtpConnectionSpec("127.0.0.1", inbox.Port, false, "", null),
                message, deadline.Token);
            Assert.Single(inbox.Messages);
            Assert.Contains("S:250 queued", inbox.Transcript);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSystemNetSenderDoesNotReportAcceptedWhenFinalDataAckIsLost(bool reset)
    {
        await using var inbox = new LoopbackSmtpInbox(dropFirstConnectionBeforeDataAck: true,
            resetConnectionBeforeDataAck: reset);
        var sender = new SystemNetSmtpMailSender();
        var message = new MailMessageSpec("logforesight@loopback.invalid", ["ops@loopback.invalid"],
            "lost acknowledgement", "DATA was captured before the connection closed");

        await WithDiagnosticsAsync(() => inbox.Diagnostics, async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            var error = await Record.ExceptionAsync(() => sender.SendAsync(
                new SmtpConnectionSpec("127.0.0.1", inbox.Port, false, "", null), message, deadline.Token));
            Assert.NotNull(error);
            Assert.IsAssignableFrom<SmtpException>(error);
            Assert.Single(inbox.Messages);
            Assert.Contains(reset ? "S:RST after DATA received; before DATA 250" : "S:FIN after DATA received; before DATA 250",
                inbox.Transcript);
        });
    }

    [Fact]
    public async Task DirectSystemNetSenderCancellationAbortsAndJoinsBlockedAckWorker()
    {
        await using var inbox = new LoopbackSmtpInbox(holdFirstConnectionBeforeDataAck: true);
        var sender = new SystemNetSmtpMailSender();
        var message = new MailMessageSpec("logforesight@loopback.invalid", ["ops@loopback.invalid"],
            "held acknowledgement", "wait until cancellation");

        await WithDiagnosticsAsync(() => inbox.Diagnostics, async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var send = sender.SendAsync(new SmtpConnectionSpec("127.0.0.1", inbox.Port, false, "", null),
                message, cancellation.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await inbox.WaitForMessagesAsync(1, deadline.Token);
            Assert.Contains("S:holding before DATA 250", inbox.Transcript);
            cancellation.Cancel();
            var error = await Record.ExceptionAsync(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Single(inbox.Messages);
        });
    }

    [Fact]
    public async Task FormalPrtgUrgentNotificationIsAcceptedAndCapturedAsRawMessage()
    {
        using var fixture = new PrtgMailFixture();
        await using var inbox = new LoopbackSmtpInbox();
        fixture.AddRecipient("ops@loopback.invalid");
        fixture.ConfigureSmtp(inbox.Port);

        await WithDiagnosticsAsync(() => fixture.Diagnostics(inbox), async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            await fixture.CreateService().NotifyAfterRunAsync(deadline.Token);

            var message = Assert.Single(inbox.Messages);
            Assert.Equal("ops@loopback.invalid", message.EnvelopeRecipients.Single());
            var to = new MailAddressCollection();
            to.Add(GetHeader(message.RawMessage, "To"));
            Assert.Contains(to, address => address.Address.Equals("ops@loopback.invalid", StringComparison.OrdinalIgnoreCase));
            var decodedBody = ExtractBody(message.RawMessage);
            Assert.Contains("CASE-53-PRTG-SMTP-BODY", decodedBody, StringComparison.Ordinal);
            Assert.Contains("SMTP-CASE-HOST", decodedBody, StringComparison.Ordinal);
            Assert.Contains(fixture.RecordDay.ToString("yyyy-MM-dd"), decodedBody, StringComparison.Ordinal);

            var state = fixture.ReadMailState();
            var intent = Assert.Single(state.UrgentOutbox.Values);
            Assert.Equal("smtp-accepted", intent.Status);
            Assert.Equal("smtp-accepted", intent.Recipients["ops@loopback.invalid"]);
            Assert.Equal(fixture.ExpectedEventKey, Assert.Single(intent.ProblemKeys));
            Assert.Contains("ops@loopback.invalid", intent.SmtpAcceptedAtUtc.Keys);
        });
    }

    [Fact]
    public async Task DataReceivedBeforeConnectionDropIsVisibleAsUnknownAndRetryKeepsSameCaseIntent()
    {
        using var fixture = new PrtgMailFixture();
        await using var inbox = new LoopbackSmtpInbox(dropFirstConnectionBeforeDataAck: true);
        fixture.AddRecipient("ops@loopback.invalid");
        fixture.ConfigureSmtp(inbox.Port);

        await WithDiagnosticsAsync(() => fixture.Diagnostics(inbox), async () =>
        {
            using (var firstDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(9)))
                await fixture.CreateService().NotifyAfterRunAsync(firstDeadline.Token);

            // Prove this is the post-DATA/pre-250 loss case before checking service state.
            var firstInboxCopy = Assert.Single(inbox.Messages);
            Assert.Equal("ops@loopback.invalid", firstInboxCopy.EnvelopeRecipients.Single());
            Assert.Contains("S:DATA received", inbox.Transcript);
            Assert.Contains("S:RST after DATA received; before DATA 250", inbox.Transcript);

            var keyBeforeRetry = Assert.Single(fixture.ReadMailState().UrgentOutbox.Keys);
            var unresolved = fixture.ReadMailState().UrgentOutbox[keyBeforeRetry];
            Assert.Equal("pending", unresolved.Status);
            Assert.Equal("failed-or-unknown", unresolved.Recipients["ops@loopback.invalid"]);

            // Recreate the consumer and state-store wrapper to exercise the durable retry path.
            using (var retryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(9)))
                await fixture.CreateService().RetryPendingUrgentAsync(retryDeadline.Token);

            var copies = inbox.Messages.ToArray();
            Assert.Equal(2, copies.Length); // first DATA was accepted by the inbox before its 250 was lost.
            Assert.All(copies, copy => Assert.Equal("ops@loopback.invalid", copy.EnvelopeRecipients.Single()));
            Assert.Equal(ExtractBody(firstInboxCopy.RawMessage), ExtractBody(copies[1].RawMessage));

            var afterRetry = fixture.ReadMailState();
            Assert.Equal(keyBeforeRetry, Assert.Single(afterRetry.UrgentOutbox.Keys));
            Assert.Equal(keyBeforeRetry, afterRetry.UrgentOutbox[keyBeforeRetry].Key);
            Assert.Equal("smtp-accepted", afterRetry.UrgentOutbox[keyBeforeRetry].Status);
            Assert.Equal("smtp-accepted", afterRetry.UrgentOutbox[keyBeforeRetry].Recipients["ops@loopback.invalid"]);
            Assert.Equal(fixture.ExpectedEventKey, Assert.Single(afterRetry.UrgentOutbox[keyBeforeRetry].ProblemKeys));
        });
    }

    [Fact]
    public async Task AcceptedRecipientRemainsAcceptedWhenMuteChangesBeforeNextRecipientAndServiceRestarts()
    {
        using var fixture = new PrtgMailFixture();
        await using var inbox = new LoopbackSmtpInbox();
        fixture.AddRecipient("ops@loopback.invalid");
        fixture.AddRecipient("second@loopback.invalid");
        fixture.ConfigureSmtp(inbox.Port);
        fixture.MuteAfterNextAcceptedSend();

        await WithDiagnosticsAsync(() => fixture.Diagnostics(inbox), async () =>
        {
            using (var firstDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(9)))
                await fixture.CreateService().NotifyAfterRunAsync(firstDeadline.Token);

            var key = Assert.Single(fixture.ReadMailState().UrgentOutbox.Keys);
            var afterFirst = fixture.ReadMailState().UrgentOutbox[key];
            Assert.Equal("smtp-accepted", afterFirst.Recipients["ops@loopback.invalid"]);
            Assert.DoesNotContain("smtp-accepted", afterFirst.Recipients
                .Where(pair => pair.Key.Equals("second@loopback.invalid", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value));
            Assert.Equal("ops@loopback.invalid", Assert.Single(inbox.Messages).EnvelopeRecipients.Single());
            Assert.Contains("S:250 queued", inbox.Transcript);

            // A new consumer reads the same SQLite-backed outbox while the mute remains active.
            using (var restartDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(9)))
                await fixture.CreateService().RetryPendingUrgentAsync(restartDeadline.Token);

            Assert.Single(inbox.Messages);
            var afterRestart = fixture.ReadMailState().UrgentOutbox[key];
            Assert.Equal("smtp-accepted", afterRestart.Recipients["ops@loopback.invalid"]);
            Assert.Equal(key, afterRestart.Key);
            Assert.DoesNotContain("smtp-accepted", afterRestart.Recipients
                .Where(pair => pair.Key.Equals("second@loopback.invalid", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value));
        });
    }

    private sealed class PrtgMailFixture : IDisposable
    {
        private const long DeviceId = 1;
        private const long SensorId = 10;
        private static readonly DateTime RecordDayValue = DateTime.Today.AddDays(-1);
        private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "lf-prtg-smtp-" + Guid.NewGuid().ToString("N"));
        private readonly EfSqliteFixture _sqlite = new();
        private readonly FakeSystemSettingsStore _settings = new();
        private readonly FakeHostStore _hosts = new();
        private readonly FakeUserStore _users = new();
        private readonly FakeUserGroupStore _groups = new();
        private readonly FakeGroupAccessStore _groupAccess = new();
        private readonly FakeAnalysisRecordQuery _records = new();
        private readonly FakeHandlingStore _handlings = new();
        private readonly FakeIssueOwnerStore _owners = new();
        private readonly FakeIssueAggregateQuery _aggregates = new();
        private readonly RecordingSmtpMailSender _sender = new(new SystemNetSmtpMailSender());
        private readonly StorageBackend _prtgBackend;
        private readonly PrtgMonitoringPolicyStore _policy;
        private readonly WebHost _host;
        private readonly LogIssueSignature _signature;

        public DateTime RecordDay => RecordDayValue;
        public string ExpectedEventKey => _signature.EventKey;

        public void MuteAfterNextAcceptedSend() => _sender.AfterNextAcceptedSend = _ => MuteFormalFinding();
        private long HostId => _host.HostId;

        public PrtgMailFixture()
        {
            _prtgBackend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _dataRoot);
            _policy = new PrtgMonitoringPolicyStore(_prtgBackend.Blob(PrtgMonitoringPolicyStore.BlobKey));
            _host = _hosts.Upsert(new WebHost { HostName = "SMTP-CASE-HOST", Active = true, Source = "netiq" });

            var adminGroup = _groups.Upsert(new UserGroup { GroupName = "smtp-test-admin", Role = UserRole.Admin, Active = true });
            foreach (var email in new[] { "ops@loopback.invalid", "second@loopback.invalid" })
            {
                _users.Upsert(new WebUser { Account = email, Email = email, Active = true, GroupIds = [adminGroup.GroupId] });
            }

            _policy.Update(policy =>
            {
                policy.Revision = "smtp-acceptance-policy-v1";
                policy.CoreSystemId = "core";
                policy.SourceGeneration = "core";
                policy.SourceTimeZoneId = TimeZoneInfo.Local.Id;
                policy.SourceCultureName = "en-US";
                policy.EndpointHint = EfPrtgObservationStore.SourceHintFor("http://127.0.0.1/");
                policy.HostIds = [HostId];
                policy.SensorIds = [SensorId];
                policy.ValidFrom = DateTimeOffset.Now.AddDays(-3);
            });

            var store = _prtgBackend.PrtgStore();
            store.UpsertDevices([new PrtgDeviceRow { Objid = DeviceId, Name = _host.HostName }], DateTime.Now);
            store.UpsertSensors([new PrtgSensorRow
            {
                Objid = SensorId, DeviceObjid = DeviceId, SensorType = "ping",
                Category = "availability", Paused = false
            }], DateTime.Now);
            store.ReplaceHostMapForDate(RecordDay, [new PrtgHostMapRow
            {
                DeviceObjid = DeviceId, MapDate = RecordDay, HostId = HostId,
                HostName = _host.HostName, MapStatus = PrtgMapStatus.Ok
            }]);
            var identity = store.BindObservedResource(SensorId, HostId, "core", "smtp-resource-10");
            var rule = new KnownIssueRule
            {
                Id = "smtp-down", Platform = "prtg", PrtgRuleCode = "down", Enabled = true,
                Severity = IssueSeverity.High, ElevatesDayRisk = true
            };
            new KnownIssueRuleStore(_prtgBackend.Blob("rules")).Save(new RuleFileContent { Rules = [rule] });
            var finding = new PrtgFinding(DeviceId, SensorId, "down", "trusted", 60, rule)
            {
                SourceGeneration = identity.SourceGeneration,
                ResourceGeneration = identity.Generation,
                IncidentStartedAt = new DateTimeOffset(RecordDay.AddHours(1))
            };
            _signature = PrtgFindingMapper.ToSignature(finding, RecordDay, HostId);
            _prtgBackend.PrtgObservationStore().Capture(HostId, RecordDay, "smtp-acceptance-v1", [(finding, _signature)]);
            new PrtgSensorTimelineStore(_prtgBackend.Blob(PrtgSensorTimelineStore.Prefix + SensorId)).Update(timeline =>
            {
                timeline.Bind(SensorId, HostId, identity.SourceGeneration, "smtp-acceptance-resource",
                    identity.Generation, identity.Epoch, identity.ChannelGeneration, _policy.Get().ValidFrom);
                timeline.MappingRevision = _prtgBackend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
            });

            var record = new DailyAnalysisRecord
            {
                RecordId = 53001, HostId = HostId, Host = _host.HostName, Date = RecordDay,
                RiskLevel = RiskLevels.High, LogSource = AnalysisLogSource.Netiq,
                TopIssues = [_signature]
            };
            _records.Add(record);

            _settings.Update(settings =>
            {
                settings.MailEnabled = true;
                settings.MailUrgentEnabled = true;
                settings.MailOnRunCompleted = false;
                settings.MailRecipients = [];
                settings.MailFrom = "logforesight@loopback.invalid";
                settings.MailBodyIntro = "CASE-53-PRTG-SMTP-BODY";
                settings.PrtgEnabled = true;
                settings.PrtgUrl = "http://127.0.0.1/";
                settings.SmtpUseTls = false;
                settings.SmtpAccount = "";
                settings.SmtpPasswordEnc = "";
                settings.RetentionDays = 30;
            });
        }

        public void AddRecipient(string email) => _settings.Update(settings => settings.MailRecipients.Add(email));

        public void ConfigureSmtp(int port) => _settings.Update(settings =>
        {
            settings.SmtpServer = "127.0.0.1";
            settings.SmtpPort = port;
        });

        public MailNotificationService CreateService()
        {
            var freshness = new ScheduleFreshnessService(
                new BatchRunStore(_sqlite.LogStore("batch_runs"), _sqlite.LogStore("batch_run_logs")),
                new ScheduleOptionsStore(_sqlite.Blob("schedule_options")));
            return new MailNotificationService(_settings, _sender, _hosts, _users,
                _groups, _groupAccess, _records, _handlings, new MailNotifyStateStore(_sqlite.Blob("mail_notify_state")),
                freshness, _owners, _aggregates, prtgMonitoring: _policy, prtgBackend: _prtgBackend);
        }

        public string Diagnostics(LoopbackSmtpInbox inbox) =>
            $"sender attempts:\n{_sender.Diagnostics}\nloopback inbox:\n{inbox.Diagnostics}";

        public MailNotifyState ReadMailState() => new MailNotifyStateStore(_sqlite.Blob("mail_notify_state")).Get();

        public void MuteFormalFinding() => _owners.Upsert(new IssueProfile
        {
            SourceName = _signature.Source, EventId = _signature.EventId,
            Mutes = [new MuteInterval { From = RecordDay, To = RecordDay }]
        });

        public void Dispose()
        {
            _sqlite.Dispose();
            if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private sealed record CapturedSmtpMessage(IReadOnlyList<string> EnvelopeRecipients, string RawMessage);

    private sealed class RecordingSmtpMailSender(ISmtpMailSender inner) : ISmtpMailSender
    {
        private readonly ConcurrentQueue<string> _attempts = new();
        public string Diagnostics => string.Join(Environment.NewLine, _attempts.DefaultIfEmpty("<no sender call>"));
        public Action<MailMessageSpec>? AfterNextAcceptedSend { get; set; }

        public async Task SendAsync(SmtpConnectionSpec connection, MailMessageSpec message, CancellationToken ct = default)
        {
            var context = $"server={connection.Server}:{connection.Port}; tls={connection.UseTls}; recipients={string.Join(",", message.To)}";
            _attempts.Enqueue($"attempt {context}");
            try
            {
                await inner.SendAsync(connection, message, ct).ConfigureAwait(false);
                _attempts.Enqueue($"accepted {context}");
                var afterAccepted = AfterNextAcceptedSend;
                AfterNextAcceptedSend = null;
                afterAccepted?.Invoke(message);
            }
            catch (Exception error)
            {
                var chain = new List<string>();
                for (Exception? current = error; current != null; current = current.InnerException)
                    chain.Add($"{current.GetType().FullName}: {current.Message}");
                _attempts.Enqueue($"failed {context}; exception-chain={string.Join(" -> ", chain)}");
                throw;
            }
        }
    }

    /// <summary>Minimal RFC 5321 DATA sink bound only to 127.0.0.1 and an OS-assigned ephemeral port.</summary>
    private sealed class LoopbackSmtpInbox : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(10));
        private readonly ConcurrentQueue<CapturedSmtpMessage> _messages = new();
        private readonly ConcurrentQueue<string> _transcript = new();
        private readonly SemaphoreSlim _messageSignal = new(0);
        private readonly TaskCompletionSource _releaseHeldAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _acceptLoop;
        private int _dropRemaining;
        private int _holdRemaining;
        private readonly bool _resetConnectionBeforeDataAck;

        public LoopbackSmtpInbox(bool dropFirstConnectionBeforeDataAck = false,
            bool resetConnectionBeforeDataAck = true, bool holdFirstConnectionBeforeDataAck = false)
        {
            _dropRemaining = dropFirstConnectionBeforeDataAck ? 1 : 0;
            _holdRemaining = holdFirstConnectionBeforeDataAck ? 1 : 0;
            _resetConnectionBeforeDataAck = resetConnectionBeforeDataAck;
            _listener.Start(4);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync(_stop.Token);
        }

        public int Port { get; }
        public IReadOnlyCollection<CapturedSmtpMessage> Messages => _messages.ToArray();
        public string Transcript => string.Join(Environment.NewLine, _transcript);
        public string Diagnostics => $"port={Port}; captured-messages={_messages.Count}\n{Transcript}";

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    client.NoDelay = true;
                    await ServeConnectionAsync(client, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
            catch (SocketException) when (ct.IsCancellationRequested) { }
        }

        private async Task ServeConnectionAsync(TcpClient client, CancellationToken ct)
        {
            var dataAcknowledged = false;
            try
            {
                using var stream = client.GetStream();
                // Latin-1 is a byte-preserving mapping here; SMTP commands stay ASCII while MIME body
                // bytes remain available for transfer-decoding and charset decoding below.
                using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(stream, Encoding.ASCII, bufferSize: 1024, leaveOpen: true)
                { NewLine = "\r\n", AutoFlush = true };
                _transcript.Enqueue("S:220 loopback.test ESMTP ready");
                await writer.WriteLineAsync("220 loopback.test ESMTP ready").ConfigureAwait(false);
                var recipients = new List<string>();
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) return;
                    var command = line.Trim();
                    if (command.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase) ||
                        command.StartsWith("HELO ", StringComparison.OrdinalIgnoreCase))
                    {
                        _transcript.Enqueue($"C:{command.Split(' ', 2)[0].ToUpperInvariant()}");
                        _transcript.Enqueue("S:250 EHLO accepted");
                        await writer.WriteLineAsync("250-loopback.test").ConfigureAwait(false);
                        await writer.WriteLineAsync("250 SIZE 10485760").ConfigureAwait(false);
                    }
                    else if (command.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase))
                    {
                        _transcript.Enqueue("C:MAIL FROM");
                        _transcript.Enqueue("S:250 sender accepted");
                        await writer.WriteLineAsync("250 sender accepted").ConfigureAwait(false);
                    }
                    else if (command.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                    {
                        recipients.Add(ExtractAddress(command["RCPT TO:".Length..]));
                        _transcript.Enqueue("C:RCPT TO");
                        _transcript.Enqueue("S:250 recipient accepted");
                        await writer.WriteLineAsync("250 recipient accepted").ConfigureAwait(false);
                    }
                    else if (command.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        _transcript.Enqueue("C:DATA");
                        _transcript.Enqueue("S:354 send message data");
                        await writer.WriteLineAsync("354 send message data").ConfigureAwait(false);
                        var data = new StringBuilder();
                        while (true)
                        {
                            var dataLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                            if (dataLine is null) return;
                            if (dataLine == ".") break;
                            data.AppendLine(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine);
                        }

                        var captured = new CapturedSmtpMessage(recipients.ToArray(), data.ToString());
                        _messages.Enqueue(captured);
                        _messageSignal.Release();
                        _transcript.Enqueue($"S:DATA received ({Encoding.Latin1.GetByteCount(captured.RawMessage)} bytes)");
                        if (Interlocked.CompareExchange(ref _holdRemaining, 0, 1) == 1)
                        {
                            _transcript.Enqueue("S:holding before DATA 250");
                            await _releaseHeldAck.Task.WaitAsync(ct).ConfigureAwait(false);
                        }
                        if (Interlocked.CompareExchange(ref _dropRemaining, 0, 1) == 1)
                        {
                            if (_resetConnectionBeforeDataAck)
                            {
                                // Force a TCP RST after DATA capture and before any final reply bytes.
                                client.Client.LingerState = new LingerOption(enable: true, seconds: 0);
                                _transcript.Enqueue("S:RST after DATA received; before DATA 250");
                            }
                            else
                            {
                                _transcript.Enqueue("S:FIN after DATA received; before DATA 250");
                            }
                            return; // DATA reached the inbox; the remote 250 acknowledgement is intentionally lost.
                        }
                        _transcript.Enqueue("S:250 queued");
                        await writer.WriteLineAsync("250 queued by loopback inbox").ConfigureAwait(false);
                        dataAcknowledged = true;
                        recipients.Clear();
                    }
                    else if (command.Equals("RSET", StringComparison.OrdinalIgnoreCase))
                    {
                        recipients.Clear();
                        _transcript.Enqueue("C:RSET");
                        _transcript.Enqueue("S:250 reset");
                        await writer.WriteLineAsync("250 reset").ConfigureAwait(false);
                    }
                    else if (command.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        _transcript.Enqueue("C:QUIT");
                        _transcript.Enqueue("S:221 closing connection");
                        await writer.WriteLineAsync("221 closing connection").ConfigureAwait(false);
                        return;
                    }
                    else
                    {
                        _transcript.Enqueue($"C:{command.Split(' ', 2)[0].ToUpperInvariant()}");
                        _transcript.Enqueue("S:250 ok");
                        await writer.WriteLineAsync("250 ok").ConfigureAwait(false);
                    }
                }
            }
            catch (IOException) when (dataAcknowledged || ct.IsCancellationRequested)
            {
                _transcript.Enqueue(dataAcknowledged
                    ? "C:peer closed after DATA 250"
                    : "C:peer closed during fixture shutdown");
            }
            catch (SocketException) when (dataAcknowledged || ct.IsCancellationRequested)
            {
                _transcript.Enqueue(dataAcknowledged
                    ? "C:peer reset after DATA 250"
                    : "C:peer reset during fixture shutdown");
            }
        }

        public async Task WaitForMessagesAsync(int count, CancellationToken ct)
        {
            while (_messages.Count < count)
                await _messageSignal.WaitAsync(ct).ConfigureAwait(false);
        }

        private static string ExtractAddress(string value)
        {
            var start = value.IndexOf('<');
            var end = value.IndexOf('>');
            return start >= 0 && end > start ? value[(start + 1)..end] : value.Trim();
        }

        public async ValueTask DisposeAsync()
        {
            _releaseHeldAck.TrySetResult();
            _stop.Cancel();
            _listener.Stop();
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            _messageSignal.Dispose();
            _stop.Dispose();
        }
    }
}
