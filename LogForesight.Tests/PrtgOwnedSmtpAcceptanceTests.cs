using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Analysis;
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
/// Crosses the qualified formal PRTG resource parent, guarded AI update, actual mail service,
/// production SMTP sender, and a bounded inbox owned by this test process. The sink binds only
/// loopback and does not claim delivery to a real mailbox.
/// </summary>
[Collection("KnownIssueCatalogState")]
public sealed class PrtgOwnedSmtpAcceptanceTests
{
    private const string Recipient = "formal-case@owned-smtp.invalid";
    private const string SecondRecipient = "second-case@owned-smtp.invalid";

    [Fact]
    public async Task QualifiedFormalCpuMailIsAcceptedCapturedAndDeduplicated()
    {
        using var formal = await FormalCpuCase.CreateAsync();
        await using var inbox = new OwnedSmtpInbox();
        formal.ConfigureMail(inbox.Port);
        var mail = formal.CreateMailService();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await mail.NotifyAfterRunAsync(deadline.Token);
        await inbox.WaitForMessagesAsync(1, deadline.Token);

        var delivered = Assert.Single(inbox.Messages);
        Assert.Equal(new[] { Recipient }, delivered.EnvelopeRecipients);
        Assert.Contains("PRTG:resource_cpu_sustained_pressure", ExtractTextBody(delivered.RawMime), StringComparison.Ordinal);
        var accepted = formal.ReadMailState();
        // SummarySentKeys use the actual persisted host-day key; this is summary deduplication,
        // separate from production issue-case identity exercised by the urgent case below.
        Assert.Contains(formal.InternalRecordKey, accepted.SummarySentKeys);

        using var repeatDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await formal.CreateMailService().NotifyAfterRunAsync(repeatDeadline.Token);
        Assert.Single(inbox.Messages);
        Assert.Contains(formal.InternalRecordKey, formal.ReadMailState().SummarySentKeys);
    }

    [Fact]
    public async Task TemporarySingleRecipientRefusalRetriesSameHostDaySummary()
    {
        using var formal = await FormalCpuCase.CreateAsync();
        await using var inbox = new OwnedSmtpInbox(refuseNextRecipient: Recipient);
        formal.ConfigureMail(inbox.Port);

        using var firstDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await formal.CreateMailService().NotifyAfterRunAsync(firstDeadline.Token);
        await inbox.WaitForRefusalsAsync(1, firstDeadline.Token);
        Assert.Empty(inbox.Messages);
        Assert.DoesNotContain(formal.InternalRecordKey, formal.ReadMailState().SummarySentKeys);

        using var retryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await formal.CreateMailService().NotifyAfterRunAsync(retryDeadline.Token);
        await inbox.WaitForMessagesAsync(1, retryDeadline.Token);

        var delivered = Assert.Single(inbox.Messages);
        Assert.Equal(new[] { Recipient }, delivered.EnvelopeRecipients);
        Assert.Contains("PRTG:resource_cpu_sustained_pressure", ExtractTextBody(delivered.RawMime), StringComparison.Ordinal);
        Assert.Equal(1, inbox.Refusals.Count);
        Assert.Contains(formal.InternalRecordKey, formal.ReadMailState().SummarySentKeys);
    }

    [Fact]
    public async Task ProductionWorkOrderCaseRemainsBoundAcrossPartialUrgentSmtpRetry()
    {
        using var formal = await FormalCpuCase.CreateAsync(elevateCpuRisk: true);
        var productionCaseId = formal.CreateProductionCase();
        await using var inbox = new OwnedSmtpInbox(refuseNextRecipient: Recipient);
        formal.ConfigureMail(inbox.Port, urgent: true);

        using var firstDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await formal.CreateMailService().NotifyAfterRunAsync(firstDeadline.Token);
        await inbox.WaitForRefusalsAsync(1, firstDeadline.Token);
        await inbox.WaitForMessagesAsync(1, firstDeadline.Token);
        var firstAccepted = Assert.Single(inbox.Messages);
        Assert.Equal(new[] { SecondRecipient }, firstAccepted.EnvelopeRecipients);
        var pending = Assert.Single(formal.ReadMailState().UrgentOutbox.Values);
        Assert.Equal("pending", pending.Status);
        Assert.Equal("failed-or-unknown", pending.Recipients[Recipient]);
        Assert.Equal("smtp-accepted", pending.Recipients[SecondRecipient]);
        Assert.Contains(formal.TargetEventKey, pending.ProblemKeys);
        Assert.Equal(productionCaseId, formal.ReadPersistedProductionCase().CaseId);

        using var retryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await formal.CreateMailService().RetryPendingUrgentAsync(retryDeadline.Token);
        await inbox.WaitForMessagesAsync(2, retryDeadline.Token);

        var delivered = Assert.Single(inbox.Messages.Where(message => message.EnvelopeRecipients.Contains(Recipient)));
        Assert.Equal(new[] { Recipient }, delivered.EnvelopeRecipients);
        var body = ExtractTextBody(delivered.RawMime);
        Assert.Contains("PRTG:resource_cpu_sustained_pressure", body, StringComparison.Ordinal);
        Assert.Contains(formal.HostName, body, StringComparison.Ordinal);
        Assert.Single(inbox.Messages.Where(message => message.EnvelopeRecipients.Contains(SecondRecipient)));
        Assert.Equal(1, inbox.Refusals.Count);
        var accepted = Assert.Single(formal.ReadMailState().UrgentOutbox.Values);
        Assert.Equal(pending.Key, accepted.Key);
        Assert.Equal(pending.ParentRecordId, accepted.ParentRecordId);
        Assert.Contains(formal.TargetEventKey, accepted.ProblemKeys);
        Assert.Equal("smtp-accepted", accepted.Status);
        Assert.Equal("smtp-accepted", accepted.Recipients[Recipient]);
        Assert.Equal("smtp-accepted", accepted.Recipients[SecondRecipient]);
        Assert.Equal(productionCaseId, formal.ReadPersistedProductionCase().CaseId);
        // MailIssueDigest currently renders finding/host/day, not IssueCase.CaseId. The real
        // CaseId is therefore verified through both production stores and bound to this email's
        // exact finding via the urgent intent ProblemKeys and captured MIME finding row.
    }

    [Fact]
    public async Task RevokedFormalModeSuppressesFormalUrgentFindingButKeepsQualifiedBaselineMail()
    {
        using var formal = await FormalCpuCase.CreateAsync(elevateCpuRisk: true, highRiskNetIqBaseline: true);
        Assert.Equal(RiskLevels.High, formal.ReadParent().PrtgBaselineRiskLevel);
        Assert.Null(formal.ReadParent().RiskReview);
        await using var inbox = new OwnedSmtpInbox();
        formal.ConfigureMail(inbox.Port, urgent: true);
        formal.RevokeFormalMode();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await formal.CreateMailService().NotifyAfterRunAsync(deadline.Token);
        try
        {
            await inbox.WaitForMessagesAsync(2, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            var state = formal.ReadMailState();
            var diagnosticIntent = state.UrgentOutbox.Values.FirstOrDefault();
            var recipientStates = diagnosticIntent == null ? "none" : string.Join(",", diagnosticIntent.Recipients.Values.OrderBy(value => value));
            var formalIssueStates = diagnosticIntent == null ? "none" : string.Join(",", diagnosticIntent.FormalIssueStates.Values.OrderBy(value => value));
            var claimStates = string.Join(",", (formal.ReadFormalMailClaimShard()?.Claims.Values
                .Select(claim => claim.Status) ?? Enumerable.Empty<string>()).OrderBy(value => value));
            var parent = formal.ReadParent();
            throw new InvalidOperationException(
                $"Expected two loopback SMTP acceptances; captured={inbox.Messages.Count}, refusals={inbox.Refusals.Count}, " +
                $"outbox={diagnosticIntent?.Status ?? "missing"}, recipientStates={recipientStates}, formalIssueStates={formalIssueStates}, " +
                $"claimStates={claimStates}, baselinePresent={parent.TopIssues.Any(issue => issue.Source == "Synthetic NetIQ disk baseline")}, " +
                $"baselineRisk={parent.PrtgBaselineRiskLevel ?? "missing"}, parentRisk={parent.RiskLevel}, review={parent.RiskReview?.Status ?? "none"}, " +
                $"cpuFormalPresent={parent.TopIssues.Any(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue)}.");
        }
        Assert.Equal(2, inbox.Messages.Count);
        foreach (var message in inbox.Messages)
        {
            var body = ExtractTextBody(message.RawMime);
            Assert.Contains("Synthetic NetIQ disk baseline", body, StringComparison.Ordinal);
            Assert.DoesNotContain("PRTG:resource_cpu_sustained_pressure", body, StringComparison.Ordinal);
        }
        Assert.Empty(inbox.Refusals);
        var intent = Assert.Single(formal.ReadMailState().UrgentOutbox.Values);
        Assert.Equal("smtp-accepted", intent.Status);
        Assert.Contains(formal.TargetEventKey, intent.ProblemKeys);
        Assert.Empty(intent.FormalStartFenceRefs);
        Assert.Empty(intent.FormalStartFences);
        Assert.Contains(intent.FormalIssueStates, item => item.Value == "revoked");
        Assert.Empty(formal.ReadMailState().FormalMailStartClaims);
        var shard = formal.ReadFormalMailClaimShard();
        Assert.NotNull(shard);
        Assert.Contains(shard!.Claims.Values, claim => claim.Status == "revoked");
        Assert.DoesNotContain(shard.Claims.Values, claim =>
            claim.Status is "sending-result-unknown" or "failed-or-unknown" or "smtp-accepted");
    }

    [Fact]
    public async Task DisabledMailDeliverySendsNothing()
    {
        using var formal = await FormalCpuCase.CreateAsync();
        await using var inbox = new OwnedSmtpInbox();
        formal.ConfigureMail(inbox.Port);
        formal.DisableMailDelivery();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await formal.CreateMailService().NotifyAfterRunAsync(deadline.Token);
        Assert.Empty(inbox.Messages);
        Assert.Empty(inbox.Refusals);
        Assert.Empty(formal.ReadMailState().SummarySentKeys);
    }

    public static IEnumerable<object[]> DeclaredFormalFamilies()
    {
        foreach (var family in new[] { "Down", "Warning", "Flapping", "Silent", "CPUFormal",
                     "MemoryFormal", "DiskTwoHourLowWater", "DiskTrend" })
        {
            yield return [family, false];
            yield return [family, true];
        }
    }

    [Theory]
    [MemberData(nameof(DeclaredFormalFamilies))]
    public async Task EachDeclaredFormalFamilyCrossesDailyAiProductionWorkOrderAndOwnedSmtp(string family, bool urgent)
    {
        using var formal = await FormalRuleMailCase.CreateAsync(family);
        var firstCase = formal.CreateProductionCase();
        Assert.True(firstCase.Outcome.CreatedOrder, $"{formal.CaseId} did not create its production work order.");
        Assert.Equal(1, firstCase.Outcome.NewCases);
        Assert.Equal(firstCase.Case.CaseId, formal.ReadPersistedProductionCase().CaseId);
        Assert.Equal(1, formal.ActiveWorkOrderCount);

        var repeatedCase = formal.CreateProductionCase();
        Assert.False(repeatedCase.Outcome.CreatedOrder, $"{formal.CaseId} created a duplicate order on replay.");
        Assert.Equal(0, repeatedCase.Outcome.NewCases);
        Assert.Equal(firstCase.Case.CaseId, repeatedCase.Case.CaseId);
        Assert.Equal(1, formal.ActiveWorkOrderCount);

        await using var inbox = new OwnedSmtpInbox();
        formal.ConfigureMail(inbox.Port, urgent);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await formal.CreateMailService().NotifyAfterRunAsync(deadline.Token);
        await inbox.WaitForMessagesAsync(1, deadline.Token);

        var delivered = Assert.Single(inbox.Messages);
        Assert.Equal(new[] { Recipient }, delivered.EnvelopeRecipients);
        var body = ExtractTextBody(delivered.RawMime);
        Assert.Contains(formal.ExpectedSource, body, StringComparison.Ordinal);
        if (urgent)
        {
            Assert.Contains(formal.HostName, body, StringComparison.Ordinal);
            var intent = Assert.Single(formal.ReadMailState().UrgentOutbox.Values);
            Assert.Equal("smtp-accepted", intent.Status);
            formal.AssertAcceptedUrgentDelivery();
        }
        else
        {
            Assert.Contains("影響 1 台", body, StringComparison.Ordinal);
            Assert.Contains(formal.InternalRecordKey, formal.ReadMailState().SummarySentKeys);
        }

        using var repeatDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await formal.CreateMailService().NotifyAfterRunAsync(repeatDeadline.Token);
        Assert.Single(inbox.Messages);
        Assert.Equal(firstCase.Case.CaseId, formal.ReadPersistedProductionCase().CaseId);
        Assert.Equal(1, formal.ActiveWorkOrderCount);
        if (urgent) formal.AssertAcceptedUrgentDelivery();
        else Assert.Contains(formal.InternalRecordKey, formal.ReadMailState().SummarySentKeys);
        // The production digest renders finding/host/day rather than IssueCase.CaseId. The exact
        // case identity is checked above; the durable summary key records this host-day delivery.
    }

    private sealed class FormalRuleMailCase : IDisposable
    {
        private readonly PrtgFormalRuleCaseFixture _fixture;
        private readonly WebHost _host;
        private readonly DateTime _day;
        private readonly SystemSettingsStore _settings;
        private readonly MailNotifyStateStore _mailState;
        private readonly string _eventKey;
        private readonly string _issueKey;

        public string CaseId { get; }
        public string ExpectedSource { get; }
        public string HostName => _host.HostName;
        public string InternalRecordKey => $"{_host.HostId}|{_day:yyyy-MM-dd}";
        public int ActiveWorkOrderCount => _fixture.Backend.WorkOrderStore().GetAllActive().Count;

        private FormalRuleMailCase(PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day,
            string caseId, string expectedSource, string eventKey, string issueKey)
        {
            _fixture = fixture;
            _host = host;
            _day = day;
            CaseId = caseId;
            ExpectedSource = expectedSource;
            _eventKey = eventKey;
            _issueKey = issueKey;
            _settings = new SystemSettingsStore(fixture.Backend.Blob("system_settings"));
            _mailState = new MailNotifyStateStore(fixture.Backend.Blob("owned_smtp_formal_family_mail_state"));
        }

        public static async Task<FormalRuleMailCase> CreateAsync(string family)
        {
            var (caseId, mutations) = FirstPositiveCase(family);
            var fixture = new PrtgFormalRuleCaseFixture();
            try
            {
                WebHost host;
                DateTime day;
                string source;
                switch (family)
                {
                    case "Down":
                    case "Warning":
                    case "Flapping":
                        ConfigureStateRuleCategory(fixture, family, mutations.GetProperty("ruleCategoryFilter").GetString()!);
                        var transitions = BuildStateTransitions(fixture.AnalysisDay, family, mutations);
                        host = fixture.SeedCoveredStateCase($"owned-smtp-{family}", "Ping", "Ping", "Up",
                            _ => transitions, PrtgSensorCategories.Availability);
                        day = fixture.AnalysisDay;
                        source = family switch
                        {
                            "Down" => "PRTG:down",
                            "Warning" => "PRTG:warning",
                            _ => "PRTG:flapping"
                        };
                        break;
                    case "Silent":
                        // Mail consumes completed host days. Use the explicitly synthetic historical
                        // source proof; native capture's real-time freshness is tested separately.
                        day = fixture.AnalysisDay;
                        Assert.True(day < DateTime.Today);
                        host = fixture.SeedSilentCase();
                        fixture.StoreSyntheticHistoricalSilentProof(host);
                        source = "PRTG:silent";
                        break;
                    case "CPUFormal":
                    case "MemoryFormal":
                    case "DiskTwoHourLowWater":
                        var values = mutations.GetProperty("completedHourAverages").EnumerateArray()
                            .Select(value => value.GetDouble()).ToArray();
                        var resourceFamily = family switch
                        {
                            "CPUFormal" => PrtgResourceFamily.Cpu,
                            "MemoryFormal" => PrtgResourceFamily.Memory,
                            _ => PrtgResourceFamily.Disk
                        };
                        host = fixture.SeedFormalResourceCase(resourceFamily, values);
                        day = fixture.AnalysisDay;
                        source = family == "CPUFormal" ? "PRTG:resource_cpu_sustained_pressure" :
                            family == "MemoryFormal" ? "PRTG:resource_memory_sustained_pressure" : "PRTG:disk_free_trend";
                        break;
                    case "DiskTrend":
                        host = fixture.SeedDiskTrendCase(mutations.GetProperty("validDays").GetInt32(),
                            mutations.GetProperty("currentPercent").GetDouble(),
                            mutations.GetProperty("declinePercentPerDay").GetDouble());
                        day = fixture.AnalysisDay;
                        source = "PRTG:disk_free_trend";
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown declared formal family.");
                }

                var daily = await fixture.RunDailyAsync(host, day, captureWorkflowReceipt: true);
                var familyIssues = daily.Registry.For(host.HostId, day).Where(issue => issue.Source == source).ToArray();
                Assert.True(familyIssues.Length == 1,
                    $"{caseId} ({family}) expected one qualified Daily finding from {source}, found {familyIssues.Length}.");
                var target = Assert.Single(familyIssues);
                var recordStore = fixture.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
                var parentBeforeAi = Assert.Single(recordStore.ReadRecent(day, 1));
                Assert.Contains(parentBeforeAi.TopIssues, issue => issue.EventKey == target.EventKey && issue.Source == source);
                Assert.NotNull(parentBeforeAi.PrtgManifest);

                Assert.Equal(1, fixture.Backend.RecordStore().MarkAllForAiRerun());
                var backfiller = new DailyRecordBackfiller(fixture.Backend.CreateContext);
                backfiller.Run(CancellationToken.None);
                var ai = new FakeAiService
                {
                    NextContent = """{"risk_level":"高","headline":"Formal PRTG finding","story":"The qualified daily finding was reviewed.","trend_story":"The source evidence remains current.","action":"Review the identified resource."}"""
                };
                var aiService = new AiAnalysisHostedService(new ScheduleOptionsStore(fixture.Backend.Blob("schedule_options")),
                    new SchedulerRunState(), new AiAnalysisRunState(), fixture.Backend.RecordStore(), fixture.Backend,
                    new FakeSystemSettingsStore(), new DataVersionStamp(),
                    new BatchRunStore(fixture.Backend.LogStore("batch_runs"), fixture.Backend.LogStore("batch_run_logs")),
                    backfiller, new AvailableWebAi(), aiService: ai);
                await aiService.ExecuteProcessingLoopAsync(CancellationToken.None);
                var analyzed = Assert.Single(recordStore.ReadRecent(day, 1));
                Assert.True(analyzed.AiAnalyzed, $"{caseId} did not finish the guarded AI update.");
                Assert.False(analyzed.AiPending, $"{caseId} remains AI-pending after the guarded update.");
                Assert.Contains(analyzed.TopIssues, issue => issue.EventKey == target.EventKey && issue.Source == source);
                Assert.True(HostDayWorkflowFingerprint.HasValidPrtgManifest(analyzed),
                    $"{caseId} lost the accepted PRTG parent fence after AI.");
                var analyzedTarget = Assert.Single(analyzed.TopIssues.Where(issue => issue.EventKey == target.EventKey));
                return new FormalRuleMailCase(fixture, host, day, caseId, source, analyzedTarget.EventKey,
                    IssueSignatureKey.For(analyzedTarget));
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public (WorkOrderMemberOutcome Outcome, IssueCase Case) CreateProductionCase()
        {
            var backend = _fixture.Backend;
            var hosts = new HostStore(backend.Blob("hosts"));
            var cases = backend.IssueCaseStore();
            var issueHandlings = backend.IssueHandlingStore();
            var recordHandlings = backend.RecordHandlingStore();
            var caseCoordinator = new IssueCaseCoordinator(cases, issueHandlings, recordHandlings,
                backend.RecordStore(), hosts, new IssueOwnerStore(backend.Blob("issue_owners")));
            var coordinator = new WorkOrderCoordinator(backend.WorkOrderStore(), cases, issueHandlings,
                caseCoordinator, recordHandlings, hosts);
            var actor = new WorkOrderActor
            {
                ActorId = 991,
                ActorAccount = "owned-smtp-formal-family",
                OccurredAt = _day.AddHours(12)
            };
            var outcome = coordinator.Create(new WorkOrderCreateRequest
            {
                Source = Assert.Single(ReadParent().TopIssues.Where(issue => issue.EventKey == _eventKey)).Source,
                EventId = Assert.Single(ReadParent().TopIssues.Where(issue => issue.EventKey == _eventKey)).EventId,
                IssueLabel = CaseId,
                HandlerId = actor.ActorId ?? throw new InvalidOperationException("Owned actor ID is required."),
                Origin = WorkOrderOrigins.Manual,
                ScopeKind = WorkOrderScopes.Hosts,
                Members = [new WorkOrderMember { HostName = _host.HostName, IssueKey = _issueKey,
                    IssueLabel = CaseId, TriggerDate = _day }],
                Actor = actor
            });
            var issueCase = cases.GetOpen(_host.HostName, _issueKey)
                ?? throw new InvalidOperationException($"{CaseId} was not persisted as a production issue case.");
            Assert.Equal(_issueKey, issueCase.IssueKey);
            return (outcome, issueCase);
        }

        public IssueCase ReadPersistedProductionCase() => _fixture.Backend.IssueCaseStore()
            .GetOpen(_host.HostName, _issueKey)
            ?? throw new InvalidOperationException($"{CaseId} production issue case disappeared.");

        public void ConfigureMail(int port, bool urgent) => _settings.Update(settings =>
        {
            settings.MailEnabled = true;
            settings.MailOnRunCompleted = !urgent;
            settings.MailUrgentEnabled = urgent;
            settings.MailMinRiskLevel = urgent ? RiskLevels.High : RiskLevels.Low;
            settings.VisibleDayRiskLevels = RiskLevels.All.ToList();
            settings.SmtpServer = "127.0.0.1";
            settings.SmtpPort = port;
            settings.SmtpUseTls = false;
            settings.SmtpAccount = "";
            settings.SmtpPasswordEnc = "";
            settings.MailFrom = "logforesight@owned-smtp.invalid";
            settings.MailRecipients = [Recipient];
            settings.MailNotifyHostOwners = false;
        });

        public MailNotificationService CreateMailService()
        {
            var backend = _fixture.Backend;
            var hosts = new HostStore(backend.Blob("hosts"));
            var groups = new FakeUserGroupStore();
            var admin = groups.Upsert(new UserGroup { GroupName = "owned-smtp-formal-admin", Role = UserRole.Admin, Active = true });
            var users = new FakeUserStore();
            users.Upsert(new WebUser { Account = Recipient, Email = Recipient, Active = true, GroupIds = [admin.GroupId] });
            var handlings = backend.IssueHandlingStore();
            var cases = backend.IssueCaseStore();
            var aggregates = backend.IssueAggregateQuery(hosts);
            var digest = new MailIssueDigest(aggregates,
                new OccurrenceStatusResolver(hosts, handlings, cases, _settings), _settings,
                new FixedIssueExclusionSource(IssueExclusion.None));
            return new MailNotificationService(_settings, new SystemNetSmtpMailSender(), hosts, users,
                groups, new FakeGroupAccessStore(), backend.RecordStore(), backend.RecordHandlingStore(),
                _mailState, new ScheduleFreshnessService(new BatchRunStore(backend.LogStore("batch_runs"),
                    backend.LogStore("batch_run_logs")), new ScheduleOptionsStore(backend.Blob("schedule_options"))),
                new IssueOwnerStore(backend.Blob("issue_owners")), aggregates, digest,
                new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)), backend);
        }

        public void AssertAcceptedUrgentDelivery()
        {
            var parent = ReadParent();
            var state = ReadMailState();
            var intent = Assert.Single(state.UrgentOutbox.Values);
            Assert.Equal("smtp-accepted", intent.Status);
            Assert.Equal(parent.RecordId, intent.ParentRecordId);
            Assert.Equal(_host.HostId, intent.HostId);
            Assert.Equal(_day, intent.RecordDate);
            var facts = parent.TopIssues.Where(issue => PrtgFindingMapper.IsPrtg(issue) &&
                    !issue.Suppressed && issue.ElevatesDayRisk)
                .GroupBy(issue => $"{parent.HostId}|{issue.EventKey}|{issue.PrtgIncidentStartedAt:O}")
                .ToDictionary(group => group.Key, group => group.Max(issue => (int)issue.Severity + 10));
            var expected = InternalRecordKey;
            if (facts.Count > 0)
            {
                var material = string.Join(";", facts.OrderBy(fact => fact.Key, StringComparer.Ordinal)
                    .Select(fact => $"{fact.Key}:{fact.Value}"));
                var modeVersion = parent.TopIssues.Any(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue)
                    ? parent.PrtgManifest?.ResourceModeBlobVersion ?? -1 : -1;
                expected += (modeVersion >= 0 ? $"|mode:{modeVersion}" : string.Empty) + "|prtg:" +
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(material)));
            }
            Assert.Equal(expected, intent.Key);
            Assert.Equal(new[] { expected }, state.UrgentSentKeys);
            Assert.Equal("smtp-accepted", intent.Recipients[Recipient]);
            Assert.True(intent.SmtpAcceptedAtUtc.ContainsKey(Recipient));
            Assert.Empty(state.SummarySentKeys);
        }

        public MailNotifyState ReadMailState() => _mailState.Get();
        private DailyAnalysisRecord ReadParent() => _fixture.Backend.RecordStore(new HostKey
            { HostId = _host.HostId, HostName = _host.HostName }).ReadRecent(_day, 1).Single();
        public void Dispose() => _fixture.Dispose();

        private static (string CaseId, JsonElement Mutations) FirstPositiveCase(string family)
        {
            const string relative = "Fixtures/prtg-round53-formal-cases.json";
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                foreach (var path in new[] { Path.Combine(directory.FullName, relative),
                             Path.Combine(directory.FullName, "LogForesight.Tests", relative) })
                {
                    if (!File.Exists(path)) continue;
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    var familyRow = document.RootElement.GetProperty("families").EnumerateArray()
                        .Single(item => item.GetProperty("family").GetString() == family);
                    var first = familyRow.GetProperty("positiveCases")[0];
                    return (first.GetProperty("caseId").GetString()!, first.GetProperty("mutations").Clone());
                }
            }
            throw new FileNotFoundException("Could not locate the fixed formal PRTG rule-case manifest.", relative);
        }

        private static void ConfigureStateRuleCategory(PrtgFormalRuleCaseFixture fixture, string family, string category)
        {
            Assert.Equal(PrtgSensorCategories.Availability, category);
            var rules = KnownIssueSeed.CreateRules();
            if (family == "Down")
            {
                rules = rules.Select(rule => rule.Id == "builtin-prtg-down"
                    ? rule.CloneForSeedOverwrite(false) : rule).ToList();
            }
            else
            {
                var id = family == "Warning" ? "builtin-prtg-warning" : "builtin-prtg-flapping";
                typeof(KnownIssueRule).GetProperty(nameof(KnownIssueRule.PrtgSensorCategory))!
                    .SetValue(rules.Single(rule => rule.Id == id), category);
            }
            new KnownIssueRuleStore(fixture.Backend.Blob("rules")).Save(new RuleFileContent
                { SeedVersion = KnownIssueSeed.Version, Rules = rules });
            KnownIssueCatalog.Initialize(rules);
        }

        private static IReadOnlyList<(DateTime At, string Status)> BuildStateTransitions(DateTime day,
            string family, JsonElement mutations)
        {
            if (family == "Down")
            {
                var minutes = mutations.GetProperty("durationMinutes").GetInt32();
                return [(day.AddHours(1), "Down"), (day.AddHours(1).AddMinutes(minutes), "Up")];
            }
            if (family == "Warning")
            {
                var minutes = mutations.GetProperty("warningMinutes").GetInt32();
                return [(day.AddHours(1), "Warning"), (day.AddHours(1).AddMinutes(minutes), "Up")];
            }
            var roundTrips = mutations.GetProperty("roundTrips").GetInt32();
            return Enumerable.Range(0, roundTrips).SelectMany(index => new[]
            {
                (At: day.AddHours(2 + index * 2), Status: "Down"),
                (At: day.AddHours(3 + index * 2), Status: "Up")
            }).ToArray();
        }
    }

    private sealed class FormalCpuCase : IDisposable
    {
        private readonly PrtgFormalRuleCaseFixture _fixture;
        private readonly WebHost _host;
        private readonly DateTime _day;
        private readonly SystemSettingsStore _settings;
        private readonly IssueOwnerStore _issueOwners;
        private readonly MailNotifyStateStore _mailState;

        public string InternalRecordKey => $"{_host.HostId}|{_day:yyyy-MM-dd}";
        public string HostName => _host.HostName;
        public string TargetEventKey => Assert.Single(_fixture.Backend.RecordStore(new HostKey
            { HostId = _host.HostId, HostName = _host.HostName }).ReadRecent(_day, 1).Single().TopIssues
            .Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue)).EventKey;

        public static async Task<FormalCpuCase> CreateAsync(bool elevateCpuRisk = false, bool highRiskNetIqBaseline = false)
        {
            var fixture = new PrtgFormalRuleCaseFixture();
            try
            {
            if (elevateCpuRisk) fixture.UseElevatingCpuFormalRule();
            if (highRiskNetIqBaseline) fixture.UseHighRiskNetIqBaseline();
            var day = fixture.AnalysisDay.Date;
            var host = fixture.SeedFormalResourceCase(PrtgResourceFamily.Cpu, elevateCpuRisk ? [99d, 99d] : [90d, 90d]);
            var daily = await fixture.RunDailyAsync(host, day, captureWorkflowReceipt: true);
            var target = Assert.Single(daily.Registry.For(host.HostId, day)
                .Where(issue => issue.Source == "PRTG:resource_cpu_sustained_pressure"));
            var parentStore = fixture.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
            var parent = Assert.Single(parentStore.ReadRecent(day, 1));
            Assert.Contains(parent.TopIssues, issue => issue.EventKey == target.EventKey &&
                issue.Source == target.Source && issue.SampleMessages.SequenceEqual(target.SampleMessages));
            Assert.NotNull(parent.PrtgManifest);
            Assert.True(parent.PrtgManifest!.ResourceModeFenceRequired);
            Assert.True(parent.PrtgManifest.ResourceModeBlobVersion >= 0);

            // The daily fixture records the qualified PRTG evidence first. Marking the synthetic
            // parent for the normal hosted consumer exercises its guarded write against that evidence.
            var markCount = fixture.Backend.RecordStore().MarkAllForAiRerun();
            Assert.Equal(1, markCount);
            var ai = new FakeAiService
            {
                NextContent = """{"risk_level":"高","headline":"CPU resource pressure","story":"Formal CPU pressure reviewed.","trend_story":"Pressure persisted across completed hours.","action":"Review CPU capacity."}"""
            };
            var backend = fixture.Backend;
            var backfiller = new DailyRecordBackfiller(backend.CreateContext);
            backfiller.Run(CancellationToken.None);
            var aiService = new AiAnalysisHostedService(new ScheduleOptionsStore(backend.Blob("schedule_options")),
                new SchedulerRunState(), new AiAnalysisRunState(), backend.RecordStore(), backend,
                new FakeSystemSettingsStore(), new DataVersionStamp(),
                new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs")),
                backfiller, new AvailableWebAi(), aiService: ai);
            await aiService.ExecuteProcessingLoopAsync(CancellationToken.None);
            var analyzed = Assert.Single(parentStore.ReadRecent(day, 1));
            Assert.True(analyzed.AiAnalyzed);
            Assert.False(analyzed.AiPending);
            if (elevateCpuRisk) Assert.Equal(RiskLevels.High, analyzed.RiskLevel);
            Assert.Contains(analyzed.TopIssues, issue => issue.EventKey == target.EventKey &&
                issue.Source == target.Source);
            using (var context = backend.CreateContext())
                Assert.True(PrtgResourceFormalDeliveryFence.TryValidateCurrentStart(context, host.HostId,
                    analyzed.RecordId, analyzed.TopIssues.Single(issue => issue.EventKey == target.EventKey),
                    null, out _));
            return new FormalCpuCase(host, day, fixture);
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        private FormalCpuCase(WebHost host, DateTime day, PrtgFormalRuleCaseFixture fixture)
        {
            _fixture = fixture;
            _host = host;
            _day = day;
            _settings = new SystemSettingsStore(_fixture.Backend.Blob("system_settings"));
            _issueOwners = new IssueOwnerStore(_fixture.Backend.Blob("issue_owners"));
            _mailState = new MailNotifyStateStore(_fixture.Backend.Blob("owned_smtp_mail_state"));
        }

        public void ConfigureMail(int port, bool urgent = false)
        {
            _settings.Update(settings =>
            {
                settings.MailEnabled = true;
                settings.MailOnRunCompleted = !urgent;
                settings.MailUrgentEnabled = urgent;
                settings.MailMinRiskLevel = urgent ? RiskLevels.High : RiskLevels.Low;
                settings.VisibleDayRiskLevels = RiskLevels.All.ToList();
                settings.SmtpServer = "127.0.0.1";
                settings.SmtpPort = port;
                settings.SmtpUseTls = false;
                settings.SmtpAccount = "";
                settings.SmtpPasswordEnc = "";
                settings.MailFrom = "logforesight@owned-smtp.invalid";
                settings.MailRecipients = urgent
                    ? new List<string> { Recipient, SecondRecipient }
                    : new List<string> { Recipient };
                settings.MailNotifyHostOwners = false;
            });
        }

        public void RevokeFormalMode()
        {
            var authorization = new PrtgResourcePressureAuthorizationService(_fixture.Backend, _settings);
            Assert.True(authorization.DisableFormalMode(_host.HostId, PrtgFormalRuleCaseFixture.SensorId,
                true, DateTime.UtcNow));
            var record = Assert.Single(_fixture.Backend.RecordStore(new HostKey
                { HostId = _host.HostId, HostName = _host.HostName }).ReadRecent(_day, 1));
            var issue = Assert.Single(record.TopIssues.Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue));
            using var context = _fixture.Backend.CreateContext();
            Assert.False(PrtgResourceFormalDeliveryFence.TryValidateCurrentStart(context, _host.HostId,
                record.RecordId, issue, null, out _));
        }

        public void DisableMailDelivery() => _settings.Update(settings => settings.MailEnabled = false);

        public PrtgFormalMailClaimShard? ReadFormalMailClaimShard()
        {
            using var context = _fixture.Backend.CreateContext();
            var prefix = $"prtg_formal_mail_claims_v3_{_host.HostId.ToString(System.Globalization.CultureInfo.InvariantCulture)}_";
            var raw = context.Blobs.Where(row => row.BlobKey.StartsWith(prefix))
                .OrderByDescending(row => row.UpdatedAt).Select(row => row.Content).FirstOrDefault();
            return raw == null ? null : JsonSerializer.Deserialize<PrtgFormalMailClaimShard>(raw, LfJsonOptions.Pretty);
        }

        public string CreateProductionCase()
        {
            var record = Assert.Single(_fixture.Backend.RecordStore(new HostKey
                { HostId = _host.HostId, HostName = _host.HostName }).ReadRecent(_day, 1));
            var issue = Assert.Single(record.TopIssues.Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue));
            var issueKey = IssueSignatureKey.For(issue);
            var backend = _fixture.Backend;
            var hosts = new HostStore(backend.Blob("hosts"));
            var cases = backend.IssueCaseStore();
            var issueHandlings = backend.IssueHandlingStore();
            var recordHandlings = backend.RecordHandlingStore();
            var issueOwners = new IssueOwnerStore(backend.Blob("issue_owners"));
            var caseCoordinator = new IssueCaseCoordinator(cases, issueHandlings, recordHandlings,
                backend.RecordStore(), hosts, issueOwners);
            var coordinator = new WorkOrderCoordinator(backend.WorkOrderStore(), cases, issueHandlings,
                caseCoordinator, recordHandlings, hosts);
            var actor = new WorkOrderActor
            {
                ActorId = 991,
                ActorAccount = "owned-smtp-acceptance",
                OccurredAt = DateTime.UtcNow
            };
            var outcome = coordinator.Create(new WorkOrderCreateRequest
            {
                Source = issue.Source,
                EventId = issue.EventId,
                IssueLabel = issue.Source,
                HandlerId = 991,
                Origin = WorkOrderOrigins.Manual,
                ScopeKind = WorkOrderScopes.Hosts,
                Members = [new WorkOrderMember
                {
                    HostName = _host.HostName,
                    IssueKey = issueKey,
                    IssueLabel = issue.Source,
                    TriggerDate = _day
                }],
                Actor = actor
            });
            Assert.True(outcome.CreatedOrder);
            Assert.Equal(1, outcome.NewCases);
            var created = cases.GetOpen(_host.HostName, issueKey)
                ?? throw new InvalidOperationException("WorkOrderCoordinator did not persist the expected issue case.");
            Assert.Equal(issueKey, created.IssueKey);
            var dayState = Assert.Single(issueHandlings.GetForDay(_host.HostName, _day)
                .Where(item => item.IssueKey == issueKey));
            Assert.Equal(created.CaseId, dayState.CaseId);
            return created.CaseId;
        }

        public IssueCase ReadPersistedProductionCase()
        {
            var issueKey = IssueSignatureKey.For(Assert.Single(_fixture.Backend.RecordStore(new HostKey
                { HostId = _host.HostId, HostName = _host.HostName }).ReadRecent(_day, 1).Single().TopIssues
                .Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue)));
            return _fixture.Backend.IssueCaseStore().GetOpen(_host.HostName, issueKey)
                ?? throw new InvalidOperationException("The production issue case was not persisted.");
        }

        public MailNotificationService CreateMailService()
        {
            var backend = _fixture.Backend;
            var hosts = new HostStore(backend.Blob("hosts"));
            var groups = new FakeUserGroupStore();
            var admin = groups.Upsert(new UserGroup { GroupName = "owned-smtp-admin", Role = UserRole.Admin, Active = true });
            var users = new FakeUserStore();
            users.Upsert(new WebUser { Account = Recipient, Email = Recipient, Active = true, GroupIds = [admin.GroupId] });
            users.Upsert(new WebUser { Account = SecondRecipient, Email = SecondRecipient, Active = true, GroupIds = [admin.GroupId] });
            var groupAccess = new FakeGroupAccessStore();
            var aggregates = backend.IssueAggregateQuery(hosts);
            var handlings = backend.IssueHandlingStore();
            var cases = backend.IssueCaseStore();
            var digest = new MailIssueDigest(aggregates,
                new OccurrenceStatusResolver(hosts, handlings, cases, _settings), _settings,
                new FixedIssueExclusionSource(IssueExclusion.None));
            return new MailNotificationService(_settings, new SystemNetSmtpMailSender(), hosts, users,
                groups, groupAccess, backend.RecordStore(), backend.RecordHandlingStore(), _mailState,
                new ScheduleFreshnessService(new BatchRunStore(backend.LogStore("batch_runs"),
                        backend.LogStore("batch_run_logs")), new ScheduleOptionsStore(backend.Blob("schedule_options"))),
                _issueOwners, aggregates, digest,
                new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)), backend);
        }

        public MailNotifyState ReadMailState() => _mailState.Get();
        public DailyAnalysisRecord ReadParent() => _fixture.Backend.RecordStore(new HostKey
            { HostId = _host.HostId, HostName = _host.HostName }).ReadRecent(_day, 1).Single();
        public void Dispose() => _fixture.Dispose();
    }

    private sealed class AvailableWebAi : IWebAiService
    {
        public bool Available => true;
        public Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt) where T : class =>
            Task.FromResult<T?>(null);
        public Task<string?> ChatOnceAsync(string systemPrompt, string userPrompt) => Task.FromResult<string?>(null);
    }

    private sealed record CapturedMail(IReadOnlyList<string> EnvelopeRecipients, string RawMime);

    /// <summary>Loopback-only SMTP sink with per-command and total DATA limits.</summary>
    private sealed class OwnedSmtpInbox : IAsyncDisposable
    {
        private const int MaxCommandBytes = 64 * 1024;
        private const int MaxMessageBytes = 256 * 1024;
        private const int MaxCommandsPerConnection = 64;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        private readonly ConcurrentQueue<CapturedMail> _messages = new();
        private readonly ConcurrentQueue<string> _refusals = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly Task _acceptLoop;
        private readonly string? _refuseNextRecipient;
        private int _refusalAvailable;

        public OwnedSmtpInbox(string? refuseNextRecipient = null)
        {
            _refuseNextRecipient = refuseNextRecipient;
            _refusalAvailable = string.IsNullOrWhiteSpace(refuseNextRecipient) ? 0 : 1;
            _listener.Start(2);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync(_stop.Token);
        }

        public int Port { get; }
        public IReadOnlyList<CapturedMail> Messages => _messages.ToArray();
        public IReadOnlyList<string> Refusals => _refusals.ToArray();

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    client.NoDelay = true;
                    try { await ServeAsync(client, ct).ConfigureAwait(false); }
                    catch (IOException ex) when (IsClientDisconnect(ex))
                    {
                        // SmtpClient may close immediately after QUIT without waiting for 221.
                        // Treat that as a per-connection teardown and keep accepting the next
                        // recipient's independent SMTP transaction; protocol/data-limit errors
                        // have no transport SocketException and still escape.
                    }
                    catch (SocketException ex) when (IsClientDisconnect(ex))
                    {
                        // Some platforms surface a client reset without the NetworkStream
                        // IOException wrapper.
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
            catch (SocketException) when (ct.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken ct)
        {
            using var stream = client.GetStream();
            await WriteLineAsync(stream, "220 owned-loopback.invalid ESMTP", ct).ConfigureAwait(false);
            var recipients = new List<string>();
            for (var commandCount = 0; commandCount < MaxCommandsPerConnection; commandCount++)
            {
                var command = await ReadLineAsync(stream, MaxCommandBytes, ct).ConfigureAwait(false);
                if (command == null) return;
                if (command.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase) ||
                    command.StartsWith("HELO ", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteLineAsync(stream, "250-owned-loopback.invalid", ct).ConfigureAwait(false);
                    await WriteLineAsync(stream, "250 SIZE 262144", ct).ConfigureAwait(false);
                }
                else if (command.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase))
                    await WriteLineAsync(stream, "250 sender accepted", ct).ConfigureAwait(false);
                else if (command.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                {
                    var recipient = ExtractAddress(command["RCPT TO:".Length..]);
                    if (string.Equals(recipient, _refuseNextRecipient, StringComparison.OrdinalIgnoreCase) &&
                        Interlocked.CompareExchange(ref _refusalAvailable, 0, 1) == 1)
                    {
                        _refusals.Enqueue(recipient);
                        await WriteLineAsync(stream, "451 transient test refusal", ct).ConfigureAwait(false);
                    }
                    else
                    {
                        recipients.Add(recipient);
                        await WriteLineAsync(stream, "250 recipient accepted", ct).ConfigureAwait(false);
                    }
                }
                else if (command.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    if (recipients.Count == 0)
                    {
                        await WriteLineAsync(stream, "503 no accepted recipients", ct).ConfigureAwait(false);
                        continue;
                    }
                    await WriteLineAsync(stream, "354 end with dot", ct).ConfigureAwait(false);
                    var mime = await ReadDataAsync(stream, MaxMessageBytes, ct).ConfigureAwait(false);
                    var acceptedRecipients = recipients.ToArray();
                    await WriteLineAsync(stream, "250 queued", ct).ConfigureAwait(false);
                    // Capture only after the SMTP acceptance response is successfully written.
                    _messages.Enqueue(new CapturedMail(acceptedRecipients, mime));
                    _signal.Release();
                    recipients.Clear();
                }
                else if (command.Equals("RSET", StringComparison.OrdinalIgnoreCase))
                {
                    recipients.Clear();
                    await WriteLineAsync(stream, "250 reset", ct).ConfigureAwait(false);
                }
                else if (command.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteLineAsync(stream, "221 closing", ct).ConfigureAwait(false);
                    return;
                }
                else
                    await WriteLineAsync(stream, "250 ok", ct).ConfigureAwait(false);
            }
            throw new InvalidDataException("Loopback SMTP command count exceeded its bound.");
        }

        private static async Task<string?> ReadLineAsync(NetworkStream stream, int maxBytes, CancellationToken ct)
        {
            var bytes = new List<byte>(128);
            var one = new byte[1];
            while (bytes.Count <= maxBytes)
            {
                var count = await stream.ReadAsync(one, ct).ConfigureAwait(false);
                if (count == 0) return bytes.Count == 0 ? null : throw new EndOfStreamException("SMTP line ended without CRLF.");
                if (one[0] == (byte)'\n')
                {
                    if (bytes.Count == 0 || bytes[^1] != (byte)'\r')
                        throw new InvalidDataException("SMTP line did not use CRLF.");
                    bytes.RemoveAt(bytes.Count - 1);
                    return Encoding.Latin1.GetString(bytes.ToArray());
                }
                bytes.Add(one[0]);
            }
            throw new InvalidDataException("SMTP command line exceeded its byte limit.");
        }

        private static async Task<string> ReadDataAsync(NetworkStream stream, int maxBytes, CancellationToken ct)
        {
            var data = new StringBuilder();
            var total = 0;
            while (true)
            {
                var line = await ReadLineAsync(stream, Math.Min(MaxCommandBytes, maxBytes - total), ct).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("SMTP DATA ended before its terminator.");
                if (line == ".") return data.ToString();
                var unstuffed = line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line;
                total = checked(total + Encoding.Latin1.GetByteCount(unstuffed) + 2);
                if (total > maxBytes) throw new InvalidDataException("SMTP MIME payload exceeded its byte limit.");
                data.Append(unstuffed).Append("\r\n");
            }
        }

        private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken ct)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static string ExtractAddress(string value)
        {
            var left = value.IndexOf('<');
            var right = value.IndexOf('>');
            return left >= 0 && right > left ? value[(left + 1)..right].Trim() : value.Trim();
        }

        private static bool IsClientDisconnect(Exception error)
        {
            for (Exception? current = error; current != null; current = current.InnerException)
            {
                if (current is SocketException socket && socket.SocketErrorCode is
                    SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown or
                    SocketError.NetworkReset or SocketError.NotConnected or SocketError.OperationAborted)
                    return true;
            }
            return false;
        }

        public async Task WaitForMessagesAsync(int count, CancellationToken ct)
        {
            while (_messages.Count < count) await _signal.WaitAsync(ct).ConfigureAwait(false);
        }

        public async Task WaitForRefusalsAsync(int count, CancellationToken ct)
        {
            while (_refusals.Count < count) await Task.Delay(20, ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _signal.Dispose();
            _stop.Dispose();
        }
    }

    private static string ExtractTextBody(string rawMime)
    {
        var separator = rawMime.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator < 0) return rawMime;
        var headers = rawMime[..separator];
        var body = rawMime[(separator + 4)..];
        var transfer = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("Content-Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))?
            .Split(':', 2)[1].Trim();
        return transfer?.ToLowerInvariant() switch
        {
            "base64" => Encoding.UTF8.GetString(Convert.FromBase64String(body.Replace("\r\n", "", StringComparison.Ordinal))),
            "quoted-printable" => DecodeQuotedPrintable(body),
            _ => body
        };
    }

    private static string DecodeQuotedPrintable(string body)
    {
        var source = Encoding.Latin1.GetBytes(body);
        using var decoded = new MemoryStream(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != '=') { decoded.WriteByte(source[i]); continue; }
            if (i + 2 < source.Length && source[i + 1] == '\r' && source[i + 2] == '\n') { i += 2; continue; }
            if (i + 2 < source.Length && byte.TryParse(Encoding.ASCII.GetString(source, i + 1, 2),
                    System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
            { decoded.WriteByte(value); i += 2; }
            else decoded.WriteByte(source[i]);
        }
        return Encoding.UTF8.GetString(decoded.ToArray());
    }
}
