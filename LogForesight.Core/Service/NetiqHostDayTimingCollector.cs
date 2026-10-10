using System.Diagnostics;

namespace LogForesight.Core.Service;

/// <summary>Aggregate-only timing evidence for one NetIQ pipeline run.</summary>
public interface INetiqHostDayTimingCollector
{
    void Begin(long runId);
    void AddPlanned(long runId, int hostDays);
    void RecordCommitted(long runId, long hostId, DateTime day, long elapsedTicks);
    void RecordSourceFailure(long runId, long hostId, DateTime day);
    void RecordUnknown(long runId, long hostId, DateTime day);
    void MarkIncomplete(long runId);
    void Complete(long runId, bool pipelineCompleted);
    NetiqHostDayTimingSnapshot? Read(long? runId = null);
}

/// <summary>
/// In-memory bounded collector. A run that exceeds the exact-value cap, repeats a host-day,
/// or finishes with unknown/source-failed work is explicitly incomplete and has no p95.
/// It keeps no host names, IP addresses, credentials, or event data.
/// </summary>
public sealed class NetiqHostDayTimingCollector : INetiqHostDayTimingCollector
{
    public const int MaximumExactHostDays = 600_000;
    private readonly object _gate = new();
    private RunSamples? _run;

    public void Begin(long runId)
    {
        if (runId <= 0) throw new ArgumentOutOfRangeException(nameof(runId));
        lock (_gate)
        {
            if (_run?.RunId == runId)
            {
                // Re-entry must never erase the evidence already collected for this run.
                _run.Incomplete = true;
                _run.PipelineCompleted = false;
                _run.Finalized = true;
                _run.P95Milliseconds = null;
                _run.CompletedAtUtc ??= DateTimeOffset.UtcNow;
                return;
            }
            _run = new RunSamples(runId, Stopwatch.GetTimestamp());
        }
    }

    public void AddPlanned(long runId, int hostDays)
    {
        if (hostDays < 0) throw new ArgumentOutOfRangeException(nameof(hostDays));
        lock (_gate)
        {
            if (!Matches(runId)) return;
            if (_run!.Expected > int.MaxValue - hostDays)
            {
                _run.Overflow = true;
                _run.Incomplete = true;
                _run.Expected = int.MaxValue;
                return;
            }
            _run.Expected += hostDays;
            if (_run.Expected > MaximumExactHostDays) _run.Overflow = true;
        }
    }

    public void RecordCommitted(long runId, long hostId, DateTime day, long elapsedTicks) =>
        Record(runId, hostId, day, Outcome.Committed, elapsedTicks);

    public void RecordSourceFailure(long runId, long hostId, DateTime day) =>
        Record(runId, hostId, day, Outcome.SourceFailed, null);

    public void RecordUnknown(long runId, long hostId, DateTime day) =>
        Record(runId, hostId, day, Outcome.Unknown, null);

    private void Record(long runId, long hostId, DateTime day, Outcome outcome, long? elapsedTicks)
    {
        if (hostId <= 0) return;
        lock (_gate)
        {
            if (!Matches(runId)) return;
            var key = new HostDay(hostId, day.Date.Ticks);
            if (_run!.Rows.ContainsKey(key))
            {
                _run.Duplicates++;
                _run.Incomplete = true;
                return;
            }
            if (_run.Rows.Count >= MaximumExactHostDays)
            {
                _run.Overflow = true;
                _run.Incomplete = true;
                return;
            }
            _run.Rows.Add(key, outcome);
            if (outcome == Outcome.Committed && elapsedTicks is >= 0)
            {
                if (_run.Durations.Count >= MaximumExactHostDays)
                {
                    _run.Overflow = true;
                    _run.Incomplete = true;
                    return;
                }
                var elapsedMs = elapsedTicks.Value * 1000d / Stopwatch.Frequency;
                if (!double.IsFinite(elapsedMs) || elapsedMs < 0)
                {
                    _run.Unknown++;
                    _run.Incomplete = true;
                    _run.Rows[key] = Outcome.Unknown;
                    return;
                }
                _run.Durations.Add(elapsedMs);
                _run.Committed++;
            }
            else if (outcome == Outcome.SourceFailed)
            {
                _run.SourceFailed++;
                _run.Incomplete = true;
            }
            else
            {
                _run.Unknown++;
                _run.Incomplete = true;
            }
        }
    }

    public void MarkIncomplete(long runId)
    {
        lock (_gate) if (Matches(runId)) _run!.Incomplete = true;
    }

    public void Complete(long runId, bool pipelineCompleted)
    {
        lock (_gate)
        {
            if (!Matches(runId)) return;
            _run!.CompletedAtUtc = DateTimeOffset.UtcNow;
            _run.PipelineCompleted = pipelineCompleted;
            _run.Finalized = true;
            var missing = Math.Max(0, _run.Expected - _run.Rows.Count);
            _run.Unknown = (int)Math.Min(int.MaxValue, (long)_run.Unknown + missing);
            if (!pipelineCompleted || _run.Overflow || _run.Duplicates > 0 || _run.Rows.Count != _run.Expected ||
                _run.Committed != _run.Expected || _run.SourceFailed != 0 || _run.Unknown != 0)
                _run.Incomplete = true;
            if (!_run.Incomplete && _run.Expected > 0 && _run.Durations.Count == _run.Expected)
            {
                var sorted = _run.Durations.Order().ToArray();
                _run.P95Milliseconds = sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1)];
            }
        }
    }

    public NetiqHostDayTimingSnapshot? Read(long? runId = null)
    {
        lock (_gate)
        {
            if (_run is null || runId.HasValue && _run.RunId != runId.Value) return null;
            var complete = _run.Finalized && _run.PipelineCompleted && _run.Expected > 0 &&
                !_run.Incomplete && !_run.Overflow && _run.Rows.Count == _run.Expected &&
                _run.Durations.Count == _run.Expected;
            var p95 = complete ? _run.P95Milliseconds : null;
            return new NetiqHostDayTimingSnapshot(
                _run.RunId,
                !_run.Finalized ? "running" : complete ? "complete" : "incomplete",
                _run.Expected,
                _run.Rows.Count,
                _run.Committed,
                _run.SourceFailed,
                _run.Unknown,
                _run.Duplicates,
                _run.Durations.Count,
                _run.Overflow,
                p95,
                _run.StartedAtUtc,
                _run.CompletedAtUtc);
        }
    }

    private bool Matches(long runId) => _run is not null && _run.RunId == runId && !_run.Finalized;

    private enum Outcome { Committed, SourceFailed, Unknown }
    private readonly record struct HostDay(long HostId, long DayTicks);

    private sealed class RunSamples(long runId, long startedTimestamp)
    {
        public long RunId { get; } = runId;
        public long StartedTimestamp { get; } = startedTimestamp;
        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public int Expected { get; set; }
        public int Committed { get; set; }
        public int SourceFailed { get; set; }
        public int Unknown { get; set; }
        public int Duplicates { get; set; }
        public bool Overflow { get; set; }
        public bool Incomplete { get; set; }
        public bool PipelineCompleted { get; set; }
        public bool Finalized { get; set; }
        public double? P95Milliseconds { get; set; }
        public Dictionary<HostDay, Outcome> Rows { get; } = new();
        public List<double> Durations { get; } = new();
    }
}

public sealed record NetiqHostDayTimingSnapshot(
    long RunId,
    string Status,
    int ExpectedHostDays,
    int ObservedHostDays,
    int CommittedHostDays,
    int SourceFailedHostDays,
    int UnknownHostDays,
    int DuplicateHostDays,
    int DurationSampleCount,
    bool Overflow,
    double? P95Milliseconds,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

