using System.Text.Json.Serialization;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using EntraGuard.MediaService.Agents;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Sessions;
using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Detection;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Enrolling and deleting a voice profile.
///
/// Every endpoint here is authenticated, and the user is taken from the token rather than
/// the body. That is the difference between a biometric factor and a liability: an
/// enrolment endpoint that trusts a browser-supplied object ID lets an attacker register
/// their own voice against your account, after which the check confirms them and refuses
/// you — permanently, because you cannot change your voice.
/// </summary>
public static class VoiceEnrollmentEndpoint
{
    /// <summary>
    /// Bumped whenever the wording the user agrees to changes.
    ///
    /// Stored with the profile so it is always answerable which text a given person
    /// consented to. "They consented" is not a defensible record when the terms have since
    /// been rewritten.
    /// </summary>
    public const string ConsentVersion = "2026-08-10.v1";

    /// <summary>Utterances collected before a template is built.</summary>
    private const int PhraseCount = 3;

    public sealed record EnrollRequest
    {
        /// <summary>Must be exactly the current consent version. Absent means no consent.</summary>
        [JsonPropertyName("consentVersion")] public string? ConsentVersion { get; init; }

        /// <summary>Teams object ID to call. Must match the signed-in user.</summary>
        [JsonPropertyName("teamsUserId")] public string? TeamsUserId { get; init; }

        [JsonPropertyName("reenroll")] public bool Reenroll { get; init; }
    }

    public static void MapVoiceEnrollment(this IEndpointRouteBuilder app)
    {
        // ── Status ──────────────────────────────────────────────────────────
        app.MapGet("/api/voice-profile/me", async (
            HttpContext context,
            VoiceprintStore store,
            CancellationToken cancellationToken) =>
        {
            var caller = context.User.Caller();
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            var print = await store.GetAsync(caller.TenantId, caller.ObjectId, cancellationToken);

            // The template itself is never returned. It is biometric data, and an endpoint
            // that hands it back turns any stolen session into a permanent copy of it.
            return Results.Ok(new
            {
                enrolled = print is not null,
                consentVersion = print?.ConsentVersion,
                consentAt = print?.ConsentAt,
                enrolledAt = print?.EnrolledAt,
                quality = print?.SelfConsistency,
                currentConsentVersion = ConsentVersion,
                upn = caller.Upn,
            });
        })
        .RequireAuthorization(VoiceProfileAuth.Policy)
        .WithName("VoiceProfileStatus");

        // ── Delete ──────────────────────────────────────────────────────────
        //
        // Unconditional, immediate, and available to the user themselves. Biometric consent
        // that cannot be withdrawn is not consent, and a soft-delete the user cannot see is
        // not a deletion.
        app.MapDelete("/api/voice-profile/me", async (
            HttpContext context,
            VoiceprintStore store,
            Sinks.LogsIngestionSink audit,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var caller = context.User.Caller();
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            var deleted = await store.DeleteAsync(caller.TenantId, caller.ObjectId, cancellationToken);

            loggerFactory.CreateLogger("VoiceProfile").LogInformation(
                "Voice profile deletion requested by {Upn}: {Result}.",
                caller.Upn, deleted ? "removed" : "failed");

            // Withdrawal of consent. Under GDPR Article 9 this is the event a regulator asks
            // about first, and until now it existed only as a log line inside a container.
            await audit.WriteBiometricEventAsync(
                deleted ? "Deleted" : "DeleteFailed",
                caller.Upn, caller.ObjectId, caller.TenantId,
                reason: "Deleted by the user from their own settings.",
                usedMfa: caller.UsedMfa,
                cancellationToken: cancellationToken);

            return deleted
                ? Results.Ok(new { deleted = true })
                : Results.Problem("The voice profile could not be deleted.");
        })
        .RequireAuthorization(VoiceProfileAuth.Policy)
        .WithName("DeleteVoiceProfile");

        // ── Enrol ───────────────────────────────────────────────────────────
        app.MapPost("/api/voice-profile/enrollment/start", async (
            EnrollRequest request,
            HttpContext context,
            CallAutomationClient callAutomation,
            LiveCallRegistry callRegistry,
            VoiceEnrollmentCoordinator enrollment,
            VoiceprintStore store,
            MfaEvidence mfa,
            IOptions<EntraGuardOptions> options,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("VoiceProfile");

            var caller = context.User.Caller();
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            // MFA. Enrolling a biometric is a credential-registration event, and doing it
            // from a session backed by a password alone would let anyone with a stolen
            // password bind their own voice to the account — turning a leaked credential
            // into a permanent one.
            // Proof of a second factor.
            //
            // amr cannot be delivered in an access token — Entra emits it in ID tokens only,
            // and configuring it as an access-token optional claim is silently ignored. So
            // the evidence is the ID token, validated here and cross-checked to be the same
            // subject, rather than a claim we hoped would appear.
            if (options.Value.RequireMfaForEnrollment)
            {
                var why = await mfa.WhyNotMfaAsync(
                    caller, context.Request.Headers["X-Id-Token"].FirstOrDefault(), cancellationToken);

                if (why is not null)
                {
                    return Results.Json(new
                    {
                        error = "mfa_required",
                        detail = why + " Registering a biometric from a password-only session "
                               + "would let a stolen password bind a voice to this account.",
                    }, statusCode: StatusCodes.Status403Forbidden);
                }
            }

            if (!string.Equals(request.ConsentVersion, ConsentVersion, StringComparison.Ordinal))
            {
                return Results.BadRequest(new
                {
                    error = "consent_required",
                    currentConsentVersion = ConsentVersion,
                    detail = "Explicit consent to the current terms is required before a "
                           + "voice profile can be created.",
                });
            }

            if (!store.IsAvailable)
            {
                // Refuses rather than storing a biometric unencrypted.
                return Results.Problem(
                    "Voice enrolment is unavailable: no encryption key is configured for "
                    + "biometric templates.");
            }

            // The call target must be the person who signed in. Accepting an arbitrary
            // Teams id here would reintroduce exactly the hole the token closes.
            if (!string.Equals(request.TeamsUserId, caller.ObjectId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    error = "identity_mismatch",
                    detail = "A voice profile can only be enrolled for the signed-in account.",
                });
            }

            if (!request.Reenroll
                && await store.GetAsync(caller.TenantId, caller.ObjectId, cancellationToken) is not null)
            {
                return Results.Conflict(new
                {
                    error = "already_enrolled",
                    detail = "A voice profile already exists. Re-enrol explicitly to replace it.",
                });
            }

            var phrases = EnrollmentPhrases.Pick(PhraseCount);
            var session = enrollment.Create(caller, phrases, request.Reenroll);

            var monitorSessionId = $"venr-{session.EnrollmentId}";
            var monitored = callRegistry.Create(monitorSessionId);
            monitored.Session.SubjectUpn = caller.Upn;
            monitored.Session.SubjectObjectId = caller.ObjectId;
            monitored.Session.IsVerificationCall = true;
            monitored.Session.MapParticipant($"8:orgid:{caller.ObjectId}", SpeakerRole.ProtectedUser);
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

            var invite = new CallInvite(new MicrosoftTeamsUserIdentifier(caller.ObjectId))
            {
                SourceDisplayName = "EntraGuard voice enrolment",
            };

            var createOptions = new CreateCallOptions(
                invite,
                new Uri($"{options.Value.PublicBaseUrl}/api/voice-profile/callbacks/{session.EnrollmentId}"))
            {
                MediaStreamingOptions = streaming,
            };

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
                session.CallConnectionId = result.Value.CallConnection.CallConnectionId;

                logger.LogInformation(
                    "Voice enrolment {Id} calling {Upn} with {Count} phrases.",
                    session.EnrollmentId, caller.Upn, phrases.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not place the enrolment call for {Upn}.", caller.Upn);
                enrollment.Fail(session, $"Could not place the call: {ex.Message}");
            }

            return Results.Ok(enrollment.Describe(session));
        })
        .RequireAuthorization(VoiceProfileAuth.Policy)
        .RequireRateLimiting("enrollment-start")
        .WithName("StartVoiceEnrollment");

        // ── Enrolment progress ──────────────────────────────────────────────
        app.MapGet("/api/voice-profile/enrollment/{enrollmentId}", (
            string enrollmentId,
            HttpContext context,
            VoiceEnrollmentCoordinator enrollment) =>
        {
            var caller = context.User.Caller();
            var session = enrollment.Get(enrollmentId);

            if (caller is null || session is null)
            {
                return Results.NotFound();
            }

            // Enrolment ids are guessable enough that ownership has to be checked; without
            // this, one user could watch another's enrolment progress.
            return session.ObjectId == caller.ObjectId
                ? Results.Ok(enrollment.Describe(session))
                : Results.NotFound();
        })
        .RequireAuthorization(VoiceProfileAuth.Policy)
        .WithName("VoiceEnrollmentStatus");

        // ── ACS callbacks ───────────────────────────────────────────────────
        //
        // Anonymous, like the verification callbacks: ACS calls this, not a browser, and it
        // presents no user token. The enrolment id is the only handle, and it grants nothing
        // beyond advancing a call already in flight for a user who authenticated to start it.
        app.MapPost("/api/voice-profile/callbacks/{enrollmentId}", async (
            HttpContext context,
            string enrollmentId,
            VoiceEnrollmentCoordinator enrollment,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var events = CallAutomationEventParser.ParseMany(
                BinaryData.FromString(await reader.ReadToEndAsync(cancellationToken)));

            foreach (var callEvent in events)
            {
                switch (callEvent)
                {
                    case CallConnected:
                        await enrollment.OnConnectedAsync(enrollmentId, cancellationToken);
                        break;

                    case PlayCompleted:
                        await enrollment.OnPhraseSpokenAsync(enrollmentId, cancellationToken);
                        break;

                    case CallDisconnected:
                        await enrollment.OnDisconnectedAsync(enrollmentId);
                        break;
                }
            }

            return Results.Ok();
        })
        .ExcludeFromDescription();
    }
}
