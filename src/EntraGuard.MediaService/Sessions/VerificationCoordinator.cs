using EntraGuard.Shared.Voice;
using System.Collections.Concurrent;
using System.Text;
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
    Persistence.VerificationLedger ledger,
    Sinks.FaultRecorder faults,
    KnowledgeStore knowledge,
    Agents.KnowledgeJudge judge,
    Agents.TelemetryChallenge telemetry,
    Agents.ProfileChallenge profileChallenge,
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
    /// Measured on a live call, a longer window made the check feel broken — the user
    /// answered, then stood holding a silent phone while the window ran down and audio kept
    /// streaming. Someone who knows their answer says it within a couple of seconds; someone
    /// who does not is not helped by ten more.
    ///
    /// Eighteen rather than the ten this comment used to argue for, and the number is the
    /// honest one: recognition needs a moment to settle after the speaker stops, and ten cut
    /// real answers off. The prose said ten long after the value said eighteen, which is the
    /// kind of drift that makes every other comment in this file worth less.
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

    /// <summary>
    /// Said the moment an answer is heard, while the judge is still deciding.
    /// </summary>
    /// <remarks>
    /// Chosen by question index and NEVER by whether the answer was right. That is the whole
    /// safety property of this list: the caller hears the same sequence of words whether they
    /// answered correctly or not, so nothing here tells them — or whoever is stood next to
    /// them — how the verification is going before the adjudicator has decided. Anything
    /// congratulatory, or any phrase reserved for a correct answer, would leak the verdict
    /// into the room.
    ///
    /// Several rather than one because the same sentence repeated after every answer is how
    /// the call sounded like a machine in the first place.
    ///
    /// None of them refer to where in the call the caller is. "One more" reads well and is a
    /// lie whenever the challenge happens to be shorter than this list — two questions makes
    /// the second one the last, and promising a third that never comes is worse than sounding
    /// slightly plainer.
    /// </remarks>
    private static readonly string[] Acknowledgements =
    [
        "Got it, thanks.",
        "Thank you.",
        "That's noted, thanks.",
    ];

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
    /// <summary>
    /// Refuse a verification that failed its questions, saying why it REALLY failed.
    /// </summary>
    /// <remarks>
    /// A refusal on the questions used to go straight to <see cref="CompleteAsync"/> as a
    /// plain <c>Failed</c>, without ever asking the analyst what it had been watching. That
    /// made the product's headline claim unreachable by the most likely route to it: somebody
    /// being coached is being fed answers by a person who does not know them, so they get the
    /// questions WRONG, and the wrong-answer path was the one path that never looked at the
    /// coercion score.
    ///
    /// Measured on vrf-9c00cd355de1. The caller was read answers by a "help desk" ("just say
    /// Kuala Lumpur, that's what it wants"), the analyst climbed from 5 to 85 and the session
    /// reached 100 — and the record says the questions were answered incorrectly, which is
    /// true, and buries the only part anybody needed to know.
    ///
    /// The gate is unchanged and is still the adjudicator's, so this cannot refuse anyone the
    /// adjudicator would not, and it cannot grant anybody anything: both outcomes deny access.
    /// All that changes is which of two refusals is recorded and spoken.
    /// </remarks>
    private async Task RefuseAsync(VerificationSession verification, string questionReason)
    {
        var assessment = ResolveAssessment(verification);

        if (assessment is not null
            && assessment.RiskScore >= VerificationAdjudicator.CoercionRiskThreshold
            && assessment.Confidence >= VerificationAdjudicator.CoercionConfidenceThreshold)
        {
            var vectors = string.Join(", ", assessment.Vectors);

            logger.LogWarning(
                "Verification {Id} BLOCKED — coercion detected while the identity questions "
              + "were being answered (risk {Risk:F0}, confidence {Confidence:P0}).",
                verification.VerificationId, assessment.RiskScore, assessment.Confidence);

            await CompleteAsync(verification, VerificationResult.BlockedCoercion,
                $"{questionReason} Separately, EntraGuard detected the user was being coached "
              + $"during the verification call (risk {assessment.RiskScore:F0}/100, confidence "
              + $"{assessment.Confidence:P0}{(vectors.Length > 0 ? $", {vectors}" : "")}). "
              + "Access was refused.");
            return;
        }

        await CompleteAsync(verification, VerificationResult.Failed, questionReason);
    }

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
        if (assessment is not null && assessment.Rationale != "No analysis performed yet.") verification.AnalystAssessed = true;
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

        var promptText = isRetry
            ? "That number was not correct. Please enter the two digit number shown on your screen."
            : $"This is a security verification from EntraGuard for {verification.ApplicationName}. " +
              "Please enter the two digit number shown on your screen, using your keypad. " +
              "If you did not just try to sign in, hang up now and contact your IT help desk.";
        if (!isRetry && verification.TransactionSummary is not null) promptText = verification.TransactionSummary + " " + promptText;

        var prompt = new TextSource(promptText) { VoiceName = "en-US-AvaMultilingualNeural" };

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

        // Hand the agent the words. This is the line that was missing.
        //
        // agentSpeaks suppressed the scripted TextSource above on the understanding that the
        // agent would speak instead — and nothing ever told it what to say. SpeakAsync was
        // the only caller of SayAsync in the whole class, and nothing called SpeakAsync here.
        //
        // What the caller actually got: server_vad heard them say hello, the model generated
        // a turn from its persona alone — "conduct one identity question at a time" — and
        // INVENTED a question. One live call was asked for a "username", which this system
        // never asks for and has no answer to. Then it fell silent waiting for a system
        // instruction that was never coming.
        //
        // Waited for, not assumed: PromptAsync runs on CallConnected and the agent registers
        // when the media socket opens, which is a different event. If the agent never
        // arrives, or arrives faulted, the scripted prompt is played after all — silence is
        // the one outcome that must not be possible here.
        if (agentSpeaks)
        {
            var agent = await WaitForHealthyAgentAsync(
                verification.VerificationId, TimeSpan.FromSeconds(6), cancellationToken);

            if (agent is not null)
            {
                await agent.SayAsync(promptText, cancellationToken);
            }
            else
            {
                logger.LogWarning(
                    "Verification {Id}: no healthy voice agent took the channel; "
                  + "playing the scripted prompt instead.", verification.VerificationId);

                await callAutomation.GetCallConnection(verification.CallConnectionId)
                    .GetCallMedia().PlayToAllAsync(new PlayToAllOptions(prompt), cancellationToken);
            }
        }

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
    /// <summary>
    /// Which facet a sign-in question belongs to.
    ///
    /// The sign-in composer predates facets and labels nothing, but the selection rule that
    /// stops a call asking three versions of "where were you" needs one. Derived from the
    /// wording rather than added to TelemetryQuestion, because that record is shared with the
    /// probe machinery and the judge, and widening it to carry a field only this needs would
    /// be the larger change.
    /// </summary>
    private static string FacetOf(string question) =>
        question.Contains("town", StringComparison.OrdinalIgnoreCase)
        || question.Contains("city", StringComparison.OrdinalIgnoreCase)
            ? "location"
        : question.Contains("device", StringComparison.OrdinalIgnoreCase)
        || question.Contains("browser", StringComparison.OrdinalIgnoreCase)
            ? "device"
            : "stored";

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

        // Everything else the caller's directory and activity can support — manager, office,
        // team, the organiser of their last meeting, who emailed them, the file they opened.
        // Fetched alongside rather than instead of: sign-in facts remain the strongest thing
        // we can ask, and each profile source is allowed to be missing without consequence.
        var profile = await profileChallenge.BuildAsync(
            verification.SubjectObjectId, verification.SubjectTenantId, cancellationToken);

        if (live.Questions.Count > 0 || profile.Count > 0)
        {
            // One pool, chosen from at random.
            //
            // A fixed set is a set an attacker can rehearse. ChallengeSelection also enforces
            // the two rules that keep a random pick honest: never two questions about the same
            // thing, and never a call built entirely from facts that are on the caller's
            // public profile.
            var pool = live.Questions
                .Select(q => new ChallengeCandidate(
                    FacetOf(q.Question), q.Question, q.ExpectedFacts, FactSource.SignIn, 3))
                .Concat(profile)
                .ToList();

            // The registered question rides ALONG with the telemetry ones rather than
            // replacing them, and it goes last.
            //
            // On its own it is the weak factor NIST rejects — researchable, permanent,
            // often already breached. Combined, it asks for something an attacker cannot
            // prepare (this morning's sign-in) AND something they cannot observe from the
            // call (a secret the user chose). Defeating one is plausible; defeating both in
            // the same minute is a different problem.
            //
            // Fetched BEFORE the selection, not after, because how many live questions to
            // draw depends on whether this exists. A call asks DefaultCount questions in
            // total; the rider takes one of those seats rather than adding a fifth.
            var registered = await knowledge.GetAsync(
                verification.SubjectTenantId, verification.SubjectObjectId, cancellationToken);

            var rider = registered?.Question.PlainAnswer is { Length: > 0 } answer
                ? new Agents.TelemetryQuestion(registered.Question.Question, [answer])
                : null;

            // Appending the rider on top of a full selection quietly made the challenge
            // HARDER: five asked needs four correct where four asked needs three, so adding
            // a weak question raised the bar instead of widening the evidence. It also made
            // the call a question longer, and length is already the top complaint. Observed
            // on vrf-9c00cd355de1, whose log announced four questions and then asked five.
            var liveCount = ChallengeSelection.DefaultCount - (rider is null ? 0 : 1);
            var selected = ChallengeSelection.Select(pool, liveCount);

            var challenge = selected
                .Select(c => new Agents.TelemetryQuestion(c.Question, c.ExpectedFacts))
                .ToList();
            var provenance = selected.Select(c => new QuestionEvidence(c.Source.ToString(), c.Facet)).ToList();

            if (rider is not null)
            {
                // Still last, still a rider. It is the weak factor NIST rejects — researchable
                // and permanent — so it adds confidence to a challenge rather than forming one,
                // and it is appended after selection so it never displaces a live fact.
                challenge.Add(rider);
                provenance.Add(new QuestionEvidence("Registered", "stored"));
                verification.KnowledgeBacking =
                    $"telemetry+{registered!.Backing.ToString().ToLowerInvariant()}";
            }
            else
            {
                verification.KnowledgeBacking = "telemetry";
            }

            // Logged AFTER the rider joins, because the count is what decides the pass rule
            // and the previous line reported the pre-rider number. A log that says four
            // while five are asked is worse than no log: it is the number you reach for
            // when the arithmetic in a refusal does not add up.
            logger.LogInformation(
                "Verification {Id}: asking {Count} of {Pool} available questions ({Required} "
              + "needed) — {Facets}{Rider}.",
                verification.VerificationId, challenge.Count, pool.Count,
                ChallengeSelection.Required(challenge.Count),
                string.Join(", ", selected.Select(c => $"{c.Facet}/{c.Source}")),
                rider is null ? "" : ", registered/Stored");

            if (challenge.Count == 0) return false;
            verification.QuestionEvidence = provenance;
            verification.KnowledgeBacking = string.Join("+", provenance.Select(p => p.Source).Distinct());
            verification.KnowledgeQuestion = challenge[0].Question;

            _ = Task.Run(
                () => RunTelemetryChallengeAsync(verification, challenge, live),
                CancellationToken.None);
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
        verification.QuestionEvidence = [new QuestionEvidence("Registered", "stored")];

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
    /// <param name="questions">
    /// The whole challenge, in the order it is asked: the telemetry questions first, then the
    /// registered question if the user has one.
    /// </param>
    /// <param name="live">
    /// What the sign-in record produced. Passed whole rather than as a probe list, because
    /// <c>live.Questions.Count</c> is what separates the questions a probe may follow from
    /// the registered one it may not.
    /// </param>
    private async Task RunTelemetryChallengeAsync(
        VerificationSession verification,
        IReadOnlyList<Agents.TelemetryQuestion> questions,
        Agents.TelemetryChallengeSet live)
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

        // Probes cost real time too, and the budget has to know.
        //
        // One try each, not TriesPerQuestion — a probe is never retried, because the caller
        // has already passed the question it deepens and a second attempt at a detail they
        // could not recall is time spent for nothing.
        //
        // Budgeted at the ELEVATED count even on a calm call, which will usually leave slack.
        // The asymmetry decides it, exactly as it did the last time this line was wrong:
        // slack costs a backstop that fires slightly later on a genuinely hung call, while
        // being short cancels the outer token mid-challenge and refuses somebody who has
        // answered everything correctly.
        var perProbeSeconds = AnswerWindow.TotalSeconds + PromptAllowance.TotalSeconds;

        using var window = new CancellationTokenSource(
            TimeSpan.FromSeconds(
                perQuestionSeconds * questions.Count
              + perProbeSeconds * Agents.ConversationDirector.ElevatedBudget
              + 45));
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
            //
            // The probes' facts are listed too, explicitly. Today they overlap the questions'
            // facts entirely — a probe narrows an answer the primary already accepted — so
            // this adds nothing, and that is exactly why it is written down. The overlap is a
            // property of the probes that exist right now, not of what a probe is, and the
            // day somebody composes one from a fact no question asks for, the agent would be
            // free to say it aloud. An instruction not to reveal a secret is a request;
            // never holding it is the control.
            voiceAgents.For(verification.VerificationId)?
                .Forbid(questions.SelectMany(q => q.ExpectedFacts)
                    .Concat(live.FollowUps.SelectMany(p => p.ExpectedFacts)));

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

            // One probe per facet across the whole call rather than per question. The
            // location probe deepens the location answer wherever that answer came from, and
            // asking it twice would ask for something the caller has already given.
            var facetsProbed = new HashSet<string>(StringComparer.Ordinal);

            // Everything the caller has said, across every question.
            //
            // Per-question was wrong in the way that matters. Somebody who names their city
            // in the first answer and their device in the second would be asked, on the
            // third, whereabouts they were — because the only answer in scope was "Windows",
            // which covers no location. Asking for something the caller volunteered two
            // questions ago is precisely the not-listening this feature exists to remove.
            var heard = new StringBuilder();

            // Tallied across the whole challenge rather than per question, because the rule
            // is now "most of them" and not "all of them".
            var answeredCorrectly = 0;
            var answeredWrongly = 0;

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
                // What they actually said, kept past the retry loop so a probe can avoid
                // asking for something they have already told us.
                string? lastSpoken = null;

                var correct = false;
                for (var tries = 0; tries < TriesPerQuestion && !correct; tries++)
                {
                    if (tries > 0)
                    {
                        // The notice is not repeated — it was heard once and repeating it
                        // makes the retry sound like a fresh challenge.
                        //
                        // The retry asks for SPELLING, because the commonest reason a second
                        // attempt is needed is that the first was transcribed wrongly rather
                        // than answered wrongly. Names, places and addresses are what speech
                        // recognition gets wrong on a phone line, and they are exactly what
                        // these questions ask for — so repeating the question verbatim asks
                        // the caller to fail the same way twice.
                        //
                        // Harmless when spelling is not needed: nobody is required to spell
                        // "Windows", and a caller who simply repeats themselves is judged
                        // exactly as before.
                        // An INSTRUCTION, never a question.
                        //
                        // This first said "Could you say it again — and spell out anything
                        // unusual?" A caller answered it, exactly as asked: the transcript
                        // reads "Yeah, one more time." That was judged as their answer to the
                        // identity question, refused, and cost them the second of three
                        // attempts. Any retry phrased as a question invites a reply that is
                        // not the answer, and the listening window cannot tell the two apart.
                        askedText = "Sorry, I did not catch that. Please say it again, and "
                                  + "spell out anything unusual, letter by letter. "
                                  + question.Question;
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
                    if (spoken is not null && IsOwnAcknowledgement(spoken))
                    {
                        logger.LogInformation(
                            "Verification {Id}: discarded our own acknowledgement echoing back — [{Spoken}].",
                            verification.VerificationId, spoken);
                        spoken = null;
                    }

                    if (spoken is not null && IsEchoOf(question.Question, spoken))
                    {
                        logger.LogInformation(
                            "Verification {Id}: discarded an echo of the question — [{Spoken}].",
                            verification.VerificationId, spoken);
                        spoken = null;
                    }

                    if (spoken is not null)
                    {
                        lastSpoken = spoken;
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
                    // exactly how it felt: answer, nothing, next question.
                    //
                    // Warm, and still empty of verdict. That combination is the constraint:
                    // "Your response has been recorded" was true and revealed nothing, and
                    // also sounded like a machine logging a ticket. Every phrase in
                    // Acknowledgements says only that somebody heard them.
                    //
                    // The judge is STARTED FIRST and awaited after, so the acknowledgement
                    // plays over the top of it.
                    //
                    // The line above describes masking the judging latency, and that is what
                    // this is for — but awaiting the acknowledgement before calling the judge
                    // made the two consecutive, so the phrase meant to hide a few seconds
                    // added a few seconds of its own. Measured on a live call, playback runs
                    // 12-13s per prompt and the judge answers in 1.5-3.5s; overlapping them
                    // takes the judge off the critical path entirely, for four questions a
                    // call, without changing one word the caller hears.
                    //
                    // This matters beyond politeness now: an External Authentication Method
                    // must finish inside the window Entra gives it before abandoning the
                    // sign-in, so seconds spent waiting in series are seconds of headroom.
                    var judging = spoken is null
                        ? Task.FromResult(false)
                        : judge.IsEquivalentAsync(
                            question.Question,
                            Agents.TelemetryChallenge.DescribeExpected(question),
                            spoken,
                            token);

                    if (spoken is not null)
                    {
                        await SpeakAsync(verification, Acknowledgements[index % Acknowledgements.Length], token);
                    }

                    correct = await judging;

                    // If they spelled it out, judge the word they spelled.
                    //
                    // The retry asks them to "spell out anything unusual, letter by letter",
                    // and nothing could read the result: a caller spelled MALAYSIA and the
                    // judge compared "M A L A Y S I A" against "Malaysia" and refused it.
                    // Asking somebody to do something and then failing them for doing it is
                    // the worst refusal this system can produce.
                    if (!correct && spoken is not null
                        && SpelledOutAnswer.Collapse(spoken) is { } spelled)
                    {
                        logger.LogInformation(
                            "Verification {Id}: [{Spoken}] read as the spelling of [{Spelled}].",
                            verification.VerificationId, spoken, spelled);

                        correct = await judge.IsEquivalentAsync(
                            question.Question,
                            Agents.TelemetryChallenge.DescribeExpected(question),
                            spelled,
                            token);
                    }
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

                if (correct)
                {
                    answeredCorrectly++;
                }
                else
                {
                    answeredWrongly++;
                }
                verification.QuestionEvidence = verification.QuestionEvidence.Select((item, i) => i == index
                    ? item with { Asked = true, Correct = correct } : item).ToArray();

                // One miss is forgiven once three or more questions are asked.
                //
                // Not leniency — arithmetic. Asking four and requiring four is a HARDER
                // challenge than asking two and requiring two, because every extra question
                // is another chance for a real person to blank on where they were on Tuesday.
                // A guesser still has to be right three times. The rule and its boundaries
                // live in ChallengeSelection, tested exhaustively, rather than here.
                var enoughAnswered = ChallengeSelection.Verdict(
                    questions.Count, answeredCorrectly, answeredWrongly);

                if (enoughAnswered is false)
                {
                    logger.LogInformation(
                        "Verification {Id}: refused after {Wrong} wrong of {Asked} asked "
                      + "({Required} needed).",
                        verification.VerificationId, answeredWrongly, questions.Count,
                        ChallengeSelection.Required(questions.Count));

                    await RefuseAsync(verification,
                        "The identity questions were not answered correctly.");
                    return;
                }

                if (!correct)
                {
                    // Wrong, but survivable. Move on rather than labouring it — telling a
                    // caller which answer they missed tells an impostor the same thing.
                    continue;
                }

                // A correct answer is not automatically a strong one. The location question
                // accepts the country because refusing a true answer refuses the genuine
                // user, which means "Malaysia" passes and is worth almost nothing. Ask for
                // the precision that leniency spent. They have already passed, so this can
                // only add.
                if (lastSpoken is not null)
                {
                    heard.Append(lastSpoken).Append(' ');
                }

                // Only after a question the probes are about.
                //
                // The registered question rides along at the end of the challenge and is not
                // drawn from the sign-in record, so probing after it would answer "Bluebell"
                // with "And whereabouts, roughly?" — a follow-up, by its wording, to a
                // question it has nothing to do with. Every telemetry question comes from the
                // same sign-in as the probes; the stored one does not.
                if (verification.QuestionEvidence.ElementAtOrDefault(index)?.Source == "SignIn")
                {
                    await ProbeAsync(
                        verification, monitored, live.FollowUps, facetsProbed,
                        heard.ToString(), token);
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
                                or VerificationResult.BlockedVoiceMismatch or VerificationResult.StepUpRequired
                    ? verdict.Reason
                    : $"Number match confirmed, and {questions.Count} identity questions "
                    + "answered from live sign-in activity. No coercion detected.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telemetry challenge failed for {Id}.", verification.VerificationId);
            if (!verification.IsComplete)
            {
                await RefuseAsync(verification,
                    "The identity questions could not be completed.");
            }
        }
    }

    /// <summary>
    /// Ask one follow-up, if the director wants one, and record what came back.
    /// </summary>
    /// <remarks>
    /// Never completes the verification, and never throws into the challenge. A probe that
    /// could end a call would be a third authority on this path, and there are already two
    /// more than enough. Everything it learns arrives as a <see cref="FollowUpOutcome"/> and
    /// is weighed later by <see cref="VerificationRisk"/>, which changes no access decision.
    ///
    /// <para>
    /// This runs only after a question was judged CORRECT. That is what makes it safe to ask
    /// at all: the caller has already cleared the factor, so a probe they fluff costs them
    /// nothing. People genuinely do not remember which browser they used at eight in the
    /// morning, and a factor that refuses correct users is a factor that gets switched off.
    /// </para>
    /// </remarks>
    private async Task ProbeAsync(
        VerificationSession verification,
        LiveCall monitored,
        IReadOnlyList<Agents.FollowUpProbe> candidates,
        HashSet<string> facetsProbed,
        string heard,
        CancellationToken token)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        // The Analyst's view of the call so far. Elevated buys a second probe and nothing
        // else — not a word of what is said, nor how it is said. A call whose warmth tracked
        // this number would be a live readout of the detector.
        var elevated = verification.PeakRiskDuringCall >= VerificationRisk.ElevatedThreshold;

        var probe = Agents.ConversationDirector.NextProbe(
            candidates, heard, facetsProbed, elevated);

        if (probe is null)
        {
            return;
        }

        // Claimed before asking. A probe that fails halfway must not be retried on the next
        // question — the caller would hear the same follow-up twice.
        facetsProbed.Add(probe.Facet);

        try
        {
            var askedAt = await SpeakAndSettleAsync(verification, monitored, probe.Question, token);
            var spoken = await ListenForAnswerAsync(monitored, askedAt, token);

            // Same echo defence as the questions: a speakerphone feeding the prompt back
            // would otherwise be judged as the answer.
            if (spoken is not null && IsOwnAcknowledgement(spoken))
            {
                spoken = null;
            }

            if (spoken is not null && IsEchoOf(probe.Question, spoken))
            {
                spoken = null;
            }

            var correct = spoken is not null && await judge.IsEquivalentAsync(
                probe.Question,
                string.Join(" OR ", probe.ExpectedFacts),
                spoken,
                token);

            logger.LogInformation(
                "Verification {Id}: probe [{Facet}] — {Outcome}. Asked: {Question}",
                verification.VerificationId, probe.Facet,
                spoken is null ? "nothing heard" : correct ? "confirmed" : "not confirmed",
                probe.Question);

            verification.FollowUps =
            [
                .. verification.FollowUps,
                new FollowUpOutcome(probe.Facet, probe.Question, spoken is not null, correct),
            ];
        }
        catch (OperationCanceledException)
        {
            // The call budget ran out mid-probe. The primary questions are already answered
            // and the verdict does not depend on this, so it is dropped rather than surfaced
            // as a failure the caller would be refused for.
            logger.LogInformation(
                "Verification {Id}: probe [{Facet}] was cut short by the call budget.",
                verification.VerificationId, probe.Facet);
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

                // The question is ALWAYS spoken, whoever owns the voice channel.
                //
                // This used to stay silent when an agent was on the call, on the belief that
                // "the agent already asks the question from its own instructions". The agent
                // is never told what the question is — so, left with nothing to say and a
                // persona telling it to conduct an identity check, it invented one. Live
                // callers were asked for an "employee ID" and a "full name", neither of
                // which this system asks for or can judge an answer to.
                //
                // The retry branch was worse: it spoke "That did not match. Ask the question
                // again." That is an instruction addressed to the agent, and it was read out
                // to the caller as though it were the question.
                //
                // Routed through SpeakAndSettleAsync, exactly like the telemetry path, so the
                // answer window opens after the question has actually been heard rather than
                // while it is still playing.
                var preamble = verification.KnowledgeAttempts == 1
                    ? "Thank you. One more check. Before you answer, please make sure "
                    + "nobody can overhear you, and that nobody is helping you. "
                    : "That did not match. Please answer again. ";

                askedAt = await SpeakAndSettleAsync(
                    verification, monitored, preamble + question.Question, token);
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

                    // Same courtesy on the registered question: we asked them to spell it.
                    if (!matched && SpelledOutAnswer.Collapse(spoken) is { } spelled)
                    {
                        logger.LogInformation(
                            "Verification {Id}: [{Spoken}] read as the spelling of [{Spelled}].",
                            verification.VerificationId, spoken, spelled);

                        matched = KnowledgeChallenge.Verify(question, spelled)
                            || await judge.IsEquivalentAsync(
                                question.Question, question.PlainAnswer, spelled, token);
                    }
                }

                if (matched)
                {
                    // Deliberately not logged, at any level. The whole point of hashing the
                    // answer is that it exists nowhere readable, and a debug log is readable.
                    logger.LogInformation(
                        "Verification {Id}: knowledge answer accepted on attempt {Attempt}.",
                        verification.VerificationId, verification.KnowledgeAttempts);

                    var assessment = ResolveAssessment(verification);
                    verification.QuestionEvidence = [new QuestionEvidence("Registered", "stored", Asked: true, Correct: true)];
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
                await RefuseAsync(verification,
                    "The security question was not answered correctly.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Knowledge challenge failed for {Id}.", verification.VerificationId);
            if (!verification.IsComplete)
            {
                await RefuseAsync(verification,
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

        // Where the transcript stood before we said anything.
        var before = monitored.Session.ElapsedMs(DateTimeOffset.UtcNow);

        // Who is about to speak decides how we know they have finished.
        var agentOwnsTheVoice = voiceAgents.For(verification.VerificationId) is { IsHealthy: true };

        // Armed BEFORE speaking: PlayCompleted can arrive before the await would start.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _playbackDone[verification.VerificationId] = finished;

        await SpeakAsync(verification, text, token);

        if (agentOwnsTheVoice)
        {
            // The agent streams its audio over its own socket. ACS is playing nothing, so
            // PlayCompleted NEVER arrives — and waiting for it burned the full 20 second
            // timeout before every single question.
            //
            // Measured on a live call, and it did far more than feel slow: the caller
            // answered during those 20 seconds of dead air, askedAt was then stamped after
            // they had already finished, and their answer fell outside the window. The
            // system heard nothing, refused, asked again, and did it three times — rejecting
            // an answer that had been given correctly each time.
            //
            // WaitUntilAskedAsync is the right instrument and was already written for this;
            // it had simply never been called. It watches the transcript until the agent
            // stops producing utterances.
            await WaitUntilAskedAsync(monitored, before, token);
        }
        else
        {
            await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(20), token));
        }

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
    /// <summary>
    /// Is this the system's own acknowledgement coming back down the line?
    /// </summary>
    /// <remarks>
    /// Measured on a live call: "Expected [Aiman], heard [Thank you.]". The phrase this class
    /// says after hearing an answer echoed off the handset, landed in the next listening
    /// window, and was judged as the caller's answer — costing them one of three attempts for
    /// words the system had said itself.
    ///
    /// IsEchoOf cannot catch this. It compares against the QUESTION, and an acknowledgement
    /// is not the question; there was nothing in common to measure.
    ///
    /// Deliberately an EXACT match on the whole utterance, after trimming punctuation and
    /// case. The asymmetry is the usual one — letting an echo through costs one attempt of
    /// three, discarding a real answer costs all of them — so "thanks, it was Kuala Lumpur"
    /// must survive, and only an utterance that is nothing but the acknowledgement is dropped.
    /// </remarks>
    private static bool IsOwnAcknowledgement(string spoken)
    {
        var said = Normalise(spoken);
        return said.Length > 0 && Acknowledgements.Any(a => Normalise(a) == said);

        static string Normalise(string text) =>
            new string(text.Where(c => !char.IsPunctuation(c)).ToArray())
                .Trim()
                .ToLowerInvariant();
    }

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

            // Nothing at all after a few seconds: stop waiting.
            //
            // The break above needs at least one utterance to have appeared, so a call where
            // the agent produced no transcript sat here for the full fifteen seconds — before
            // EVERY question, and again before every retry. Three questions and a couple of
            // retries is a minute of silence the caller is asked to sit through, and they
            // reasonably conclude the call has died.
            //
            // Five seconds is longer than the agent takes to start speaking when it is going
            // to speak at all, so this only fires when there was nothing coming.
            if (count == 0 && DateTimeOffset.UtcNow - lastSeen > TimeSpan.FromSeconds(5))
            {
                break;
            }
        }

        // Answers are timed from here, so the agent's own words are behind us.
        return monitored.Session.ElapsedMs(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Wait for a voice agent that can actually speak, or give up.
    /// </summary>
    /// <remarks>
    /// The agent registers when the ACS media socket opens, which happens independently of
    /// the CallConnected callback that drives the prompt. Polling a short window is what lets
    /// one wait for the other without either having to know the other's timing.
    ///
    /// Returns null rather than throwing: "no agent" is a normal outcome with a defined
    /// answer — say it with PlayToAll — and turning it into an exception would take the call
    /// down over something entirely recoverable.
    /// </remarks>
    private async Task<Agents.VoiceAgent?> WaitForHealthyAgentAsync(
        string verificationId, TimeSpan within, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + within;

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (voiceAgents.For(verificationId) is { IsHealthy: true } agent)
            {
                return agent;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return null;
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
        // IsHealthy, not merely "an agent exists".
        //
        // A realtime session that has errored keeps its socket open and stays registered, so
        // "an agent exists" was true for an agent that could not speak — and every line this
        // class produced went into it and was never heard. Falling through to PlayToAll is
        // the whole point of having one funnel.
        if (voiceAgents.For(verification.VerificationId) is { IsHealthy: true } agent)
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
        if (result == VerificationResult.Passed && verification.RequiresStepUp)
        {
            result = VerificationResult.StepUpRequired;
            reason = "Call checks completed. Confirm a fresh Microsoft MFA event before access is granted.";
        }

        // Carry the final peak across before the monitor session is torn down.
        ResolveAssessment(verification);

        // And how the agent ended up speaking. Read from the agent rather than tracked
        // alongside it, so the recorded value cannot disagree with what the caller heard.
        if (voiceAgents.For(verification.VerificationId) is { } speaking)
        {
            verification.Register = speaking.Register.ToString();
        }

        // What this attempt looked like, as a number, recorded and never acted on.
        var risk = VerificationRisk.Score(
            verification.PeakRiskDuringCall,
            verification.VoiceOutcome,
            verification.Attempts,
            verification.EndpointKind,
            verification.KnowledgeAttempts,
            verification.FollowUps);

        verification.RiskScore = risk.Score;
        verification.RiskBand = risk.Band.ToString();
        verification.RiskContributors = risk.Contributors;

        // And what it actually established, which is a different question from how risky it
        // looked. Computed here rather than in the adjudicator because the adjudicator must
        // not be able to see it: assurance reports, it does not decide.
        var assurance = EvidenceAssurance.Evaluate(
            // The RESULT PARAMETER, not verification.GrantsAccess.
            //
            // GrantsAccess reads verification.Result, and Result is not assigned until
            // registry.TryComplete a few lines below — so reading it here saw Pending and
            // reported None for every call, including ones that passed. Caught by a
            // behavioural check against the deployed service: a simulated verification came
            // back Passed with assurance None.
            //
            // The parameter is the verdict this method was CALLED with, so it is correct
            // regardless of where the assignment happens to sit.
            result is VerificationResult.Passed or VerificationResult.StepUpRequired,
            verification.QuestionEvidence,
            verification.FollowUps,
            verification.VoiceOutcome);

        verification.AssuranceLevel = assurance.Level.ToString();
        verification.AssuranceBasis = assurance.Basis;
        verification.AssuranceGaps = assurance.Gaps;

        logger.LogInformation(
            "Verification {Id}: assurance {Level}{Gaps}",
            verification.VerificationId, assurance.Level,
            assurance.Gaps.Count == 0 ? "." : $" — missing: {string.Join("; ", assurance.Gaps)}");

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
        //
        // A refusal says WHY, in the words already recorded against the verification.
        //
        // This line used to assert "That number was not correct" for every Failed outcome,
        // whatever had actually gone wrong. On vrf-9c00cd355de1 the caller keyed the number
        // correctly and was refused on the questions; they were told, aloud, that their
        // number was wrong, and reported afterwards that the call had given them no feedback
        // at all — a sentence that does not match what just happened does not register as
        // feedback. Every Failed path already passes a truthful one-line reason to this
        // method, and throwing it away here was the only reason the caller could not be told.
        var closing = result switch
        {
            VerificationResult.Passed => "Thank you. Your identity is verified.",
            VerificationResult.StepUpRequired => "Your call checks are complete. Please finish the Microsoft verification shown on your screen.",
            VerificationResult.BlockedCoercion =>
                "This verification has been refused because it appears someone is guiding you through it. " +
                "If you are under pressure from a caller, hang up and contact your security team.",

            // The reason, then what to do about it. A refused user whose next step is
            // unstated calls the help desk and says "it just hung up on me", which is where
            // the social engineering this product exists to stop begins.
            VerificationResult.Failed =>
                $"{reason} If you think that is wrong, contact your service desk and ask them "
              + "to send a new verification.",

            // Written out rather than left to fall through with the reason appended. That
            // reason carries the similarity score, and reading a biometric threshold aloud
            // to whoever failed it tells an impostor how close they got.
            VerificationResult.BlockedVoiceMismatch =>
                "This verification has been refused because the voice on this call could not "
              + "be confirmed as yours. Contact your service desk to verify another way.",
            _ => "This verification could not be completed. Contact your service desk if you "
               + "were expecting it to succeed.",
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
        // Capture before committing: a durable receipt must describe the actual media.
        if (verification.MonitorSessionId is { } monitorId && callRegistry.Get(monitorId)?.Session is { } mediaSnapshot)
        {
            verification.MediaStreamConnected = mediaSnapshot.MediaStreamConnectedAt is not null;
            verification.AudioFramesReceived = mediaSnapshot.AudioFramesReceived;
            verification.DtmfReceived = mediaSnapshot.DtmfReceived;
        }
        try
        {
            if (verification.RpSessionId is not null)
            {
                var committed = await ledger.CompleteAsync(verification, CancellationToken.None);
                if (!committed && verification.SubjectTenantId is not null && verification.SubjectObjectId is not null)
                {
                    var durable = await ledger.GetAsync(new Auth.Owner(verification.SubjectTenantId, verification.SubjectObjectId, verification.SubjectUpn), verification.VerificationId);
                    if (durable is not null) { verification.Result = Enum.Parse<VerificationResult>(durable.Result); verification.Reason = durable.Reason; }
                }
            }
            else await sink.WriteVerificationAsync(verification, CancellationToken.None);
        }
        catch (Exception persistenceError)
        {
            logger.LogError(persistenceError, "Could not persist verification {Id}; no server grant can be issued.", verification.VerificationId);
            verification.Result = VerificationResult.CallFailed;
            verification.Reason = "The result could not be saved. Please start a new verification.";
        }
        if (verification.Result != result) closing = verification.Reason;
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
            // Copy the evidence off the live session BEFORE disposing it.
            //
            // The order here is the whole fix. The status endpoint reads these counters from
            // the live session, and this removal is the last thing a verification does — so
            // every completed call reported no audio, no DTMF and no stream, however much of
            // each it had actually carried. A call that worked and a call that never
            // connected produced identical records, which is the one distinction anybody
            // triaging a failure needs.
            if (callRegistry.Get(verification.MonitorSessionId)?.Session is { } media)
            {
                verification.MediaStreamConnected = media.MediaStreamConnectedAt is not null;
                verification.AudioFramesReceived = media.AudioFramesReceived;
                verification.DtmfReceived = media.DtmfReceived;
            }

            await callRegistry.RemoveAsync(verification.MonitorSessionId);
        }

        logger.LogInformation(
            "Verification {Id}: media carried {Frames} audio frames and {Dtmf} keypad tones.",
            verification.VerificationId, verification.AudioFramesReceived, verification.DtmfReceived);

        logger.LogInformation("Verification {Id} for {Upn}: {Result} — {Reason}",
            verification.VerificationId, verification.SubjectUpn, result, reason);
    }
}
