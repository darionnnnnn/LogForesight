using System.Text.RegularExpressions;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

public enum PrtgDiskSemanticEvidenceSource
{
    Manual,
    Automated
}

/// <summary>目前用來核對語意證據的最小 sensor／對應資料。</summary>
public sealed record PrtgDiskSemanticContext(
    long SensorObjid,
    long DeviceObjid,
    long HostId,
    string SensorType,
    string MainChannelIdentifier,
    string MainChannelName,
    string Unit,
    double Scale,
    string Direction);

/// <summary>不含原始 PRTG 回應的磁碟語意確認證據。</summary>
public sealed record PrtgDiskSemanticEvidence(
    long SensorObjid,
    long DeviceObjid,
    long HostId,
    string SensorType,
    string MainChannelIdentifier,
    string MainChannelName,
    string Unit,
    double Scale,
    string Direction,
    PrtgDiskSemanticEvidenceSource Source,
    long? VerifierUserId,
    string EvidenceSummary,
    DateTime VerifiedAtUtc,
    string ParserSemanticVersion);

public sealed class PrtgDiskSemanticEvidenceValidity
{
    public bool IsValid { get; init; }
    public string? InvalidReason { get; init; }
    public PrtgDiskSemanticEvidence? Evidence { get; init; }
}

/// <summary>
/// 每顆 PRTG 磁碟 sensor 的最小、持久語意證據。有效性依目前對應與解析語意判定，
/// 不依賴白名單或規則門檻。此 store 不連線 PRTG。
/// </summary>
public sealed class PrtgDiskSemanticEvidenceStore : JsonBlobSingleton<Dictionary<long, PrtgDiskSemanticEvidence>>
{
    public const string BlobKey = "prtg_disk_semantic_evidence";
    private static readonly Regex SecretMarker = new(@"(?i)(password|passwd|token|api[_ -]?key|authorization|cookie)\s*[:=]", RegexOptions.Compiled);

    public PrtgDiskSemanticEvidenceStore(EfJsonBlobStore blob) : base(blob) { }

    public IReadOnlyCollection<PrtgDiskSemanticEvidence> GetAll() => Get().Values.ToArray();

    public PrtgDiskSemanticEvidence? Get(long sensorObjid) =>
        Get().TryGetValue(sensorObjid, out var evidence) ? evidence : null;

    public PrtgDiskSemanticEvidence ConfirmManually(
        PrtgDiskSemanticContext context,
        long verifierUserId,
        string evidenceSummary,
        DateTime verifiedAtUtc,
        string parserSemanticVersion)
    {
        ValidateContext(context);
        if (verifierUserId <= 0) throw new ArgumentOutOfRangeException(nameof(verifierUserId));
        ValidateEvidenceSummary(evidenceSummary);
        ValidateVersion(parserSemanticVersion);
        if (verifiedAtUtc == default || verifiedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("確認時間必須是明確的 UTC 時間。", nameof(verifiedAtUtc));

        return Save(new PrtgDiskSemanticEvidence(
            context.SensorObjid, context.DeviceObjid, context.HostId, context.SensorType.Trim(),
            context.MainChannelIdentifier.Trim(), context.MainChannelName.Trim(), context.Unit.Trim(),
            context.Scale, context.Direction.Trim(), PrtgDiskSemanticEvidenceSource.Manual,
            verifierUserId, evidenceSummary.Trim(), verifiedAtUtc, parserSemanticVersion.Trim()));
    }

    /// <summary>只有呼叫端已完成強證據核驗時，才可寫入自動確認結果。</summary>
    public PrtgDiskSemanticEvidence RecordAutomatedVerification(
        PrtgDiskSemanticContext context,
        bool robustEvidenceEstablished,
        string evidenceSummary,
        DateTime verifiedAtUtc,
        string parserSemanticVersion)
    {
        ValidateContext(context);
        if (!robustEvidenceEstablished)
            throw new InvalidOperationException("沒有明確且充分的自動證據，不得標記為已驗證。");
        ValidateEvidenceSummary(evidenceSummary);
        ValidateVersion(parserSemanticVersion);
        if (verifiedAtUtc == default || verifiedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("確認時間必須是明確的 UTC 時間。", nameof(verifiedAtUtc));

        return Save(new PrtgDiskSemanticEvidence(
            context.SensorObjid, context.DeviceObjid, context.HostId, context.SensorType.Trim(),
            context.MainChannelIdentifier.Trim(), context.MainChannelName.Trim(), context.Unit.Trim(),
            context.Scale, context.Direction.Trim(), PrtgDiskSemanticEvidenceSource.Automated,
            null, evidenceSummary.Trim(), verifiedAtUtc, parserSemanticVersion.Trim()));
    }

    public PrtgDiskSemanticEvidenceValidity CheckValidity(
        long sensorObjid,
        PrtgDiskSemanticContext? current,
        string currentParserSemanticVersion)
    {
        var evidence = Get(sensorObjid);
        if (evidence is null) return Invalid("尚無語意證據。");
        if (current is null) return Invalid("目前 sensor／主機對應不存在。", evidence);
        try { ValidateContext(current); }
        catch (ArgumentException) { return Invalid("目前 sensor 中繼資料不完整。", evidence); }

        string? reason = null;
        if (evidence.SensorObjid != current.SensorObjid || evidence.DeviceObjid != current.DeviceObjid || evidence.HostId != current.HostId)
            reason = "主機或裝置對應已變更。";
        else if (!Same(evidence.SensorType, current.SensorType)) reason = "sensor 類型已變更。";
        else if (!Same(evidence.MainChannelIdentifier, current.MainChannelIdentifier) || !Same(evidence.MainChannelName, current.MainChannelName))
            reason = "主頻道已變更。";
        else if (!Same(evidence.Unit, current.Unit) || evidence.Scale != current.Scale || !Same(evidence.Direction, current.Direction))
            reason = "單位或量測尺度／方向已變更。";
        else if (!Same(evidence.ParserSemanticVersion, currentParserSemanticVersion)) reason = "解析語意版本已變更。";

        return reason is null
            ? new PrtgDiskSemanticEvidenceValidity { IsValid = true, Evidence = evidence }
            : Invalid(reason, evidence);

        static PrtgDiskSemanticEvidenceValidity Invalid(string reason, PrtgDiskSemanticEvidence? item = null) =>
            new() { IsValid = false, InvalidReason = reason, Evidence = item };
    }

    private PrtgDiskSemanticEvidence Save(PrtgDiskSemanticEvidence evidence)
    {
        Update(all => all[evidence.SensorObjid] = evidence);
        return evidence;
    }

    private static void ValidateContext(PrtgDiskSemanticContext c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.SensorObjid <= 0 || c.DeviceObjid <= 0 || c.HostId <= 0 ||
            string.IsNullOrWhiteSpace(c.SensorType) || string.IsNullOrWhiteSpace(c.MainChannelIdentifier) ||
            string.IsNullOrWhiteSpace(c.MainChannelName) || string.IsNullOrWhiteSpace(c.Unit) ||
            string.IsNullOrWhiteSpace(c.Direction) || !double.IsFinite(c.Scale) || c.Scale <= 0)
            throw new ArgumentException("sensor、主機、主頻道、單位與量測尺度／方向均為必要資訊。", nameof(c));
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 100)
            throw new ArgumentException("解析語意版本不可空白且最多 100 字元。", nameof(version));
    }

    private static void ValidateEvidenceSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 500 ||
            summary.Any(char.IsControl) || SecretMarker.IsMatch(summary) ||
            summary.TrimStart().StartsWith('{') || summary.TrimStart().StartsWith('[') ||
            summary.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("證據摘要必須是 1–500 字的純文字摘要，不可包含原始 payload、網址或疑似憑證。", nameof(summary));
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
