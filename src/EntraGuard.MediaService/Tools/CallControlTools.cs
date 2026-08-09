using System.Diagnostics;
using Azure.Communication.CallAutomation;
using Azure.Core;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Policy;
using EntraGuard.Shared.Sessions;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Tools;

/// <summary>
/// Speaks a warning into the live call.
///
/// This is the action that justifies bidirectional streaming rather than receive-only, and
/// it is the only remediation that reaches the victim inside the attack window. Everything
/// else — revoking sessions, elevating risk, raising an incident — happens after the fact
/// and is invisible to the person currently being manipulated.
///
/// It is also deliberately reversible and therefore NOT confidence-gated: interrupting a
/// legitimate support call with a caution costs a moment of confusion, while staying silent
/// through a real attack costs the account.
/// </summary>
public sealed class InjectVoiceWarningTool(
    LiveCallRegistry registry,
    IOptions<EntraGuardOptions> options,
    TokenCredential credential,
    ILogger<InjectVoiceWarningTool> logger) : IRemediationTool
{
    private readonly EntraGuardOptions _options = options.Value;

    public RemediationAction Action => RemediationAction.InjectVoiceWarning;

    public string Description =>
        "Interrupt the live call with a synthesised spoken warning telling the protected user " +
        "that this call shows signs of an authentication scam and that they should not approve " +
        "any prompt or read out any code. Reversible and non-destructive.";

    /// <summary>
    /// Deliberately plain and instruction-led rather than alarming.
    ///
    /// The listener is mid-manipulation and already being told a confident story by someone
    /// who sounds authoritative. Competing on urgency loses. Naming the specific actions to
    /// refuse gives them something concrete to hold onto, and telling them to hang up and
    /// dial a known number is the one instruction that defeats the attack regardless of how
    /// the rest of the call goes.
    /// </summary>
    private const string WarningScript =
        "This is an automated security alert from EntraGuard. " +
        "This call shows signs of an authentication scam. " +
        "Do not approve any sign-in prompt, and do not read out any verification code. " +
        "Please hang up and contact your IT help desk directly on a number you already know.";

    public async Task<RemediationResult> ExecuteAsync(
        CallSession session,
        CancellationToken cancellationToken = default)
    {
        var call = registry.Get(session.SessionId);
        if (call?.SendAudioAsync is null)
        {
            return RemediationResult.Unavailable(Action,
                "Media stream is not connected — cannot play audio into this call.");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var pcm = await SynthesiseAsync(session.SampleRate, cancellationToken);
            await call.SendAudioAsync(pcm, cancellationToken);
            stopwatch.Stop();

            logger.LogWarning("Injected spoken warning into call {SessionId}.", session.SessionId);

            return RemediationResult.Success(Action,
                "Spoken warning played into the live call, advising the user not to approve prompts or share codes.",
                0, (int)stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to inject voice warning into {SessionId}.", session.SessionId);
            return RemediationResult.Failed(Action, $"Speech synthesis or playback failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Synthesise the warning as raw PCM matching the call's sample rate.
    ///
    /// Output format must match what ACS negotiated on the stream — 24 kHz audio pushed
    /// into a 16 kHz call plays back as chipmunk speech, which is both useless and a
    /// memorable way to lose a demo.
    /// </summary>
    private async Task<byte[]> SynthesiseAsync(int sampleRate, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
            cancellationToken);

        var config = SpeechConfig.FromAuthorizationToken(token.Token, _options.SpeechRegion);
        config.SpeechSynthesisVoiceName = _options.WarningVoice;
        config.SetSpeechSynthesisOutputFormat(sampleRate == 16000
            ? SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm
            : SpeechSynthesisOutputFormat.Raw24Khz16BitMonoPcm);

        using var synthesizer = new SpeechSynthesizer(config, audioConfig: null);
        using var result = await synthesizer.SpeakTextAsync(WarningScript);

        if (result.Reason != ResultReason.SynthesizingAudioCompleted)
        {
            throw new InvalidOperationException($"Speech synthesis failed: {result.Reason}");
        }

        return result.AudioData;
    }
}

/// <summary>
/// Hangs up the call.
///
/// The most disruptive thing EntraGuard can do, and the only one a user experiences as the
/// system acting against them rather than for them. The policy gate requires both
/// near-certainty and an imminent approval before this is even proposed.
/// </summary>
public sealed class TerminateCallTool(
    CallAutomationClient callAutomation,
    LiveCallRegistry registry,
    ILogger<TerminateCallTool> logger) : IRemediationTool
{
    public RemediationAction Action => RemediationAction.TerminateCall;

    public string Description =>
        "Immediately disconnect the call for all participants. Use only when a credential " +
        "compromise is seconds from completing and a warning will not arrive in time.";

    public async Task<RemediationResult> ExecuteAsync(
        CallSession session,
        CancellationToken cancellationToken = default)
    {
        var call = registry.Get(session.SessionId);
        var connectionId = call?.CallConnectionId ?? session.CallConnectionId;

        if (string.IsNullOrEmpty(connectionId))
        {
            return RemediationResult.Unavailable(Action, "No active call connection to terminate.");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await callAutomation
                .GetCallConnection(connectionId)
                .HangUpAsync(forEveryone: true, cancellationToken);
            stopwatch.Stop();

            logger.LogWarning("Terminated call {SessionId} — imminent credential compromise.",
                session.SessionId);

            return RemediationResult.Success(Action,
                "Call disconnected for all participants to prevent an imminent credential compromise.",
                0, (int)stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to terminate call {SessionId}.", session.SessionId);
            return RemediationResult.Failed(Action, $"Hang-up failed: {ex.Message}");
        }
    }
}
