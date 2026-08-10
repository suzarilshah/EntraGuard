using EntraGuard.Shared.Policy;

namespace EntraGuard.MediaService.Configuration;

/// <summary>
/// Runtime configuration, bound from environment variables set by the Container App.
///
/// No secrets: every Azure dependency is reached with the user-assigned managed identity,
/// so these are endpoints and switches only.
/// </summary>
public sealed class EntraGuardOptions
{
    public const string SectionName = "EntraGuard";

    /// <summary>ACS resource endpoint.</summary>
    public string AcsEndpoint { get; set; } = string.Empty;

    /// <summary>Local-dev fallback. Empty in Azure, where the managed identity is used instead.</summary>
    public string AcsConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Public HTTPS base for callbacks and the media socket.
    ///
    /// ACS dials this from outside, so it must be publicly resolvable — the Container App
    /// FQDN in Azure, a dev tunnel locally. A private or loopback URL produces ACS error
    /// 8581 ("Transport url is not valid or web socket server is not operational"), which
    /// is reported against the answer call rather than against this setting.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    public string SpeechEndpoint { get; set; } = string.Empty;
    public string SpeechRegion { get; set; } = "eastus";

    /// <summary>
    /// Azure AI services (multi-service) endpoint that ACS uses for speech.
    ///
    /// Required for TextSource — the spoken verification challenge and closing message are
    /// synthesised by ACS itself, not by this service. Without it, StartRecognizing fails at
    /// call time with the call already connected, which is exactly the "challenge could not
    /// be played" failure. A Speech-only account does not satisfy this; ACS needs a
    /// multi-service resource.
    /// </summary>
    public string AiServicesEndpoint { get; set; } = string.Empty;

    public string OpenAiEndpoint { get; set; } = string.Empty;
    public string OpenAiDeployment { get; set; } = "entraguard-analyst";

    /// <summary>
    /// Azure OpenAI REST API version.
    ///
    /// Configurable because <c>reasoning_effort</c> is only accepted from certain preview
    /// versions onward, and which one varies by model family. If the Analyst starts
    /// returning 400s after a model change, this is the first thing to move — the response
    /// body names the offending parameter directly.
    /// </summary>
    public string OpenAiApiVersion { get; set; } = "2024-12-01-preview";

    public string DceEndpoint { get; set; } = string.Empty;
    public string DcrImmutableId { get; set; } = string.Empty;
    public string CallAnalysisStream { get; set; } = "Custom-EntraGuard_CallAnalysis_CL";
    public string RemediationStream { get; set; } = "Custom-EntraGuard_Remediation_CL";
    public string VerificationStream { get; set; } = "Custom-EntraGuard_Verification_CL";

    public string StorageAccountName { get; set; } = string.Empty;

    /// <summary>
    /// Azure OpenAI Realtime endpoint for the conversational voice agent.
    ///
    /// A SEPARATE account from the Analyst's: realtime models are bound to their account's
    /// region and eastus — where the rest of this system lives — has none. Empty disables
    /// the agent and the call falls back to scripted prompts, which is a degradation rather
    /// than a failure.
    /// </summary>
    public string RealtimeEndpoint { get; set; } = string.Empty;

    /// <summary>Deployment name of the realtime model.</summary>
    public string RealtimeDeployment { get; set; } = string.Empty;

    /// <summary>
    /// Client ID of the multitenant application EntraGuard federates into other tenants as.
    ///
    /// Empty means cross-tenant Graph is off: users from other directories still verify,
    /// they simply get the checks that need no directory access.
    /// </summary>
    public string ServiceClientId { get; set; } = string.Empty;

    /// <summary>Our own tenant, so the federated path is skipped where it is unnecessary.</summary>
    public string HomeTenantId { get; set; } = string.Empty;

    /// <summary>
    /// Internal URL of the SpeechBrain speaker-verification sidecar.
    ///
    /// Empty disables voice biometrics entirely, and that must remain a silent, harmless
    /// state: a deployment without the sidecar verifies exactly as it did before, because
    /// voice is a supplementary factor and an absent scorer must never refuse anyone.
    /// </summary>
    public string VoiceprintUrl { get; set; } = string.Empty;

    /// <summary>
    /// "enforce" lets voice scores trigger step-up. Anything else observes only.
    ///
    /// Defaults to observing. Published equal error rates come from studio recordings;
    /// thresholds have to be earned against this deployment's own telephony audio before
    /// they are allowed to affect a real person's access.
    /// </summary>
    public bool VoiceEnforce { get; set; }

    /// <summary>Cosine similarity at or above which a voice is accepted.</summary>
    public double VoiceAcceptThreshold { get; set; } = Shared.Voice.VoiceThresholds.DefaultAccept;

    /// <summary>Cosine similarity at or below which a voice is treated as a different speaker.</summary>
    public double VoiceRejectThreshold { get; set; } = Shared.Voice.VoiceThresholds.DefaultReject;

    /// <summary>Client ID whose access tokens are accepted on the voice-profile endpoints.</summary>
    public string RpClientId { get; set; } = string.Empty;

    /// <summary>
    /// Key material for encrypting voice templates at rest.
    ///
    /// Empty disables enrolment outright rather than falling back to storing a biometric in
    /// the clear. A voiceprint is not a password — the user cannot change their voice after
    /// a breach — so "encrypt it if convenient" is not an acceptable posture.
    /// </summary>
    public string VoiceprintKey { get; set; } = string.Empty;

    /// <summary>Entra ID object ID of the Conditional Access quarantine group (degraded tier).</summary>
    public string QuarantineGroupId { get; set; } = string.Empty;

    /// <summary>
    /// Whether the tenant can actually write Identity Protection risk state.
    /// Resolved once by scripts/00-preflight.sh — not probed mid-incident.
    /// </summary>
    public TenantRiskTier RiskTier { get; set; } = TenantRiskTier.Degraded;

    /// <summary>
    /// Master switch for live remediation. When false the full pipeline runs and reports
    /// but takes no outward action — shadow mode, for demos and for piloting in a real tenant.
    /// </summary>
    public bool AutonomousActionsEnabled { get; set; } = true;

    /// <summary>
    /// How often the Analyst re-scores the conversation.
    ///
    /// Every recognised phrase would be wasteful and would make the portal's risk gauge
    /// jitter; much slower and the intervention arrives after the victim has already
    /// approved. Three seconds keeps the detect-to-act loop inside the attack window.
    /// </summary>
    public TimeSpan AnalysisInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How much recent conversation the Analyst scores on each pass.</summary>
    public TimeSpan AnalysisWindow { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Spoken language for recognition and for the injected warning.</summary>
    public string SpeechLanguage { get; set; } = "en-US";

    /// <summary>Neural voice used for the in-call warning.</summary>
    public string WarningVoice { get; set; } = "en-US-AvaMultilingualNeural";
}
