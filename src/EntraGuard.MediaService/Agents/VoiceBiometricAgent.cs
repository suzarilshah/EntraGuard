using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Sessions;
using EntraGuard.Shared.Voice;

namespace EntraGuard.MediaService.Agents;

/// <summary>
/// Collects the protected user's speech during a call, so it can be scored against their
/// enrolled voice.
///
/// Only their channel. ACS streams unmixed audio, one participant per frame, and that is
/// the property the whole feature rests on: a coercer standing beside the victim lands on a
/// different channel, so their voice can never be mistaken for the enrolled speaker's, and
/// scoring a mixed stream would let the loudest person in the room define the "match".
///
/// Nothing is written anywhere. Audio lives in memory for the length of the call, is sent
/// to the scorer as bytes, and the buffer is dropped as soon as an embedding exists. There
/// is no path from here to disk, to blob storage, or to a log.
/// </summary>
public sealed class VoiceBiometricAgent(CallSession session, ILogger logger)
{
    /// <summary>
    /// Cap on retained audio: 60 seconds at 16 kHz PCM16.
    ///
    /// Bounded because a call can run long and this is per-call memory. Speaker embeddings
    /// stop improving after ten or fifteen seconds of speech, so keeping more would cost
    /// memory for no accuracy.
    /// </summary>
    private const int MaxBytes = 60 * AudioResampler.ModelSampleRate * 2;

    private readonly MemoryStream _buffer = new();
    private readonly Lock _gate = new();

    /// <summary>16 kHz PCM16 of the protected user's speech, oldest first.</summary>
    public byte[] Snapshot()
    {
        lock (_gate)
        {
            return _buffer.ToArray();
        }
    }

    public double Seconds
    {
        get
        {
            lock (_gate)
            {
                return AudioResampler.Seconds((int)_buffer.Length);
            }
        }
    }

    /// <summary>
    /// Offer one frame of call audio. Frames from anyone but the protected user are ignored.
    /// </summary>
    public void Offer(string participantRawId, ReadOnlyMemory<byte> pcm, int sampleRate)
    {
        // The channel is identified by the same participant→role map the transcript uses,
        // so "whose voice is this" has exactly one answer in the system rather than two
        // that can disagree.
        if (session.RoleFor(participantRawId) != SpeakerRole.ProtectedUser)
        {
            return;
        }

        // Only the format ACS actually sends. A different rate would need a different
        // conversion, and silently resampling it wrongly produces audio at the wrong speed
        // — which scores a genuine user as an impostor and looks like model inaccuracy.
        if (sampleRate != AudioResampler.AcsSampleRate)
        {
            return;
        }

        var converted = AudioResampler.Downsample24To16(pcm.Span);
        if (converted.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_buffer.Length >= MaxBytes)
            {
                return;
            }

            _buffer.Write(converted, 0, converted.Length);
        }
    }

    /// <summary>
    /// Score the collected speech against an enrolled template.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="VoiceDecision.NotAssessed"/> for every failure — no template, too
    /// little speech, mostly silence, scorer unreachable. None of those are the user's
    /// fault and none of them are evidence of anything, so none of them may cost them
    /// access.
    /// </remarks>
    public async Task<VoiceDecision> ScoreAsync(
        IReadOnlyList<double>? template,
        VoiceprintClient client,
        bool enforce,
        double accept,
        double reject,
        CancellationToken cancellationToken)
    {
        if (template is null || template.Count == 0 || !client.IsConfigured)
        {
            return VoiceDecision.NotAssessed;
        }

        var audio = Snapshot();
        var seconds = AudioResampler.Seconds(audio.Length);

        if (seconds < VoiceThresholds.MinimumSeconds)
        {
            return VoiceThresholds.Evaluate(null, seconds, enforce, accept, reject) with
            {
                Reason = $"Only {seconds:F1}s of the user's speech was captured — "
                       + "too little to compare a voice on.",
            };
        }

        // Mostly line noise. An embedding of a quiet room is a confident-looking number
        // about nothing, and comparing it to a person would be worse than not trying.
        var voiced = AudioResampler.VoicedRatio(audio);
        if (voiced < 0.05)
        {
            logger.LogInformation(
                "Voice scoring skipped for {SessionId}: audio is {Percent:P0} voiced.",
                session.SessionId, voiced);
            return VoiceDecision.NotAssessed;
        }

        var embedding = await client.EmbedAsync(audio, cancellationToken);
        if (embedding is null)
        {
            return VoiceDecision.NotAssessed;
        }

        var score = VoiceprintClient.Similarity(embedding, template);

        logger.LogInformation(
            "Voice score for {SessionId}: {Score} over {Seconds:F1}s ({Voiced:P0} voiced).",
            session.SessionId, score?.ToString("F4") ?? "none", seconds, voiced);

        return VoiceThresholds.Evaluate(score, seconds, enforce, accept, reject);
    }

    /// <summary>Drop the audio. Called when the call ends, whatever the outcome.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _buffer.SetLength(0);
            _buffer.Capacity = 0;
        }
    }
}
