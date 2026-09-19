namespace EntraGuard.Shared.Verification;

/// <summary>
/// The three kinds of authentication factor Microsoft Entra ID distinguishes.
/// </summary>
/// <remarks>
/// Entra requires the second factor to be a different TYPE from the first, which is the
/// whole reason this enum exists rather than a list of method names. A password is
/// knowledge; satisfying MFA after it needs possession or inherence, and saying "we did
/// another knowledge check" is not an answer.
/// </remarks>
public enum FactorType
{
    /// <summary>Something you know.</summary>
    Knowledge,

    /// <summary>Something you have.</summary>
    Possession,

    /// <summary>Something you are.</summary>
    Inherence,
}

/// <summary>
/// What EntraGuard is allowed to tell Entra ID it proved.
///
/// <para>
/// Pure and free of I/O, for the same reason <c>PolicyGate</c> and <c>ChallengeSelection</c>
/// are: the <c>acr</c> and <c>amr</c> claims in the response token are the entire basis on
/// which Entra decides that multifactor authentication happened. They are a security
/// assertion about a real person's sign-in, not a formatting detail, and a rule nobody can
/// test exhaustively is a claim rather than a control.
/// </para>
///
/// <para>
/// The values are Microsoft's, not ours. They are fixed strings from the External
/// Authentication Method reference and cannot be invented, extended or abbreviated.
/// </para>
/// </summary>
public static class EamClaims
{
    /// <summary>
    /// What a completed EntraGuard call honestly establishes.
    ///
    /// <para>
    /// <c>tel</c> — "confirmation by telephone" — and therefore possession. EntraGuard rings
    /// an endpoint enrolled to that directory account and the caller keys a number shown on
    /// a screen only they can see. That is possession of the endpoint, and it is the
    /// strongest thing this system can say without overstating.
    /// </para>
    ///
    /// <para>
    /// NOT <c>vbm</c>, "biometric with voiceprint", though it describes this product almost
    /// too well. Voice runs in observe mode: it is scored, recorded and shown, and it cannot
    /// refuse anybody. The same enrolled speaker has scored 0.278 on one call and 0.7401 on
    /// another. Sending <c>vbm</c> would tell Entra an inherence factor was verified when
    /// nothing about the voice can fail a sign-in, and Entra would then grant MFA on the
    /// strength of it. Claiming a factor you do not enforce is the one lie this file exists
    /// to prevent.
    /// </para>
    ///
    /// <para>
    /// Move to <c>vbm</c> only when VOICE_MODE enforces and a genuine speaker reliably lands
    /// in the genuine band — at which point this constant changes and its tests change with
    /// it, deliberately, rather than drifting.
    /// </para>
    /// </summary>
    public const string CallAmr = "tel";

    /// <summary>
    /// Every <c>amr</c> value Entra accepts, and the factor type it maps each one to.
    /// </summary>
    /// <remarks>
    /// Reproduced from Microsoft's published table. Kept whole rather than trimmed to the
    /// one value this system sends, because <see cref="SatisfiableAcr"/> has to reason about
    /// what a request is asking for, and a request names methods we do not implement.
    /// </remarks>
    private static readonly Dictionary<string, FactorType> MethodTypes = new(StringComparer.Ordinal)
    {
        ["face"] = FactorType.Inherence,
        ["fido"] = FactorType.Possession,
        ["fpt"] = FactorType.Inherence,
        ["hwk"] = FactorType.Possession,
        ["iris"] = FactorType.Inherence,
        ["otp"] = FactorType.Possession,
        ["pop"] = FactorType.Possession,
        ["retina"] = FactorType.Inherence,
        ["sc"] = FactorType.Possession,
        ["sms"] = FactorType.Possession,
        ["swk"] = FactorType.Possession,
        ["tel"] = FactorType.Possession,
        ["vbm"] = FactorType.Inherence,
    };

    /// <summary>
    /// Every <c>acr</c> value Entra requests, and which factor types satisfy it.
    /// </summary>
    private static readonly Dictionary<string, FactorType[]> AcrRequirements = new(StringComparer.Ordinal)
    {
        ["knowledge"] = [FactorType.Knowledge],
        ["possession"] = [FactorType.Possession],
        ["inherence"] = [FactorType.Inherence],
        ["knowledgeorpossession"] = [FactorType.Knowledge, FactorType.Possession],
        ["knowledgeorinherence"] = [FactorType.Knowledge, FactorType.Inherence],
        ["possessionorinherence"] = [FactorType.Possession, FactorType.Inherence],
        ["knowledgeorpossessionorinherence"] =
            [FactorType.Knowledge, FactorType.Possession, FactorType.Inherence],
    };

    /// <summary>The factor type a given <c>amr</c> method proves, or null if unrecognised.</summary>
    public static FactorType? TypeOf(string? method) =>
        method is not null && MethodTypes.TryGetValue(method, out var type) ? type : null;

    /// <summary>
    /// Which <c>acr</c> value this call may assert, or null if it may assert none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null is a real and correct answer, not an error to be worked around. If the sign-in's
    /// first factor was itself possession-based — a FIDO key, a passkey — Entra asks for
    /// <c>inherence</c>, and a telephone call cannot supply it. The honest response is to
    /// decline and let Entra fail the sign-in, which is what the caller's own security policy
    /// asked for.
    /// </para>
    ///
    /// <para>
    /// The alternative, returning <c>vbm</c> because the call happened to carry a voice, would
    /// pass that sign-in by asserting a biometric check that observe-mode voice cannot fail.
    /// That is the failure this whole class is shaped to make impossible: there is no path
    /// through this method that returns an acr the achieved factor does not satisfy.
    /// </para>
    /// </summary>
    /// <param name="requested">
    /// The <c>acr</c> values from Entra's <c>claims</c> parameter. Order is Entra's
    /// preference order and is honoured.
    /// </param>
    /// <param name="achieved">The factor type this call actually established.</param>
    public static string? SatisfiableAcr(IReadOnlyList<string>? requested, FactorType achieved)
    {
        if (requested is null || requested.Count == 0)
        {
            return null;
        }

        foreach (var acr in requested)
        {
            if (AcrRequirements.TryGetValue(acr, out var accepts) && accepts.Contains(achieved))
            {
                return acr;
            }
        }

        return null;
    }

    /// <summary>
    /// Which <c>amr</c> method this call may assert, or null if the request will not take it.
    /// </summary>
    /// <remarks>
    /// Exactly one value is returned. Entra's reference is explicit that the response must
    /// carry a single method, and returning the set of everything arguably true — a call, a
    /// number match, a voice sample, four knowledge questions — would both violate that and
    /// overstate what any one of them proves.
    ///
    /// An empty or absent request list is treated as accepting the default. Entra populates
    /// it in practice; a provider that refused to answer without it would fail closed on a
    /// technicality rather than on a security property.
    /// </remarks>
    public static string? ChooseAmr(IReadOnlyList<string>? requested, string method = CallAmr)
    {
        if (!MethodTypes.ContainsKey(method))
        {
            return null;
        }

        return requested is null || requested.Count == 0 || requested.Contains(method, StringComparer.Ordinal)
            ? method
            : null;
    }
}
