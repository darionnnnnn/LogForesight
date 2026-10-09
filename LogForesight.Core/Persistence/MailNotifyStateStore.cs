using System.Data;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

/// <summary>郵件通知寄送狀態的儲存（回饋十五輪批次D），單一物件型 blob，見 <see cref="JsonBlobSingleton{T}"/>。</summary>
public class MailNotifyStateStore : JsonBlobSingleton<MailNotifyState>
{
    internal const string BlobKey = "mail_notify_state";
    internal const int MaxMutationCharacters = 4 * 1024 * 1024;
    private readonly EfJsonBlobStore _blob;

    public MailNotifyStateStore(EfJsonBlobStore blob) : base(blob) => _blob = blob;

    /// <summary>
    /// Mutates mail delivery state in the same serializable database transaction used to
    /// validate the PRTG mode fence. The returned state is committed before SMTP begins.
    /// </summary>
    public TResult MutateWithContext<TResult>(
        Func<LfDbContext, MailNotifyState, TResult> mutation,
        IsolationLevel isolationLevel = IsolationLevel.Serializable)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return _blob.MutateWithContext((ctx, raw) =>
        {
            var state = string.IsNullOrWhiteSpace(raw)
                ? new MailNotifyState()
                : JsonSerializer.Deserialize<MailNotifyState>(raw, LfJsonOptions.Pretty)
                    ?? throw new JsonException("Mail notification state JSON root is null.");
            var result = mutation(ctx, state);
            var serialized = JsonSerializer.Serialize(state, LfJsonOptions.Pretty);
            EnsureBoundedSerializedContent(serialized);
            return (serialized, result);
        }, maxCurrentCharacters: MaxMutationCharacters, isolationLevel: isolationLevel);
    }

    internal static void EnsureBoundedSerializedContent(string serialized)
    {
        if (serialized.Length > MaxMutationCharacters || Encoding.UTF8.GetByteCount(serialized) > MaxMutationCharacters)
            throw new InvalidDataException($"Mail notification state output exceeds its {MaxMutationCharacters}-character/byte mutation limit.");
    }

    /// <summary>
    /// Removes only fully terminal urgent intents once their parent date is beyond the effective
    /// record-retention horizon. This releases old shard references for the formal-mail retention
    /// consumer; unknown delivery outcomes and mixed recipient states remain durable.
    /// </summary>
    public int PruneTerminalUrgentIntents(int recordRetentionDays, DateTime nowUtc)
    {
        var horizonDays = Math.Max(90, Math.Max(0, recordRetentionDays));
        var cutoff = nowUtc.Date.AddDays(-horizonDays);
        return MutateWithContext((_, state) =>
        {
            var eligible = state.UrgentOutbox.Where(pair => pair.Value is { } intent &&
                    intent.RecordDate.Date < cutoff && IsFullyTerminal(intent))
                .Select(pair => pair.Key).ToArray();
            foreach (var key in eligible) state.UrgentOutbox.Remove(key);
            return eligible.Length;
        });
    }

    private static bool IsFullyTerminal(MailUrgentIntent intent)
    {
        if (intent.Status is not ("smtp-accepted" or "cancelled-formal-revoked" or "no-qualified-recipient"))
            return false;
        if (intent.Recipients.Values.Any(status => status is not ("smtp-accepted" or "cancelled-formal-revoked")))
            return false;
        return intent.FormalIssueStates.Values.All(status => status is "smtp-accepted" or "revoked");
    }
}
