using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>單次 PRTG probe 的快照，供狀態 API 一次性讀出</summary>
public record PrtgProbeSnapshot(
    bool IsRunning, DateTime? StartedAt, DateTime? CompletedAt,
    bool? Success, string? LatestMessage, IReadOnlyList<string> Output,
    bool Cancelled = false, string? EvidenceJson = null);

/// <summary>
/// PRTG probe 的行程內單例執行狀態＋併發 1 的 gate。
/// </summary>
public class PrtgProbeRunState
{
    private readonly object _lock = new();
    private readonly List<string> _output = new();

    private bool _isRunning;
    private DateTime? _startedAt;
    private DateTime? _completedAt;
    private bool? _success;
    private string? _latestMessage;
    private string? _evidenceJson;

    private CancellationTokenSource? _cts;
    private bool _cancelled;

    /// <summary>最近一趟是否被使用者停止（新一趟開始時歸零）。</summary>
    public bool Cancelled
    {
        get { lock (_lock) return _cancelled; }
    }

    /// <summary>
    /// 搶執行權並建立本趟的取消來源。已在執行中回 false。
    /// </summary>
    public bool TryBeginRun(out CancellationToken token)
    {
        lock (_lock)
        {
            if (!TryBegin())
            {
                token = default;
                return false;
            }
            _cts = new CancellationTokenSource();
            _cancelled = false;
            token = _cts.Token;
            return true;
        }
    }

    /// <summary>要求停止進行中的探測；沒有執行中時回 false。</summary>
    public bool TryCancel()
    {
        lock (_lock)
        {
            if (_cts == null || !_isRunning) return false;
            _evidenceJson = null;
            _cts.Cancel();
            return true;
        }
    }

    /// <summary>結束本趟：記住是否被停止、釋放並清空取消來源，再結束執行狀態。</summary>
    public void FinishRun(bool success, bool cancelled, string? cancelledEvidenceJson = null)
    {
        lock (_lock)
        {
            _cancelled = cancelled;
            if (cancelled) _evidenceJson = cancelledEvidenceJson;
            _cts?.Dispose();
            _cts = null;
        }
        EndRun(success);
    }

    public bool TryBegin()
    {
        lock (_lock)
        {
            if (_isRunning) return false;
            _isRunning = true;
            _startedAt = DateTime.Now;
            _completedAt = null;
            _success = null;
            _latestMessage = null;
            _output.Clear();
            _evidenceJson = null;
            return true;
        }
    }

    public void AppendLine(string message)
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            _output.Add(message);
            if (!string.IsNullOrWhiteSpace(message)) _latestMessage = message;
        }
    }

    public void SetEvidenceJson(string? evidenceJson)
    {
        lock (_lock)
        {
            if (_isRunning) _evidenceJson = evidenceJson;
        }
    }

    public void EndRun(bool success)
    {
        lock (_lock)
        {
            _isRunning = false;
            _success = success;
            _completedAt = DateTime.Now;
        }
    }

    public PrtgProbeSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new PrtgProbeSnapshot(
                _isRunning, _startedAt, _completedAt, _success,
                _latestMessage, _output.ToList(), _cancelled, _evidenceJson);
        }
    }
}

/// <summary>
/// 極薄的 IRunConsole adapter：將探測輸出逐行收集至 <see cref="PrtgProbeRunState"/>。
/// </summary>
public class PrtgProbeConsole : IRunConsole
{
    private readonly PrtgProbeRunState _state;

    public PrtgProbeConsole(PrtgProbeRunState state) => _state = state;

    public void WriteLine(string message = "") => _state.AppendLine(message);
}

/// <summary>
/// PRTG 環境探測服務：Singleton，背景執行 PRTG 探測任務並維護狀態。
/// </summary>
public class PrtgProbeService
{
    private readonly ISystemSettingsStore _settings;
    private readonly PrtgProbeRunState _state;
    // 必要相依，不設預設值：探測與回填會打同一台 PRTG，這道互斥是保護。
    // 做成可選參數的話，哪天有人漏注入，保護會靜默消失而不是編譯失敗。
    private readonly PrtgBackfillRunState _backfillState;
    private readonly SchedulerRunState _schedulerState;
    private readonly PrtgStructureSyncRunState _structureState;
    // 站台對照要對照的三個本機事實來源：鏡像、主機清單、Sentinel 清單。
    // 同樣不設預設值——漏注入的話這一段會靜默消失，而不是編譯失敗。
    private readonly StorageBackend _backend;
    private readonly IHostStore _hosts;
    private readonly ISentinelStore _sentinels;
    private readonly IAiProbeService? _aiProbe;
    internal Func<SystemSettings, PrtgClient>? ClientFactoryForTesting { get; set; }

    public PrtgProbeService(ISystemSettingsStore settings, PrtgProbeRunState state, PrtgBackfillRunState backfillState,
        StorageBackend backend, IHostStore hosts, ISentinelStore sentinels,
        SchedulerRunState schedulerState, PrtgStructureSyncRunState structureState, IAiProbeService? aiProbe = null)
    {
        _settings = settings;
        _state = state;
        _backfillState = backfillState;
        _schedulerState = schedulerState;
        _structureState = structureState;
        _aiProbe = aiProbe;
        _backend = backend;
        _hosts = hosts;
        _sentinels = sentinels;
    }

    public PrtgProbeStatusDto GetStatus()
    {
        var s = _state.Snapshot();
        return new PrtgProbeStatusDto
        {
            IsRunning = s.IsRunning,
            StartedAt = s.StartedAt,
            CompletedAt = s.CompletedAt,
            Success = s.Success,
            LatestMessage = s.LatestMessage,
            Output = s.Output,
            Cancelled = s.Cancelled,
            EvidenceJson = s.EvidenceJson
        };
    }

    /// <summary>Read-only local diagnostics; does not create a client, contact PRTG/AI, or update probe readiness.</summary>
    public async Task<string> GetLocalEvidenceAsync(CancellationToken ct)
    {
        var settings = _settings.Get();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        var assembly = typeof(PrtgProbeService).Assembly;
        var buildVersion = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var build = LogForesightBuildIdentity.ReadCurrent(assembly, typeof(PrtgClient).Assembly);
        using var db = _backend.CreateContext();
        var provider = db.Database.ProviderName;
        var storage = await PrtgStorageEnvironmentProbe.CollectAsync(db, _backend.DataRoot, token);
        var facts = await PrtgProbeLocalFactsCollector.CollectAsync(_backend, settings, null, token);
        var messageTimeEvidence = CaptureMessageTimeEvidence(settings);
        token.ThrowIfCancellationRequested();
        if (_settings.Get().Revision != settings.Revision)
            throw new InvalidOperationException("設定已變更，請重新匯出本機診斷。");
        var context = new PrtgProbeEvidenceContext
        {
            SchemaVersion = "1.1.0", BuildVersion = buildVersion, GeneratedAtUtc = DateTimeOffset.UtcNow,
            HostUtcOffset = DateTimeOffset.Now.ToString("zzz"), SettingsRevision = settings.Revision,
            SourceFingerprint = PrtgCompatibilityProbe.ComputeUrlFingerprint(settings.PrtgUrl),
            StorageProvider = provider switch
            {
                "Microsoft.EntityFrameworkCore.Sqlite" => "Sqlite",
                "Microsoft.EntityFrameworkCore.SqlServer" => "SqlServer",
                _ => "unknown"
            },
            EfCoreProvider = provider is "Microsoft.EntityFrameworkCore.Sqlite" or "Microsoft.EntityFrameworkCore.SqlServer" ? provider : null,
            StorageEnvironment = storage, RetentionDays = settings.PrtgRetentionDays,
            LocalFacts = facts, BuildIdentity = PrtgProbeBuildFacts.From(build),
            MessageTimeEvidence = messageTimeEvidence,
            ReadCurrentMessageTimeFingerprint = ReadCurrentMessageTimeFingerprint
        };
        var json = PrtgCompatibilityProbe.SerializeEvidence(
            PrtgCompatibilityProbe.CreateUnobservedEvidence(context,
                string.IsNullOrWhiteSpace(settings.PrtgUrl) || !PrtgClientFactory.HasUsableCredentials(settings)
                    ? "source-not-configured" : "source-observations-not-started"));
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 64 * 1024)
            throw new InvalidDataException("本機診斷超過有界匯出上限。");
        return json;
    }

    public bool TryCancel() => _state.TryCancel();

    private PrtgProbeMessageTimeEvidence CaptureMessageTimeEvidence(SystemSettings settings)
    {
        try
        {
            if (!PrtgSharedAuthoritySnapshot.TryRead(_backend, out var snapshot, out _) || snapshot is null ||
                !string.Equals(snapshot.Settings.PrtgUrl, settings.PrtgUrl, StringComparison.Ordinal))
                return new PrtgProbeMessageTimeEvidence { AuthorityStatus = "unconfigured" };
            var policy = snapshot.Policy;
            var fingerprint = policy.MessageTimeFingerprint(snapshot.Settings.PrtgUrl);
            if (fingerprint.Length == 0) return new PrtgProbeMessageTimeEvidence { AuthorityStatus = "unconfigured" };
            return new PrtgProbeMessageTimeEvidence
            {
                AuthorityStatus = "saved-explicit-authority",
                DateFormat = policy.MessageDateFormat,
                SourceTimezone = policy.SourceTimeZoneId,
                SourceCulture = policy.SourceCultureName,
                AuthorityRevision = policy.MessageTimeAuthorityRevision,
                AuthorityFingerprint = fingerprint,
                SampleStatus = "not-observed"
            };
        }
        catch
        {
            return new PrtgProbeMessageTimeEvidence { AuthorityStatus = "unconfigured" };
        }
    }

    private string? ReadCurrentMessageTimeFingerprint()
    {
        try
        {
            return PrtgSharedAuthoritySnapshot.TryRead(_backend, out var snapshot, out _) && snapshot is not null
                ? snapshot.Policy.MessageTimeFingerprint(snapshot.Settings.PrtgUrl) : null;
        }
        catch { return null; }
    }

    public bool TryStartDataFlow(out string? error, out bool isConflict) =>
        TryStartCore(dataFlow: true, out error, out isConflict);

    /// <param name="error">拒絕原因；成功時為 null。</param>
    /// <param name="isConflict">true＝被互斥擋下（回填執行中／探測已在執行中），呼叫端該回 409；
    /// false＝設定不齊，該回 400。與回填、結構同步的 TryStart 同一套。</param>
    public bool TryStart(out string? error, out bool isConflict)
        => TryStartCore(dataFlow: false, out error, out isConflict);

    private bool TryStartCore(bool dataFlow, out string? error, out bool isConflict)
    {
        error = null;
        isConflict = false;
        var s = _settings.Get();

        if (string.IsNullOrWhiteSpace(s.PrtgUrl))
        {
            error = "尚未設定 PRTG 連線位址，無法執行探測。";
            return false;
        }

        if (!PrtgClientFactory.HasUsableCredentials(s))
        {
            error = "尚未設定 PRTG 認證資訊（API token 或帳號密碼），無法執行探測。";
            return false;
        }

        if (_backfillState.Snapshot().IsRunning)
        {
            error = "回填執行中，請稍後再試。";
            isConflict = true;
            return false;
        }

        if (dataFlow && (_schedulerState.IsRunning || _structureState.Snapshot().IsRunning))
        {
            error = _schedulerState.IsRunning
                ? "取數排程正在執行，請完成後再驗證小範圍資料流。"
                : "結構同步正在執行，請完成後再驗證小範圍資料流。";
            isConflict = true;
            return false;
        }

        if (!_state.TryBeginRun(out var ct))
        {
            error = "探測已在執行中。";
            isConflict = true;
            return false;
        }

        var console = new PrtgProbeConsole(_state);
        console.WriteLine($"探測模式：{(dataFlow ? "小範圍資料流" : "完整環境探測")}");
        console.WriteLine($"開始時間：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        var assembly = typeof(PrtgProbeService).Assembly;
        var buildVersion = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        console.WriteLine($"程式版本：{buildVersion}");
        var buildIdentity = LogForesightBuildIdentity.ReadCurrent(assembly, typeof(PrtgClient).Assembly);
        console.WriteLine($"建置識別：{buildIdentity.Status}／{buildIdentity.Reason}；來源 {buildIdentity.SourceCommit}；指紋 {buildIdentity.SourceFingerprint}。若不一致，請部署同一份 Web/Core 與 build-identity.json 後重新探測。");
        if (s.Revision == "legacy") console.WriteLine("設定修訂 legacy：既有設定可能仍有效，但缺少可追溯修訂；請由管理者檢視並明確儲存，不自動改設定。");
        console.WriteLine($"設定版本：{s.Revision}；PRTG 啟用：{(s.PrtgEnabled ? "是" : "否")}；取數策略：{s.PrtgFetchStrategy}");
        console.WriteLine("輸出可能含主機名稱、IP 或 PRTG 訊息；分享前請先檢視並遮蔽。");
        console.WriteLine();
        var evidenceContext = new PrtgProbeEvidenceContext
        {
            BuildVersion = buildVersion, GeneratedAtUtc = DateTimeOffset.UtcNow,
            HostUtcOffset = DateTimeOffset.Now.ToString("zzz"),
            SourceFingerprint = PrtgCompatibilityProbe.ComputeUrlFingerprint(s.PrtgUrl),
            SettingsRevision = s.Revision, RetentionDays = s.PrtgRetentionDays,
            BuildIdentity = PrtgProbeBuildFacts.From(buildIdentity)
        };
        PrtgClient? client = null;
        try
        {
            client = ClientFactoryForTesting is { } factory ? factory(s) : PrtgClientFactory.Create(s);
        }
        catch (Exception ex)
        {
            _state.AppendLine($"初始化 PRTG 連線失敗：{ex.Message}");
            if (!dataFlow) _state.SetEvidenceJson(PrtgCompatibilityProbe.SerializeEvidence(
                PrtgCompatibilityProbe.CreateUnobservedEvidence(evidenceContext, "client-initialization-failed")));
            _state.FinishRun(false, false);
            error = $"初始化 PRTG 連線失敗：{ex.Message}";
            return false;
        }

        _ = Task.Run(async () =>
        {
            var scopeReader = new PrtgScopeRevisionReader(_backend, _hosts);
            // Historic values use raw authority; the environment probe also observes messages.
            using var operation = new PrtgOperationScope(s, _settings.Get, ct,
                dataFlow ? scopeReader.ReadRawSemantic : scopeReader.Read,
                dataFlow ? "資料流探測" : "環境探測", requireEnabled: false);
            client.OperationCheckpoint = operation.Checkpoint;
            var runToken = operation.Token;
            var success = false;
            var cancelled = false;
            var compatibilityEvidenceProduced = false;
            try
            {
                using (client)
                {
                    operation.Checkpoint();
                    if (dataFlow)
                    {
                        success = await PrtgProbeDataFlowRunner.RunAsync(client, _backend, _hosts, s, console, runToken);
                    }
                    else
                    {
                        string storageProvider = "unknown";
                        string? efCoreProvider = null;
                        var storageEnvironment = new PrtgStorageEnvironmentFacts();
                        try
                        {
                            using var db = _backend.CreateContext();
                            var pName = db.Database.ProviderName;
                            // Export only the actual, known provider identity. It describes this
                            // service's storage; it does not establish remote PRTG host resources.
                            (storageProvider, efCoreProvider) = pName switch
                            {
                                "Microsoft.EntityFrameworkCore.SqlServer" => ("SqlServer", pName),
                                "Microsoft.EntityFrameworkCore.Sqlite" => ("Sqlite", pName),
                                _ => ("unknown", null)
                            };
                            storageEnvironment = await PrtgStorageEnvironmentProbe
                                .CollectAsync(db, _backend.DataRoot, runToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            storageProvider = "unknown";
                            efCoreProvider = null;
                        }

                        var localFacts = await PrtgProbeLocalFactsCollector.CollectAsync(_backend, s, _aiProbe, runToken);
                        var messageTimeEvidence = CaptureMessageTimeEvidence(s);
                        operation.Checkpoint();
                        console.WriteLine($"本機摘要：{localFacts.Status}／{localFacts.Reason}；正式設定 {localFacts.PolicyHosts} 台／{localFacts.PolicySensors} 顆；守門覆寫 {localFacts.GuardOverrideSensors} 顆。摘要不授權正式風險。");
                        console.WriteLine($"昨日 NetIQ 回執 {localFacts.NetiqReceipts} 筆、成功 {localFacts.NetiqSuccess}、等待 {localFacts.NetiqWaiting}；合格父紀錄 {localFacts.NetiqEligibleParents}／{localFacts.NetiqParentRows}。AI 小請求：{localFacts.AiProbeStatus}；不是正式服務驗收。");
                        evidenceContext = new PrtgProbeEvidenceContext
                        {
                            SchemaVersion = "1.1.0",
                            BuildVersion = buildVersion,
                            GeneratedAtUtc = DateTimeOffset.UtcNow,
                            HostUtcOffset = DateTimeOffset.Now.ToString("zzz"),
                            SourceFingerprint = PrtgCompatibilityProbe.ComputeUrlFingerprint(s.PrtgUrl),
                            SettingsRevision = s.Revision,
                            SourceTimezone = "unknown",
                            SourceLocale = "unknown",
                            StorageProvider = storageProvider,
                            EfCoreProvider = efCoreProvider,
                            StorageEnvironment = storageEnvironment,
                            RetentionDays = s.PrtgRetentionDays,
                            ScopeSummary = "unknown",
                            ReadinessSummary = "unknown",
                            LocalFacts = localFacts,
                            BuildIdentity = PrtgProbeBuildFacts.From(buildIdentity),
                            MessageTimeEvidence = messageTimeEvidence,
                            ReadCurrentMessageTimeFingerprint = ReadCurrentMessageTimeFingerprint
                        };
                        success = await PrtgProbeRunner.RunAsync(client, console, evidenceContext, runToken, json =>
                        {
                            compatibilityEvidenceProduced = true;
                            _state.SetEvidenceJson(json);
                        });
                    }

                    // 站台對照只在探測本身成功後才做（連線都不通時對照不出東西），
                    // 而且不影響 success：它是附加資訊，不是探測的成敗條件。
                    if (success && !dataFlow)
                    {
                        try
                        {
                            await PrtgProbeSiteCheck.RunAsync(client, console, _backend.PrtgStore(), _hosts, s, _sentinels.GetAll(),
                                new PrtgLiveGuardSource(client, runToken, console), runToken);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            console.WriteLine($"站台對照發生未預期錯誤：{ex.Message}");
                        }
                    }
                    operation.CompletedStage("探測與站台對照已返回");
                }
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                cancelled = true;
                success = false;
                console.WriteLine();
                console.WriteLine(operation.SettingsChanged ? "設定或範圍變更，探測已停止。" : "探測已由使用者停止。");
            }
            catch (Exception ex)
            {
                console.WriteLine($"探測過程發生未預期錯誤：{ex.Message}");
                success = false;
            }
            finally
            {
                // Failed/cancelled source work still exports the captured, sanitized local context.
                // Never start extra transport or repeat database reads during cancellation cleanup.
                string? incompleteEvidence = null;
                if (!dataFlow && (cancelled || !compatibilityEvidenceProduced))
                {
                    incompleteEvidence = PrtgCompatibilityProbe.SerializeEvidence(
                        PrtgCompatibilityProbe.CreateUnobservedEvidence(evidenceContext,
                            cancelled ? "probe-cancelled" : "source-observations-unavailable"));
                    if (!cancelled) _state.SetEvidenceJson(incompleteEvidence);
                }
                _state.FinishRun(success, cancelled, cancelled ? incompleteEvidence : null);
            }
        });

        return true;
    }
}
