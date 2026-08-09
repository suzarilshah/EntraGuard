using System.Collections.Concurrent;
using Azure.Communication.CallAutomation;
using EntraGuard.MediaService.Endpoints;
using EntraGuard.MediaService.Hubs;
using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Verification;
using Microsoft.AspNetCore.SignalR;

namespace EntraGuard.MediaService.Sessions;

/// <summary>
/// Collects verification digits and adjudicates, from whichever source delivers them.
///
/// There are two independent ways a keypad press can reach this service, and relying on
/// only one is how the flow dead-ends:
///
///   1. Call Automation's <c>StartRecognizing</c> → <c>RecognizeCompleted</c>. This is the
///      documented path and it is built for PSTN, where DTMF arrives as RFC 2833 tones.
///      Whether it fires reliably for a pure VoIP ACS-to-ACS call, where the digits come
///      from a browser calling sendDtmf(), is not something to assume.
///
///   2. <c>DtmfData</c> frames on the bidirectional media stream, which this service
///      already receives because EnableDtmfTones is set on the call. This path is entirely
///      under our control and does not depend on the recogniser behaving.
///
/// Both feed the same collector and the same <see cref="VerificationAdjudicator"/>, and
/// whichever arrives first wins — the other is ignored idempotently. A verification factor
/// that silently fails to hear the user is worse than one that refuses to start, so this
/// deliberately has a second way to hear.
/// </summary>
public sealed class VerificationCoordinator(
    VerificationRegistry registry,
    LiveCallRegistry callRegistry,
    CallAutomationClient callAutomation,
    LogsIngestionSink sink,
    KnowledgeStore knowledge,
    IHubContext<LiveHub> hub,
    ILogger<VerificationCoordinator> logger)
{
    /// <summary>How long to wait for a spoken answer before treating silence as a failure.</summary>
    private static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(20);

    /// <summary>Spoken answers allowed, matching the three attempts the code gets.</summary>
    private const int MaxKnowledgeAttempts = 3;
    /// <summary>Digits accumulated per verification, across both input paths.</summary>
    private readonly ConcurrentDictionary<string, string> _buffers = new();

    /// <summary>Serialises the claim on a prompt round between the two input paths.</summary>
    private readonly Lock _roundLock = new();

    /// <summary>Monitor session id → verification id, so the media socket can route digits.</summary>
    private readonly ConcurrentDictionary<string, string> _monitorToVerification = new();

    public void LinkMonitorSession(string monitorSessionId, string verificationId) =>
        _monitorToVerification[monitorSessionId] = verificationId;

    public string? VerificationForMonitorSession(string monitorSessionId) =>
        _monitorToVerification.TryGetValue(monitorSessionId, out var id) ? id : null;

    /// <summary>
    /// A single keypad digit arrived on the media stream.
    ///
    /// Buffered until the match code's length is reached, then adjudicated exactly as if
    /// the recogniser had reported it.
    /// </summary>
    public async Task OnMediaDtmfAsync(
        string monitorSessionId,
        string digit,
        CancellationToken cancellationToken = default)
    {
        if (!_monitorToVerification.TryGetValue(monitorSessionId, out var verificationId))
        {
            return;
        }

        var verification = registry.Get(verificationId);
        if (verification is null || verification.IsComplete)
        {
            return;
        }

        // Only digits are meaningful here; * and # are not part of a match code.
        if (digit.Length != 1 || !char.IsAsciiDigit(digit[0]))
        {
            return;
        }

        var buffer = _buffers.AddOrUpdate(verificationId, digit, (_, existing) => existing + digit);

        logger.LogInformation(
            "Verification {Id}: digit received on the media stream ({Count}/{Needed}).",
            verificationId, buffer.Length, verification.MatchCode.Length);

        if (buffer.Length < verification.MatchCode.Length)
        {
            return;
        }

        _buffers.TryRemove(verificationId, out _);
        await SubmitAsync(verificationId, buffer, "media-stream", cancellationToken);
    }

    /// <summary>
    /// Adjudicate a complete entry, from either input path.
    ///
    /// Idempotent: <see cref="VerificationRegistry.TryComplete"/> settles the race when the
    /// recogniser and the media stream both deliver the same digits.
    /// </summary>
    public async Task SubmitAsync(
        string verificationId,
        string entered,
        string source,
        CancellationToken cancellationToken = default)
    {
        var verification = registry.Get(verificationId);
        if (verification is null || verification.IsComplete)
        {
            return;
        }

        // One entry, one adjudication. The recogniser and the media stream both report the
        // same keypress, arriving milliseconds apart, and judging both spent two of the
        // three attempts on a single try. Whichever path gets here first claims the round;
        // the other returns silently, which is the "ignored idempotently" this class always
        // claimed and did not actually do outside of terminal verdicts.
        lock (_roundLock)
        {
            if (verification.AdjudicatedRound >= verification.PromptRound)
            {
                logger.LogDebug(
                    "Verification {Id}: duplicate entry via {Source} for round {Round}, ignored.",
                    verificationId, source, verification.PromptRound);
                return;
            }

            verification.AdjudicatedRound = verification.PromptRound;
        }

        verification.EnteredCode = entered;
        verification.Attempts++;
        verification.CallState = VerificationCallState.Adjudicating;

        var assessment = ResolveAssessment(verification);
        if (assessment is not null)
        {
            verification.PeakRiskDuringCall = Math.Max(verification.PeakRiskDuringCall, assessment.RiskScore);
        }

        var verdict = VerificationAdjudicator.Adjudicate(
            verification.MatchCode, entered, verification.Attempts, assessment);

        logger.LogInformation(
            "Verification {Id}: entry adjudicated via {Source} — {Outcome}",
            verificationId, source, verdict.Result?.ToString() ?? "retry");

        if (verdict.Result is { } outcome)
        {
            if (outcome == VerificationResult.BlockedCoercion)
            {
                logger.LogWarning(
                    "Verification {Id} BLOCKED — coercion detected during the verification call.",
                    verificationId);
            }

            // A correct code is necessary but may not be sufficient: if this user has a
            // registered knowledge question, the call continues rather than granting here.
            if (outcome == VerificationResult.Passed
                && await TryBeginKnowledgeChallengeAsync(verification, cancellationToken))
            {
                return;
            }

            await CompleteAsync(verification, outcome, verdict.Reason, cancellationToken);
            return;
        }

        // Wrong but retries remain. Re-prompt without hinting how close the entry was.
        await PromptAsync(verification, cancellationToken);
    }

    /// <summary>
    /// The Analyst's current read of the verification call, or null when it has not scored.
    ///
    /// The benign sentinel must not be mistaken for evidence of a quiet room — a call the
    /// Analyst never got to would otherwise read as "no coercion detected".
    /// </summary>
    private RiskAssessment? ResolveAssessment(VerificationSession verification)
    {
        var monitored = verification.MonitorSessionId is null
            ? null
            : callRegistry.Get(verification.MonitorSessionId);

        var assessment = monitored?.Session.CurrentAssessment;
        return assessment is not null && assessment.Rationale == "No analysis performed yet."
            ? null
            : assessment;
    }

    /// <summary>Speak the challenge and arm DTMF capture.</summary>
    public async Task PromptAsync(VerificationSession verification, CancellationToken cancellationToken = default)
    {
        var isRetry = verification.Attempts > 0;

        // Opens the round this prompt is asking for. Both input paths race to claim it and
        // exactly one wins.
        verification.PromptRound++;
        _buffers.TryRemove(verification.VerificationId, out _);

        var prompt = new TextSource(isRetry
            ? "That number was not correct. Please enter the two digit number shown on your screen."
            : $"This is a security verification from EntraGuard for {verification.ApplicationName}. " +
              "Please enter the two digit number shown on your screen, using your keypad. " +
              "If you did not just try to sign in, hang up now and contact your IT help desk.")
        {
            VoiceName = "en-US-AvaMultilingualNeural",
        };

        // The recogniser is scoped to a participant, so the identifier type has to match the
        // leg. Handing a Teams object ID to CommunicationUserIdentifier yields a target that
        // is on no call, and recognition then waits for digits from nobody.
        Azure.Communication.CommunicationIdentifier target =
            verification.EndpointKind == "teams"
                ? new Azure.Communication.MicrosoftTeamsUserIdentifier(verification.CalleeAcsId)
                : new Azure.Communication.CommunicationUserIdentifier(verification.CalleeAcsId);

        var recognize = new CallMediaRecognizeDtmfOptions(target, maxTonesToCollect: 2)
        {
            Prompt = prompt,
            InterToneTimeout = TimeSpan.FromSeconds(10),
            InitialSilenceTimeout = TimeSpan.FromSeconds(20),
            InterruptPrompt = true,
            OperationContext = verification.VerificationId,
        };

        try
        {
            await callAutomation
                .GetCallConnection(verification.CallConnectionId)
                .GetCallMedia()
                .StartRecognizingAsync(recognize, cancellationToken);

            verification.CallState = VerificationCallState.AwaitingDigits;
            await hub.Clients.All.SendAsync(
                LiveHub.VerificationEvent, VerificationEndpoint.Describe(verification), cancellationToken);
        }
        catch (Exception ex)
        {
            // Recognition failing does NOT end the verification any more: the media-stream
            // path can still deliver the digits, and the prompt may well have played. Log
            // it, mark the call as listening, and let the timeout catch a genuine silence.
            logger.LogError(ex,
                "StartRecognizing failed for {Id}; relying on media-stream DTMF instead.",
                verification.VerificationId);

            verification.CallState = VerificationCallState.AwaitingDigits;
            await hub.Clients.All.SendAsync(
                LiveHub.VerificationEvent, VerificationEndpoint.Describe(verification), cancellationToken);
        }
    }

    /// <summary>
    /// Ask the registered knowledge question, if there is one.
    /// </summary>
    /// <returns>
    /// True when the call is continuing into the question stage, so the caller must not
    /// complete the verification. False means there is nothing to ask and the code alone
    /// decides — which is the correct outcome for a user who never registered one.
    /// </returns>
    private async Task<bool> TryBeginKnowledgeChallengeAsync(
        VerificationSession verification, CancellationToken cancellationToken)
    {
        // No object ID means no identity to look a question up against. Silently granting
        // is right here: the code challenge already passed, and inventing a second factor
        // the user never enrolled in would lock out everyone who did not.
        if (string.IsNullOrEmpty(verification.SubjectObjectId)
            || string.IsNullOrEmpty(verification.SubjectTenantId))
        {
            return false;
        }

        var stored = await knowledge.GetAsync(
            verification.SubjectTenantId, verification.SubjectObjectId, cancellationToken);

        if (stored is null)
        {
            return false;
        }

        verification.KnowledgeQuestion = stored.Question.Question;
        verification.KnowledgeBacking = stored.Backing.ToString().ToLowerInvariant();

        // Run detached: this waits on a human speaking, and the caller here is an ACS
        // callback handler that must return promptly or Call Automation retries it.
        _ = Task.Run(() => RunKnowledgeChallengeAsync(verification, stored.Question), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Ask, listen, and decide — up to three times.
    /// </summary>
    /// <remarks>
    /// The answer arrives as speech on the transcript the Analyst is already building, so
    /// this reads from the same utterance stream rather than opening a second recogniser.
    /// That has a deliberate side effect: the answer, and anything said around it, is
    /// visible to the coercion analysis. A second voice prompting the user through this
    /// question is exactly the signal worth catching, and it would be invisible if the
    /// answer were captured on a private channel.
    /// </remarks>
    private async Task RunKnowledgeChallengeAsync(
        VerificationSession verification, KnowledgeQuestion question)
    {
        using var window = new CancellationTokenSource(
            TimeSpan.FromSeconds(AnswerWindow.TotalSeconds * MaxKnowledgeAttempts + 30));
        var token = window.Token;

        var monitored = verification.MonitorSessionId is null
            ? null
            : callRegistry.Get(verification.MonitorSessionId);

        if (monitored is null)
        {
            // Without the media session there is no transcript, so no answer can ever
            // arrive. Failing closed beats leaving the call hanging in AwaitingAnswer.
            logger.LogWarning(
                "Verification {Id}: no media session, cannot run the knowledge challenge.",
                verification.VerificationId);
            await CompleteAsync(verification, VerificationResult.Failed,
                "The spoken question could not be asked because the call had no audio channel.");
            return;
        }

        try
        {
            while (verification.KnowledgeAttempts < MaxKnowledgeAttempts && !verification.IsComplete)
            {
                verification.KnowledgeAttempts++;
                verification.CallState = VerificationCallState.AwaitingAnswer;

                // Mark the transcript position BEFORE speaking, so nothing said earlier in
                // the call can be mistaken for an answer to a question not yet asked.
                var askedAt = monitored.Session.ElapsedMs(DateTimeOffset.UtcNow);

                var preamble = verification.KnowledgeAttempts == 1
                    ? "Thank you. One more check. "
                    : "That did not match. Please answer again. ";

                await SpeakAsync(verification, preamble + question.Question, token);
                await hub.Clients.All.SendAsync(
                    LiveHub.VerificationEvent, VerificationEndpoint.Describe(verification), token);

                var spoken = await ListenForAnswerAsync(monitored, askedAt, token);

                if (spoken is not null && KnowledgeChallenge.Verify(question, spoken))
                {
                    // Deliberately not logged, at any level. The whole point of hashing the
                    // answer is that it exists nowhere readable, and a debug log is readable.
                    logger.LogInformation(
                        "Verification {Id}: knowledge answer accepted on attempt {Attempt}.",
                        verification.VerificationId, verification.KnowledgeAttempts);

                    var assessment = ResolveAssessment(verification);
                    var verdict = VerificationAdjudicator.Adjudicate(
                        verification.MatchCode, verification.EnteredCode ?? verification.MatchCode,
                        verification.Attempts, assessment);

                    // Re-adjudicated rather than passed outright: coercion detected DURING
                    // the spoken question must still refuse, and that evidence only exists
                    // now. Answering correctly while being coached is the attack.
                    await CompleteAsync(verification,
                        verdict.Result ?? VerificationResult.Passed,
                        verdict.Result == VerificationResult.BlockedCoercion
                            ? verdict.Reason
                            : "Number match and knowledge question both confirmed. No coercion detected.");
                    return;
                }

                logger.LogInformation(
                    "Verification {Id}: knowledge answer rejected ({Attempt}/{Max}) — {Cause}.",
                    verification.VerificationId, verification.KnowledgeAttempts, MaxKnowledgeAttempts,
                    spoken is null ? "nothing was said" : "did not match");
            }

            if (!verification.IsComplete)
            {
                await CompleteAsync(verification, VerificationResult.Failed,
                    "The security question was not answered correctly.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Knowledge challenge failed for {Id}.", verification.VerificationId);
            if (!verification.IsComplete)
            {
                await CompleteAsync(verification, VerificationResult.Failed,
                    "The security question could not be completed.");
            }
        }
    }

    /// <summary>Speak a line into the live call.</summary>
    private async Task SpeakAsync(
        VerificationSession verification, string text, CancellationToken cancellationToken) =>
        await callAutomation
            .GetCallConnection(verification.CallConnectionId)
            .GetCallMedia()
            .PlayToAllAsync(
                new PlayToAllOptions(new TextSource(text) { VoiceName = "en-US-AvaMultilingualNeural" }),
                cancellationToken);

    /// <summary>
    /// Wait for the protected user to say something after the question was asked.
    /// </summary>
    /// <returns>Everything they said in the window, or null if they said nothing.</returns>
    private static async Task<string?> ListenForAnswerAsync(
        LiveCall monitored, long askedAt, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + AnswerWindow;

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            // Only the protected user's channel. Unmixed audio is what makes this possible:
            // a coercer saying the answer out loud lands on a different channel and must
            // never satisfy the challenge on the user's behalf.
            var said = monitored.Session.FinalUtterances
                .Where(u => u.OffsetMs > askedAt && u.Speaker == SpeakerRole.ProtectedUser)
                .Select(u => u.Text)
                .ToArray();

            // Wait for a short pause after they start, so a two-word answer is not judged
            // on its first word.
            if (said.Length > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

                said = monitored.Session.FinalUtterances
                    .Where(u => u.OffsetMs > askedAt && u.Speaker == SpeakerRole.ProtectedUser)
                    .Select(u => u.Text)
                    .ToArray();

                return string.Join(' ', said);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }

        return null;
    }

    public async Task CompleteAsync(
        VerificationSession verification,
        VerificationResult result,
        string reason,
        CancellationToken cancellationToken = default)
    {
        verification.CallState = VerificationCallState.Ended;
        _buffers.TryRemove(verification.VerificationId, out _);

        if (!registry.TryComplete(verification.VerificationId, result, reason))
        {
            return;
        }

        if (verification.MonitorSessionId is not null)
        {
            _monitorToVerification.TryRemove(verification.MonitorSessionId, out _);
        }

        // Tell the user the outcome before hanging up. Silence after a refusal is how a
        // legitimate user concludes the system is broken rather than protecting them.
        var closing = result switch
        {
            VerificationResult.Passed => "Thank you. Your identity is verified.",
            VerificationResult.BlockedCoercion =>
                "This verification has been refused because it appears someone is guiding you through it. " +
                "If you are under pressure from a caller, hang up and contact your security team.",
            VerificationResult.Failed => "That number was not correct. This verification has been refused.",
            _ => "This verification could not be completed.",
        };

        // Detached from the caller's token on purpose. This runs at the END of a call, and
        // the token that got us here is usually the call's own lifetime — cancelled the
        // moment the media socket closes. Honouring it means the user hears nothing and the
        // leg is left for ACS to time out. Bounded so a wedged ACS call cannot hold a task.
        using var closingWindow = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var closingToken = closingWindow.Token;

        try
        {
            if (!string.IsNullOrEmpty(verification.CallConnectionId))
            {
                await callAutomation.GetCallConnection(verification.CallConnectionId)
                    .GetCallMedia()
                    .PlayToAllAsync(new PlayToAllOptions(
                        new TextSource(closing) { VoiceName = "en-US-AvaMultilingualNeural" }),
                        closingToken);

                // Give the closing line time to play before dropping the call.
                await Task.Delay(TimeSpan.FromSeconds(5), closingToken);
                await callAutomation.GetCallConnection(verification.CallConnectionId)
                    .HangUpAsync(forEveryone: true, closingToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not play the closing message for {Id}.", verification.VerificationId);
        }

        // Write the audit record BEFORE tearing the session down, and on a token that the
        // teardown cannot cancel. The verdict is the one row that must exist.
        await sink.WriteVerificationAsync(verification, closingToken);
        await hub.Clients.All.SendAsync(
            LiveHub.VerificationEvent, VerificationEndpoint.Describe(verification), closingToken);

        if (verification.MonitorSessionId is not null)
        {
            await callRegistry.RemoveAsync(verification.MonitorSessionId);
        }

        logger.LogInformation("Verification {Id} for {Upn}: {Result} — {Reason}",
            verification.VerificationId, verification.SubjectUpn, result, reason);
    }
}
