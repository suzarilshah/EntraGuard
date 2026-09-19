using Azure.Communication;
using Azure.Communication.CallAutomation;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Hubs;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Verification;
using EntraGuard.MediaService.Configuration;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Sessions;

/// <summary>Who to call, and what to tell them it is for.</summary>
/// <param name="Upn">Display and audit only.</param>
/// <param name="ObjectId">Entra object ID of the subject.</param>
/// <param name="TenantId">Subject's home tenant, needed to read their own directory.</param>
/// <param name="TeamsUserId">Teams object ID to ring, when the channel is Teams.</param>
/// <param name="AcsUserId">ACS identity to ring, when the channel is a registered device.</param>
/// <param name="EndpointKind">"teams", or the device kind.</param>
/// <param name="ApplicationName">Spoken aloud and written to the audit row.</param>
public sealed record CallTarget(
    string Upn,
    string ObjectId,
    string TenantId,
    string? TeamsUserId,
    string? AcsUserId,
    string EndpointKind,
    string ApplicationName);

/// <summary>
/// Places a verification call.
///
/// <para>
/// Extracted from <c>VerificationEndpoint</c> so the relying-party path and the Entra
/// External Authentication Method path place calls the same way rather than each keeping
/// its own copy of the ACS setup.
/// </para>
/// </summary>
/// <remarks>
/// The duplication this avoids would have been the dangerous kind. Unmixed media, the
/// participant-to-role mapping, bidirectional streaming and the DTMF channel are not
/// configuration — they are the reason a coercer speaking the answer cannot satisfy the
/// challenge on the user's behalf. A second copy that omitted <c>MapParticipant</c> would
/// still ring, still transcribe, and silently lose every answer to "who said this?", which
/// is a failure this project has already had once.
///
/// What stays with the callers is what genuinely differs: the relying-party path checks
/// device registration, tenant channel policy and transaction binding against a signed-in
/// owner, none of which exist when Entra sends a user mid-sign-in.
/// </remarks>
public sealed class VerificationLauncher(
    CallAutomationClient callAutomation,
    VerificationRegistry registry,
    LiveCallRegistry callRegistry,
    VerificationCoordinator verifications,
    TransportProtection transport,
    IOptions<EntraGuardOptions> options,
    ILogger<VerificationLauncher> logger)
{
    /// <summary>
    /// Create the verification and wire the media session, without dialling yet.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DialAsync"/> because the relying-party path has work to do in
    /// between: tenant channel policy, transaction binding and the ledger write all need the
    /// verification to exist and all must happen before anybody's phone rings. Splitting here
    /// lets both callers share the ACS setup without the launcher having to know what a
    /// payment is.
    /// </remarks>
    public VerificationSession Create(CallTarget target)
    {
        var callingTeams = !string.IsNullOrWhiteSpace(target.TeamsUserId);
        var calleeId = callingTeams ? target.TeamsUserId! : target.AcsUserId!;

        var verification = registry.Create(target.Upn, target.ObjectId, calleeId, target.ApplicationName);
        verification.EndpointKind = target.EndpointKind;
        verification.SubjectTenantId = target.TenantId;

        // Created up front so the media socket has somewhere to attach the moment ACS dials
        // back, rather than racing the callback.
        var monitorSessionId = $"vmon-{verification.VerificationId}";
        var monitored = callRegistry.Create(monitorSessionId);
        monitored.Session.SubjectUpn = target.Upn;
        monitored.Session.SubjectObjectId = target.ObjectId;
        monitored.Session.IsVerificationCall = true;

        // Label the callee's channel. Attribution is not decoration: the knowledge challenge
        // listens for the protected user specifically, so an unlabelled channel means every
        // correct spoken answer is heard, transcribed, and then discarded as unattributable.
        monitored.Session.MapParticipant(
            callingTeams ? $"8:orgid:{target.TeamsUserId}" : target.AcsUserId!,
            SpeakerRole.ProtectedUser);

        verification.MonitorSessionId = monitorSessionId;
        verifications.LinkMonitorSession(monitorSessionId, verification.VerificationId);

        return verification;
    }

    /// <summary>Place the call for a verification that <see cref="Create"/> prepared.</summary>
    /// <exception cref="InvalidOperationException">The call could not be placed.</exception>
    public async Task<VerificationSession> DialAsync(
        VerificationSession verification,
        CallTarget target,
        CancellationToken cancellationToken)
    {
        var callingTeams = !string.IsNullOrWhiteSpace(target.TeamsUserId);
        var monitorSessionId = verification.MonitorSessionId!;
        var monitored = callRegistry.Get(monitorSessionId)
            ?? throw new InvalidOperationException("The media session was torn down before the call was placed.");

        // Unmixed so the Analyst can tell the user apart from anyone talking over them.
        // A coercer is, by definition, a second voice on the user's own line.
        var streaming = new MediaStreamingOptions(
            MediaStreamingAudioChannel.Unmixed,
            StreamingTransport.Websocket)
        {
            TransportUri = new Uri(transport.Url(
                $"{options.Value.PublicBaseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)}/ws/media/{monitorSessionId}")),
            MediaStreamingContent = MediaStreamingContent.Audio,
            StartMediaStreaming = true,
            EnableBidirectional = true,
            AudioFormat = AudioFormat.Pcm24KMono,
            EnableDtmfTones = true,
        };

        // A Teams leg gets a display name: the interop invite otherwise arrives as a call
        // from an unnamed external party, and "unknown caller wants to verify your identity"
        // is precisely the shape of the attack this defends against.
        var invite = callingTeams
            ? new CallInvite(new MicrosoftTeamsUserIdentifier(target.TeamsUserId))
            {
                SourceDisplayName = $"EntraGuard verification · {target.ApplicationName}",
            }
            : new CallInvite(new CommunicationUserIdentifier(target.AcsUserId));

        var createOptions = new CreateCallOptions(
            invite,
            new Uri(transport.Url($"{options.Value.PublicBaseUrl}/api/verify/callbacks/{verification.VerificationId}")))
        {
            MediaStreamingOptions = streaming,
        };

        // Without this ACS has no speech service to render TextSource with, and
        // StartRecognizing fails once the call is already up — surfacing as "the challenge
        // could not be played" rather than as a configuration error.
        if (!string.IsNullOrEmpty(options.Value.AiServicesEndpoint))
        {
            createOptions.CallIntelligenceOptions = new CallIntelligenceOptions
            {
                CognitiveServicesEndpoint = new Uri(options.Value.AiServicesEndpoint),
            };
        }

        try
        {
            var result = await callAutomation.CreateCallAsync(createOptions, cancellationToken);
            verification.CallConnectionId = result.Value.CallConnection.CallConnectionId;
            monitored.CallConnectionId = verification.CallConnectionId;
            monitored.Session.CallConnectionId = verification.CallConnectionId;

            logger.LogInformation(
                "Verification {Id} calling {Upn} on {Endpoint}.",
                verification.VerificationId, target.Upn,
                callingTeams ? $"Teams user {target.TeamsUserId}" : "soft-phone");

            return verification;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not place verification call for {Upn}.", target.Upn);

            // ACS reports a missing Teams federation grant as a bare authorization failure,
            // which reads as a bug in this service. It is not: it is the Teams tenant
            // declining calls from this ACS resource. Naming it is the difference between a
            // five-minute fix and an afternoon of guessing.
            var hint = callingTeams && ex is Azure.RequestFailedException { Status: 401 or 403 }
                ? " The Teams tenant has not allow-listed this Communication Services "
                + "resource, or the federation change has not propagated yet. "
                + "See docs/teams-setup.md, step 2."
                : string.Empty;

            registry.TryComplete(verification.VerificationId, VerificationResult.CallFailed,
                $"Could not place the verification call: {ex.Message}{hint}");

            throw new InvalidOperationException(
                $"Could not place the verification call.{hint}", ex);
        }
    }
}

public static class VerificationLauncherExtensions
{
    /// <summary>Create and dial in one step, for callers with nothing to do in between.</summary>
    public static async Task<VerificationSession> PlaceAsync(
        this VerificationLauncher launcher,
        CallTarget target,
        CancellationToken cancellationToken) =>
        await launcher.DialAsync(launcher.Create(target), target, cancellationToken);
}
