# Architecture

Implementation reference, reviewed 20 September 2026. See [README](../README.md) for product scope and [deployment](deployment.md) for resources. This describes source behavior, not a live estate audit.

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

1. Treasury's MSAL hook obtains an EntraGuard API token. The backend validates it, creates a revocable session, and the frontend stores the opaque credential in an HttpOnly cookie.
2. The user selects Teams, a browser, or a handset web page. Browser/handset reachability uses the presence API; Teams delivery depends on registered Teams endpoints and federation.
3. Treasury proxies `POST /api/verify/start` with the server session. Subject identity is overwritten from validated claims. Device ownership, policy and optional transaction binding are checked; a durable receipt is created before dialling.
4. ACS calls the selected endpoint and opens `WSS /ws/media/{monitorSessionId}` with unmixed, bidirectional 24 kHz mono PCM and DTMF enabled.
5. The coordinator speaks the number-match prompt. Digits can arrive through ACS recognition, streamed DTMF or the browser-device submission route. Prompt-round bookkeeping avoids consuming one entry twice.
6. Sign-in and profile sources build a candidate pool. `ChallengeSelection` chooses distinct facets with an expiring fact when available. A registered rider takes a seat within the four-question cap. Actual asked/correct source outcomes are tracked separately from question text.
7. The service listens for answers, tolerates spelling/recognition variations, and may ask deterministic follow-ups. Three or more questions permit one miss; two or fewer require all answers. Coercion is checked during answer waits and at final adjudication.
8. When configured and enough speech exists, the speaker service compares the protected user's speech with their encrypted enrolled template.
9. Completion captures media evidence and source-aware assurance, then atomically persists receipt/history/outbox work. Treasury requests a separate server grant; a call result alone cannot authorize a browser session or payment.

All channels derive subject identity from validated tenant/object claims. The handset signs in before registration. Settings expose full-source readiness without expected answers; it is an upper bound, not a guaranteed outcome.

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
- **VerificationAdjudicator:** code-entry verdict and coercion refusal at risk ≥60, confidence ≥0.75. An enforced weak voice match produces non-authorizing `StepUpRequired`.
- **ConversationDirector:** budgets follow-ups and selects the conversational register; cannot grant access.
- **VerificationRisk:** informational composite risk, not authorization.
- **EvidenceAssurance:** source-aware `None/Low/Substantial/High`; only correctly answered sign-in evidence plus corroboration can reach High. Directory-only evidence stays Low. These are not certified AAL/eIDAS levels.
- **TenantPolicyService / GrantService:** enforce policy version, minimum assurance, freshness, channels and optional Analyst presence against the durable receipt and requesting session.

### Receipt, policy and step-up

Assurance no longer infers source from a `telemetry` string. It uses `QuestionEvidence` containing source, facet, asked and correct fields; no expected answers enter receipts. Readiness considers the same categories across sign-in, profile, registered knowledge and voice availability.

`StepUpRequired` grants nothing. `StepUpService` validates same-owner ID-token MFA evidence and `auth_time` after the verification start. Only then may normal grant/approval checks run. Coercion cannot be overridden, and legacy `BlockedVoiceMismatch` remains refused. Default voice mode remains observation.

## Speech paths

Scripted ACS speech is the default. `AI_SERVICES_ENDPOINT` supplies ACS's cognitive endpoint; AI Speech supplies ongoing recognition and warning synthesis.

`VOICE_AGENT=on` in the deploy script passes the configured realtime endpoint/deployment. The optional `VoiceAgent` uses a separate OpenAI realtime connection and is now constrained to supplied prompts, with output guardrails and scripted fallback. Historical designs describing a freely conversational colleague are not the current behavior.

The audio receive loop, analysis timer and SignalR fan-out run independently. The configured analysis interval is three seconds over a 45-second window; actual model latency determines effective verdict cadence.

## State and persistence

| Data | Actual destination |
|---|---|
| Active call sockets/coordinators | In-process collections; one supported media replica |
| Session credentials, grants and revocation | `EntraGuardState`, tenant partition with owner-prefixed rows; only credential hashes stored |
| Verification receipts, history and media snapshots | `EntraGuardState`; durable before grant, with conditional writes and reverse-time history indexes |
| New ACS registrations and presence | Owner-scoped `EntraGuardState` device rows; legacy `IdentityMap` is not ownership proof |
| Policies, audit, preferences and notifications | `EntraGuardState` |
| Demo payments and approval receipts | Tenant-scoped conditional transactions in `EntraGuardState` |
| Registered question, salt, hash **and readable answer** | Entra custom security attributes where permitted, otherwise `EntraGuardKnowledge` Table |
| Encrypted voice template and consent | `EntraGuardVoiceprints` Table |
| Full transcript archive | **Not implemented**; Blob container `transcripts` is provisioned only |
| Legacy `Sessions` Table writer | Unused; the new receipt ledger uses `EntraGuardState` |

Replica restart loses the call, not its durable receipt/history. Deadline recovery marks unfinished attempts failed after 15 minutes; it does not resume audio. Media is pinned to one replica because sticky cookies do not establish routing across unrelated ACS and browser requests. Distributed live-call routing/backplane and in-flight handoff remain future work.

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

Owner APIs validate credentials and derive identity from claims. Exact tenant issuers and the API scope are checked; ID tokens cannot be bearer credentials. Session-cookie mutations require the configured frontend origin. Operator pages are gated before rendering, and diagnostics/simulations/SignalR require a home-tenant operator. Callbacks/media carry expiring path-bound HMAC capabilities; Event Grid uses a separate webhook secret. Request query capabilities are removed from request telemetry.

`VOICEPRINT_KEY` is AES-GCM encryption key material. Local ACS connection-string fallback also exists. “No OAuth client secret for service-to-service Azure access” must not be expanded into “no secrets anywhere.”

## Failure and operating modes

- Analyst failure retains the previous verdict; no assessment is not proof of a safe call.
- Failed Graph/profile sources degrade available questions and record faults.
- Missing/unavailable voice scoring gives `NotAssessed`, not a fabricated mismatch.
- Unanswered calls and incomplete challenges fail rather than grant.
- Verification completion atomically writes telemetry outbox work. A background worker retries at least once; duplicates after acknowledgment failures are possible and must be deduplicated analytically.
- `ENTRAGUARD_SHADOW_MODE=true` still permits telemetry, SOC notification and warranted incidents. It suppresses call/identity remediation, not every external effect or verification decision.

## Treasury presentation

`TreasuryDashboard` loads sample payment records from a protected server ledger. Approver-role users can request a new verification bound to the exact payment digest and persist a demo approval; there is no bank execution. Session security displays server evidence. Settings include durable history pagination, preferences/devices/inbox and role-restricted policy/readiness, with panels kept mounted during enrollment.

See [security migration](security-migration.md) for roles, secrets, deployment sequencing, TTLs and remaining production limits. The optional EAM integration in this checkout has a separate configuration/lifecycle from Treasury grants.

See [backend roadmap](backend-roadmap.md) for the next capabilities to build behind this interface.
