using System.Collections.Concurrent;
using EntraGuard.MediaService.Agents;
using EntraGuard.Shared.Sessions;

namespace EntraGuard.MediaService.Sessions;

/// <summary>
/// A call in flight, plus the handles needed to act on it.
///
/// The outbound audio sender is a delegate rather than the WebSocket itself so the
/// remediation tools never touch transport directly — a tool asks for speech to be played,
/// and the media endpoint owns how that reaches the wire.
/// </summary>
public sealed class LiveCall
{
    public required CallSession Session { get; init; }

    /// <summary>ACS call connection ID, needed to hang up.</summary>
    public string? CallConnectionId { get; set; }

    /// <summary>
    /// Streams PCM back into the live call. Null until the media WebSocket connects, which
    /// is why the voice-warning tool reports Unavailable rather than failing if it fires
    /// in the gap between answering and the socket coming up.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, CancellationToken, Task>? SendAudioAsync { get; set; }

    public PerceptionAgent? Perception { get; set; }

    /// <summary>Cancelled when the call ends, to stop the analysis loop.</summary>
    public CancellationTokenSource Lifetime { get; } = new();
}

/// <summary>
/// In-memory registry of live calls.
///
/// In-process state is viable because the media service runs with sticky sessions and a
/// small replica count: a call's WebSocket, its analysis loop, and its remediation all
/// land on the replica that answered it. Historical sessions go to Table Storage, so a
/// restart loses in-flight calls but no history.
/// </summary>
public sealed class LiveCallRegistry
{
    private readonly ConcurrentDictionary<string, LiveCall> _calls = new();

    public LiveCall Create(string sessionId)
    {
        var call = new LiveCall
        {
            Session = new CallSession
            {
                SessionId = sessionId,
                StartedAt = DateTimeOffset.UtcNow,
            },
        };

        _calls[sessionId] = call;
        return call;
    }

    public LiveCall? Get(string sessionId) =>
        _calls.TryGetValue(sessionId, out var call) ? call : null;

    public IReadOnlyCollection<LiveCall> Active =>
        _calls.Values.Where(c => c.Session.IsActive).ToList();

    public async Task RemoveAsync(string sessionId)
    {
        if (!_calls.TryRemove(sessionId, out var call))
        {
            return;
        }

        call.Session.IsActive = false;
        call.Session.EndedAt = DateTimeOffset.UtcNow;

        await call.Lifetime.CancelAsync();

        if (call.Perception is not null)
        {
            await call.Perception.DisposeAsync();
        }

        call.Lifetime.Dispose();
    }
}
