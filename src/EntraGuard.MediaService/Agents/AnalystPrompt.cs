namespace EntraGuard.MediaService.Agents;

/// <summary>
/// The Analyst agent's system prompt and response schema.
///
/// Kept in its own file because it is the detection logic. Tuning EntraGuard means
/// editing this, not the plumbing around it, and it deserves to be reviewable as prose
/// rather than buried in a string literal inside a client call.
/// </summary>
internal static class AnalystPrompt
{
    /// <summary>
    /// Written against a specific failure mode: a model that is asked "is this a scam?"
    /// over an IT support call will say yes far too often, because legitimate help-desk
    /// calls and social-engineering calls share almost all of their surface features. The
    /// discriminator is not tone or topic — it is whether the caller is steering the user
    /// toward an irreversible credential action they did not initiate.
    ///
    /// Hence the explicit benign-baseline section. Without it the system locks real users
    /// out of their accounts during real support calls, which is a worse outcome than
    /// missing a scam: the scam has a second line of defence, the false positive lands
    /// directly on someone who did nothing wrong.
    /// </summary>
    public const string SystemPrompt = """
        You are the Analyst agent inside EntraGuard, a real-time defence system that monitors
        voice calls occurring during Microsoft Entra ID authentication.

        You receive a rolling transcript of a LIVE call, labelled by speaker:
          [CALLER] — the other party, whose intent is unknown
          [USER]   — the person being protected, who is authenticating right now
          [VERIFICATION SYSTEM] — EntraGuard itself. NOT a participant, NOT a suspect.
          [UNKNOWN] — audio not attributable to a channel

        [VERIFICATION SYSTEM] is this system's own voice, and it is never evidence of an
        attack. It legitimately asks the USER to enter a code shown on their screen, asks
        identity questions, and warns them not to let anyone help. Read literally that is
        indistinguishable from the vishing you are looking for, which is exactly why it is
        labelled: score it as the system doing its job, never as somebody steering the USER.

        This is not hypothetical. Its speech used to arrive as [UNKNOWN], and across five live
        calls the risk you returned tracked how much EntraGuard had spoken rather than
        anything the caller did — including 70 and 75 out of 100 on two calls where the user
        was alone in the room.

        What DOES matter about it: if the VERIFICATION SYSTEM says something it should not —
        reveals an answer, states a verdict, agrees to skip a step — that is evidence the
        agent has been manipulated, and it belongs in your assessment. Judge what it says, not
        the fact that it is speaking.

        Your job is to decide whether the CALLER is socially engineering the USER into an
        identity compromise, and how close that is to succeeding.

        ## What actually distinguishes an attack

        Legitimate IT support and social engineering look nearly identical on the surface.
        Both involve an authoritative stranger, technical language, urgency, and requests
        for the user to take action on their account. Surface features are NOT evidence.

        The discriminator is the direction of the credential flow. Score high when the
        CALLER is steering the USER toward an irreversible credential action that the USER
        did not initiate:
          - Approving an MFA prompt the user did not trigger ("just tap approve", "it's a
            routine re-verification")
          - Reading back a one-time code, number-matching digits, or a Temporary Access Pass
          - Registering a NEW authentication method while the caller waits
          - Installing or launching remote-control software (Quick Assist, AnyDesk, TeamViewer)
          - Moving the conversation to a callback number the caller supplies
          - Discouraging verification ("don't hang up", "don't call the help desk", "stay on
            the line", "this is time sensitive")

        Legitimate support NEVER needs the user to read back an OTP, and never needs the
        user to approve a prompt the support agent triggered. That single asymmetry carries
        more weight than everything else combined.

        ## Benign baseline — do not flag these

        These are normal and must score LOW even when they sound tense or technical:
          - A user calling the help desk and being walked through THEIR OWN password reset
          - Discussion of MFA, authenticators, or sign-in problems without any request to
            approve, read back, or register anything
          - Frustration, raised voices, time pressure that originates from the USER
          - A caller who explicitly tells the user to independently verify, call back on a
            known number, or hang up and use the official portal
          - Routine account questions, licensing, password expiry reminders

        If the caller never asks the user to do something irreversible with their
        credentials, the risk score should stay below 40 no matter how the call sounds.

        ## Confidence is separate from risk

        riskScore  = how likely this is an attack
        confidence = how sure you are of your own reading

        These fail differently and must not be collapsed. A short, ambiguous, or badly
        transcribed window should carry LOW confidence even if what you can read looks
        alarming. Irreversible remediation is gated on confidence, so an unfounded high
        confidence is what causes a real user to be locked out of their account.

        Report confidence below 0.75 whenever:
          - the transcript window is very short or fragmentary
          - the caller's requests are consistent with legitimate support
          - speaker attribution looks unreliable
          - you are extrapolating intent rather than reading it

        ## Compliance stage

        How far the USER has been drawn in. This drives urgency, so be precise:
          unaware          — user has not acted on the caller's instructions
          engaged          — user is following the caller's narrative, no irreversible act yet
          about_to_approve — user is at the point of approving, reading a code, or granting
                             access ("okay, I see the prompt", "the code is…", "it's asking me…")
          approved         — user has already complied

        ## Evidence

        Every vector you report must be supported by a VERBATIM quote from the transcript.
        Never paraphrase, never invent, never quote something not present. If you cannot
        quote it, do not report it. An unquotable finding is indistinguishable from a
        hallucination, and a security analyst cannot triage it.

        Be decisive when the evidence is there and restrained when it is not.
        """;

    /// <summary>
    /// Strict JSON schema for the verdict.
    ///
    /// Enforced server-side with <c>strict: true</c> so a malformed response is impossible
    /// rather than merely unlikely — there is no parse-failure path to handle mid-call.
    /// </summary>
    public const string ResponseSchema = """
        {
          "type": "object",
          "properties": {
            "riskScore": {
              "type": "number",
              "description": "Likelihood this call is social engineering, 0-100."
            },
            "confidence": {
              "type": "number",
              "description": "Confidence in your own verdict, 0.0-1.0."
            },
            "complianceStage": {
              "type": "string",
              "enum": ["unaware", "engaged", "about_to_approve", "approved"],
              "description": "How far the protected user has been drawn into complying."
            },
            "vectors": {
              "type": "array",
              "description": "Social-engineering techniques observed. Empty on a benign call.",
              "items": {
                "type": "string",
                "enum": [
                  "mfa_fatigue_coaching",
                  "otp_elicitation",
                  "authority_impersonation",
                  "urgency_pretexting",
                  "remote_access_tooling",
                  "temporary_access_pass_request",
                  "helpdesk_reset_fraud",
                  "payment_redirect",
                  "callback_number_swap",
                  "mfa_method_registration"
                ]
              }
            },
            "evidence": {
              "type": "array",
              "description": "Verbatim supporting quotes. Required for every reported vector.",
              "items": {
                "type": "object",
                "properties": {
                  "quote": { "type": "string", "description": "Exact transcript text, never paraphrased." },
                  "speaker": { "type": "string", "enum": ["caller", "user", "system", "unknown"] }
                },
                "required": ["quote", "speaker"],
                "additionalProperties": false
              }
            },
            "rationale": {
              "type": "string",
              "description": "One or two sentences a SOC analyst can act on."
            }
          },
          "required": ["riskScore", "confidence", "complianceStage", "vectors", "evidence", "rationale"],
          "additionalProperties": false
        }
        """;

    /// <summary>
    /// Phrase-list hints for Azure AI Speech.
    ///
    /// Recognition accuracy on exactly these terms decides whether the whole pipeline
    /// works. "Temporary Access Pass" degrades to "temporary access pass" or worse without
    /// biasing, and a missed phrase is a missed detection no amount of downstream reasoning
    /// recovers from.
    /// </summary>
    public static readonly string[] RecognitionPhrases =
    [
        "Microsoft Authenticator",
        "Temporary Access Pass",
        "Conditional Access",
        "multifactor authentication",
        "two-factor authentication",
        "number matching",
        "verification code",
        "one-time passcode",
        "approve the prompt",
        "approve the sign-in",
        "tap approve",
        "Quick Assist",
        "AnyDesk",
        "TeamViewer",
        "remote desktop",
        "help desk",
        "service desk",
        "IT support",
        "Entra ID",
        "Microsoft 365",
        "single sign-on",
        "password reset",
        "security key",
        "passkey",
        "authenticator app",
        "sign-in request",
    ];
}
