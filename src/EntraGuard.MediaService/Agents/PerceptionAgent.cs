using Azure.Core;
using EntraGuard.MediaService.Configuration;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Sessions;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Agents;

/// <summary>
/// Turns raw PCM from the call into attributed, timestamped text.
///
/// One recogniser per participant channel. ACS unmixed streaming already separates
/// speakers onto their own channels, so attribution is a property of the transport rather
/// than a diarisation guess — which is what makes "the CALLER said this" trustworthy
/// enough to act on.
/// </summary>
public sealed class PerceptionAgent : IAsyncDisposable
{
    private readonly CallSession _session;
    private readonly EntraGuardOptions _options;
    private readonly ILogger _logger;
    private readonly SpeechConfig _speechConfig;

    private readonly Dictionary<string, ChannelRecognizer> _recognizers = [];
    private readonly Lock _recognizerLock = new();
    private bool _disposed;

    /// <summary>Raised when a phrase is recognised. Interim results have IsFinal false.</summary>
    public event Action<Utterance>? UtteranceRecognized;

    private PerceptionAgent(
        CallSession session,
        EntraGuardOptions options,
        SpeechConfig speechConfig,
        ILogger logger)
    {
        _session = session;
        _options = options;
        _speechConfig = speechConfig;
        _logger = logger;
    }

    /// <summary>
    /// Build a perception agent authenticated with the managed identity.
    ///
    /// The Speech SDK takes an Entra token as an "aad#{resourceId}#{token}" authorization
    /// token rather than a TokenCredential, so the token is fetched explicitly here. Local
    /// auth is disabled on the Speech account, so there is no key path to fall back to.
    /// </summary>
    public static async Task<PerceptionAgent> CreateAsync(
        CallSession session,
        EntraGuardOptions options,
        TokenCredential credential,
        string speechResourceId,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
            cancellationToken);

        var speechConfig = SpeechConfig.FromAuthorizationToken(
            $"aad#{speechResourceId}#{token.Token}",
            options.SpeechRegion);

        speechConfig.SpeechRecognitionLanguage = options.SpeechLanguage;
        // Punctuation and casing materially change how the Analyst reads a transcript —
        // "the code is 419382" versus "the code is four one nine".
        speechConfig.OutputFormat = OutputFormat.Detailed;

        return new PerceptionAgent(session, options, speechConfig, logger);
    }

    /// <summary>
    /// Push one audio frame into the recogniser for its channel, creating it on first sight.
    /// </summary>
    public void PushAudio(string participantRawId, ReadOnlyMemory<byte> pcm, int sampleRate)
    {
        if (_disposed)
        {
            return;
        }

        var recognizer = GetOrCreateRecognizer(participantRawId, sampleRate);
        recognizer.Stream.Write(pcm.ToArray());
    }

    private ChannelRecognizer GetOrCreateRecognizer(string participantRawId, int sampleRate)
    {
        lock (_recognizerLock)
        {
            if (_recognizers.TryGetValue(participantRawId, out var existing))
            {
                return existing;
            }

            // ACS sends 16-bit PCM mono at either 16 kHz or 24 kHz; the format comes from
            // the AudioMetadata frame rather than being assumed.
            var format = AudioStreamFormat.GetWaveFormatPCM((uint)sampleRate, 16, 1);
            var pushStream = AudioInputStream.CreatePushStream(format);
            var audioConfig = AudioConfig.FromStreamInput(pushStream);
            var recognizer = new SpeechRecognizer(_speechConfig, audioConfig);

            // Bias recognition toward the vocabulary that decides the verdict. A missed
            // "Temporary Access Pass" is a missed detection nothing downstream recovers from.
            var phraseList = PhraseListGrammar.FromRecognizer(recognizer);
            foreach (var phrase in AnalystPrompt.RecognitionPhrases)
            {
                phraseList.AddPhrase(phrase);
            }

            var role = _session.RoleFor(participantRawId);

            recognizer.Recognizing += (_, e) => Emit(e.Result, role, isFinal: false);
            recognizer.Recognized += (_, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech)
                {
                    Emit(e.Result, role, isFinal: true);
                }
            };
            recognizer.Canceled += (_, e) => _logger.LogWarning(
                "Speech recognition cancelled for {Participant} on {SessionId}: {Reason} {Detail}",
                participantRawId, _session.SessionId, e.Reason, e.ErrorDetails);

            var channel = new ChannelRecognizer(recognizer, pushStream);
            _recognizers[participantRawId] = channel;

            _ = recognizer.StartContinuousRecognitionAsync();
            _logger.LogInformation(
                "Started recognition for participant {Participant} (role {Role}) at {SampleRate}Hz on {SessionId}",
                participantRawId, role, sampleRate, _session.SessionId);

            return channel;
        }
    }

    private void Emit(SpeechRecognitionResult result, SpeakerRole role, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var utterance = new Utterance(role, result.Text, _session.ElapsedMs(now), now, isFinal);

        if (isFinal)
        {
            _session.AddUtterance(utterance);
        }

        UtteranceRecognized?.Invoke(utterance);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<ChannelRecognizer> recognizers;
        lock (_recognizerLock)
        {
            recognizers = [.. _recognizers.Values];
            _recognizers.Clear();
        }

        foreach (var channel in recognizers)
        {
            try
            {
                await channel.Recognizer.StopContinuousRecognitionAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop recogniser cleanly on {SessionId}.", _session.SessionId);
            }
            finally
            {
                channel.Stream.Close();
                channel.Recognizer.Dispose();
                channel.Stream.Dispose();
            }
        }
    }

    private sealed record ChannelRecognizer(SpeechRecognizer Recognizer, PushAudioInputStream Stream);
}
