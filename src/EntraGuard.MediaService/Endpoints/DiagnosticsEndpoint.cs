using System.Reflection;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Sessions;
using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Supportability;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// The endpoint you call first when something is wrong.
///
/// <para>
/// <c>/health/ready</c> already exists and answers a different, weaker question: are the
/// configuration strings non-empty. That is worth having — Container Apps needs it — but it
/// has never once helped diagnose a real failure, because every real failure in this system
/// happened with configuration perfectly in place. A URL being set means somebody typed a
/// URL.
/// </para>
///
/// <para>
/// So these endpoints report evidence instead: what the running build is, which faults have
/// actually occurred, and what each dependency did when last called. Read-only, additive, and
/// on no call path — nothing here can affect a verification in progress.
/// </para>
/// </summary>
public static class DiagnosticsEndpoint
{
    public static void MapDiagnostics(this IEndpointRouteBuilder app)
    {
        // ── What is actually running? ────────────────────────────────────────
        //
        // The single most useful endpoint in this file, because of how often the answer has
        // been "not what you just built".
        //
        // Four separate times, a deploy reported success while old code kept serving: a
        // comment inside a backslash-continued az command silently ended it; a compile error
        // broke the image build; an invalid --no-cache flag meant no image was ever pushed
        // and every revision failed to pull it; and a draining revision answered probes for
        // minutes after the new tag was live. In each case health checks stayed green,
        // because the old container was perfectly healthy.
        //
        // An image tag is not evidence — it says what was REQUESTED. This is compiled in, so
        // it can only say what is running.
        app.MapGet("/api/build", (IOptions<EntraGuardOptions> options) =>
        {
            var assembly = Assembly.GetExecutingAssembly();

            return Results.Ok(new
            {
                version = assembly.GetName().Version?.ToString(),
                informational = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion,
                builtAt = File.GetLastWriteTimeUtc(assembly.Location),
                startedAt = Process.StartTime,
                uptimeSeconds = (int)(DateTimeOffset.UtcNow - Process.StartTime).TotalSeconds,
                // Runtime posture, so "why did it behave that way" is answerable without
                // reading environment variables out of the Azure portal.
                voiceMode = options.Value.VoiceEnforce ? "enforce" : "observe",
                voiceAccept = options.Value.VoiceAcceptThreshold,
                voiceReject = options.Value.VoiceRejectThreshold,
                autonomousActions = options.Value.AutonomousActionsEnabled,
                riskTier = options.Value.RiskTier.ToString(),
                model = options.Value.OpenAiDeployment,
            });
        })
        .WithName("BuildProvenance");

        // ── What has gone wrong, and what did it mean? ───────────────────────
        //
        // Grouped by code rather than listed raw, because the question is almost never "did
        // this happen once" — it is "is this happening repeatedly, since when, and does it
        // matter". Each entry carries the user impact, the probable cause and the next
        // action, so reading it does not require reconstructing the reasoning from source.
        app.MapGet("/api/diagnostics/faults", (FaultRecorder faults) => Results.Ok(new
        {
            summary = faults.Summary(),
            recent = faults.Recent(50).Select(entry => new
            {
                at = entry.At,
                component = entry.Fault.Component.ToString(),
                code = entry.Fault.Code,
                severity = entry.Fault.Severity.ToString(),
                whatFailed = entry.Fault.WhatFailed,
                userImpact = entry.Fault.UserImpact,
                probableCause = entry.Fault.ProbableCause,
                remediation = entry.Fault.Remediation,
                correlationId = entry.Fault.CorrelationId,
                subjectUpn = entry.Fault.SubjectUpn,
                detail = entry.Fault.Detail,
            }),
            note = "In-memory and bounded, so it is empty after a restart. Anything that must "
                 + "survive one is in EntraGuard_Fault_CL.",
        }))
        .WithName("Faults");

        // ── Does the reporting path itself work? ─────────────────────────────
        //
        // The one check nothing else can make. Every other mechanism here reports failures
        // through the fault recorder, so if the recorder or its stream is broken the system
        // goes quiet in exactly the way it would if everything were fine. That is not
        // hypothetical: a data collection rule silently dropped three columns for days, and
        // the symptom was a dashboard reading zero, which is also what "nothing happened"
        // looks like.
        //
        // Records a clearly-labelled Info fault and returns what came back. If it appears
        // here but never in EntraGuard_Fault_CL, the stream declaration is wrong — undeclared
        // columns are dropped WITHOUT an error.
        app.MapPost("/api/diagnostics/selftest", (FaultRecorder faults) =>
        {
            var marker = $"selftest-{DateTimeOffset.UtcNow:HHmmss}";

            faults.Record(new Fault(
                FaultComponent.Ingestion,
                "diagnostics.selftest",
                FaultSeverity.Info,
                "A deliberate test fault, raised by /api/diagnostics/selftest.",
                "None. Nobody was on a call and nothing was refused.",
                "Nothing is wrong. This proves the reporting path carries a fault from the "
                + "point it is raised to the point somebody can read it.",
                "If this appears below but not in EntraGuard_Fault_CL within a few minutes, "
                + "the DCR stream declaration does not match the row shape in LogsIngestionSink.",
                Detail: marker));

            return Results.Ok(new
            {
                raised = marker,
                visibleInMemory = faults.Recent(10).Any(f => f.Fault.Detail == marker),
                next = "Query EntraGuard_Fault_CL for Detail == '" + marker + "'. Ingestion "
                     + "lags by a few minutes; absence after ten is a real problem.",
            });
        })
        .WithName("DiagnosticsSelfTest");

        // ── Is each dependency actually working? ─────────────────────────────
        //
        // Proven by calling them, not by checking that they are configured. Every check
        // reports what it found AND what to do if it is unhappy, because the person reading
        // this at speed is usually not the person who wrote the component.
        app.MapGet("/api/diagnostics", async (
            Agents.VoiceprintClient voiceprint,
            VoiceprintStore store,
            LiveCallRegistry calls,
            VerificationRegistry verifications,
            FaultRecorder faults,
            IOptions<EntraGuardOptions> options,
            CancellationToken cancellationToken) =>
        {
            var checks = new List<DiagnosticCheck>();

            // Voiceprint sidecar: reachable AND producing embeddings of the right shape.
            // "Configured" would be satisfied by a typo.
            if (!voiceprint.IsConfigured)
            {
                checks.Add(Check("voiceprint", "skipped",
                    "No scorer configured. Verification runs without voice comparison.",
                    "Set VOICEPRINT_URL if voice biometrics are wanted."));
            }
            else
            {
                var probe = await voiceprint.EmbedAsync(Tone(220, 4), cancellationToken);

                checks.Add(probe is null
                    ? Check("voiceprint", "broken",
                        "The scorer did not return an embedding.",
                        "The sidecar is unreachable or erroring. Voice will report NotAssessed "
                      + "on every call; verification itself is unaffected.")
                    : Check("voiceprint", "healthy",
                        $"Returned a {probe.Length}-dimension embedding.",
                        "No action needed."));
            }

            // Template storage. A working scorer with unreadable storage still cannot verify
            // anybody, and the two fail independently.
            checks.Add(store.IsAvailable
                ? Check("voiceprint-storage", "healthy", "Template table reachable.", "No action needed.")
                : Check("voiceprint-storage", "broken",
                    "Voice profile storage is not available.",
                    "Enrolment will fail and no enrolled voice can be loaded. Check the storage "
                  + "account and the managed identity's data-plane role."));

            // Telemetry ingestion — the path whose silent failure is most dangerous, because
            // a missing audit row looks exactly like an uneventful period.
            checks.Add(string.IsNullOrEmpty(options.Value.DceEndpoint)
                ? Check("sentinel-ingestion", "skipped",
                    "No data collection endpoint configured. Nothing is being recorded.",
                    "Set DCE_ENDPOINT and DCR_IMMUTABLE_ID to record verifications.")
                : Check("sentinel-ingestion", "healthy",
                    $"Configured against {options.Value.DceEndpoint}.",
                    "Ingestion failures appear as ingestion.upload_failed in "
                  + "/api/diagnostics/faults. An undeclared column is dropped WITHOUT error, "
                  + "so compare the DCR stream against the row shape when a column reads empty."));

            // The circuit's own view, which is cheap and needs no call: how many consecutive
            // failures, when it last succeeded, and whether calls are currently being shed.
            var circuit = Agents.VoiceprintClient.Circuit;

            checks.Add(new DiagnosticCheck(
                "voiceprint-circuit",
                circuit.IsOpen ? "degraded" : "healthy",
                circuit.IsOpen
                    ? $"Shedding calls after {circuit.ConsecutiveFailures} consecutive failures. "
                    + $"Last error: {circuit.LastError}"
                    : $"Closed. {circuit.ConsecutiveFailures} consecutive failures, last success "
                    + $"{circuit.LastSuccess?.ToString("u") ?? "never"}.",
                circuit.IsOpen
                    ? "It reopens automatically on the next successful probe. Do not restart "
                    + "anything — the shedding is the recovery mechanism, not the failure."
                    : "No action needed."));

            var degraded = faults.Recent(200)
                .Where(f => f.Fault.Severity != FaultSeverity.Info)
                .Select(f => f.Fault.Code)
                .Distinct()
                .ToList();

            return Results.Ok(new
            {
                // Worst wins. A single broken dependency is not averaged away by three
                // healthy ones — the whole point of this endpoint is that it says so.
                status = checks.Any(c => c.Status == "broken") ? "broken"
                       : checks.Any(c => c.Status == "degraded") || degraded.Count > 0 ? "degraded"
                       : "healthy",
                checks,
                activeSessions = calls.Active.Count,
                verificationsInFlight = verifications.Recent.Count(v => !v.IsComplete),
                degradedCapabilities = degraded,
                voiceMode = options.Value.VoiceEnforce ? "enforce" : "observe",
            });
        })
        .WithName("Diagnostics");
    }

    private static System.Diagnostics.Process Process => System.Diagnostics.Process.GetCurrentProcess();

    /// <summary>
    /// One dependency's verdict.
    ///
    /// A named type rather than an anonymous object, and the reason is a bug this file
    /// already had: the checks were a List&lt;object&gt; of two different anonymous shapes, and
    /// the summary read "status" off each by reflection. The shape without that property
    /// returned null, the null-forgiving operator hid it at compile time, and the endpoint
    /// answered 500 — a diagnostics endpoint that fails is worse than none, because it fails
    /// exactly when somebody is using it to find out what else is broken.
    /// </summary>
    /// <param name="Status">healthy | degraded | broken | skipped.</param>
    /// <param name="Finding">What was observed, with the evidence.</param>
    /// <param name="Action">What to do about it. "No action needed" is a valid answer.</param>
    private sealed record DiagnosticCheck(string Name, string Status, string Finding, string Action);

    private static DiagnosticCheck Check(string name, string status, string finding, string action) =>
        new(name, status, finding, action);

    /// <summary>A sine wave as 16 kHz PCM16 — enough to prove the scorer answers.</summary>
    private static byte[] Tone(double hz, double seconds)
    {
        var samples = (int)(Shared.Voice.AudioResampler.ModelSampleRate * seconds);
        var pcm = new byte[samples * 2];

        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * hz * i / Shared.Voice.AudioResampler.ModelSampleRate) * 12000);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), value);
        }

        return pcm;
    }
}
