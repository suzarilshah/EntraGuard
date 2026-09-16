namespace EntraGuard.Shared.Detection;

/// <summary>
/// A verbatim span of transcript supporting a verdict.
///
/// Evidence is mandatory, not decorative. A risk score with no quotable basis cannot be
/// triaged by an analyst, cannot be appealed by the user it locked out, and cannot be
/// distinguished from a hallucination.
/// </summary>
/// <param name="Quote">Exact transcript text. Never paraphrased.</param>
/// <param name="Speaker">Which participant said it.</param>
/// <param name="OffsetMs">Offset from call start, for replay alignment.</param>
public sealed record EvidenceSpan(string Quote, SpeakerRole Speaker, long OffsetMs);

/// <summary>
/// Who is talking. Derived from the ACS unmixed-audio participant channel, so it is a
/// property of the media stream rather than an inference the model could get wrong.
/// </summary>
public enum SpeakerRole
{
    /// <summary>The protected Entra ID user.</summary>
    ProtectedUser,

    /// <summary>The other party — the suspected attacker.</summary>
    Caller,

    /// <summary>Channel could not be attributed to a known participant.</summary>
    Unknown,

    /// <summary>
    /// EntraGuard itself — the verification agent, or a scripted prompt it played.
    ///
    /// Exists because without it EntraGuard was scored as the attacker. Its speech went into
    /// the transcript as <see cref="Unknown"/>, and an unidentified speaker telling somebody
    /// to key a two-digit code is the textbook signature of the vishing this system detects.
    /// Measured across five live calls: the coercion score tracked how much EntraGuard itself
    /// had spoken — 0 on a call it never spoke on, 70 and 75 on two calls where the caller was
    /// demonstrably alone, against a blocking threshold of 60.
    ///
    /// Labelled rather than removed, deliberately. An agent that has been talked into
    /// something is itself evidence, so the Analyst must still see what it said — it only
    /// needs to know who said it.
    /// </summary>
    Agent,
}

/// <summary>
/// One Analyst-agent verdict over a rolling transcript window.
///
/// Produced by <c>AnalystAgent</c> via Azure OpenAI structured output, then consumed by
/// <c>PolicyGate</c>. Deliberately a pure value type: the gate must be testable without
/// touching Azure.
/// </summary>
public sealed record RiskAssessment
{
    /// <summary>Composite scam likelihood, 0-100.</summary>
    public required double RiskScore { get; init; }

    /// <summary>
    /// The model's confidence in its own verdict, 0.0-1.0.
    ///
    /// Tracked separately from <see cref="RiskScore"/> because they fail differently. A
    /// confidently-detected scam and a tentative guess that "this might be very bad" are
    /// not the same input to an irreversible action, and collapsing them into one number
    /// loses exactly the distinction the policy gate needs.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>How far the victim has been drawn in.</summary>
    public required ComplianceStage Stage { get; init; }

    /// <summary>Techniques observed. Empty on a benign call.</summary>
    public IReadOnlyList<ScamVector> Vectors { get; init; } = [];

    /// <summary>Transcript spans supporting the verdict.</summary>
    public IReadOnlyList<EvidenceSpan> Evidence { get; init; } = [];

    /// <summary>Natural-language justification, surfaced to the SOC analyst.</summary>
    public string Rationale { get; init; } = string.Empty;

    /// <summary>When this verdict was produced (UTC).</summary>
    public DateTimeOffset AssessedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Analyst round-trip latency, for the portal's performance panel.</summary>
    public int AnalysisLatencyMs { get; init; }

    /// <summary>A benign, low-risk baseline. Used before the first verdict lands.</summary>
    public static RiskAssessment Benign { get; } = new()
    {
        RiskScore = 0,
        Confidence = 1.0,
        Stage = ComplianceStage.Unaware,
        Rationale = "No analysis performed yet.",
    };
}
