using System.Globalization;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace LogForesight.Web.Services;

/// <summary>Durable, single-General-lane read-only channel discovery. It never publishes a profile or proof.</summary>
public sealed class PrtgTrustedSamplingChannelDiscoveryHostedService : BackgroundService
{
    public static readonly TimeSpan JobDeadline = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LaneLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(1);
    private const int MaxResponseBytes = 128 * 1024;
    private readonly StorageBackend backend;
    private readonly IHostStore hosts;
    private readonly ILogger<PrtgTrustedSamplingChannelDiscoveryHostedService> logger;
    private readonly Func<SystemSettings, PrtgClient> clientFactory;
    private readonly IServiceScopeFactory? scopeFactory;
    private readonly PrtgChannelDiscoveryJobStore store;
    private readonly string owner = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim wake = new(0, 1);

    // Test seam only: accepts a fixed plan for transport-level worker tests without
    // claiming the fixture is real source-capacity evidence. Production always rebuilds admission.
    internal Func<SystemSettings, PrtgSnapshotTargetSelection,
        (PrtgCapacityAdmissionPlan? Plan, string Reason)>? AdmissionOverride { get; set; }

    public PrtgTrustedSamplingChannelDiscoveryHostedService(StorageBackend backend, IHostStore hosts,
        ILogger<PrtgTrustedSamplingChannelDiscoveryHostedService> logger, IServiceScopeFactory scopeFactory)
        : this(backend, hosts, logger, settings => PrtgClientFactory.Create(settings), scopeFactory) { }

    internal PrtgTrustedSamplingChannelDiscoveryHostedService(StorageBackend backend, IHostStore hosts,
        ILogger<PrtgTrustedSamplingChannelDiscoveryHostedService> logger,
        Func<SystemSettings, PrtgClient> clientFactory, IServiceScopeFactory? scopeFactory = null)
    {
        this.backend = backend;
        this.hosts = hosts;
        this.logger = logger;
        this.clientFactory = clientFactory;
        this.scopeFactory = scopeFactory;
        store = new PrtgChannelDiscoveryJobStore(backend);
    }

    public PrtgChannelDiscoveryJobStore.Job Queue(PrtgChannelDiscoveryJobStore.Job job)
    {
        var accepted = store.Queue(job, DateTimeOffset.UtcNow);
        try { wake.Release(); } catch (SemaphoreFullException) { }
        return accepted;
    }

    public PrtgChannelDiscoveryJobStore.Job? Read(string jobId)
    {
        store.Expire(DateTimeOffset.UtcNow);
        return store.Read(jobId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                store.Expire(DateTimeOffset.UtcNow);
                var job = store.TryAcquire(owner, DateTimeOffset.UtcNow, LaneLease);
                if (job is null)
                {
                    await wake.WaitAsync(PollDelay, stoppingToken);
                    continue;
                }
                await ProcessLeasedAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Read-only PRTG channel discovery stopped safely: {Reason}", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    internal async Task<bool> RunOneAsync(CancellationToken ct)
    {
        store.Expire(DateTimeOffset.UtcNow);
        var job = store.TryAcquire(owner, DateTimeOffset.UtcNow, LaneLease);
        if (job is null) return false;
        await ProcessLeasedAsync(job, ct);
        return true;
    }

    private async Task ProcessLeasedAsync(PrtgChannelDiscoveryJobStore.Job job, CancellationToken stoppingToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var remaining = job.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            store.Fail(job, DateTimeOffset.UtcNow, "expired", "deadline-reached");
            return;
        }
        operation.CancelAfter(remaining);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = RenewLeaseAsync(job, operation, heartbeatStop.Token);
        try
        {
            if (!HasCurrentFences(job, out var settings, out var policy, out var identity, out var binding,
                    out var hostVersion, out var reason))
            {
                store.Fail(job, DateTimeOffset.UtcNow, "failed-stale", reason);
                return;
            }

            var hostSnapshot = hosts.CapturePrtgSnapshot();
            var selection = PrtgSnapshotTargetResolver.Resolve(backend, hosts, settings,
                new SentinelStore(backend.Blob("sentinels")).GetAll(), policy);
            PrtgCapacityAdmissionPlan? plan;
            string capacityReason;
            if (AdmissionOverride is { } admissionOverride)
                (plan, capacityReason) = admissionOverride(settings, selection);
            else
                PrtgCapacityRuntimeAdmission.TryGetCurrent(backend, hosts, settings, selection, out plan, out capacityReason);
            if (plan is null)
            {
                store.Defer(job, DateTimeOffset.UtcNow, capacityReason, TimeSpan.FromSeconds(5));
                return;
            }

            using var client = clientFactory(settings);
            client.RequestPurpose = PrtgRequestPurpose.General;
            client.AdmissionPlanFingerprint = plan.Fingerprint;
            var json = await client.GetBoundedJsonAsync(
                $"api/table.json?content=channels&id={job.SensorObjid}&columns=objid,name,lastvalue_raw,unit&usecaption=1&count=100",
                MaxResponseBytes, operation.Token);
            var channels = ParseChannels(json, out var truncated);

            if (!HasCurrentFences(job, out _, out _, out _, out _, out var finalHostVersion, out var finalReason) ||
                hostSnapshot.Version != finalHostVersion || hostVersion != finalHostVersion)
            {
                store.Fail(job, DateTimeOffset.UtcNow, "failed-stale", finalReason.Length == 0 ? "host-snapshot-changed" : finalReason);
                return;
            }
            store.Complete(job, DateTimeOffset.UtcNow, channels, truncated);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            var now = DateTimeOffset.UtcNow;
            if (job.DeadlineUtc <= now) store.Fail(job, now, "expired", "deadline-reached");
            else store.Fail(job, now, "failed", "read-only-discovery-timeout-or-lease-lost");
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("prtg-capacity-admission-", StringComparison.Ordinal))
        { store.Defer(job, DateTimeOffset.UtcNow, "capacity-plan-changed", TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or HttpRequestException or PrtgClientException or TimeoutException)
        {
            logger.LogWarning("Read-only PRTG channel discovery failed for sensor {SensorObjid}: {Reason}", job.SensorObjid, ex.GetType().Name);
            store.Fail(job, DateTimeOffset.UtcNow, "failed", ex is InvalidDataException or JsonException ? "channel-response-shape-invalid" : "source-unavailable");
        }
        finally
        {
            heartbeatStop.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
    }

    private async Task RenewLeaseAsync(PrtgChannelDiscoveryJobStore.Job job, CancellationTokenSource operation,
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(8));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            if (!store.Renew(job, DateTimeOffset.UtcNow, LaneLease))
            { operation.Cancel(); return; }
    }

    private bool HasCurrentFences(PrtgChannelDiscoveryJobStore.Job job, out SystemSettings settings,
        out PrtgMonitoringPolicy policy, out PrtgResourceIdentity identity,
        out PrtgTrustedSamplingBinding? binding, out long hostVersion, out string reason)
    {
        settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        hostVersion = hosts.CapturePrtgSnapshot().Version;
        binding = new PrtgTrustedSamplingBindingStore(backend).Get(job.SensorObjid);
        var identities = backend.PrtgStore().GetResourceIdentities([job.SensorObjid]);
        identity = identities.GetValueOrDefault(job.SensorObjid)!;
        if (identity is null || !identity.Active || identity.PendingReconciliation || !settings.PrtgEnabled ||
            settings.Revision != job.SettingsRevision || policy.Revision != job.PolicyRevision ||
            policy.SourceGeneration != job.SourceGeneration || !policy.Ready(settings.PrtgUrl) ||
            !policy.SensorIds.Contains(job.SensorObjid) || !policy.HostIds.Contains(identity.HostId) ||
            identity.SourceGeneration != job.SourceGeneration || identity.Generation != job.ResourceGeneration ||
            identity.Epoch != job.IdentityEpoch || identity.ChannelGeneration != job.ChannelGeneration ||
            hostVersion != job.HostSnapshotVersion || (binding?.BindingRevision ?? 0) != job.BindingRevision ||
            (binding?.BindingFingerprint ?? "") != job.BindingFingerprint ||
            identity.ChannelFingerprint != job.IdentityChannelFingerprint)
        { reason = "source-binding-policy-or-host-fence-changed"; return false; }
        if (!RequesterStillAuthorized(job, identity, policy))
        { reason = "requester-capability-or-host-visibility-revoked"; return false; }
        reason = "";
        return true;
    }

    private bool RequesterStillAuthorized(PrtgChannelDiscoveryJobStore.Job job,
        PrtgResourceIdentity identity, PrtgMonitoringPolicy policy)
    {
        if (job.RequesterWasServerAdmin) return job.RequesterUserId == 0;
        if (job.RequesterUserId <= 0 || scopeFactory is null) return false;
        var user = new UserStore(backend.Blob("users"), backend.Blob("user_last_login")).Get(job.RequesterUserId);
        if (user is null || !user.Active) return false;
        var userGroups = new UserGroupStore(backend.Blob("user_groups"));
        var issueOwners = new IssueOwnerStore(backend.Blob("issue_owners"));
        if (!new UserCapabilityResolver(userGroups, hosts, issueOwners).Resolve(user).Contains(Capability.Maintain))
            return false;
        using var scope = scopeFactory.CreateScope();
        var visibility = scope.ServiceProvider.GetRequiredService<IVisibilityService>();
        return visibility.GetVisibleHostIdsFor(job.RequesterUserId).Contains(identity.HostId) &&
            !policy.HostIds.Any(visibility.IsCaseGrantOnly);
    }

    private static List<PrtgChannelDiscoveryJobStore.Channel> ParseChannels(string json, out bool truncated)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("channels", out var rows) ||
            rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 100)
            throw new InvalidDataException("channel-list-shape-invalid");
        truncated = rows.GetArrayLength() == 100;
        var output = new List<PrtgChannelDiscoveryJobStore.Channel>(rows.GetArrayLength());
        var seen = new HashSet<long>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("objid", out var idToken) ||
                !TryPositiveInt64(idToken, out var id) || !seen.Add(id) ||
                !row.TryGetProperty("name", out var captionToken) || captionToken.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("channel-list-row-invalid");
            var caption = captionToken.GetString()!;
            var unit = row.TryGetProperty("unit", out var unitToken) && unitToken.ValueKind == JsonValueKind.String
                ? unitToken.GetString()! : "";
            if (caption.Length > 512 || unit.Length > 128) throw new InvalidDataException("channel-list-field-cap-exceeded");
            double? raw = null;
            if (row.TryGetProperty("lastvalue_raw", out var rawToken) && rawToken.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                var text = rawToken.ValueKind == JsonValueKind.String ? rawToken.GetString() : rawToken.GetRawText();
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                    throw new InvalidDataException("channel-list-raw-value-invalid");
                raw = value;
            }
            output.Add(new() { ChannelObjectId = id, Caption = caption, Unit = unit, RawValue = raw });
        }
        return output;
    }

    private static bool TryPositiveInt64(JsonElement token, out long value)
    {
        value = 0;
        if (token.ValueKind == JsonValueKind.Number) return token.TryGetInt64(out value) && value > 0;
        return token.ValueKind == JsonValueKind.String && long.TryParse(token.GetString(), NumberStyles.None,
            CultureInfo.InvariantCulture, out value) && value > 0;
    }
}
