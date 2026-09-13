namespace EntraGuard.Shared.Verification;

/// <summary>
/// Outcome of a step-up voice verification attempt.
///
/// <see cref="BlockedCoercion"/> is the one that matters and the reason this factor is
/// different from every other MFA channel. It means the user keyed the RIGHT code and was
/// still refused, because the Analyst detected they were being coached through it. Number
/// matching proves the phone-holder is the browser-user; it cannot prove the phone-holder
/// is acting freely. This value is what covers that gap.
/// </summary>
public enum VerificationResult
{
    /// <summary>Call placed, waiting on the user.</summary>
    Pending,

    /// <summary>Correct code entered and no coercion detected. Access granted.</summary>
    Passed,

    /// <summary>Wrong code entered, or too many attempts.</summary>
    Failed,

    /// <summary>
    /// Correct code, but EntraGuard detected the user was being coached through it.
    /// A pass on the knowledge factor and a fail on the intent behind it.
    /// </summary>
    BlockedCoercion,

    /// <summary>
    /// Correct code, no coercion — but the voice on the call did not match the enrolled
    /// speaker. A pass on possession and knowledge, and a fail on who was holding the phone.
    ///
    /// Only ever produced when VOICE_MODE=enforce. In observe mode the score is recorded
    /// and the verification is decided exactly as it was before voice existed, because
    /// thresholds that have not been measured against real telephony would refuse real
    /// users.
    /// </summary>
    BlockedVoiceMismatch,

    /// <summary>User never answered, or the call ended before a response.</summary>
    Timeout,

    /// <summary>The call could not be placed at all.</summary>
    CallFailed,
}

/// <summary>
/// One step-up verification attempt, from call placement to verdict.
///
/// Deliberately separate from <c>CallSession</c>: that models a call EntraGuard
/// intercepted defensively, this models a call EntraGuard originated as an auth factor.
/// They share the media pipeline but answer different questions, and collapsing them would
/// make "was this call ours?" ambiguous in the audit trail.
/// </summary>
/// <summary>
/// Where the verification call has actually reached.
///
/// Distinct from <see cref="VerificationResult"/>, which is the verdict. This is progress,
/// and it exists because the first version showed the user "Calling your device…" from the
/// moment the request was issued until the end of time — one label covering placing,
/// ringing, connected, prompting and stalled alike. When it hung, that told nobody
/// anything. Each transition below is driven by a real ACS callback, so the UI cannot
/// claim progress the call has not made.
/// </summary>
public enum VerificationCallState
{
    /// <summary>CreateCall issued; ACS has not confirmed anything yet.</summary>
    Placing,

    /// <summary>ACS reported CallConnected — a device answered.</summary>
    Connected,

    /// <summary>The spoken challenge is playing and DTMF capture is armed.</summary>
    AwaitingDigits,

    /// <summary>Digits received; the adjudicator is deciding.</summary>
    Adjudicating,

    /// <summary>
    /// The code passed and a knowledge question is being asked aloud.
    ///
    /// A distinct state because it is the one stage a coercer can hear and answer for the
    /// user. Anything reading this session — the portal, the audit row, the Analyst —
    /// should be able to tell that this call reached the coachable part.
    /// </summary>
    AwaitingAnswer,

    /// <summary>Call is over, for any reason.</summary>
    Ended,
}

public sealed class VerificationSession
{
    /// <summary>
    /// Where the challenge was delivered: "teams", "phone" or "browser".
    ///
    /// Recorded because it changes how much the result is worth. A Teams call reaches a
    /// managed, signed-in application on a real device; a browser soft-phone reaches
    /// whatever tab happened to be open. Same verdict, different assurance, and the audit
    /// trail should not pretend otherwise.
    /// </summary>
    public string EndpointKind { get; set; } = "browser";

    /// <summary>Live call progress. See <see cref="VerificationCallState"/>.</summary>
    public VerificationCallState CallState { get; set; } = VerificationCallState.Placing;

    /// <summary>The knowledge question being asked, if one is registered. Never the answer.</summary>
    public string? KnowledgeQuestion { get; set; }

    /// <summary>Spoken answers tried. Bounded exactly as the code attempts are.</summary>
    public int KnowledgeAttempts { get; set; }

    /// <summary>
    /// Probes asked after an answer that was correct but coarse, and how each went.
    ///
    /// Empty is the normal case on a calm call answered specifically, and it means "nothing
    /// needed asking" rather than "nothing was recorded". See <see cref="FollowUpOutcome"/>
    /// for why none of these can refuse anybody.
    /// </summary>
    public IReadOnlyList<FollowUpOutcome> FollowUps { get; set; } = [];

    /// <summary>
    /// How the agent is speaking: "Warm" or "Protective".
    ///
    /// Two states rather than a gradient, and the agent chooses neither. Warmth that tracked
    /// the risk score would turn the call into a live readout of the detector — a scammer
    /// runs it three times, learns which phrasing turns the voice cold, drops that phrasing,
    /// and leaves the detector blind to exactly the tactics that work. Two states with an
    /// externally authorised transition leak only at the moment the gate has already decided
    /// to act, which is a cost already being paid for the spoken warning.
    /// </summary>
    public string Register { get; set; } = "Warm";

    /// <summary>
    /// Which prompt round the user is currently answering.
    ///
    /// Incremented once per spoken challenge. Exists because the digits arrive on TWO
    /// independent paths — the Call Automation recogniser and the media stream — and both
    /// report the same keypress. Without a round to pin an adjudication to, one entry was
    /// judged twice and burned two of the three attempts, so a user who mistyped once was
    /// refused as though they had mistyped three times.
    /// </summary>
    public int PromptRound { get; set; }

    /// <summary>The last round that was adjudicated. Never adjudicate the same round twice.</summary>
    public int AdjudicatedRound { get; set; }

    /// <summary>Cosine similarity against the enrolled voice, or null if not compared.</summary>
    public double? VoiceScore { get; set; }

    /// <summary>Match, Inconclusive, Mismatch or NotAssessed — as a string for the wire.</summary>
    public string VoiceOutcome { get; set; } = "NotAssessed";

    /// <summary>
    /// True when the voice check wants the relying party to force interactive
    /// re-authentication before granting.
    ///
    /// Separate from the verdict on purpose: voice never denies access on its own, it asks
    /// for a stronger factor. A model with a phone-quality recording of an accent it was
    /// not trained on is not something that should be able to lock a person out of their
    /// own money.
    /// </summary>
    public bool RequiresStepUp { get; set; }

    /// <summary>
    /// Whether the caller repeated the phrase generated for them on this call.
    ///
    /// NotAssessed | Passed | PhraseMismatch | NoResponse. This is the anti-replay signal: a
    /// recording made before the call cannot contain a phrase invented during it. The voice
    /// model contributes no replay resistance whatsoever — ECAPA scores a played-back
    /// recording of the enrolled speaker as the enrolled speaker, because it is one.
    /// </summary>
    public string LivenessOutcome { get; set; } = "NotAssessed";

    /// <summary>
    /// Milliseconds from the end of the prompt to the first word of the reply.
    ///
    /// Recorded as a signal, not a verdict. Real-time voice conversion adds pipeline latency,
    /// but so does a bad line and so does a person who paused to think, which is exactly why
    /// this is logged for a while before anything is decided on it.
    /// </summary>
    public int? LivenessLatencyMs { get; set; }

    /// <summary>Presentation-attack probability from the PAD model, when one ran.</summary>
    public double? SpoofScore { get; set; }

    /// <summary>
    /// Why the voice outcome is what it is, in a sentence.
    ///
    /// Exists because NotAssessed had four indistinguishable causes — no enrolled profile,
    /// too little speech, an unreachable scorer, and scoring never running on this path — and
    /// all four reached Sentinel as an empty score. Diagnosing which one required reading
    /// container logs from the right replica at the right moment.
    /// </summary>
    public string VoiceDetail { get; set; } = string.Empty;

    /// <summary>
    /// Composite risk for this attempt, 0-100. See <see cref="VerificationRisk"/>.
    ///
    /// Recorded and displayed. It changes no access decision.
    /// </summary>
    public double RiskScore { get; set; }

    /// <summary>Low | Moderate | Elevated | High.</summary>
    public string RiskBand { get; set; } = nameof(Verification.RiskBand.Low);

    /// <summary>What drove <see cref="RiskScore"/>, largest first.</summary>
    public IReadOnlyList<string> RiskContributors { get; set; } = [];

    /// <summary>
    /// Capability token proving the bearer is the browser that STARTED this verification.
    ///
    /// The match code is shown on one screen and typed on one keypad, and the whole factor
    /// rests on nobody else seeing it. It was readable by anyone: the list endpoint returned
    /// the live code for every in-flight attempt alongside the target's UPN, and the SignalR
    /// hub broadcast the same projection to every connected client. Reproduced against the
    /// deployed service before this was written — an anonymous GET returned match code 96 for
    /// victim@contoso.com.
    ///
    /// Not a session cookie and not an identity: it says "I am the tab that asked for this
    /// verification", which is exactly the claim needed to be shown its code. Minted once,
    /// returned once, compared in fixed time, and never logged or written to telemetry.
    /// </summary>
    public string ViewerToken { get; set; } = string.Empty;

    /// <summary>The digits last judged, and when.</summary>
    /// <remarks>
    /// The round counter alone could not settle this. A wrong entry re-prompts immediately,
    /// which opens the next round — so the duplicate arriving milliseconds later found a
    /// fresh unclaimed round and consumed it. Two of three attempts gone on one keypress.
    ///
    /// The entry itself is the stable identity of an attempt: both input paths report the
    /// same digits, and a human cannot hear a re-prompt and retype the same wrong code
    /// inside the dedupe window.
    /// </remarks>
    public string? LastAdjudicatedEntry { get; set; }

    public DateTimeOffset LastAdjudicatedAt { get; set; }

    /// <summary>
    /// Where the question came from: "directory", "table" or null.
    ///
    /// Surfaced because "held in your own Entra directory" and "held in our database" are
    /// different promises, and the audit trail should record which one was actually kept.
    /// </summary>
    public string? KnowledgeBacking { get; set; }

    public required string VerificationId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Who is being verified.</summary>
    public required string SubjectUpn { get; init; }

    /// <summary>Entra ID object ID, when the RP app supplied one.</summary>
    public string? SubjectObjectId { get; init; }

    /// <summary>
    /// Home tenant of the subject, from the tid claim.
    ///
    /// Needed because a knowledge question registered in the user's own directory can only
    /// be read with a token for THAT tenant — an object ID alone is ambiguous across
    /// directories, and reading the wrong one returns "no question registered", which would
    /// silently downgrade the factor rather than fail.
    /// </summary>
    public string? SubjectTenantId { get; set; }

    /// <summary>ACS identity the verification call was placed to.</summary>
    public required string CalleeAcsId { get; init; }

    /// <summary>Application requesting the step-up, shown in the spoken prompt.</summary>
    public string ApplicationName { get; init; } = "the application";

    /// <summary>
    /// The two-digit code shown in the browser and demanded on the call.
    ///
    /// Two digits, not six: it must be readable from a screen and keyable during a live
    /// call without the user losing the thread. The security does not come from entropy —
    /// a guess has a 1-in-90 chance and there are only three attempts — it comes from
    /// requiring simultaneous possession of the browser session and the phone.
    /// </summary>
    public required string MatchCode { get; init; }

    public string? CallConnectionId { get; set; }

    /// <summary>Correlates with the <c>CallSession</c> carrying the Analyst's monitoring of this call.</summary>
    public string? MonitorSessionId { get; set; }

    public VerificationResult Result { get; set; } = VerificationResult.Pending;

    /// <summary>Human-readable justification, surfaced to the user and the admin blade.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>DTMF the user actually entered, for the audit trail.</summary>
    public string? EnteredCode { get; set; }

    public int Attempts { get; set; }

    /// <summary>Peak Analyst risk observed while the verification call was up.</summary>
    public double PeakRiskDuringCall { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsComplete => Result != VerificationResult.Pending;

    /// <summary>
    /// True only when the user may be let into the application.
    ///
    /// Expressed as a single positive property rather than "not failed" so that a new
    /// result value added later defaults to denying access rather than granting it.
    /// </summary>
    public bool GrantsAccess => Result == VerificationResult.Passed;

    public int DurationMs =>
        (int)((CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalMilliseconds;
}
