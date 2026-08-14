using EntraGuard.Shared.Voice;
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
    Sinks.FaultRecorder faults,
    KnowledgeStore knowledge,
    Agents.KnowledgeJudge judge,
    Agents.TelemetryChallenge telemetry,
    Agents.VoiceprintClient voiceprint,
    Sinks.VoiceprintStore voiceprints,
    VoiceAgentRegistry voiceAgents,
    Microsoft.Extensions.Options.IOptions<Configuration.EntraGuardOptions> options,
    IHubContext<LiveHub> hub,
    ILogger<VerificationCoordinator> logger)
{
    /// <summary>
    /// How long to wait for a spoken answer before treating silence as a failure.
    ///
    /// Ten, not twenty. Measured on a live call, the previous window made the check feel
    /// broken — the user answered, then stood holding a silent phone while the window ran
    /// down and audio kept streaming. Someone who knows their answer says it within a
    /// couple of seconds; someone who does not is not helped by ten more.
    /// </summary>
    private static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(18);

    /// <summary>Spoken answers allowed, matching the three attempts the code gets.</summary>
    private const int MaxKnowledgeAttempts = 3;

    /// <summary>Attempts allowed at each telemetry question before moving on.</summary>
    private const int TriesPerQuestion = 2;

    /// <summary>
    /// Time to allow for speaking a prompt and letting its echo drain, per question.
    ///
    /// Not a guess at network latency — it is the playback itself. The first question now
    /// carries the privacy notice as well, which is the longest thing said on the call.
    /// </summary>
    private static readonly TimeSpan PromptAllowance = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How close together identical digits must be to count as one keypress.
    ///
    /// The two input paths land milliseconds apart; a human re-entering after hearing a
    /// re-prompt takes several seconds at minimum. Five is comfortably between.
    /// </summary>
    private static readonly TimeSpan DuplicateEntryWindow = TimeSpan.FromSeconds(5);
    /// <summary>Digits accumulated per verification, across both input paths.</summary>
    private readonly ConcurrentDictionary<string, string> _buffers = new();

    /// <summary>Serialises the claim on a prompt round between the two input paths.</summary>
    private readonly Lock _roundLock = new();

    /// <summary>Signals that ACS has finished playing the closing line for a verification.</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _playbackDone = new();

    /// <summary>Called from the ACS callback when a PlayCompleted event arrives.</summary>
    public void OnPlaybackCompleted(string verificationId)
    {
        if (_playbackDone.TryGetValue(verificationId, out var signal))
        {
            signal.TrySetResult();
        }
    }

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
            var now = DateTimeOffset.UtcNow;

            // Same round already judged — the ordinary duplicate.
            var sameRound = verification.AdjudicatedRound >= verification.PromptRound;

            // Same digits, moments ago. This is the case the round counter missed: a wrong
            // entry re-prompts at once, opening a new round that the late duplicate then
            // claimed. A person cannot hear the re-prompt and retype the same wrong code
            // this fast, so identical digits inside the window are always the second path
            // reporting the first keypress.
            var sameEntry = verification.LastAdjudicatedEntry == entered
                && now - verification.LastAdjudicatedAt < DuplicateEntryWindow;

            if (sameRound || sameEntry)
            {
                logger.LogInformation(
                    "Verification {Id}: duplicate entry via {Source} ignored ({Cause}).",
                    verificationId, source, sameRound ? "same round" : "same digits");
                return;
            }

            verification.AdjudicatedRound = verification.PromptRound;
            verification.LastAdjudicatedEntry = entered;
            verification.LastAdjudicatedAt = now;
        }

        verification.EnteredCode = entered;
        verification.Attempts++;
        verification.CallState = VerificationCallState.Adjudicating;

        // ResolveAssessment now carries the peak forward itself, from the session's
        // EffectiveRisk rather than this one moment's raw score.
        var assessment = ResolveAssessment(verification);

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

        // Carry the peak across every time anything asks for an assessment.
        //
        // This used to be captured once, during DTMF adjudication — seconds into the call,
        // before the caller had said a word — so it read 0 on every real verification while
        // the Analyst was running correctly the whole time. The session's own PeakRisk is
        // maintained every three seconds from the policy gate's EffectiveRisk, which is
        // urgency-weighted; the old code took the raw RiskScore and under-reported even when
        // it did fire.
        //
        // Every adjudication path calls this method, so the peak now follows the call rather
        // than a single moment in it.
        if (monitored is not null)
        {
            verification.PeakRiskDuringCall =
                Math.Max(verification.PeakRiskDuringCall, monitored.Session.PeakRisk);
        }

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

        // Who speaks is decided here, once, from configuration — not raced at runtime.
        //
        // When the conversational agent is configured it owns the voice channel: it greets,
        // explains, and asks. Playing a scripted TextSource as well would put two voices on
        // the line talking over each other, which is worse than either alone. DTMF capture
        // is still armed either way, because the digits are the factor and they must be
        // heard whether or not a model is available to chat.
        var agentSpeaks = !string.IsNullOrEmpty(options.Value.RealtimeEndpoint);

        var recognize = new CallMediaRecognizeDtmfOptions(target, maxTonesToCollect: 2)
        {
            Prompt = agentSpeaks ? null : prompt,
            InterToneTimeout = TimeSpan.FromSeconds(10),
            // Longer when an agent is talking: the user is having a conversation before
            // they reach for the keypad, and 20s of that counted as silence.
            InitialSilenceTimeout = TimeSpan.FromSeconds(agentSpeaks ? 55 : 20),
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

        // Live telemetry first. Nothing to store, nothing to breach, and the answers expire
        // on their own — which is why NIST rejects the stored kind and not this.
        var live = await telemetry.BuildAsync(
            verification.SubjectObjectId, verification.SubjectTenantId, 3, cancellationToken);

        if (live.Count > 0)
        {
            var challenge = new List<Agents.TelemetryQuestion>(live);

            // The registered question rides ALONG with the telemetry ones rather than
            // replacing them, and it goes last.
            //
            // On its own it is the weak factor NIST rejects — researchable, permanent,
            // often already breached. Combined, it asks for something an attacker cannot
            // prepare (this morning's sign-in) AND something they cannot observe from the
            // call (a secret the user chose). Defeating one is plausible; defeating both in
            // the same minute is a different problem.
            var registered = await knowledge.GetAsync(
                verification.SubjectTenantId, verification.SubjectObjectId, cancellationToken);

            if (registered?.Question.PlainAnswer is { Length: > 0 } answer)
            {
                challenge.Add(new Agents.TelemetryQuestion(registered.Question.Question, [answer]));
                verification.KnowledgeBacking =
                    $"telemetry+{registered.Backing.ToString().ToLowerInvariant()}";
            }
            else
            {
                verification.KnowledgeBacking = "telemetry";
            }

            verification.KnowledgeQuestion = challenge[0].Question;

            _ = Task.Run(
                () => RunTelemetryChallengeAsync(verification, challenge), CancellationToken.None);
            return true;
        }

        // Otherwise a registered question, if this user set one up. Kept as the fallback
        // for accounts too new to have telemetry, and for tenants that withhold sign-in logs.
        var stored = await knowledge.GetAsync(
            verification.SubjectTenantId, verification.SubjectObjectId, cancellationToken);

        // Record the downgrade. This is the whole reason the fault type exists.
        //
        // Reaching here means the call is about to authenticate somebody with a stored secret
        // instead of facts from their own sign-in activity minutes earlier — a materially
        // weaker check, chosen silently, indistinguishable from the strong path to everyone
        // including the caller. A user hearing only "your first pet" reasonably concluded the
        // location and device questions had been removed from the product.
        faults.Record(Shared.Supportability.Fault.TelemetryUnavailable(
            verification.SubjectTenantId,
            verification.SubjectObjectId,
            telemetry.LastFailure ?? $"Graph returned {telemetry.LastCounts.Raw} sign-ins, "
                + $"{telemetry.LastCounts.Usable} usable (apps: {telemetry.LastCounts.Apps})",
            hadStoredQuestion: stored is not null));

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
    /// Ask a short series of questions built from live sign-in telemetry.
    /// </summary>
    /// <remarks>
    /// ALL of them must pass. That is the point of asking more than one: a single question
    /// with a lucky guess behind it is a coin toss, and an attacker who happens to know
    /// where their victim works should not clear the whole factor on that alone.
    ///
    /// One attempt each, deliberately. These are facts the user lived through hours ago —
    /// if they cannot answer, a second try does not help them, and it does help someone
    /// guessing.
    /// </remarks>
    private async Task RunTelemetryChallengeAsync(
        VerificationSession verification, IReadOnlyList<Agents.TelemetryQuestion> questions)
    {
        // Budget every try, not every question.
        //
        // This used to be AnswerWindow * questions + 45s, which silently assumed one attempt
        // each. Every question actually gets TriesPerQuestion, and each is preceded by a
        // spoken prompt and a settle delay. Dropping from three questions to two therefore
        // cut the budget from 99s to 81s while the work needed stayed near 90s — so the outer
        // token cancelled mid-challenge and refused a caller who had answered everything
        // correctly.
        //
        // Generous on purpose. This is a backstop against a hung call, not a pacing
        // mechanism: the per-answer windows bound the real duration, and the challenge ends
        // the moment the last question is answered.
        var perQuestionSeconds =
            AnswerWindow.TotalSeconds * TriesPerQuestion + PromptAllowance.TotalSeconds;

        using var window = new CancellationTokenSource(
            TimeSpan.FromSeconds(perQuestionSeconds * questions.Count + 45));
        var token = window.Token;

        var monitored = verification.MonitorSessionId is null
            ? null
            : callRegistry.Get(verification.MonitorSessionId);

        if (monitored is null)
        {
            logger.LogWarning(
                "Verification {Id}: no media session, cannot run the telemetry challenge.",
                verification.VerificationId);
            await CompleteAsync(verification, VerificationResult.Failed,
                "The spoken questions could not be asked because the call had no audio channel.");
            return;
        }

        // Whether a conversational agent owns the voice channel changes how the answer
        // window has to be timed, so it is resolved once rather than assumed per branch.
        var agentSpeaks = voiceAgents.For(verification.VerificationId) is not null;

        try
        {
            // Hand the agent the answers it must never say. It is not told what they are
            // for and cannot read them back — they exist only as a list to be cut off on.
            voiceAgents.For(verification.VerificationId)?
                .Forbid(questions.SelectMany(q => q.ExpectedFacts));

            // What Entra actually reported. If a question is unanswerable because the
            // directory holds a city derived from an IP address the user has never been
            // near, that is visible here rather than inferred from a refusal.
            foreach (var q in questions)
            {
                logger.LogInformation(
                    "Verification {Id}: will ask \"{Question}\" expecting [{Facts}].",
                    verification.VerificationId, q.Question, string.Join(" | ", q.ExpectedFacts));
            }

            // Said once, before the first spoken answer is asked for — not in the greeting,
            // where the number-match instruction has to dominate.
            //
            // This existed once, in the conversational agent's prompt, and was deleted in
            // 19cb817 as collateral in a prompt rewrite whose message never mentioned it. It
            // survived only as text on a screen the caller is not looking at and as an
            // apology AFTER enrolment had already failed. Everything that follows is spoken
            // aloud into whatever room the user is standing in, so warning them belongs
            // before the questions, not after them.
            //
            // Spoken as PART OF THE FIRST QUESTION, not as an utterance of its own.
            //
            // As a separate playback it was audibly cut off mid-sentence. Each ACS play is a
            // separate request against the same call, so the question that followed could
            // begin before the warning had finished being heard — and a warning the caller
            // only half hears is worse than none, because they act on the half they got.
            // One playback cannot be interrupted by the next one.
            // Discard anything buffered before the questions began — the number-match
            // prompt, its retries, and their echo. Enrolment has always done this before
            // each recording window; verification never did, which is why its snapshots were
            // dominated by synthesised speech.
            monitored.Biometrics?.Clear();

            var privacyNotice =
                "Before we continue. Please make sure nobody can overhear you, and that "
              + "nobody is helping you answer. If someone is listening, move somewhere "
              + "private now. ";

            for (var index = 0; index < questions.Count && !verification.IsComplete; index++)
            {
                var question = questions[index];

                verification.KnowledgeAttempts++;
                verification.KnowledgeQuestion = question.Question;
                verification.CallState = VerificationCallState.AwaitingAnswer;

                var askedAt = monitored.Session.ElapsedMs(DateTimeOffset.UtcNow);

                await hub.Clients.All.SendAsync(
                    LiveHub.VerificationEvent, VerificationEndpoint.Describe(verification), token);

                // Ask, and wait until it has finished being heard. The privacy notice rides
                // on the first question so the two are one uninterruptible playback.
                var askedText = index == 0 ? privacyNotice + question.Question : question.Question;

                askedAt = await SpeakAndSettleAsync(verification, monitored, askedText, token);

                // Wait for the question to finish being ASKED before timing the answer.
                //
                // The window used to start the instant the request to speak was sent. The
                // agent then took several seconds to generate and say the question, and
                // those seconds came out of the user's time — they heard the question with
                // about two seconds left, said nothing in time, and were refused. They were
                // not given a chance, which is exactly how it felt.
                // Two chances at each question. One was fail-fast on a factor where the
                // agent's own preamble can talk over the start of an answer; a person who
                // mishears a question deserves to be asked again, and a guesser gains
                // almost nothing from a second try at a fact they do not know.
                var correct = false;
                for (var tries = 0; tries < TriesPerQuestion && !correct; tries++)
                {
                    if (tries > 0)
                    {
                        // The notice is not repeated — it was heard once and repeating it
                        // makes the retry sound like a fresh challenge.
                        askedText = "Sorry, once more: " + question.Question;
                        askedAt = await SpeakAndSettleAsync(verification, monitored, askedText, token);
                    }

                    var spoken = await ListenForAnswerAsync(monitored, askedAt, token);

                    // Late echo, or a speakerphone feeding the prompt back for the whole
                    // call. Discarded rather than judged: it costs an attempt for words the
                    // user never said.
                    // Measured against the QUESTION only, never the whole prompt.
                    //
                    // Matching the full utterance looks stricter and is actively harmful: the
                    // test discards a reply when half its words appear in the reference text,
                    // so enlarging that text with a privacy notice — last, time, signed,
                    // answer, private, sure, now — made genuine answers trip it. "I signed in
                    // from Kuala Lumpur last time" is three of six words, and the caller was
                    // told nothing had been heard.
                    //
                    // The asymmetry decides it. Missing an echo costs one attempt of three.
                    // Discarding a real answer costs every attempt and refuses somebody who
                    // answered correctly.
                    if (spoken is not null && IsEchoOf(question.Question, spoken))
                    {
                        logger.LogInformation(
                            "Verification {Id}: discarded an echo of the question — [{Spoken}].",
                            verification.VerificationId, spoken);
                        spoken = null;
                    }

                    if (spoken is null)
                    {
                        logger.LogWarning(
                            "Verification {Id}: NOTHING was heard for question {Index} "
                            + "(attempt {Try}). The transcript had no protected-user speech "
                            + "in the answer window.",
                            verification.VerificationId, index + 1, tries + 1);
                    }
                    else
                    {
                        logger.LogInformation(
                            "Verification {Id}: heard [{Spoken}] for question {Index}.",
                            verification.VerificationId, spoken, index + 1);
                    }

                    // Acknowledge the instant we have the answer, before judging it.
                    //
                    // Judging takes a few seconds against a model, and silence during those
                    // seconds is indistinguishable from not having been heard — which is
                    // exactly how it felt: answer, nothing, next question. The wording is
                    // deliberately neutral: "recorded" is true and reveals nothing, whereas
                    // anything warmer would leak the verdict before the adjudicator has one.
                    if (spoken is not null)
                    {
                        await SpeakAsync(
                            verification, "Thank you. Your response has been recorded.", token);
                    }

                    correct = spoken is not null && await judge.IsEquivalentAsync(
                        question.Question,
                        Agents.TelemetryChallenge.DescribeExpected(question),
                        spoken,
                        token);
                }

                // "Not answered correctly" conflated two different failures — nothing was
                // heard, and something was heard and rejected. They have opposite fixes:
                // one is a microphone, transcription or timing problem, the other is a
                // matching problem. Reported separately so the next call diagnoses itself.
                logger.LogInformation(
                    "Verification {Id}: telemetry question {Index}/{Total} — {Outcome}. Asked: {Question}",
                    verification.VerificationId, index + 1, questions.Count,
                    correct ? "correct" : "rejected",
                    question.Question);

                if (!correct)
                {
                    await CompleteAsync(verification, VerificationResult.Failed,
                        "The identity questions were not answered correctly.");
                    return;
                }
            }

            if (verification.IsComplete)
            {
                return;
            }

            // Score the voice on the speech they just produced answering the questions.
            //
            // Passive on purpose: no extra prompt, no extra time on a call that is already
            // long, and several seconds of natural speech rather than one read-aloud phrase.
            // Replay resistance comes from the questions being unpredictable — an attacker
            // cannot pre-record an answer to a question that did not exist until this call.
            var voice = await ScoreVoiceAsync(verification, token);

            // Re-adjudicated rather than passed outright: coercion heard DURING these
            // questions must still refuse, and that evidence only exists now.
            var assessment = ResolveAssessment(verification);
            var verdict = VerificationAdjudicator.Adjudicate(
                verification.MatchCode, verification.EnteredCode ?? verification.MatchCode,
                verification.Attempts, assessment, voice);

            await CompleteAsync(verification,
                verdict.Result ?? VerificationResult.Passed,
                verdict.Result is VerificationResult.BlockedCoercion
                                or VerificationResult.BlockedVoiceMismatch
                    ? verdict.Reason
                    : $"Number match confirmed, and {questions.Count} identity questions "
                    + "answered from live sign-in activity. No coercion detected.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telemetry challenge failed for {Id}.", verification.VerificationId);
            if (!verification.IsComplete)
            {
                await CompleteAsync(verification, VerificationResult.Failed,
                    "The identity questions could not be completed.");
            }
        }
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

                // The agent already asks the question once, from its own instructions, so
                // the first attempt is its turn and this stays silent. Retries do need
                // prompting — and go through SpeakAsync, which routes them to the agent
                // rather than putting a second voice on the line.
                var agentOwnsTheVoice = voiceAgents.For(verification.VerificationId) is not null;

                if (!agentOwnsTheVoice)
                {
                    // Same warning as the telemetry path, and for the same reason: the answer
                    // is about to be spoken aloud into whatever room the user is in. Folded
                    // into the first prompt rather than sent as a separate utterance so this
                    // path gains no extra playback round trip.
                    var preamble = verification.KnowledgeAttempts == 1
                        ? "Thank you. One more check. Before you answer, please make sure "
                        + "nobody can overhear you, and that nobody is helping you. "
                        : "That did not match. Please answer again. ";

                    await SpeakAsync(verification, preamble + question.Question, token);
                }
                else if (verification.KnowledgeAttempts > 1)
                {
                    await SpeakAsync(
                        verification, "That did not match. Ask the question again.", token);
                }
                await hub.Clients.All.SendAsync(
                    LiveHub.VerificationEvent, VerificationEndpoint.Describe(verification), token);

                var spoken = await ListenForAnswerAsync(monitored, askedAt, token);

                // Exact first: free, certain, and needs no readable secret. Only when it
                // fails does the language model look at the two answers — which is the whole
                // reason a readable copy is kept, and why it is read no earlier than this.
                var matched = spoken is not null && KnowledgeChallenge.Verify(question, spoken);

                if (!matched && spoken is not null && !string.IsNullOrEmpty(question.PlainAnswer))
                {
                    matched = await judge.IsEquivalentAsync(
                        question.Question, question.PlainAnswer, spoken, token);
                }

                if (matched)
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

    /// <summary>
    /// Compare the caller's voice to their enrolled template, and record what came of it.
    /// </summary>
    /// <remarks>
    /// Never throws and never fails the verification. Every unhappy path — no enrolment, an
    /// unreachable scorer, too little speech — lands on NotAssessed, because none of them
    /// are evidence about who is on the call and none should cost anybody access.
    /// </remarks>
    private async Task<VoiceDecision?> ScoreVoiceAsync(
        VerificationSession verification, CancellationToken token)
    {
        try
        {
            if (verification.MonitorSessionId is null
                || string.IsNullOrEmpty(verification.SubjectObjectId)
                || string.IsNullOrEmpty(verification.SubjectTenantId))
            {
                return null;
            }

            var biometrics = callRegistry.Get(verification.MonitorSessionId)?.Biometrics;
            if (biometrics is null)
            {
                return null;
            }

            var enrolled = await voiceprints.GetAsync(
                verification.SubjectTenantId, verification.SubjectObjectId, token);

            var decision = await biometrics.ScoreAsync(
                enrolled?.Template,
                voiceprint,
                options.Value.VoiceEnforce,
                options.Value.VoiceAcceptThreshold,
                options.Value.VoiceRejectThreshold,
                token);

            verification.VoiceScore = decision.Score;
            verification.VoiceOutcome = decision.Outcome.ToString();
            verification.VoiceDetail = decision.Reason;
            verification.RequiresStepUp = decision.RequiresStepUp;

            // Not an error, and worth recording anyway: the factor that asks WHO is speaking
            // did not run, so this verification rests on things a thief of the handset also
            // satisfies. The reason is carried through so the four quite different causes stay
            // distinguishable.
            if (decision.Outcome == Shared.Voice.VoiceOutcome.NotAssessed)
            {
                faults.Record(Shared.Supportability.Fault.VoiceNotAssessed(
                    verification.VerificationId, verification.SubjectUpn, decision.Reason));
            }

            logger.LogInformation(
                "Verification {Id}: voice {Outcome} (score {Score}, step-up {StepUp}).",
                verification.VerificationId, decision.Outcome,
                decision.Score?.ToString("F4") ?? "none", decision.RequiresStepUp);

            return decision;
        }
        catch (Exception ex)
        {
            // Deliberately swallowed. A supplementary factor must not be able to break the
            // primary one.
            logger.LogWarning(ex, "Voice scoring failed for {Id}.", verification.VerificationId);
        }

        // Null, never a Mismatch. A supplementary factor that fails to run must not be able
        // to lock anyone out — the one way this feature could take down the primary path.
        return null;
    }

    /// <summary>
    /// Say something and wait until the caller has actually heard all of it.
    /// </summary>
    /// <returns>The transcript position AFTER the prompt, to time an answer from.</returns>
    /// <remarks>
    /// This exists because the prompt comes back on the caller's own channel. Teams echoes
    /// it, or their handset's speaker feeds its microphone, and speech recognition
    /// attributes it to the protected user like any other speech. Listening from the moment
    /// playback is REQUESTED therefore captures the question itself as the answer — the log
    /// showed "heard [OK, which town, city or?]" judged against "Petaling Jaya", twice,
    /// spending both attempts in eight seconds before the user had finished listening.
    ///
    /// Waiting on PlayCompleted, then a short tail for the echo to drain, means the window
    /// contains only what the person said afterwards.
    /// </remarks>
    private async Task<long> SpeakAndSettleAsync(
        VerificationSession verification, LiveCall monitored, string text, CancellationToken token)
    {
        // Stop keeping audio for the voiceprint while we talk, and resume once the echo has
        // drained. This method already brackets exactly that window for the transcript, so
        // the biometric buffer rides the same boundary rather than inventing a second notion
        // of "is EntraGuard speaking" that could drift from it.
        if (monitored.Biometrics is not null)
        {
            monitored.Biometrics.Accepting = false;
        }

        // Armed BEFORE speaking: PlayCompleted can arrive before the await would start.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _playbackDone[verification.VerificationId] = finished;

        await SpeakAsync(verification, text, token);

        await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(20), token));
        _playbackDone.TryRemove(verification.VerificationId, out _);

        // Tail for the echo of the last syllable to stop arriving.
        await Task.Delay(TimeSpan.FromMilliseconds(900), token);

        // Whatever arrives from here until the next prompt is the caller.
        if (monitored.Biometrics is not null)
        {
            monitored.Biometrics.Accepting = true;
        }

        return monitored.Session.ElapsedMs(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Is this "answer" just the question coming back?
    /// </summary>
    /// <remarks>
    /// A second line of defence behind the playback wait. Echo can arrive late, and a
    /// caller on speakerphone can produce it for the whole call — so anything that is
    /// mostly words from the question just asked is discarded rather than judged. Judging
    /// it costs the user an attempt for something they did not say.
    /// </remarks>
    private static bool IsEchoOf(string question, string spoken)
    {
        var asked = question.ToLowerInvariant()
            .Split([' ', ',', '?', '.', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToHashSet();

        var said = spoken.ToLowerInvariant()
            .Split([' ', ',', '?', '.', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToArray();

        if (said.Length == 0 || asked.Count == 0)
        {
            return false;
        }

        // New words settle it before any ratio does.
        //
        // The ratio alone was wrong, and wrong in the direction that refuses people. Callers
        // answer in the question's own words — "I signed in from Kuala Lumpur last time"
        // mirrors "the last time you signed in" — so signed, last and time are three of its
        // six words, it hit the fifty-percent line exactly, and a correct answer was thrown
        // away. The caller was then told nothing had been heard, spent both attempts that
        // way, and was refused having answered correctly.
        //
        // An echo is the question and nothing else. It cannot introduce content the question
        // does not contain, so two unseen words are proof a person contributed something —
        // a place, a device, a name. Two rather than one, because a single transcription
        // error inside a real echo should not rescue it.
        var novel = said.Count(w => !asked.Contains(w));
        if (novel >= 2)
        {
            return false;
        }

        // Otherwise: mostly the question's own words, and nothing new said. That is an echo.
        return said.Count(asked.Contains) * 2 >= said.Length;
    }

    /// <summary>
    /// Block until the agent has stopped talking, then return the new transcript position.
    /// </summary>
    /// <remarks>
    /// The agent speaks asynchronously — the coordinator asks it to say something and gets
    /// control back immediately, long before the caller has heard a word. Timing the answer
    /// window from that moment charges the user for the agent's own speech: they heard the
    /// question with about two seconds left and were refused for not answering in time.
    ///
    /// "Stopped talking" is a short gap with no new utterance. Bounded, because an agent
    /// that never stops must not hold the call open indefinitely.
    /// </remarks>
    private static async Task<long> WaitUntilAskedAsync(
        LiveCall monitored, long askedAt, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        var lastSeen = DateTimeOffset.UtcNow;
        var count = monitored.Session.FinalUtterances.Count(u => u.OffsetMs > askedAt);

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);

            var now = monitored.Session.FinalUtterances.Count(u => u.OffsetMs > askedAt);
            if (now != count)
            {
                count = now;
                lastSeen = DateTimeOffset.UtcNow;
                continue;
            }

            if (count > 0 && DateTimeOffset.UtcNow - lastSeen > TimeSpan.FromSeconds(1.2))
            {
                break;
            }
        }

        // Answers are timed from here, so the agent's own words are behind us.
        return monitored.Session.ElapsedMs(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Say something on the call, through whoever owns the voice channel.
    /// </summary>
    /// <remarks>
    /// Every line this class speaks goes through here, so there is exactly one place that
    /// decides between the agent and PlayToAll. Callers cannot get it wrong by forgetting a
    /// check, which is how the second voice kept coming back.
    /// </remarks>
    private async Task SpeakAsync(
        VerificationSession verification, string text, CancellationToken cancellationToken)
    {
        if (voiceAgents.For(verification.VerificationId) is { } agent)
        {
            await agent.SayAsync(text, cancellationToken);
            return;
        }

        await callAutomation
            .GetCallConnection(verification.CallConnectionId)
            .GetCallMedia()
            .PlayToAllAsync(
                new PlayToAllOptions(new TextSource(text) { VoiceName = "en-US-AvaMultilingualNeural" }),
                cancellationToken);
    }

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
                // Settle time for a multi-word answer. Kept short: this delay is felt as
                // dead air by someone who has just finished speaking.
                await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);

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

    /// <summary>
    /// Speak the scripted challenge after all, because the agent could not be created.
    /// </summary>
    /// <remarks>
    /// The fallback exists because a verification call with no voice on it is a dead end —
    /// the user hears silence and hangs up. A degraded call that still asks for the number
    /// is worth far more than a conversational one that never starts.
    /// </remarks>
    public async Task SpeakScriptedFallbackAsync(
        VerificationSession verification, CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "Verification {Id}: voice agent unavailable, speaking the scripted challenge.",
            verification.VerificationId);

        try
        {
            await callAutomation.GetCallConnection(verification.CallConnectionId).GetCallMedia()
                .PlayToAllAsync(new PlayToAllOptions(new TextSource(
                    $"This is a security verification from EntraGuard for {verification.ApplicationName}. " +
                    "Please enter the two digit number shown on your screen, using your keypad. " +
                    "If you did not just try to sign in, hang up now and contact your IT help desk.")
                {
                    VoiceName = "en-US-AvaMultilingualNeural",
                }), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not speak the scripted fallback for {Id}.", verification.VerificationId);
        }
    }

    public async Task CompleteAsync(
        VerificationSession verification,
        VerificationResult result,
        string reason,
        CancellationToken cancellationToken = default)
    {
        verification.CallState = VerificationCallState.Ended;
        _buffers.TryRemove(verification.VerificationId, out _);

        // Last chance to compare a voice.
        //
        // Scoring used to hang off one call site inside the telemetry challenge, so a
        // verification that passed on the code alone — or took the stored-question path —
        // finished having never compared anything, and recorded NotAssessed for a user who
        // had a perfectly good enrolled profile. This is the single funnel every completion
        // passes through, so it is the one place the check cannot be skipped.
        //
        // The earlier call site stays: under VOICE_MODE=enforce the verdict has to know
        // before it is decided, and this runs after. Here it only fills a gap.
        if (verification.VoiceOutcome == "NotAssessed")
        {
            await ScoreVoiceAsync(verification, CancellationToken.None);
        }

        // Carry the final peak across before the monitor session is torn down.
        ResolveAssessment(verification);

        // What this attempt looked like, as a number, recorded and never acted on.
        var risk = VerificationRisk.Score(
            verification.PeakRiskDuringCall,
            verification.VoiceOutcome,
            verification.Attempts,
            verification.EndpointKind,
            verification.KnowledgeAttempts);

        verification.RiskScore = risk.Score;
        verification.RiskBand = risk.Band.ToString();
        verification.RiskContributors = risk.Contributors;

        if (risk.Score > 0)
        {
            logger.LogInformation(
                "Verification {Id}: risk {Score:F0}/100 ({Band}) — {Why}.",
                verification.VerificationId, risk.Score, risk.Band,
                string.Join("; ", risk.Contributors));
        }

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
        // Room for the closing line AND the hang-up after it. The budget was 20 seconds
        // while the playback wait alone could take 15, so the hang-up inherited an expired
        // token, threw, and was swallowed — leaving the caller holding an open line after
        // being told the outcome.
        using var closingWindow = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var closingToken = closingWindow.Token;

        // Publish the verdict BEFORE saying goodbye.
        //
        // The relying party polls for the outcome, and it used to be published only after
        // the closing line had played and the call had been torn down — so the browser sat
        // on "verifying" for the length of a goodbye it could not hear. The decision exists
        // the moment it is made; announcing it should not wait on the courtesy that follows.
        await sink.WriteVerificationAsync(verification, CancellationToken.None);
        await hub.Clients.All.SendAsync(
            LiveHub.VerificationEvent, VerificationEndpoint.Describe(verification), CancellationToken.None);

        // Armed before playing, because PlayCompleted can arrive before the await starts.
        _playbackDone.TryAdd(
            verification.VerificationId,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        try
        {
            if (!string.IsNullOrEmpty(verification.CallConnectionId))
            {
                // Through the same single owner. The closing line used to be played
                // directly, so it landed on top of whatever the agent was mid-way through
                // saying — the double voice at the END of the call rather than during it.
                await SpeakAsync(verification, closing, closingToken);

                // Wait for the line to FINISH, rather than guessing how long it takes.
                //
                // A fixed delay cut the user off mid-word — "Your identity is va—" — because
                // the sentence is longer than the guess. Any fixed number is wrong for some
                // sentence, so this waits for the PlayCompleted event ACS already sends, and
                // falls back to a generous ceiling if that event never arrives.
                var finished = _playbackDone.GetOrAdd(
                    verification.VerificationId,
                    _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

                await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(12), closingToken));
                _playbackDone.TryRemove(verification.VerificationId, out _);

                // A breath after the last word, so the hang-up does not clip its tail.
                await Task.Delay(TimeSpan.FromMilliseconds(700), closingToken);
                // Uncancellable. Ending the call is the last obligation of this method and
                // must not be skipped because an earlier step ran long — an open line after
                // a verdict is worse than any delay that caused it.
                await callAutomation.GetCallConnection(verification.CallConnectionId)
                    .HangUpAsync(forEveryone: true, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not play the closing message for {Id}.", verification.VerificationId);

            // Still hang up. The previous shape returned here on any playback problem and
            // left the line open, so a failure to say goodbye became a failure to end.
            try
            {
                if (!string.IsNullOrEmpty(verification.CallConnectionId))
                {
                    await callAutomation.GetCallConnection(verification.CallConnectionId)
                        .HangUpAsync(forEveryone: true, CancellationToken.None);
                }
            }
            catch (Exception hangUpError)
            {
                logger.LogWarning(hangUpError, "Could not hang up {Id}.", verification.VerificationId);
            }
        }

        if (verification.MonitorSessionId is not null)
        {
            await callRegistry.RemoveAsync(verification.MonitorSessionId);
        }

        logger.LogInformation("Verification {Id} for {Upn}: {Result} — {Reason}",
            verification.VerificationId, verification.SubjectUpn, result, reason);
    }
}
