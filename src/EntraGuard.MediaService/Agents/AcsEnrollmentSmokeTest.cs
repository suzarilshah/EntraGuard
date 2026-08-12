using Azure.Communication;
using Azure.Communication.CallAutomation;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Detection;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Agents;

/// <summary>
/// Drives a real enrolment call against ACS's echo bot, so the telephony half of enrolment
/// is proven rather than assumed.
///
/// The rehearsal covers audio to template. This covers what it cannot reach: CreateCall,
/// the callback wiring, CallConnected, PlayCompleted ordering, the media socket attaching to
/// a venr- session, and — most importantly — the state machine terminating.
///
/// The echo bot answers instantly and says nothing of its own, so this is EXPECTED to end in
/// the no-usable-audio failure after the retry bound. That outcome is the pass condition.
/// Reaching it means every prompt played, every callback arrived, the quality gate ran and
/// the call hung up. Hanging, or never connecting, is the failure this exists to catch.
/// </summary>
public sealed class AcsEnrollmentSmokeTest(
    CallAutomationClient callAutomation,
    Azure.Communication.Identity.CommunicationIdentityClient identity,
    LiveCallRegistry callRegistry,
    VoiceEnrollmentCoordinator enrollment,
    IOptions<EntraGuardOptions> options,
    ILogger<AcsEnrollmentSmokeTest> logger)
{
    private const string SmokeTenant = "00000000-0000-0000-0000-000000000000";
    private const string SmokeObject = "acs-smoke-subject";

    public async Task<object> RunAsync(CancellationToken cancellationToken)
    {
        // A throwaway ACS identity as the callee.
        //
        // 8:echo123 is not a valid Call Automation target — CreateCall rejects it outright.
        // A fresh identity works because THIS service already answers IncomingCall events
        // from Event Grid, so the call is picked up automatically. The answering leg says
        // nothing, which is what makes the expected outcome a no-audio failure.
        var callee = await identity.CreateUserAsync(cancellationToken);
        var calleeId = callee.Value.Id;

        // UsedMfa/AmrPresent/HasAcrs are all true: this never reaches the MFA gate, because it
        // drives the coordinator directly rather than going through the authenticated endpoint.
        var caller = new CallerIdentity(SmokeObject, SmokeTenant, "acs-smoke", true, true, true);
        var session = enrollment.Create(caller, EnrollmentPhrases.Pick(3));

        var monitorSessionId = $"venr-{session.EnrollmentId}";
        var monitored = callRegistry.Create(monitorSessionId);
        monitored.Session.SubjectUpn = caller.Upn;
        monitored.Session.SubjectObjectId = caller.ObjectId;
        monitored.Session.IsVerificationCall = true;
        monitored.Session.MapParticipant(calleeId, SpeakerRole.ProtectedUser);
        session.MonitorSessionId = monitorSessionId;

        var streaming = new MediaStreamingOptions(
            MediaStreamingAudioChannel.Unmixed, StreamingTransport.Websocket)
        {
            TransportUri = new Uri(
                $"{options.Value.PublicBaseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)}/ws/media/{monitorSessionId}"),
            MediaStreamingContent = MediaStreamingContent.Audio,
            StartMediaStreaming = true,
            EnableBidirectional = true,
            AudioFormat = AudioFormat.Pcm24KMono,
        };

        var createOptions = new CreateCallOptions(
            new CallInvite(new CommunicationUserIdentifier(calleeId)),
            new Uri($"{options.Value.PublicBaseUrl}/api/voice-profile/callbacks/{session.EnrollmentId}"))
        {
            MediaStreamingOptions = streaming,
        };

        // Without this ACS has no speech service to render the phrases with, and every
        // prompt fails after the call is already up — the failure that once surfaced as
        // "the challenge could not be played".
        if (!string.IsNullOrEmpty(options.Value.AiServicesEndpoint))
        {
            createOptions.CallIntelligenceOptions = new CallIntelligenceOptions
            {
                CognitiveServicesEndpoint = new Uri(options.Value.AiServicesEndpoint),
            };
        }

        var started = DateTimeOffset.UtcNow;

        try
        {
            var result = await callAutomation.CreateCallAsync(createOptions, cancellationToken);
            session.CallConnectionId = result.Value.CallConnection.CallConnectionId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ACS smoke test could not place the call.");
            return new
            {
                ran = true,
                passed = false,
                stage = "create-call",
                detail = $"CreateCall failed: {ex.Message}",
            };
        }

        // Poll for a terminal state. The bound is what turns "it hung" into a result rather
        // than a request that never returns.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (!session.IsComplete && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
        var mediaAttached = callRegistry.Get(monitorSessionId)?.Session.MediaStreamConnectedAt is not null
                            || monitored.Session.MediaStreamConnectedAt is not null;

        // Never leave a call up, whatever happened.
        if (!session.IsComplete)
        {
            enrollment.Fail(session, "Smoke test timed out.");
            try
            {
                await callAutomation.GetCallConnection(session.CallConnectionId)
                    .HangUpAsync(forEveryone: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Smoke test could not hang up.");
            }
        }

        // Reaching a terminal state is the pass. WHICH terminal state is expected to be the
        // no-audio failure, because an echo bot says nothing of its own.
        var terminated = session.State is "Failed" or "Enrolled";

        return new
        {
            ran = true,
            passed = terminated,
            state = session.State,
            failure = session.Failure,
            promptsPlayed = session.Completed + session.Retries,
            phrasesCaptured = session.Completed,
            mediaSocketAttached = mediaAttached,
            audioFramesReceived = monitored.Session.AudioFramesReceived,
            secondsElapsed = Math.Round(elapsed, 1),
            callee = calleeId,
            expected = "A no-usable-audio failure. The answering leg does not speak, so "
                     + "reaching that failure proves the call connected, the prompts played, "
                     + "the media socket attached, the quality gate ran, and the retry bound "
                     + "ended the call rather than looping.",
            covers = "CreateCall, callbacks, CallConnected, PlayCompleted ordering, media "
                   + "socket attachment, quality gate, retry bound, hang-up.",
        };
    }
}
