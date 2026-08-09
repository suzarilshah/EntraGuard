using System.Diagnostics;
using System.Text.Json;
using EntraGuard.MediaService.Configuration;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Sessions;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Agents;

/// <summary>
/// Scores a rolling transcript window for social-engineering risk.
///
/// The perception layer produces text; this turns text into a judgement. It deliberately
/// does NOT decide what to do about that judgement — <see cref="Shared.Policy.PolicyGate"/>
/// owns that, and keeping the two apart is what stops a persuasive model response from
/// becoming an account lockout.
/// </summary>
public sealed class AnalystAgent(
    AnalystClient client,
    IOptions<EntraGuardOptions> options,
    ILogger<AnalystAgent> logger)
{
    private readonly EntraGuardOptions _options = options.Value;

    /// <summary>
    /// Score the recent conversation.
    /// </summary>
    /// <returns>
    /// A verdict, or null when there is not enough conversation to judge. Returning null
    /// rather than a zero-risk verdict matters: "nothing said yet" and "nothing suspicious
    /// said" are different, and only the second should reset a risk gauge.
    /// </returns>
    public async Task<RiskAssessment?> AssessAsync(
        CallSession session,
        CancellationToken cancellationToken = default)
    {
        var elapsed = session.ElapsedMs(DateTimeOffset.UtcNow);
        var transcript = session.TranscriptWindow(_options.AnalysisWindow, elapsed);

        // Scoring two words costs a model call and yields a verdict with no basis.
        if (transcript.Length < 40)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await client.CompleteAsync(
                AnalystPrompt.SystemPrompt,
                $"""
                Call elapsed: {TimeSpan.FromMilliseconds(elapsed):mm\:ss}
                Protected user: {session.SubjectUpn ?? "unidentified"}

                Transcript window (most recent {_options.AnalysisWindow.TotalSeconds:F0} seconds):
                ---
                {transcript}
                ---

                Assess this call.
                """,
                AnalystPrompt.ResponseSchema,
                cancellationToken);

            stopwatch.Stop();

            if (!result.Success || result.Content is null)
            {
                // The previous verdict stands and the next pass retries in a few seconds.
                // Logged at Error because a persistently failing Analyst is a silent
                // outage: the pipeline keeps running and detects nothing.
                logger.LogError("Analyst call failed for {SessionId}: {Error}",
                    session.SessionId, result.Error);
                return null;
            }

            var assessment = Parse(result.Content, (int)stopwatch.ElapsedMilliseconds);

            logger.LogInformation(
                "Analyst verdict for {SessionId}: risk={Risk} confidence={Confidence} stage={Stage} in {Latency}ms",
                session.SessionId, assessment.RiskScore, assessment.Confidence,
                assessment.Stage, stopwatch.ElapsedMilliseconds);

            return assessment;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected Analyst failure for {SessionId}.", session.SessionId);
            return null;
        }
    }

    /// <summary>
    /// Map the model's JSON to the domain type.
    ///
    /// Tolerant by design even though the schema is strict: a verdict is only useful if a
    /// schema drift or an unrecognised enum value degrades to a usable-but-cautious result
    /// rather than an exception on the media path.
    /// </summary>
    internal static RiskAssessment Parse(string json, int latencyMs)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var vectors = new List<ScamVector>();
        if (root.TryGetProperty("vectors", out var vectorArray) &&
            vectorArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in vectorArray.EnumerateArray())
            {
                if (TryMapVector(element.GetString(), out var vector))
                {
                    vectors.Add(vector);
                }
            }
        }

        var evidence = new List<EvidenceSpan>();
        if (root.TryGetProperty("evidence", out var evidenceArray) &&
            evidenceArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in evidenceArray.EnumerateArray())
            {
                var quote = element.TryGetProperty("quote", out var q) ? q.GetString() : null;
                if (string.IsNullOrWhiteSpace(quote))
                {
                    continue;
                }

                var speaker = element.TryGetProperty("speaker", out var s) ? s.GetString() : null;
                evidence.Add(new EvidenceSpan(quote, MapSpeaker(speaker), 0));
            }
        }

        return new RiskAssessment
        {
            // Clamped here as well as in the gate: defence in depth against a model that
            // returns 150 or -10 under an unusual prompt.
            RiskScore = Math.Clamp(ReadDouble(root, "riskScore"), 0, 100),
            Confidence = Math.Clamp(ReadDouble(root, "confidence"), 0, 1),
            Stage = MapStage(root.TryGetProperty("complianceStage", out var st) ? st.GetString() : null),
            Vectors = vectors,
            Evidence = evidence,
            Rationale = root.TryGetProperty("rationale", out var r) ? r.GetString() ?? string.Empty : string.Empty,
            AssessedAt = DateTimeOffset.UtcNow,
            AnalysisLatencyMs = latencyMs,
        };
    }

    private static double ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    /// <summary>Wire values match the enum in <c>AnalystPrompt.ResponseSchema</c>.</summary>
    private static readonly Dictionary<string, ScamVector> VectorsByWireName = new()
    {
        ["mfa_fatigue_coaching"] = ScamVector.MfaFatigueCoaching,
        ["otp_elicitation"] = ScamVector.OtpElicitation,
        ["authority_impersonation"] = ScamVector.AuthorityImpersonation,
        ["urgency_pretexting"] = ScamVector.UrgencyPretexting,
        ["remote_access_tooling"] = ScamVector.RemoteAccessTooling,
        ["temporary_access_pass_request"] = ScamVector.TemporaryAccessPassRequest,
        ["helpdesk_reset_fraud"] = ScamVector.HelpdeskResetFraud,
        ["payment_redirect"] = ScamVector.PaymentRedirect,
        ["callback_number_swap"] = ScamVector.CallbackNumberSwap,
        ["mfa_method_registration"] = ScamVector.MfaMethodRegistration,
    };

    private static bool TryMapVector(string? value, out ScamVector vector)
    {
        if (value is not null)
        {
            return VectorsByWireName.TryGetValue(value, out vector);
        }

        vector = default;
        return false;
    }

    private static ComplianceStage MapStage(string? value) => value switch
    {
        "unaware" => ComplianceStage.Unaware,
        "engaged" => ComplianceStage.Engaged,
        "about_to_approve" => ComplianceStage.AboutToApprove,
        "approved" => ComplianceStage.Approved,
        // An unrecognised stage must not silently escalate urgency.
        _ => ComplianceStage.Unaware,
    };

    private static SpeakerRole MapSpeaker(string? value) => value switch
    {
        "caller" => SpeakerRole.Caller,
        "user" => SpeakerRole.ProtectedUser,
        _ => SpeakerRole.Unknown,
    };
}
