using System.Collections.Concurrent;
using EntraGuard.Shared.Supportability;

namespace EntraGuard.MediaService.Sinks;

/// <summary>
/// Where faults go: a ring buffer that survives long enough to be read over HTTP, and
/// Sentinel for anything that needs to outlive the container.
///
/// <para>
/// Both, deliberately. Sentinel is authoritative but lags ingestion by minutes, which is
/// useless when somebody is standing in front of a demo asking why a call behaved oddly
/// thirty seconds ago. The ring buffer answers that immediately and forgets; Sentinel answers
/// slowly and remembers.
/// </para>
///
/// <para>
/// This type may never throw and may never block. It exists to explain failures, so a version
/// of it that can cause one would be worse than not having it. Every method swallows its own
/// errors, and the Sentinel write is detached.
/// </para>
/// </summary>
public sealed class FaultRecorder(ILogger<FaultRecorder> logger)
{
    /// <summary>
    /// How many recent faults stay readable in memory.
    ///
    /// Enough to cover a demo and the conversation after it. Not a log: anything that needs
    /// to survive a restart is in Sentinel.
    /// </summary>
    private const int Capacity = 200;

    private readonly ConcurrentQueue<TimestampedFault> _recent = new();

    /// <summary>Optional. Set once at startup when telemetry is configured.</summary>
    public LogsIngestionSink? Sink { get; set; }

    public sealed record TimestampedFault(DateTimeOffset At, Fault Fault);

    /// <summary>
    /// Record a fault. Cheap, non-blocking, and safe to call from anywhere including a
    /// catch block on the call path.
    /// </summary>
    public void Record(Fault fault)
    {
        try
        {
            _recent.Enqueue(new TimestampedFault(DateTimeOffset.UtcNow, fault));

            while (_recent.Count > Capacity && _recent.TryDequeue(out _))
            {
                // Oldest out. The buffer is a window, not an archive.
            }

            // Logged at a level that matches what it means for a user, not what it means to
            // the code. A silent downgrade of an authentication factor is a warning even
            // though every line of code involved behaved exactly as written.
            var level = fault.Severity switch
            {
                FaultSeverity.Broken => LogLevel.Error,
                FaultSeverity.Degraded => LogLevel.Warning,
                _ => LogLevel.Information,
            };

            logger.Log(
                level,
                "FAULT {Code} [{Component}/{Severity}] {WhatFailed} | Impact: {Impact} | "
              + "Likely: {Cause} | Do: {Remediation} | {Detail}",
                fault.Code, fault.Component, fault.Severity, fault.WhatFailed,
                fault.UserImpact, fault.ProbableCause, fault.Remediation, fault.Detail ?? "-");

            // Detached: a telemetry write must never delay the call that reported the fault.
            _ = WriteAsync(fault);
        }
        catch (Exception ex)
        {
            // The recorder failing must not become the incident. One line, and move on.
            logger.LogWarning(ex, "Could not record a fault. Swallowed deliberately.");
        }
    }

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<TimestampedFault> Recent(int take = 50) =>
        _recent.Reverse().Take(take).ToList();

    /// <summary>
    /// Faults grouped by code, which is the question actually worth asking: not "did this
    /// ever happen" but "is this happening repeatedly, and since when".
    /// </summary>
    public IReadOnlyList<object> Summary() =>
        _recent
            .GroupBy(f => f.Fault.Code)
            .Select(group => new
            {
                code = group.Key,
                component = group.First().Fault.Component.ToString(),
                severity = group.Max(f => f.Fault.Severity).ToString(),
                occurrences = group.Count(),
                firstSeen = group.Min(f => f.At),
                lastSeen = group.Max(f => f.At),
                whatFailed = group.First().Fault.WhatFailed,
                userImpact = group.First().Fault.UserImpact,
                probableCause = group.First().Fault.ProbableCause,
                remediation = group.First().Fault.Remediation,
            })
            .OrderByDescending(summary => summary.lastSeen)
            .Cast<object>()
            .ToList();

    private async Task WriteAsync(Fault fault)
    {
        if (Sink is null)
        {
            return;
        }

        try
        {
            await Sink.WriteFaultAsync(fault);
        }
        catch
        {
            // Already logged above, and an ingestion failure here would recurse into the
            // very sink that just failed. Dropped on purpose.
        }
    }
}
