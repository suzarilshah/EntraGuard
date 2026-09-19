# Architecture

Implementation reference, reviewed 20 September 2026. See [README](../README.md) for product scope and [`infra/`](../infra/) for the resources it creates. This describes source behavior, not a live estate audit.

## Runtime boundaries

| Component | Responsibility |
|---|---|
| `src/portal` / operator mode | Server-side Graph/KQL/Resource Graph reads; live SignalR console |
| `src/portal` / Treasury mode | Entra sign-in, verification UI, illustrative payment workspace, personal settings; separate hostname using the same image |
| `EntraGuard.MediaService` | ACS callbacks/WebSockets, speech, analysis, verification orchestration, Graph and Sentinel actions |
| `EntraGuard.Shared` | Domain models, pure policy gate, question selection, assurance/risk and voice rules |
| `src/voiceprint` | Internal FastAPI service; ECAPA-TDNN embeddings, cosine scores and enrollment consistency; no access decisions or persistent state |

The “agents” mostly run inside the Media Service process, not as independent containers. Only the speaker-scoring service is separately deployed.

## Outbound verification

1. Treasury's MSAL hook signs a work/school account in through Entra ID.
2. The user selects Teams, a browser, or a handset web page. Browser/handset reachability uses the presence API; Teams delivery depends on registered Teams endpoints and federation.
3. Treasury proxies `POST /api/verify/start`. The backend creates both a verification record and a monitored call session before ACS starts streaming.
4. ACS calls the selected endpoint and opens `WSS /ws/media/{monitorSessionId}` with unmixed, bidirectional 24 kHz mono PCM and DTMF enabled.
5. The coordinator speaks the number-match prompt. Digits can arrive through ACS recognition, streamed DTMF or the browser-device submission route. Prompt-round bookkeeping avoids consuming one entry twice.
6. If subject object/tenant IDs are available, sign-in and profile sources build a candidate pool. `ChallengeSelection` chooses up to four distinct facets, preferring at least one expiring fact when available. A readable registered answer can add another question; otherwise a registered question is the fallback.
7. The service listens for answers, tolerates spelling/recognition variations, and may ask deterministic follow-ups. Three or more questions permit one miss; two or fewer require all answers. Coercion is checked during answer waits and at final adjudication.
8. When configured and enough speech exists, the speaker service compares the protected user's speech with their encrypted enrolled template.
9. The backend completes the result once, captures media evidence, computes risk/assurance, writes telemetry and broadcasts a redacted projection. Treasury polls the result.

The browser/handset branch in Treasury currently sends fewer subject fields than the Teams branch; do not assume all channels receive the same knowledge/voice checks. Readiness/profile probes exist on the media API but are not a fully integrated preflight gate in Treasury.

## Incoming monitored calls

```text
Call to monitored ACS identity
  → Event Grid IncomingCall
  → POST /api/events/incoming-call
  → AnswerCall with streaming configured
  → WSS /ws/media/{sessionId}
  → PerceptionAgent (participant-attributed speech)
  → AnalystAgent / AnalystClient (risk + confidence + evidence + compliance stage)
  → PolicyGate (permitted and withheld actions)
  → ActuatorAgent (ordered tools, actual outcomes)
```

The answer path is kept fast. Analysis and Graph calls do not block audio receipt. An ACS participant is not necessarily one physical speaker: a coercer beside the victim can be heard on the protected user's channel. The Analyst prompt explicitly accounts for that, and labels the agent's own prompts separately.

## Decision authority

- **PolicyGate:** urgency-adjusted remediation thresholds 40/60/80/90; disruptive actions need confidence ≥0.75. Termination additionally requires `AboutToApprove`.
- **VerificationAdjudicator:** code-entry verdict and coercion refusal at risk ≥60, confidence ≥0.75. An enforced voice decision requesting step-up can return `BlockedVoiceMismatch`.
- **ConversationDirector:** budgets follow-ups and selects the conversational register; cannot grant access.
- **VerificationRisk:** informational composite risk, not authorization.
- **VerificationAssurance:** informational `None/Low/Substantial/High`, not AAL/eIDAS conformance or an enforced Treasury policy.

### Current inconsistencies to retain visibility of

The newer profile pool is stored under `KnowledgeBacking="telemetry"` even when sign-in questions did not contribute. Assurance currently interprets that prefix as sign-in telemetry. The readiness API still examines sign-in questions, stored knowledge and voice enrollment rather than the entire profile pool. These contracts need explicit source provenance before a relying party uses the scale as an authorization rule.

The frontend supports extra Microsoft authentication for a successful result with `requiresStepUp`; the adjudicator can instead refuse with `BlockedVoiceMismatch`. Do not promise that every weak voice match gets a recovery prompt. Default voice mode remains observation.

## Speech paths

Scripted ACS speech is the default. `AI_SERVICES_ENDPOINT` supplies ACS's cognitive endpoint; AI Speech supplies ongoing recognition and warning synthesis.

`VOICE_AGENT=on` in the deploy script passes the configured realtime endpoint/deployment. The optional `VoiceAgent` uses a separate OpenAI realtime connection and is now constrained to supplied prompts, with output guardrails and scripted fallback. Historical designs describing a freely conversational colleague are not the current behavior.

The audio receive loop, analysis timer and SignalR fan-out run independently. The configured analysis interval is three seconds over a 45-second window; actual model latency determines effective verdict cadence.

## State and persistence

| Data | Actual destination |
|---|---|
| Active calls, verification records, presence | In-process collections |
| Recent completed results and media snapshots | In-process verification registry; eligible for eviction after five minutes, eviction runs when creating another attempt |
| Entra/ACS identity mapping | `IdentityMap` Table |
| Registered question, salt, hash **and readable answer** | Entra custom security attributes where permitted, otherwise `EntraGuardKnowledge` Table |
| Encrypted voice template and consent | `EntraGuardVoiceprints` Table |
| Full transcript archive | **Not implemented**; Blob container `transcripts` is provisioned only |
| Historical session Table writer | **Not implemented**; `Sessions` is provisioned only |

Replica restart loses in-flight and retained in-memory state. Sticky sessions are configured, but do not establish affinity across unrelated ACS callback, WebSocket and browser clients. Distributed state/routing and cross-replica SignalR fan-out are production work.

### Log Analytics

The DCE/DCR Logs Ingestion API writes five streams:

- `EntraGuard_CallAnalysis_CL`: assessments, evidence and transcript excerpts truncated to 4,000 characters.
- `EntraGuard_Remediation_CL`: actions, refusals, API status and actual outcomes.
- `EntraGuard_Verification_CL`: result, risk, voice, follow-up and assurance fields.
- `EntraGuard_Biometric_CL`: enrollment/re-enrollment/failure/deletion and consent metadata.
- `EntraGuard_Fault_CL`: component failures, user impact and suggested remediation.

Table and DCR schemas must agree. Bicep declares 30-day retention. The Sentinel high-risk-call analytics rule runs every five minutes; direct incidents are also available from remediation tools. This is not an implemented repeated-biometric-refusal rule.

## Identity and access boundaries

Azure dependencies primarily use `DefaultAzureCredential` and `id-entraguard-demo`. Cross-tenant Graph uses workload identity federation into a separately configured multitenant application, with customer-tenant consent.

Voice-profile management validates tokens and derives ownership from claims; enrollment requires MFA evidence by default. Wider verification, identity-token broker, diagnostic and SignalR routes still need a uniform production authorization boundary. Frontend filtering is not tenant isolation, and separate Treasury routing is not equivalent to API authorization.

`VOICEPRINT_KEY` is AES-GCM encryption key material. Local ACS connection-string fallback also exists. “No OAuth client secret for service-to-service Azure access” must not be expanded into “no secrets anywhere.”

## Failure and operating modes

- Analyst failure retains the previous verdict; no assessment is not proof of a safe call.
- Failed Graph/profile sources degrade available questions and record faults.
- Missing/unavailable voice scoring gives `NotAssessed`, not a fabricated mismatch.
- Unanswered calls and incomplete challenges fail rather than grant.
- Telemetry errors are recorded/logged but do not make storage durable.
- `ENTRAGUARD_SHADOW_MODE=true` still permits telemetry, SOC notification and warranted incidents. It suppresses call/identity remediation, not every external effect or verification decision.

## Treasury presentation

`TreasuryDashboard` contains labeled sample payment records and client-side search/filter/sort/export/detail controls. It has no transaction write API. Its session-security view displays fields from the verification response without modifying the verdict. `TreasurySettings` retains the existing enrollment/question contracts, distinguishes activity failure from empty results, and keeps section panels mounted so switching views does not discard an ongoing enrollment.

See [backend roadmap](backend-roadmap.md) for the next capabilities to build behind this interface.
