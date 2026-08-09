using System.Collections.Concurrent;
using System.Text;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Policy;

namespace EntraGuard.Shared.Sessions;

/// <summary>One recognised phrase from one participant.</summary>
/// <param name="Speaker">Attributed from the unmixed audio channel, not inferred.</param>
/// <param name="Text">Recognised text.</param>
/// <param name="OffsetMs">Offset from call start.</param>
/// <param name="At">Wall-clock time of recognition.</param>
/// <param name="IsFinal">
/// False for Speech interim hypotheses, which drive the live portal transcript but must
/// never drive analysis — interim text changes under you as recognition settles.
/// </param>
public sealed record Utterance(
    SpeakerRole Speaker,
    string Text,
    long OffsetMs,
    DateTimeOffset At,
    bool IsFinal);

/// <summary>
/// Live state for one intercepted call.
///
/// Mutated from several places at once — the media socket pushes utterances while the
/// Analyst timer reads windows and the Actuator records executed actions — so every
/// mutable field is either concurrent or guarded.
/// </summary>
public sealed class CallSession
{
    private readonly ConcurrentQueue<Utterance> _utterances = new();
    private readonly ConcurrentDictionary<string, SpeakerRole> _participantRoles = new();
    private readonly HashSet<RemediationAction> _executedActions = [];
    private readonly Lock _actionLock = new();

    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    public string? CallConnectionId { get; set; }
    public string? AcsCorrelationId { get; set; }
    public string? ServerCallId { get; set; }

    /// <summary>UPN of the protected user, once resolved. Null on an unattributable call.</summary>
    public string? SubjectUpn { get; set; }

    /// <summary>Entra ID object ID of the protected user, once resolved.</summary>
    public string? SubjectObjectId { get; set; }

    /// <summary>Raw ACS participant ID of the other party.</summary>
    public string? CallerIdentity { get; set; }

    /// <summary>Most recent Analyst verdict. Starts benign so the portal has something to render.</summary>
    public RiskAssessment CurrentAssessment { get; set; } = RiskAssessment.Benign;

    /// <summary>Highest effective risk seen, so a call that de-escalates still reports its peak.</summary>
    public double PeakRisk { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// True when this session came from a replayed script rather than an intercepted call.
    ///
    /// Surfaced everywhere the session is — the portal, the session API, Sentinel — so a
    /// simulated run can never be mistaken for a real interception. A security tool that
    /// blurs that line is worse than one that cannot simulate at all.
    /// </summary>
    public bool IsSimulated { get; set; }

    /// <summary>
    /// True when this call is EntraGuard's own step-up verification call rather than an
    /// intercepted one.
    ///
    /// The distinction matters in the audit trail: "we detected a scam call" and "we
    /// monitored our own MFA call and found the user was being coached" are different
    /// findings, and a dashboard that conflates them overstates the interception rate.
    /// </summary>
    public bool IsVerificationCall { get; set; }

    /// <summary>
    /// When ACS opened the media WebSocket, if it has.
    ///
    /// Proof that the other side of the call is genuinely streaming to us rather than the
    /// call merely having been placed. "Is ACS listening?" is otherwise unanswerable from
    /// the UI, and unanswerable questions are how this flow kept failing silently.
    /// </summary>
    public DateTimeOffset? MediaStreamConnectedAt { get; set; }

    /// <summary>Audio frames received from ACS. Non-zero means real audio is flowing.</summary>
    public long AudioFramesReceived { get; set; }

    /// <summary>Most recent audio frame, for spotting a stream that has gone quiet.</summary>
    public DateTimeOffset? LastAudioAt { get; set; }

    /// <summary>Keypad tones ACS has forwarded on the media stream.</summary>
    public int DtmfReceived { get; set; }

    /// <summary>Stream format, captured from the first AudioMetadata frame.</summary>
    public int SampleRate { get; set; } = 24000;

    /// <summary>Snapshot of actions already executed. Feeds <see cref="PolicyContext"/> idempotency.</summary>
    public IReadOnlySet<RemediationAction> ExecutedActions
    {
        get
        {
            lock (_actionLock)
            {
                return new HashSet<RemediationAction>(_executedActions);
            }
        }
    }

    /// <summary>
    /// Record an executed action.
    /// </summary>
    /// <returns>
    /// True if this was the first execution. The Actuator uses this to win the race when
    /// two assessments land close enough together to both propose the same action.
    /// </returns>
    public bool TryMarkExecuted(RemediationAction action)
    {
        lock (_actionLock)
        {
            return _executedActions.Add(action);
        }
    }

    /// <summary>
    /// Bind an ACS participant channel to a role.
    ///
    /// The protected user is whoever authenticated through the portal; everyone else on
    /// the call is the counterparty. Getting this backwards would attribute the victim's
    /// own words to the attacker, so it is derived from the media channel rather than guessed.
    /// </summary>
    public void MapParticipant(string participantRawId, SpeakerRole role)
    {
        if (!string.IsNullOrEmpty(participantRawId))
        {
            _participantRoles[participantRawId] = role;
        }
    }

    public SpeakerRole RoleFor(string participantRawId) =>
        _participantRoles.TryGetValue(participantRawId, out var role) ? role : SpeakerRole.Unknown;

    public void AddUtterance(Utterance utterance) => _utterances.Enqueue(utterance);

    /// <summary>All final utterances in order. Interim hypotheses are excluded.</summary>
    public IReadOnlyList<Utterance> FinalUtterances =>
        _utterances.Where(u => u.IsFinal).OrderBy(u => u.OffsetMs).ToList();

    /// <summary>
    /// Format the recent transcript for the Analyst.
    ///
    /// Speaker labels are included because the same sentence means opposite things
    /// depending on who says it: "read me the code" from the caller is elicitation, from
    /// the protected user it is confusion.
    /// </summary>
    /// <param name="window">How far back to include.</param>
    /// <param name="now">Current call offset, so this stays testable without a clock.</param>
    public string TranscriptWindow(TimeSpan window, long now)
    {
        var cutoff = now - (long)window.TotalMilliseconds;
        var builder = new StringBuilder();

        foreach (var utterance in FinalUtterances.Where(u => u.OffsetMs >= cutoff))
        {
            var label = utterance.Speaker switch
            {
                SpeakerRole.Caller => "CALLER",
                SpeakerRole.ProtectedUser => "USER",
                _ => "UNKNOWN",
            };
            builder.Append('[').Append(label).Append("] ").AppendLine(utterance.Text);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Elapsed call time in milliseconds, relative to <see cref="StartedAt"/>.</summary>
    public long ElapsedMs(DateTimeOffset now) => (long)(now - StartedAt).TotalMilliseconds;
}
