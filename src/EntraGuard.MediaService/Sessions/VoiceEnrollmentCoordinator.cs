using System.Collections.Concurrent;
using Azure.Communication.CallAutomation;
using EntraGuard.MediaService.Agents;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Endpoints;
using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Voice;

namespace EntraGuard.MediaService.Sessions;

/// <summary>One enrolment in progress.</summary>
public sealed class EnrollmentSession
{
    public required string EnrollmentId { get; init; }
    public required string ObjectId { get; init; }
    public required string TenantId { get; init; }
    public required string Upn { get; init; }
    public required IReadOnlyList<string> Phrases { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Whether this replaces an existing profile, so the audit row says which.</summary>
    public bool Reenroll { get; init; }

    public string? CallConnectionId { get; set; }
    public string? MonitorSessionId { get; set; }

    /// <summary>How many phrases have been spoken and captured.</summary>
    public int Completed { get; set; }

    /// <summary>Retries spent on the phrase currently being recorded.</summary>
    public int Retries { get; set; }

    /// <summary>Embeddings, one per successfully captured phrase.</summary>
    public List<double[]> Embeddings { get; } = [];

    public string State { get; set; } = "Calling";
    public string? Failure { get; set; }
    public double SelfConsistency { get; set; }
    public bool IsComplete => State is "Enrolled" or "Failed";
}

/// <summary>
/// Runs the enrolment call: speak a phrase, capture the reply, repeat, then build a template.
///
/// The audio never leaves this process except as an embedding, and the buffer is cleared
/// after each phrase. There is no code path from here to disk, to blob storage, or to a log
/// line — a recording of somebody reading a sentence is far more dangerous than the vector
/// derived from it, because the recording can be replayed at anything.
/// </summary>
public sealed class VoiceEnrollmentCoordinator(
    LiveCallRegistry callRegistry,
    CallAutomationClient callAutomation,
    VoiceprintClient voiceprint,
    VoiceprintStore store,
    Sinks.LogsIngestionSink audit,
    ILogger<VoiceEnrollmentCoordinator> logger)
{
    /// <summary>
    /// Utterances must agree with each other at least this much.
    ///
    /// Three recordings of one person, seconds apart, on one device, should be far more
    /// similar to each other than any of them is to a stranger. If they are not, something
    /// is wrong — a second speaker read one of the phrases, a phone was passed around, or
    /// the line was too noisy — and a template averaged from disagreeing utterances matches
    /// nobody, including its owner.
    /// </summary>
    private const double MinimumSelfConsistency = 0.60;

    /// <summary>Retries allowed per phrase before enrolment is abandoned.</summary>
    private const int MaxRetriesPerPhrase = 2;

    private readonly ConcurrentDictionary<string, EnrollmentSession> _sessions = new();

    public EnrollmentSession Create(
        CallerIdentity caller, IReadOnlyList<string> phrases, bool reenroll = false)
    {
        var session = new EnrollmentSession
        {
            EnrollmentId = $"enr-{Guid.NewGuid():N}"[..16],
            ObjectId = caller.ObjectId,
            TenantId = caller.TenantId,
            Upn = caller.Upn,
            Phrases = phrases,
            StartedAt = DateTimeOffset.UtcNow,
            Reenroll = reenroll,
        };

        _sessions[session.EnrollmentId] = session;
        return session;
    }

    public EnrollmentSession? Get(string enrollmentId) =>
        _sessions.TryGetValue(enrollmentId, out var s) ? s : null;

    public void Fail(EnrollmentSession session, string reason)
    {
        session.State = "Failed";
        session.Failure = reason;
        Cleanup(session);

        // Every failure path funnels through here, including callers outside this class, so
        // this is the one place the audit row cannot be forgotten. Repeated failures against
        // one account are what somebody probing another person's voice profile looks like,
        // and that pattern is invisible in per-container log lines.
        //
        // Not awaited: a telemetry write must never delay tearing down a call, and the sink
        // already detaches from the caller's cancellation and swallows its own failures.
        _ = audit.WriteBiometricEventAsync(
            "EnrolmentFailed", session.Upn, session.ObjectId, session.TenantId,
            consentVersion: VoiceEnrollmentEndpoint.ConsentVersion,
            consentAt: session.StartedAt,
            phraseCount: session.Embeddings.Count,
            selfConsistency: session.SelfConsistency,
            reason: reason,
            usedMfa: true);
    }

    /// <summary>The call was answered — ask for the first phrase.</summary>
    public async Task OnConnectedAsync(string enrollmentId, CancellationToken cancellationToken)
    {
        var session = Get(enrollmentId);
        if (session is null || session.IsComplete)
        {
            return;
        }

        session.State = "Speaking";
        await SpeakPhraseAsync(session, first: true, cancellationToken);
    }

    /// <summary>
    /// A prompt finished playing — listen, capture, then move on.
    /// </summary>
    /// <remarks>
    /// Driven by PlayCompleted rather than a timer, so the recording window starts when the
    /// user has actually heard the phrase. Timing from when playback was requested charges
    /// them for the prompt's own duration, which is how an earlier version of the
    /// verification challenge ended up refusing people who answered promptly.
    /// </remarks>
    public async Task OnPhraseSpokenAsync(string enrollmentId, CancellationToken cancellationToken)
    {
        var session = Get(enrollmentId);
        if (session is null || session.IsComplete || session.MonitorSessionId is null)
        {
            return;
        }

        var biometrics = callRegistry.Get(session.MonitorSessionId)?.Biometrics;
        if (biometrics is null)
        {
            Fail(session, "The call had no audio channel to record from.");
            await HangUpAsync(session);
            return;
        }

        session.State = "Listening";

        // Discard everything captured BEFORE this point, then listen.
        //
        // This is the difference between enrolling the user and enrolling the user mixed
        // with EntraGuard. The prompt echoes back on the caller's own channel — the same
        // effect that had the verification challenge answering its own questions — and the
        // buffer was previously only cleared AFTER the snapshot. Phrase one carries a long
        // preamble, so a substantial part of the very first recording would have been
        // synthesised speech, baked permanently into the template and depressing every
        // score the real person ever gets.
        biometrics.Clear();

        // Fixed window after the prompt. Long enough to read one sentence unhurried, short
        // enough that a confused user is not left in silence.
        await Task.Delay(TimeSpan.FromSeconds(7), cancellationToken);

        var audio = biometrics.Snapshot();
        var seconds = AudioResampler.Seconds(audio.Length);
        var voiced = AudioResampler.VoicedRatio(audio);

        // Cleared before anything else can go wrong, so the next phrase records only itself
        // and no audio outlives the moment it was needed.
        biometrics.Clear();

        if (seconds < 1.5 || voiced < 0.05)
        {
            session.Retries++;

            // Bounded. The retry re-enters this method through its own PlayCompleted, so
            // without a counter a caller who says nothing loops forever and the call never
            // ends — they would sit listening to the same request indefinitely.
            if (session.Retries > MaxRetriesPerPhrase)
            {
                logger.LogInformation(
                    "Enrolment {Id}: phrase {N} gave no usable audio after {Retries} attempts.",
                    enrollmentId, session.Completed + 1, session.Retries);

                await Speak(session,
                    "I could not hear you clearly enough to set this up. "
                    + "Please try again somewhere quieter.", cancellationToken);

                Fail(session,
                    "No usable audio was recorded. This is usually a muted microphone, "
                    + "a very noisy line, or the phrase not being read aloud.");

                await Task.Delay(TimeSpan.FromSeconds(4), CancellationToken.None);
                await HangUpAsync(session);
                return;
            }

            logger.LogInformation(
                "Enrolment {Id}: phrase {N} gave {Seconds:F1}s at {Voiced:P0} voiced — retry {Retry}.",
                enrollmentId, session.Completed + 1, seconds, voiced, session.Retries);

            await Speak(session,
                "I did not hear enough. Please repeat the phrase clearly.", cancellationToken);
            return;
        }

        var embedding = await voiceprint.EmbedAsync(audio, cancellationToken);
        Array.Clear(audio);

        if (embedding is null)
        {
            Fail(session, "The voice service could not process the recording.");
            await HangUpAsync(session);
            return;
        }

        session.Embeddings.Add(embedding);
        session.Completed++;
        session.Retries = 0;

        if (session.Completed < session.Phrases.Count)
        {
            await SpeakPhraseAsync(session, first: false, cancellationToken);
            return;
        }

        await FinishAsync(session, cancellationToken);
    }

    public async Task OnDisconnectedAsync(string enrollmentId)
    {
        var session = Get(enrollmentId);
        if (session is null || session.IsComplete)
        {
            return;
        }

        Fail(session, "The call ended before enrolment finished.");
        await Task.CompletedTask;
    }

    /// <summary>Check the utterances agree, build the template, store it.</summary>
    private async Task FinishAsync(EnrollmentSession session, CancellationToken cancellationToken)
    {
        session.State = "Checking";

        // Lowest pairwise similarity, not the average: one odd utterance among three should
        // fail the check, and an average would hide it behind the two that agree.
        var worst = 1.0;
        for (var i = 0; i < session.Embeddings.Count; i++)
        {
            for (var j = i + 1; j < session.Embeddings.Count; j++)
            {
                var score = VoiceprintClient.Similarity(session.Embeddings[i], session.Embeddings[j]);
                if (score is not null && score < worst)
                {
                    worst = score.Value;
                }
            }
        }

        session.SelfConsistency = worst;

        if (worst < MinimumSelfConsistency)
        {
            logger.LogWarning(
                "Enrolment {Id} rejected: utterances agree only {Score:F3}.",
                session.EnrollmentId, worst);

            await Speak(session,
                "Enrolment could not be completed. The recordings did not match each other "
                + "closely enough. Please try again somewhere quieter.", cancellationToken);

            Fail(session,
                $"The recordings did not match each other closely enough (score {worst:F2}). "
                + "This usually means background noise, or more than one person speaking.");

            await HangUpAsync(session);
            return;
        }

        var template = VoiceprintClient.Average(session.Embeddings);

        var stored = await store.SaveAsync(
            session.TenantId, session.ObjectId,
            new Voiceprint(
                template,
                VoiceEnrollmentEndpoint.ConsentVersion,
                session.StartedAt,
                DateTimeOffset.UtcNow,
                worst),
            cancellationToken);

        if (!stored)
        {
            Fail(session, "The voice profile could not be stored.");
            await HangUpAsync(session);
            return;
        }

        session.State = "Enrolled";
        logger.LogInformation(
            "Enrolment {Id} complete for {Upn}: {Count} phrases, consistency {Score:F3}.",
            session.EnrollmentId, session.Upn, session.Embeddings.Count, worst);

        // The consent record, not a log line. See LogsIngestionSink.WriteBiometricEventAsync.
        await audit.WriteBiometricEventAsync(
            session.Reenroll ? "ReEnrolled" : "Enrolled",
            session.Upn, session.ObjectId, session.TenantId,
            consentVersion: VoiceEnrollmentEndpoint.ConsentVersion,
            consentAt: session.StartedAt,
            phraseCount: session.Embeddings.Count,
            selfConsistency: worst,
            usedMfa: true);

        await Speak(session,
            "Your voice profile has been created. Thank you.", cancellationToken);

        Cleanup(session);
        await Task.Delay(TimeSpan.FromSeconds(4), CancellationToken.None);
        await HangUpAsync(session);
    }

    private async Task SpeakPhraseAsync(
        EnrollmentSession session, bool first, CancellationToken cancellationToken)
    {
        var phrase = session.Phrases[session.Completed];

        var preamble = first
            ? "This is EntraGuard setting up your voice profile. "
            + "After the tone, please repeat this phrase clearly. "
            : "Now please repeat this phrase. ";

        session.State = "Speaking";
        await Speak(session, preamble + phrase, cancellationToken);
    }

    private async Task Speak(EnrollmentSession session, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(session.CallConnectionId))
        {
            return;
        }

        try
        {
            await callAutomation.GetCallConnection(session.CallConnectionId).GetCallMedia()
                .PlayToAllAsync(new PlayToAllOptions(
                    new TextSource(text) { VoiceName = "en-US-AvaMultilingualNeural" }),
                    cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not speak during enrolment {Id}.", session.EnrollmentId);
        }
    }

    private async Task HangUpAsync(EnrollmentSession session)
    {
        try
        {
            if (!string.IsNullOrEmpty(session.CallConnectionId))
            {
                await callAutomation.GetCallConnection(session.CallConnectionId)
                    .HangUpAsync(forEveryone: true, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not hang up enrolment {Id}.", session.EnrollmentId);
        }
    }

    /// <summary>
    /// Drop everything derived from the user's speech.
    /// </summary>
    /// <remarks>
    /// Including the embeddings. Once a template is stored there is no reason to keep the
    /// individual utterance vectors, and the shortest life for biometric data is the right
    /// one.
    /// </remarks>
    private void Cleanup(EnrollmentSession session)
    {
        session.Embeddings.Clear();

        if (session.MonitorSessionId is not null)
        {
            callRegistry.Get(session.MonitorSessionId)?.Biometrics?.Clear();
            _ = callRegistry.RemoveAsync(session.MonitorSessionId);
        }
    }

    /// <summary>What the browser is allowed to see. Never an embedding.</summary>
    public object Describe(EnrollmentSession session) => new
    {
        enrollmentId = session.EnrollmentId,
        state = session.State,
        phrasesTotal = session.Phrases.Count,
        phrasesCaptured = session.Completed,
        // The phrase currently being asked for, so the page can show it alongside the audio.
        currentPhrase = session.Completed < session.Phrases.Count
            ? session.Phrases[session.Completed]
            : null,
        isComplete = session.IsComplete,
        enrolled = session.State == "Enrolled",
        failure = session.Failure,
        quality = session.SelfConsistency,
    };
}
