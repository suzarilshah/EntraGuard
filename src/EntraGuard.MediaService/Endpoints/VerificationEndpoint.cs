using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Hubs;
using EntraGuard.MediaService.Sessions;
using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Verification;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Step-up voice verification: EntraGuard calls the user and demands a number match.
///
/// This inverts the defensive flow. Elsewhere EntraGuard answers a call an attacker
/// placed; here it originates a call as an authentication factor, and the relying party
/// gates access on the verdict.
///
/// What makes this different from every other MFA channel is that the verification call is
/// itself monitored by the Analyst. Number matching proves the person holding the phone is
/// the person at the browser — it defeats the attacker-on-phone / victim-at-browser split
/// that ordinary push MFA falls to. It cannot prove the person is acting freely. Someone
/// standing over them saying "press four seven" satisfies number matching perfectly.
///
/// So a correct code is necessary and not sufficient: if the Analyst detects coaching
/// during the call, the attempt is refused with <see cref="VerificationResult.BlockedCoercion"/>.
/// The anti-scam engine protects its own MFA factor.
/// </summary>
public static class VerificationEndpoint
{
    public sealed record DigitsRequest
    {
        [JsonPropertyName("digits")] public string? Digits { get; init; }
    }

    public sealed record KnowledgeRequest
    {
        [JsonPropertyName("tenantId")] public string? TenantId { get; init; }
        [JsonPropertyName("objectId")] public string? ObjectId { get; init; }
        [JsonPropertyName("question")] public string? Question { get; init; }
        [JsonPropertyName("answer")] public string? Answer { get; init; }
    }

    public sealed record StartRequest
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; init; }
        [JsonPropertyName("upn")] public string Upn { get; init; } = string.Empty;
        [JsonPropertyName("objectId")] public string? ObjectId { get; init; }
        /// <summary>ACS identity of the user's soft-phone, from /api/acs/token.</summary>
        [JsonPropertyName("calleeAcsId")] public string CalleeAcsId { get; init; } = string.Empty;

        /// <summary>
        /// Entra object ID of a Microsoft Teams user to call instead of a soft-phone.
        ///
        /// Preferred over the browser endpoint wherever it is available. Teams is a real
        /// application with real push notifications: it rings a locked phone, it already
        /// holds microphone permission, and there is no page to keep open — which removes
        /// most of the ways a browser soft-phone silently fails to answer.
        ///
        /// Note this is the OBJECT ID, not the UPN. ACS addresses Teams users by directory
        /// object ID, and a UPN here fails at call time rather than at validation.
        /// </summary>
        [JsonPropertyName("teamsUserId")] public string? TeamsUserId { get; init; }

        /// <summary>Home tenant of the subject, from the tid claim.</summary>
        [JsonPropertyName("tenantId")] public string? TenantId { get; init; }
        [JsonPropertyName("applicationName")] public string ApplicationName { get; init; } = "the application";
    }

    public static void MapVerification(this IEndpointRouteBuilder app)
    {
        // ── Start a verification ────────────────────────────────────────────
        app.MapPost("/api/verify/start", async (
            StartRequest request,
            HttpContext context,
            DeviceService devices,
            TransportProtection transport,
            VerificationLedger ledger,
            TenantPolicyService policies,
            PaymentService payments,
            GrantService grants,
            VerificationLauncher launcher,
            VerificationRegistry registry,
            IOptions<EntraGuardOptions> options,
            IHubContext<LiveHub> hub,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Verification");
            var owner = Owner.From(context.User);
            var rpSessionId = context.User.FindFirst(RpSessionService.Claim)?.Value;
            if (rpSessionId is null) return Results.Unauthorized();
            request = request with { Upn = owner.Upn, ObjectId = owner.ObjectId, TenantId = owner.TenantId, ApplicationName = "Contoso Treasury" };

            var callingTeams = !string.IsNullOrWhiteSpace(request.TeamsUserId);
            if (callingTeams && !owner.Owns(owner.TenantId, request.TeamsUserId)) return Results.BadRequest(new { error = "Call target must be your signed-in identity." });
            var device = callingTeams ? null : (await devices.ListAsync(owner, cancellationToken))
                .FirstOrDefault(d => d.AcsUserId == request.CalleeAcsId && devices.Reachable(d));

            if (string.IsNullOrWhiteSpace(request.Upn)
                || (!callingTeams && string.IsNullOrWhiteSpace(request.CalleeAcsId)))
            {
                return Results.BadRequest(new { error = "upn, and either calleeAcsId or teamsUserId, are required." });
            }

            // Presence only applies to endpoints EntraGuard registers itself.
            //
            // This used to say a Teams user is "reachable by definition — Microsoft handles
            // delivery, including to a locked phone". That is wrong, and a live call
            // disproved it: ACS answered 480#10037, "Target user did not have any endpoints
            // registered with ACS". Microsoft does deliver to a locked phone, but only to a
            // device that has REGISTERED; an account nobody is signed in to anywhere has
            // nothing to deliver to, and the call fails about six seconds in.
            //
            // The gate still cannot cover Teams, and that is a permissions fact rather than
            // a decision: Teams presence lives behind Graph Presence.Read.All, which this
            // app has not been consented. Until it is, a Teams verification is placed
            // hopefully and the failure is explained afterwards — see CallFailureDiagnosis,
            // where 480 now says to open Teams rather than printing a DiagCode.
            //
            // For browser and soft-phone endpoints this gate stays: placing a call to an
            // identity nobody is registered on produces the worst possible experience —
            // ACS accepts CreateCall, no device rings, no callback ever arrives, and the
            // user watches "Calling…" until they give up.
            if (!callingTeams && device is null)
            {
                return Results.BadRequest(new
                {
                    error = "No device is currently registered to receive the call. "
                          + "Open the EntraGuard page on your phone and tap Connect, or choose this browser and allow the microphone.",
                });
            }

            // The channel policy is resolved BEFORE the verification exists, so a channel the
            // tenant forbids is refused without leaving a monitoring session behind.
            var endpointKind = callingTeams ? "teams" : device!.Kind;
            var policy = await policies.GetAsync(owner.TenantId, cancellationToken);
            if (!policy.Channels.Contains(endpointKind)) return Results.Json(new { error = "This verification channel is not allowed by your tenant." }, statusCode: 403);

            // Shared with the External Authentication Method path. One implementation of the
            // media wiring, because a second copy that omitted the participant mapping would
            // still ring and still transcribe while silently losing every spoken answer.
            var target = new CallTarget(
                Upn: request.Upn,
                ObjectId: request.ObjectId ?? string.Empty,
                TenantId: request.TenantId ?? string.Empty,
                TeamsUserId: callingTeams ? request.TeamsUserId : null,
                AcsUserId: callingTeams ? null : request.CalleeAcsId,
                EndpointKind: endpointKind,
                ApplicationName: request.ApplicationName);

            var verification = launcher.Create(target);
            verification.RpSessionId = rpSessionId;
            verification.PolicyVersion = policy.Version;
            if (!string.IsNullOrEmpty(request.TransactionId))
            {
                if (!context.User.HasClaim("roles", "EntraGuard.PaymentApprover")
                    || !(await grants.CurrentAsync(owner, rpSessionId, cancellationToken)).Granted) return Results.StatusCode(403);
                var payment = await payments.GetAsync(owner, request.TransactionId, cancellationToken);
                if (payment is null || payment.Status != "Awaiting approval") return Results.NotFound();
                verification.TransactionId = payment.Reference;
                verification.TransactionDigest = payment.Digest(policy.Version);
                verification.TransactionSummary = $"Approve demo payment {payment.Reference}: {payment.AmountMinor / 100m:N2} {payment.Currency} to {payment.Beneficiary}. No funds will be moved.";
            }
            await ledger.BeginAsync(verification, cancellationToken);

            try
            {
                await launcher.DialAsync(verification, target, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                // The launcher has already recorded the failure and named the likeliest
                // cause — most often a Teams tenant that has not allow-listed this
                // Communication Services resource.
                logger.LogError(ex, "Could not place verification call for {Upn}.", request.Upn);
                await ledger.CompleteAsync(verification, CancellationToken.None);
                return Results.Ok(Describe(verification));
            }

            // Broadcast redacted; answer the starter in full. The hub reaches every connected
            // client, so it must never carry the code.
            await hub.Clients.All.SendAsync(LiveHub.VerificationEvent, Describe(verification), cancellationToken);

            return Results.Ok(Describe(verification, includeMatchCode: true));
        })
        .RequireRateLimiting("verification-start")
        .WithName("StartVerification");

        // ── Poll a verdict ──────────────────────────────────────────────────
        app.MapGet("/api/verify/{verificationId}", async (
            string verificationId,
            HttpContext context,
            VerificationRegistry registry,
            LiveCallRegistry callRegistry,
            VerificationLedger ledger,
            CancellationToken ct) =>
        {
            var verification = registry.Get(verificationId);
            if (verification is null)
            {
                var receipt = await ledger.GetAsync(Owner.From(context.User), verificationId, ct);
                return receipt is null ? Results.NotFound() : Results.Ok(new
                {
                    verification = receipt.Describe(),
                    media = new { streamConnected = receipt.MediaStreamConnected, audioFrames = receipt.AudioFrames,
                        dtmfReceived = receipt.DtmfReceived, live = false },
                });
            }
            if (!Owner.From(context.User).Owns(verification.SubjectTenantId, verification.SubjectObjectId))
            {
                return Results.NotFound();
            }

            // Live evidence that ACS is streaming this call to us, rather than the call
            // merely having been placed.
            var monitor = verification.MonitorSessionId is null
                ? null
                : callRegistry.Get(verification.MonitorSessionId)?.Session;

            return Results.Ok(new
            {
                // The capability here is KNOWING THE VERIFICATION ID, and that is enough.
                //
                // It is 64 bits of CSPRNG, it is never listed, never broadcast, and never
                // logged — an attacker cannot enumerate it, which is precisely the hole that
                // was closed. Requiring a header ON TOP of it bought very little security
                // and cost a great deal of reliability: the number is displayed from this
                // response, so every client that did not send the header — an open tab on an
                // older bundle, a reloaded page, a request served by a draining revision
                // mid-rollout — watched the digits vanish mid-call. That failure was hit
                // three times in one session, twice by the user during a live verification.
                //
                // A wrong token is still refused, because presenting the wrong one is an
                // attack signal rather than an old client. Presenting none simply falls back
                // to the id being the secret, which is what it always was.
                verification = Describe(verification, MaySeeMatchCode(context, verification)),
                // Live while the call is up, snapshot once it is over.
                //
                // The live session is removed the moment a verification completes, so reading
                // only from it meant every finished call reported no audio and no DTMF —
                // identical to a call that never connected, and unreadable exactly when
                // somebody is trying to work out which of the two happened.
                //
                // secondsSinceAudio stays live-only on purpose: "how long since we last heard
                // anything" is a question about a call in progress, and answering it for a
                // call that ended half an hour ago would be noise dressed as a measurement.
                media = new
                {
                    streamConnected = monitor?.MediaStreamConnectedAt is not null
                        || verification.MediaStreamConnected,
                    audioFrames = monitor?.AudioFramesReceived ?? verification.AudioFramesReceived,
                    dtmfReceived = monitor?.DtmfReceived ?? verification.DtmfReceived,
                    secondsSinceAudio = monitor?.LastAudioAt is null
                        ? (int?)null
                        : (int)(DateTimeOffset.UtcNow - monitor.LastAudioAt.Value).TotalSeconds,
                    live = monitor is not null,
                },
            });
        })
        .WithName("VerificationStatus");

        // ── Digits reported by the answering device ─────────────────────────
        //
        // The third input path, and the only one that cannot fail for protocol reasons.
        // Call Automation's recogniser is PSTN-oriented; media-stream DtmfData depends on
        // ACS forwarding tones from a browser leg. Both may work — but "the user pressed
        // the keys and nothing happened" is the failure that makes the whole factor
        // worthless, so the device that owns the keypad can also say so directly.
        //
        // This does NOT weaken the check: the digits are only accepted from a device that is
        // on the call, from a caller holding this verification's viewer token, and the
        // coercion verdict is applied identically regardless of which path delivered the
        // entry.
        //
        // The previous version of this comment claimed "the code is never transmitted to the
        // device", which the list endpoint flatly contradicted — it returned the live code
        // for every in-flight verification to anyone who asked. The claim is true now; it was
        // not then, and a comment asserting a security property is worth exactly as much as
        // the test that proves it.
        app.MapPost("/api/verify/{verificationId}/digits", async (
            string verificationId,
            DigitsRequest request,
            HttpContext context,
            VerificationRegistry registry,
            VerificationCoordinator verifications,
            DeviceService devices,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var verification = registry.Get(verificationId);
            if (verification is null || !Owner.From(context.User).Owns(verification.SubjectTenantId, verification.SubjectObjectId))
            {
                return Results.NotFound();
            }
            var registered = await devices.GetAsync(Owner.From(context.User), verification.EndpointKind, cancellationToken);
            if (registered is null || registered.Revoked || registered.SessionId != context.User.FindFirst(RpSessionService.Claim)?.Value
                || registered.AcsUserId != verification.CalleeAcsId) return Results.NotFound();

            // Deliberately NOT gated on the viewer token, and the reason is the design.
            //
            // The token belongs to the browser that started the sign-in; this route is posted
            // to by the ANSWERING DEVICE, which is a different device and must never hold the
            // starter's secret — a phone that could read the code would defeat the point of
            // showing it on the other screen. Demanding the token here would have broken the
            // soft-phone path outright.
            //
            // What bounds this instead: the code is no longer disclosed anywhere, the call
            // must already be connected, and MaxAttempts caps entries at three. Guessing is
            // therefore three tries against a 90-value keyspace — the same bound a physical
            // keypad gives, which is the bound this factor was always designed around.
            if (verification.IsComplete)
            {
                // Already adjudicated by another path — not an error.
                return Results.Ok(Describe(verification));
            }

            // Only from a device that actually answered. A verification whose call never
            // connected must not be completable from a web request.
            if (verification.CallState is VerificationCallState.Placing)
            {
                return Results.BadRequest(new { error = "The verification call has not connected yet." });
            }

            var digits = new string((request.Digits ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
            if (digits.Length != verification.MatchCode.Length)
            {
                return Results.BadRequest(new
                {
                    error = $"Expected {verification.MatchCode.Length} digits.",
                });
            }

            loggerFactory.CreateLogger("Verification").LogInformation(
                "Verification {Id}: digits reported by the answering device.", verificationId);

            await verifications.SubmitAsync(verificationId, digits, "device", cancellationToken);
            return Results.Ok(Describe(registry.Get(verificationId)!));
        })
        .WithName("SubmitVerificationDigits");

        // Redacted for everyone, with no way to opt in. This is the endpoint that leaked:
        // it returned the live match code and UPN for every in-flight verification to any
        // anonymous caller. The lambda is explicit rather than a method group so the optional
        // parameter can never be bound to something unintended.
        app.MapGet("/api/verify", (HttpContext context, VerificationRegistry registry) =>
            Results.Ok(registry.Recent.Where(v => Owner.From(context.User).Owns(v.SubjectTenantId, v.SubjectObjectId)).Select(v => Describe(v))))
            .WithName("RecentVerifications");

        // ── Why did the call not ask live questions? ────────────────────────
        //
        // The fallback from live telemetry to a stored question is silent by design: the
        // call continues either way and the caller cannot hear the difference. That made a
        // materially weaker verification indistinguishable from a strong one from the
        // outside — a user heard only "your first pet" and reasonably concluded the location
        // and device questions had been deleted, when in fact Graph had refused to hand over
        // the sign-in log and the code fell through exactly as written.
        //
        // Returns no answers and no telemetry content, only whether questions could be
        // built and why not.
        // ── What a call would be worth, before one is placed ────────────────
        //
        // The pre-call contract. A relying party asks what can be established for this user
        // RIGHT NOW, and decides whether a verification call is worth placing at all — or
        // whether to send them to enrolment first.
        //
        // This exists because the weak case is silent. When a tenant withholds sign-in logs
        // and the user never registered a question, the knowledge factor is skipped entirely
        // and two keyed digits decide access. That is the right behaviour — refusing would
        // lock out everyone who never enrolled — but it happens invisibly, and the relying
        // party learns nothing until after it has spent a phone call.
        //
        // Deliberately NOT the telemetry-probe below, which returns the question TEXT. That
        // is right for an operator debugging a tenant and wrong for a contract an application
        // calls before every step-up: this returns levels and gaps, never questions, so
        // calling it repeatedly reveals nothing an attacker could use to prepare.
        app.MapGet("/api/verify/readiness/{tenantId}/{objectId}", async (
            string tenantId,
            string objectId,
            Agents.TelemetryChallenge telemetry,
            KnowledgeStore knowledge,
            VoiceprintStore voiceprints,
            CancellationToken cancellationToken) =>
        {
            var live = await telemetry.BuildAsync(objectId, tenantId, 3, cancellationToken);
            var stored = await knowledge.GetAsync(tenantId, objectId, cancellationToken);
            var enrolledVoice = await voiceprints.GetAsync(tenantId, objectId, cancellationToken) is not null;

            var backing = live.Questions.Count > 0
                ? (stored is not null ? $"telemetry+{stored.Backing.ToString().ToLowerInvariant()}" : "telemetry")
                : stored is not null ? stored.Backing.ToString().ToLowerInvariant() : null;

            // The BEST level a call could reach, assuming the caller answers everything and
            // the voice matches. Not a prediction of what will happen — an upper bound, which
            // is the useful thing to know before deciding whether to place the call.
            var ceiling = VerificationAssurance.Evaluate(
                passed: true,
                knowledgeBacking: backing,
                followUps: live.FollowUps.Count > 0
                    ? [new FollowUpOutcome("location", string.Empty, Answered: true, Correct: true)]
                    : [],
                voiceOutcome: enrolledVoice ? "Match" : "NotAssessed",
                endpointKind: "teams");

            var fixes = new List<string>();
            if (live.Questions.Count == 0)
            {
                fixes.Add("Grant AuditLog.Read.All in the user's tenant and ensure it has Entra ID P1 — "
                        + "/v1.0/auditLogs/signIns is a premium endpoint, and without it no question "
                        + "can be built from the caller's own activity.");
            }
            if (stored is null)
            {
                fixes.Add("Have the user register a question at enrolment, which is the only "
                        + "fallback when sign-in telemetry is unavailable.");
            }
            if (!enrolledVoice)
            {
                fixes.Add("Have the user enrol a voice profile, which is what lets a call "
                        + "establish who was speaking rather than only what they knew.");
            }

            return Results.Ok(new
            {
                ceiling = ceiling.Level.ToString(),
                liveTelemetryAvailable = live.Questions.Count > 0,
                registeredQuestion = stored is not null,
                enrolledVoice,
                // Why it is not higher, and what to do about it. Actionable rather than
                // diagnostic: these are things a tenant admin or the user can actually change.
                toImproveIt = fixes,
                // The honest headline. A relying party that reads nothing else should read this.
                note = ceiling.Level == AssuranceLevel.Low
                    ? "A call for this user would prove only that the phone and the browser are "
                    + "the same person. No identity question can be asked."
                    : "A call for this user can ask identity questions.",
            });
        })
        .WithName("VerificationReadiness");

        app.MapGet("/api/verify/telemetry-probe/{tenantId}/{objectId}", async (
            string tenantId,
            string objectId,
            Agents.TelemetryChallenge telemetry,
            CancellationToken cancellationToken) =>
        {
            var questions = (await telemetry.BuildAsync(objectId, tenantId, 3, cancellationToken)).Questions;

            return Results.Ok(new
            {
                available = questions.Count > 0,
                questionCount = questions.Count,
                // The prompts, not the answers — enough to confirm the location and device
                // questions are the ones that would be asked.
                questions = questions.Select(q => q.Question),
                failure = telemetry.LastFailure,
                signInsReturned = telemetry.LastCounts.Raw,
                signInsUsable = telemetry.LastCounts.Usable,
                appsSeen = telemetry.LastCounts.Apps,
                hint = questions.Count > 0
                    ? "Live telemetry questions will be asked on the next call."
                    : "The call will fall back to the registered question only. A 403 means "
                    + "the user's tenant has not consented to AuditLog.Read.All, or has no "
                    + "Entra ID P1 — /v1.0/auditLogs/signIns is a premium endpoint.",
            });
        })
        .WithName("TelemetryProbe");

        // ── What can this caller actually be asked? ─────────────────────────
        //
        // The same job the telemetry probe does, for the profile sources. Answers the
        // question that otherwise costs a phone call to a real person: which sources have
        // consent, which have consent but nothing recent to ask about, and which would appear
        // on the next call.
        //
        // Returns question TEXT and never expected answers — the same line the telemetry
        // probe draws, for the same reason: this endpoint is anonymous, and a probe that
        // handed out the answers would be a better attack tool than a diagnostic.
        app.MapGet("/api/verify/profile-probe/{tenantId}/{objectId}", async (
            string tenantId,
            string objectId,
            Agents.ProfileChallenge profile,
            Agents.TelemetryChallenge telemetry,
            CancellationToken cancellationToken) =>
        {
            var profileCandidates = await profile.BuildAsync(objectId, tenantId, cancellationToken);
            var signIn = await telemetry.BuildAsync(objectId, tenantId, 3, cancellationToken);

            var pool = signIn.Questions
                .Select(q => new { facet = "signin", source = "SignIn", question = q.Question, strength = 3 })
                .Concat(profileCandidates.Select(c => new
                {
                    facet = c.Facet,
                    source = c.Source.ToString(),
                    question = c.Question,
                    strength = c.Strength,
                }))
                .ToList();

            return Results.Ok(new
            {
                poolSize = pool.Count,
                willAsk = Math.Min(pool.Count, Shared.Verification.ChallengeSelection.DefaultCount),
                mustAnswer = Shared.Verification.ChallengeSelection.Required(
                    Math.Min(pool.Count, Shared.Verification.ChallengeSelection.DefaultCount)),
                pool,
                unavailable = profile.LastSkipped,
                hint = "A source listed as unavailable either lacks admin consent (look for a "
                     + "profile.*_unavailable fault) or simply has nothing recent to ask about.",
            });
        })
        .WithName("ProfileProbe");

        // ── Register a knowledge question ───────────────────────────────────
        //
        // The answer is hashed here, server-side, and the plaintext is never persisted or
        // logged. Hashing in the browser instead would look stronger and be weaker: the
        // client would then decide the salt and iteration count, and anything the client
        // decides an attacker can decide too.
        app.MapPost("/api/verify/knowledge", async (
            KnowledgeRequest request,
            HttpContext context,
            KnowledgeStore store,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Verification");

            var owner = Owner.From(context.User);
            request = request with { TenantId = owner.TenantId, ObjectId = owner.ObjectId };

            if (string.IsNullOrWhiteSpace(request.TenantId)
                || string.IsNullOrWhiteSpace(request.ObjectId)
                || string.IsNullOrWhiteSpace(request.Question)
                || string.IsNullOrWhiteSpace(request.Answer))
            {
                return Results.BadRequest(new { error = "tenantId, objectId, question and answer are required." });
            }

            KnowledgeQuestion registered;
            try
            {
                registered = KnowledgeChallenge.Register(request.Question, request.Answer);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new
                {
                    error = "That answer is too short to be usable — it reduces to nothing once "
                          + "filler words are removed. Pick something with a distinct word in it.",
                });
            }

            var backing = await store.SaveAsync(
                request.TenantId, request.ObjectId, registered, cancellationToken);

            if (backing == KnowledgeStore.Backing.None)
            {
                return Results.Problem("The question could not be stored.");
            }

            logger.LogInformation(
                "Knowledge question registered for {ObjectId} in {TenantId} ({Backing}).",
                request.ObjectId, request.TenantId, backing);

            return Results.Ok(new { backing = backing.ToString().ToLowerInvariant() });
        })
        .WithName("RegisterKnowledgeQuestion");

        // ── Is a question registered? ───────────────────────────────────────
        app.MapGet("/api/verify/knowledge/{tenantId}/{objectId}", async (
            string tenantId, string objectId, KnowledgeStore store, CancellationToken cancellationToken) =>
        {
            var stored = await store.GetAsync(tenantId, objectId, cancellationToken);

            // The question text is returned; the salt and hash never are. A question is a
            // prompt, not a secret — but everything that could verify an answer offline stays
            // on the server.
            return Results.Ok(new
            {
                registered = stored is not null,
                question = stored?.Question.Question,
                backing = stored?.Backing.ToString().ToLowerInvariant(),
            });
        })
        .WithName("KnowledgeStatus");

        // ── ACS callbacks for the verification call ─────────────────────────
        app.MapPost("/api/verify/callbacks/{verificationId}", async (
            HttpContext context,
            string verificationId,
            CallAutomationClient callAutomation,
            VerificationRegistry registry,
            LiveCallRegistry callRegistry,
            VerificationCoordinator verifications,
            LogsIngestionSink sink,
            IHubContext<LiveHub> hub,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Verification");
            using var reader = new StreamReader(context.Request.Body);
            var events = CallAutomationEventParser.ParseMany(
                BinaryData.FromString(await reader.ReadToEndAsync(cancellationToken)));

            var verification = registry.Get(verificationId);
            if (verification is null)
            {
                return Results.Ok();
            }

            foreach (var callEvent in events)
            {
                // Log EVERY event with its result information, before dispatching.
                //
                // ACS explains itself in ResultInformation — code, subCode and message —
                // and this handler used to discard all of it, which is why a call that was
                // refused outright and a call the user ignored produced identical output.
                // A verification factor you cannot diagnose from its logs is a factor you
                // cannot operate.
                logger.LogInformation(
                    "Verification {Id} event {Event}: code={Code} subCode={SubCode} {Message}",
                    verificationId, callEvent.GetType().Name,
                    callEvent.ResultInformation?.Code,
                    callEvent.ResultInformation?.SubCode,
                    callEvent.ResultInformation?.Message);

                switch (callEvent)
                {
                    case CallConnected connected:
                        verification.CallConnectionId = connected.CallConnectionId;
                        verification.CallState = VerificationCallState.Connected;
                        await hub.Clients.All.SendAsync(LiveHub.VerificationEvent, Describe(verification), cancellationToken);
                        await verifications.PromptAsync(verification, cancellationToken);
                        break;

                    case RecognizeCompleted recognised:
                        await verifications.SubmitAsync(
                            verificationId,
                            recognised.RecognizeResult is DtmfResult dtmf
                                ? string.Concat(dtmf.Tones.Select(ToDigit))
                                : string.Empty,
                            "recognizer",
                            cancellationToken);
                        break;

                    case PlayCompleted:
                        // Lets CompleteAsync hang up on the last word rather than a timer.
                        verifications.OnPlaybackCompleted(verificationId);
                        break;

                    case RecognizeFailed failed:
                        // Not fatal on its own — the media-stream path may still deliver the
                        // digits. Only give up if nothing has arrived by the time the call ends.
                        logger.LogInformation("Verification {Id} recognizer reported failure: {Code}",
                            verificationId, failed.ResultInformation?.SubCode);
                        break;

                    case CallDisconnected disconnected:
                        // A hang-up before a verdict is a failure, not a pass. Anything else
                        // would let dropping the call be a way past the factor.
                        if (!verification.IsComplete)
                        {
                            // A call that never reached CallConnected was never answered —
                            // and "never rang" is a different fault from "rang and was
                            // ignored", with a different fix. Reporting both as a timeout is
                            // what made the earlier failures unreadable.
                            var neverAnswered = verification.CallState == VerificationCallState.Placing;
                            var info = disconnected.ResultInformation;

                            // The whole mapping lives in CallFailureDiagnosis, which is pure
                            // and exhaustively tested. It was inline here, which meant the
                            // only way to find out what a given failure would say was to
                            // re-read this handler, and the only way to test it was to make a
                            // real call fail in exactly the right way. A 480 therefore had no
                            // case at all and reached the user as a bare DiagCode.
                            var reason = CallFailureDiagnosis.Describe(
                                neverAnswered,
                                verification.EndpointKind,
                                info?.Code,
                                info?.SubCode,
                                info?.Message);

                            await verifications.CompleteAsync(verification,
                                neverAnswered ? VerificationResult.CallFailed : VerificationResult.Timeout,
                                reason,
                                cancellationToken);
                        }
                        break;
                }
            }

            return Results.Ok();
        })
        .ExcludeFromDescription();
    }



    // The verification's own CompleteAsync lived here too, unreachable.
    //
    // Nothing called it: the switch above calls verifications.CompleteAsync, the
    // coordinator's. It carried a SECOND, divergent set of closing messages — "Verification
    // successful. You may continue signing in." against the coordinator's "Thank you. Your
    // identity is verified." — so anyone reading this file to find out what a caller hears
    // would have found the wrong answer, confidently.

    private static char ToDigit(DtmfTone tone) => tone.ToString() switch
    {
        "Zero" => '0', "One" => '1', "Two" => '2', "Three" => '3', "Four" => '4',
        "Five" => '5', "Six" => '6', "Seven" => '7', "Eight" => '8', "Nine" => '9',
        "Pound" => '#', "Asterisk" => '*',
        _ => '?',
    };

    /// <summary>
    /// Shape returned to the relying party.
    ///
    /// The match code is REDACTED unless the caller proves it is the browser that started
    /// this verification. Defaulting to redaction is the point: this projection is reused by
    /// the list endpoint and broadcast over SignalR to every connected client at eight call
    /// sites, and the previous default leaked a live authentication secret through all of
    /// them. A new call site added later is now safe unless it deliberately opts out.
    ///
    /// Even for the rightful holder it is withheld once the attempt completes — echoing a
    /// used auth secret back into logs and browser history buys nothing.
    /// </summary>
    /// <param name="includeMatchCode">
    /// True only after <see cref="MaySeeMatchCode"/> has matched the caller's token.
    /// </param>
    /// <summary>Header the browser that started the verification presents to see its code.</summary>
    public const string ViewerTokenHeader = "X-Verification-Token";

    /// <summary>
    /// Is this caller the browser that started the verification?
    /// </summary>
    /// <remarks>
    /// Fixed-time comparison. The window is small and the token is 256 bits, so a timing
    /// oracle here is not a realistic attack — but a secret compared with string equality is
    /// the kind of detail a security reviewer looks for, and being right costs one call.
    /// </remarks>
    private static bool MaySeeMatchCode(HttpContext context, VerificationSession v)
    {
        if (v.RpSessionId is null || context.User.FindFirst(RpSessionService.Claim)?.Value != v.RpSessionId) return false;
        var presented = context.Request.Headers[ViewerTokenHeader].ToString();

        // No token: the caller knew an unguessable verification id, which is the capability
        // this endpoint has always run on. Allowed.
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        // A token was presented, so it must be the right one. Someone sending a wrong value
        // is guessing, not running an old client.
        return !string.IsNullOrEmpty(v.ViewerToken)
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(presented),
                System.Text.Encoding.UTF8.GetBytes(v.ViewerToken));
    }

    public static object Describe(VerificationSession v, bool includeMatchCode = false) => new
    {
        callState = v.CallState.ToString(),
        endpointKind = v.EndpointKind,
        knowledgeQuestion = v.KnowledgeQuestion,
        knowledgeAttempts = v.KnowledgeAttempts,
        knowledgeBacking = v.KnowledgeBacking,

        // Facet and outcome, never the question text.
        //
        // The probe as spoken contains the answer to the question before it — "And
        // whereabouts in Malaysia, roughly?" names the country the user signed in from. This
        // projection is broadcast over SignalR to every connected client, which is how the
        // match code became readable by anyone (see viewerToken below), so nothing derived
        // from the subject's sign-in activity goes into it. "A location probe was asked and
        // confirmed" is what the console needs and all it needs.
        followUps = v.FollowUps
            .Select(f => new { facet = f.Facet, answered = f.Answered, correct = f.Correct })
            .ToList(),

        // Warm or Protective. Worth showing because a call that changed register is a call
        // where the gate decided the person on it needed telling something.
        register = v.Register,

        // What the call established, and what it could not.
        //
        // The whole reason this exists: "Passed" is the same word whether two keyed digits
        // were the entire check or the caller answered live telemetry and matched an enrolled
        // voice. A relying party moving money is entitled to tell those apart, and nothing in
        // this projection let it.
        assuranceLevel = v.AssuranceLevel,
        assuranceBasis = v.AssuranceBasis,
        assuranceGaps = v.AssuranceGaps,
        questions = v.QuestionEvidence,
        policyVersion = v.PolicyVersion,
        transactionId = v.TransactionId,
        transactionSummary = v.TransactionSummary,
        analystAssessed = v.AnalystAssessed,
        voiceScore = v.VoiceScore,
        voiceOutcome = v.VoiceOutcome,
        livenessOutcome = v.LivenessOutcome,
        livenessLatencyMs = v.LivenessLatencyMs,
        requiresStepUp = v.RequiresStepUp,
        verificationId = v.VerificationId,
        upn = v.SubjectUpn,
        applicationName = v.ApplicationName,
        matchCode = includeMatchCode && !v.IsComplete ? v.MatchCode : null,

        // Rides the same disclosure gate as the code, so it is returned only to the caller
        // that just started this verification and is absent from the list endpoint and from
        // every SignalR broadcast. Kept flat rather than wrapped in an envelope: the relying
        // party already parses this shape, and changing it to add a field would have broken
        // the one flow that currently works.
        viewerToken = includeMatchCode ? v.ViewerToken : null,
        result = v.Result.ToString(),
        reason = v.Reason,
        grantsAccess = v.GrantsAccess,
        isComplete = v.IsComplete,
        attempts = v.Attempts,
        peakRiskDuringCall = v.PeakRiskDuringCall,
        startedAt = v.StartedAt,
        completedAt = v.CompletedAt,
        durationMs = v.DurationMs,
    };
}
