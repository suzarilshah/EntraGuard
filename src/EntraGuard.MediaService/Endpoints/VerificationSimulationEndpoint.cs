using System.Text.Json.Serialization;
using EntraGuard.MediaService.Hubs;
using EntraGuard.MediaService.Sessions;
using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Verification;
using EntraGuard.Shared.Voice;
using Microsoft.AspNetCore.SignalR;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Exercises the verification decision without a live answered call.
///
/// The outbound call, the TTS prompt and the DTMF capture all need a real endpoint with a
/// working microphone. That is the right final test and it cannot run headlessly — a
/// browser with no microphone permission cannot accept an ACS call at all.
///
/// Everything downstream of "the user pressed these digits" is identical here: the same
/// <see cref="VerificationAdjudicator"/> decides, the same Sentinel row is written, the
/// same event reaches the portal. So the security-critical logic is testable and demoable
/// even when audio is unavailable.
///
/// Attempts created here are marked simulated, because a step-up factor that cannot
/// distinguish a rehearsal from a real authorization is not a factor at all.
/// </summary>
public static class VerificationSimulationEndpoint
{
    public sealed record SimRequest
    {
        /// <summary>pass | wrong-code | coerced | timeout</summary>
        [JsonPropertyName("scenario")]
        public string Scenario { get; init; } = "pass";

        [JsonPropertyName("upn")]
        public string Upn { get; init; } = "demo.user@contoso.com";
    }

    public static void MapVerificationSimulation(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/verify/simulate", async (
            SimRequest request,
            VerificationRegistry registry,
            LogsIngestionSink sink,
            IHubContext<LiveHub> hub,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("VerificationSim");

            var verification = registry.Create(
                request.Upn, subjectObjectId: null,
                calleeAcsId: "8:acs:simulated-endpoint",
                applicationName: "Contoso Treasury (simulated)");

            // What the user "pressed", and what the Analyst "heard".
            var (entered, assessment, attempts) = request.Scenario switch
            {
                "wrong-code" => (NextWrongCode(verification.MatchCode),
                                 (RiskAssessment?)null,
                                 VerificationAdjudicator.MaxAttempts),

                "coerced" => (verification.MatchCode,
                              new RiskAssessment
                              {
                                  RiskScore = 88,
                                  Confidence = 0.91,
                                  Stage = ComplianceStage.AboutToApprove,
                                  Vectors = [ScamVector.MfaFatigueCoaching, ScamVector.OtpElicitation],
                                  Rationale = "A second voice is instructing the user which digits to press.",
                                  Evidence =
                                  [
                                      new EvidenceSpan("just press the number he told you, six eight",
                                          SpeakerRole.Unknown, 4200),
                                  ],
                              },
                              1),

                "timeout" => (string.Empty, (RiskAssessment?)null, 0),

                // Right code, nothing audible wrong — the voice decides this one.
                "voice-mismatch" => (verification.MatchCode, (RiskAssessment?)null, 1),

                _ => (verification.MatchCode,
                      new RiskAssessment
                      {
                          RiskScore = 4,
                          Confidence = 0.95,
                          Stage = ComplianceStage.Unaware,
                          Rationale = "Verification call audio is unremarkable; no third party heard.",
                      },
                      1),
            };

            verification.EnteredCode = entered;
            verification.Attempts = attempts;
            if (assessment is not null)
            {
                verification.PeakRiskDuringCall = assessment.RiskScore;
            }

            VerificationResult result;
            string reason;

            if (request.Scenario == "timeout")
            {
                result = VerificationResult.Timeout;
                reason = "The verification call ended before the code was entered.";
            }
            else
            {
                // An impostor who has the phone and has researched the answers: correct
                // digits, no coaching to hear, and the wrong person speaking. Simulated
                // because the alternative is asking a second human to join a live call, and
                // this is the one scenario a demo cannot stage on its own.
                var voice = request.Scenario == "voice-mismatch"
                    ? VoiceThresholds.Evaluate(
                        score: 0.12, seconds: 9.4, enforce: true)
                    : null;

                if (voice is not null)
                {
                    verification.VoiceScore = voice.Score;
                    verification.VoiceOutcome = voice.Outcome.ToString();
                    verification.RequiresStepUp = voice.RequiresStepUp;
                    verification.LivenessOutcome = "Passed";
                    verification.LivenessLatencyMs = 780;
                }

                // The real rules, not a copy of them.
                var verdict = VerificationAdjudicator.Adjudicate(
                    verification.MatchCode, entered, attempts, assessment, voice);

                result = verdict.Result ?? VerificationResult.Failed;
                reason = verdict.Reason;
            }

            registry.TryComplete(verification.VerificationId, result, reason);
            await sink.WriteVerificationAsync(verification, cancellationToken);
            await hub.Clients.All.SendAsync(
                LiveHub.VerificationEvent, VerificationEndpoint.Describe(verification), cancellationToken);

            logger.LogInformation("Simulated verification {Id} ({Scenario}): {Result} — {Reason}",
                verification.VerificationId, request.Scenario, result, reason);

            return Results.Ok(new
            {
                verificationId = verification.VerificationId,
                scenario = request.Scenario,
                expectedCode = verification.MatchCode,
                enteredCode = entered,
                result = result.ToString(),
                reason,
                grantsAccess = verification.GrantsAccess,
                peakRiskDuringCall = verification.PeakRiskDuringCall,
                simulated = true,
            });
        })
        .WithName("SimulateVerification");
    }

    /// <summary>A wrong two-digit code that is genuinely different from the expected one.</summary>
    private static string NextWrongCode(string expected)
    {
        var value = int.Parse(expected);
        var wrong = value == 99 ? 10 : value + 1;
        return wrong.ToString();
    }
}
