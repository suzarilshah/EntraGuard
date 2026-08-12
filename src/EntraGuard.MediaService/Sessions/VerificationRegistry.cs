using System.Collections.Concurrent;
using System.Security.Cryptography;
using EntraGuard.Shared.Verification;

namespace EntraGuard.MediaService.Sessions;

/// <summary>
/// In-flight and recently completed verification attempts.
///
/// Completed attempts are kept for a short window rather than dropped immediately: the RP
/// app polls for its verdict, and evicting the moment the call ends would race the poll
/// and leave the browser unable to tell "denied" from "never happened". Those two must
/// never be confusable on an auth path.
/// </summary>
public sealed class VerificationRegistry
{
    private readonly ConcurrentDictionary<string, VerificationSession> _sessions = new();

    /// <summary>How long a completed verdict stays readable by the relying party.</summary>
    private static readonly TimeSpan RetentionAfterComplete = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Generate the number-matching code.
    ///
    /// Cryptographically random rather than <c>Random</c>: this is an authentication
    /// factor, and a predictable code would let an attacker who controls the phone leg
    /// answer without ever seeing the browser. 10-99 keeps it two digits, which is what
    /// makes it readable aloud and keyable mid-call.
    /// </summary>
    public static string NewMatchCode() => RandomNumberGenerator.GetInt32(10, 100).ToString();

    public VerificationSession Create(
        string subjectUpn,
        string? subjectObjectId,
        string calleeAcsId,
        string applicationName)
    {
        Evict();

        var session = new VerificationSession
        {
            VerificationId = $"vrf-{Guid.NewGuid():N}"[..16],
            StartedAt = DateTimeOffset.UtcNow,
            SubjectUpn = subjectUpn,
            SubjectObjectId = subjectObjectId,
            CalleeAcsId = calleeAcsId,
            ApplicationName = applicationName,
            MatchCode = NewMatchCode(),
            ViewerToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        };

        _sessions[session.VerificationId] = session;
        return session;
    }

    public VerificationSession? Get(string verificationId) =>
        _sessions.TryGetValue(verificationId, out var session) ? session : null;

    /// <summary>Find the attempt driving a given ACS call, for callback handling.</summary>
    public VerificationSession? ByCallConnection(string callConnectionId) =>
        _sessions.Values.FirstOrDefault(s => s.CallConnectionId == callConnectionId);

    /// <summary>Find the attempt whose monitored call is the given media session.</summary>
    public VerificationSession? ByMonitorSession(string monitorSessionId) =>
        _sessions.Values.FirstOrDefault(s => s.MonitorSessionId == monitorSessionId);

    public IReadOnlyCollection<VerificationSession> Recent =>
        _sessions.Values.OrderByDescending(s => s.StartedAt).Take(50).ToList();

    /// <summary>
    /// Record a verdict.
    ///
    /// Returns false if the attempt already had one. Both the DTMF recognizer and the
    /// coercion monitor can decide independently and near-simultaneously; whichever lands
    /// first wins, and a later "passed" must never overwrite an earlier "blocked".
    /// </summary>
    public bool TryComplete(string verificationId, VerificationResult result, string reason)
    {
        if (!_sessions.TryGetValue(verificationId, out var session))
        {
            return false;
        }

        lock (session)
        {
            if (session.IsComplete)
            {
                return false;
            }

            session.Result = result;
            session.Reason = reason;
            session.CompletedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    private void Evict()
    {
        var cutoff = DateTimeOffset.UtcNow - RetentionAfterComplete;
        foreach (var (id, session) in _sessions)
        {
            if (session.CompletedAt is { } completed && completed < cutoff)
            {
                _sessions.TryRemove(id, out _);
            }
        }
    }
}
