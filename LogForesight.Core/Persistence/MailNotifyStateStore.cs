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
    private static readonly TimeSpan PreparedMailReservationLifetime = TimeSpan.FromMinutes(5);

    public MailNotifyStateStore(EfJsonBlobStore blob) : base(blob) => _blob = blob;

    public new MailNotifyState Get() => ReadBoundedValueWithVersion(MaxMutationCharacters).Value;

    public new MailNotifyState Update(Action<MailNotifyState> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return MutateWithContext((_, state) => { mutation(state); return state; });
    }

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

    /// <summary>
    /// Atomically reserves bounded receipt slots before SMTP. Null means the whole recipient
    /// send must be skipped; an empty key set returns an empty reservation ID and needs no slot.
    /// Only not-yet-attempted reservations expire. Once a sender call may have started, the slot
    /// remains occupied until an accepted or explicitly known-rejected result is recorded.
    /// </summary>
    internal string? TryReserveMailContributionKeys(IReadOnlyCollection<string> keys,
        DateTime nowUtc, int maximumKeys = 12_000)
    {
        if (nowUtc.Kind != DateTimeKind.Utc || maximumKeys < 1 || keys.Any(key =>
                key.Length != 64 || key.Any(ch => !Uri.IsHexDigit(ch))))
            throw new ArgumentOutOfRangeException(nameof(keys));
        var distinct = keys.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0) return string.Empty;
        var reservationId = Guid.NewGuid().ToString("N");
        return MutateWithContext((_, state) =>
        {
            PruneMailContributionReceipts(state, nowUtc);
            foreach (var stale in state.MailContributionReservations.Where(pair =>
                         pair.Value.Status == "reserved" &&
                         pair.Value.CreatedAtUtc <= nowUtc - PreparedMailReservationLifetime)
                     .Select(pair => pair.Key).ToArray())
                state.MailContributionReservations.Remove(stale);

            // An accepted, held, or in-flight/uncertain key already owns this contribution.
            // The caller will re-read and render again on the next pass.
            if (distinct.Any(key => state.AcceptedMailContributionReceipts.ContainsKey(key) ||
                                    state.HeldLegacyMailContributionReceipts.ContainsKey(key) ||
                                    state.MailContributionReservations.ContainsKey(key)))
                return null;

            var occupied = state.AcceptedMailContributionReceipts.Keys
                .Concat(state.HeldLegacyMailContributionReceipts.Keys)
                .Concat(state.MailContributionReservations.Keys)
                .Distinct(StringComparer.Ordinal).Count();
            if (occupied + distinct.Length > maximumKeys) return null;

            foreach (var key in distinct)
                state.MailContributionReservations.Add(key, new MailContributionReservation
                {
                    ReservationId = reservationId,
                    Status = "reserved",
                    CreatedAtUtc = nowUtc
                });
            return reservationId;
        });
    }

    /// <summary>Durably marks the exact reserved key set as about to enter SMTP.</summary>
    internal bool MarkMailContributionAttemptStarted(string reservationId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reservationId) || nowUtc.Kind != DateTimeKind.Utc) return false;
        return MutateWithContext((_, state) =>
        {
            var rows = state.MailContributionReservations.Where(pair => pair.Value.ReservationId == reservationId).ToArray();
            if (rows.Length == 0 || rows.Any(pair => pair.Value.Status != "reserved" ||
                    pair.Value.CreatedAtUtc <= nowUtc - PreparedMailReservationLifetime))
                return false;
            foreach (var pair in rows) pair.Value.Status = "attempted";
            return true;
        });
    }

    /// <summary>
    /// Completes a reservation after SMTP. Uncertain outcomes remain durable and block automatic
    /// resend; only a precisely classified rejection can release an attempted slot.
    /// </summary>
    internal bool CompleteMailContributionReservation(string reservationId,
        MailContributionReservationOutcome outcome, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reservationId) || nowUtc.Kind != DateTimeKind.Utc) return false;
        return MutateWithContext((_, state) =>
        {
            var rows = state.MailContributionReservations.Where(pair => pair.Value.ReservationId == reservationId).ToArray();
            if (rows.Length == 0 || rows.Any(pair => pair.Value.Status != "attempted")) return false;
            if (outcome == MailContributionReservationOutcome.Accepted)
            {
                foreach (var pair in rows)
                {
                    state.MailContributionReservations.Remove(pair.Key);
                    state.AcceptedMailContributionReceipts[pair.Key] = nowUtc;
                }
            }
            else if (outcome == MailContributionReservationOutcome.DefinitelyRejected)
            {
                foreach (var pair in rows) state.MailContributionReservations.Remove(pair.Key);
            }
            else
            {
                foreach (var pair in rows) pair.Value.Status = "unknown";
            }
            return true;
        });
    }

    internal bool ReleasePreparedMailContributionReservation(string reservationId)
    {
        if (string.IsNullOrWhiteSpace(reservationId)) return false;
        return MutateWithContext((_, state) =>
        {
            var rows = state.MailContributionReservations.Where(pair => pair.Value.ReservationId == reservationId).ToArray();
            if (rows.Length == 0 || rows.Any(pair => pair.Value.Status != "reserved")) return false;
            foreach (var pair in rows) state.MailContributionReservations.Remove(pair.Key);
            return true;
        });
    }

    private static void PruneMailContributionReceipts(MailNotifyState state, DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-180);
        foreach (var key in state.AcceptedMailContributionReceipts.Where(pair => pair.Value < cutoff)
                     .Select(pair => pair.Key).ToArray()) state.AcceptedMailContributionReceipts.Remove(key);
        foreach (var key in state.HeldLegacyMailContributionReceipts.Where(pair => pair.Value < cutoff)
                     .Select(pair => pair.Key).ToArray()) state.HeldLegacyMailContributionReceipts.Remove(key);
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
        return intent.Recipients.Values.All(status => status is "smtp-accepted" or "cancelled-formal-revoked");
    }
}
