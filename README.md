# EntraGuard

**Voice-based identity verification and anti-social-engineering detection for Microsoft Entra ID applications.**

Microsoft Garage hackathon MVP. This document describes the checked-in implementation as reviewed on **20 September 2026**, not a certification or a live deployment-health report.

## What it does

EntraGuard has two connected flows:

1. **Outbound step-up verification.** A relying-party application signs a user in with Entra ID, then asks EntraGuard to call them. Number matching, available identity questions, optional voice comparison and coercion analysis contribute to the result.
2. **Monitored-call scam detection.** Calls routed to a monitored ACS identity are transcribed and assessed for social engineering. A deterministic policy gate authorizes warnings and remediation.

It does **not** automatically monitor every phone or Teams call in a tenant. Treasury demonstrates application-level step-up; native tenant-wide Entra external authentication-method integration is not implemented.

```text
Contoso Treasury → Microsoft Entra sign-in → EntraGuard verification request
                                               │
                                      ACS call to Teams / browser / handset web page
                                               │
                                  Number match + available identity questions
                                               │
                         ┌─────────────────────┴──────────────────────┐
                         │                                            │
                  Speech → Analyst                           Voiceprint comparison
                  coercion evidence                          optional; observe by default
                         └─────────────────────┬──────────────────────┘
                                               │
                                Deterministic verification decision
                                               │
                         Treasury result + assurance + audit telemetry

Incoming monitored ACS call → Event Grid → Media Service → Speech → Analyst
                                                                    │
                                                              Policy Gate
                                                                    │
                                                Warning / Graph / Sentinel / hangup
```

## Current capabilities

| Capability | Implementation and limits |
|---|---|
| Work-account sign-in | MSAL with Entra work/school accounts; Treasury uses the signed-in identity for its Teams flow |
| Verification channels | Teams, browser ACS softphone, or a handset web page connected through QR enrollment; no PSTN number is provisioned in the documented demo |
| Number matching | Two random digits, three attempts; ACS recognition, media-stream DTMF and browser-device submission paths |
| Identity questions | Sign-in telemetry plus directory/profile and recent calendar, mail, chat and file activity where permissions/data permit; includes manager/direct-report questions |
| Question selection | Up to four distinct-facet candidates, randomized, with an expiring source selected when available; a registered question may be appended. With three or more questions, the threshold permits one miss |
| Coercion detection | The Analyst evaluates the conversation, including coaching on the protected user's own audio channel. The coordinator checks coercion while waiting for answers and at final adjudication |
| Adaptive conversation | Deterministic follow-up budgets, question retries and warm/protective register; optional realtime voice agent constrained to supplied speech |
| Voice biometrics | SpeechBrain ECAPA-TDNN, consented three-phrase enrollment, encrypted 192-dimensional templates, re-enrollment and deletion |
| Assurance | `None`, `Low`, `Substantial`, `High` plus basis/gaps; descriptive, not a certified AAL and not a minimum-level policy enforced by Treasury |
| Readiness and diagnostics | Pre-call readiness, telemetry/profile probes, media evidence and actionable fault records |
| Remediation | Spoken warnings, session revocation, quarantine membership, P2 risk elevation where available, Sentinel incidents and conditional call termination |
| Simulations | Separate intercepted-call replay and verification-adjudication simulation; see [demo runbook](docs/demo-runbook.md) for what each actually exercises |

Profile-source questions are implemented, not merely planned. Permission failures remove individual sources. Directory facts remain researchable, and the selector can use directory-only candidates when no expiring source exists. Current assurance/readiness reporting needs reconciliation with the newer profile pool; see [architecture](docs/architecture.md).

## The two web experiences

Both are built from **`src/portal/`**, using Next.js 15, React 19 and Node.js 22+.

### Contoso Treasury — the relying party

`APP_MODE=treasury` gives Treasury its own hostname; middleware routes `/` to `/app` and excludes operator pages.

- Redesigned Microsoft sign-in and guided verification journey.
- Signed-in overview with illustrative portfolio totals and composition.
- Searchable, status-filtered and sortable payment runs, detail dialogs and CSV export.
- Session security page displaying the verification result, reported assurance, voice outcome and detected risk.
- Settings sections for identity, optional voice recognition, verification methods and recent activity.
- Responsive layouts, keyboard-operable controls and reduced-motion support.

**Financial records are explicitly demo data.** There is no banking ledger, payment approval API or funds movement. Verification evidence is supplied by EntraGuard, not fabricated from the payment samples. Returning to Treasury after a full page navigation can require a new verification because access-stage state is held in the browser component.

### EntraGuard operator console

Overview `/`, live calls `/live`, verification ledger `/verification`, and resource footprint `/health`. Data comes from the Media Service, SignalR, Graph, KQL and Resource Graph. Historical references to standalone `/sentinel` or `/identity` pages are obsolete.

## Azure infrastructure

Four Container Apps, three images:

| Container App | Responsibility | Ingress | Bicep CPU / memory / replicas |
|---|---|---|---|
| `ca-entraguard-media` | .NET 9 call processing, verification, detection and remediation | Public :8080 | 1 / 2 GiB / 1–3 |
| `ca-entraguard-portal` | Operator console | Public :3000 | 0.5 / 1 GiB / 1–2 |
| `ca-contoso-treasury` | Treasury, using the same portal image | Public :3000 | 0.5 / 1 GiB / 1–2 |
| `ca-entraguard-voiceprint` | Python/FastAPI speaker scoring | Internal :8000 | 2 / 4 GiB / 1–2 |

The voiceprint “sidecar” is a **separate Container App**. Main deployment: `rg-entraguard-demo`, `eastus`, environment `cae-entraguard-demo`. Supporting services include ACS, Event Grid, AI Speech, AI Services, Azure OpenAI, ACR, Storage, a user-assigned managed identity, Log Analytics, Sentinel, DCE/DCR and Application Insights.

See [architecture](docs/architecture.md) for how these fit together, [`infra/`](infra/) for the Bicep that creates them, and [Deployment](#deployment) below for the ordered scripts.

## Decision boundaries and operating modes

The Analyst returns evidence; [`PolicyGate`](src/EntraGuard.Shared/Policy/PolicyGate.cs) decides remediation. Effective risk includes compliance-stage urgency:

- **40:** SOC notification.
- **60:** spoken warning.
- **80:** incident and identity containment proposals.
- **90 + `AboutToApprove`:** termination may be authorized.
- Disruptive actions require analyst confidence **≥0.75**, and identity actions require a resolved subject.

[`VerificationAdjudicator`](src/EntraGuard.MediaService/Endpoints/VerificationAdjudicator.cs) separately refuses detected coercion at risk **≥60** and confidence **≥0.75**, even with correct digits.

| Configuration | Effect |
|---|---|
| `ENTRAGUARD_RISK_TIER=degraded` | Substitutes quarantine membership for unavailable P2 risk elevation. Group membership needs a configured Conditional Access policy to affect access |
| `ENTRAGUARD_SHADOW_MODE=true` | Suppresses identity/call remediation, **but telemetry, SOC notifications and warranted Sentinel incidents remain allowed**. Not a universal dry-run switch for verification |
| `VOICE_MODE=observe` | Default deployment behavior: records scores without voice-driven enforcement |
| `VOICE_MODE=enforce` | Can produce `BlockedVoiceMismatch`; the UI's additional-authentication path and backend refusal semantics need alignment before adopting enforcement |
| `VOICE_AGENT=on` | Deployment-script opt-in for the realtime agent. Otherwise scripted speech is used |

Session revocation is not a guarantee of instantaneous invalidation of every existing access token. Graph permissions, licensing, Conditional Access setup and successful telemetry ingestion all matter; a supported action is not guaranteed to succeed.

## Data and current maturity

- Live calls, presence and verification results use **in-process state**. Recent completed attempts are eligible for eviction after five minutes; this is not durable user history.
- Table Storage persists identity mappings, registered knowledge questions and encrypted voiceprints.
- `Sessions` and Blob container `transcripts` are provisioned, but session/archive writers are not implemented.
- Log Analytics receives verdicts, evidence and **transcript excerpts up to 4,000 characters**, plus remediation, verification, biometric lifecycle and fault records. Raw voice-enrollment audio is processed in memory.
- Registered answers are currently retained in **readable form as well as hashes** for spoken-answer matching. The older “hash-only” claim is incorrect.
- Azure service access primarily uses managed identity. `VOICEPRINT_KEY` is still application encryption key material; this is not an entirely secret-free deployment.
- Voice-profile APIs have token-based authorization. The wider verification/diagnostic/SignalR surface still needs production authorization, tenant isolation and callback/media authentication.
- No PAD/voice-clone detection is implemented. Synthetic-voice calibration is not real-telephony accuracy validation.

This is a substantial MVP, not a production-complete authentication service. See [threat model](docs/standards-and-threat-model.md) and the prioritized [backend roadmap](docs/backend-roadmap.md).

## Development and verification

Use the .NET 9 SDK selected by `global.json`:

```bash
dotnet test EntraGuard.sln
```

In **`src/portal/`**:

```bash
npm ci
npm run dev
npm run build
```

Supply runtime variables from [`.env.example`](.env.example). ASP.NET Core does not automatically load a root `.env` file: export the variables in the service process or use your local configuration tooling. Next.js development can use an uncommitted `src/portal/.env.local`. The existing `npm test` entry has no committed frontend test suite; a production build checks compilation/types, not end-to-end calls.

Real call testing needs a publicly reachable HTTPS/WSS media callback host, correct Entra redirect URIs, consent and a registered calling endpoint. Local visual previews alone do not prove sign-in or telephony.

## Deployment

The repository's full-deployment sequence is:

```bash
./scripts/00-preflight.sh
./scripts/01-deploy-infra.sh
./scripts/02-entra-apps.sh
./scripts/deploy-apps.sh
./scripts/04-eventgrid-subscribe.sh
./scripts/smoke-test.sh
```

These scripts modify cloud resources. `deploy-apps.sh` updates media, portal **and** Treasury; it is not a Treasury-only deployment command. Voiceprint image builds require `VOICEPRINT=rebuild`. Teams federation, attribute permissions, relying-party registration and cross-tenant federation require additional setup; the six commands alone are not proof of a complete fresh multitenant installation.

## Repository map

| Path | Purpose |
|---|---|
| [`src/EntraGuard.MediaService/`](src/EntraGuard.MediaService) | ASP.NET Core backend |
| [`src/EntraGuard.Shared/`](src/EntraGuard.Shared) | Domain models and deterministic policy/verification/voice rules |
| [`src/portal/`](src/portal) | Azure-deployed operator console and Treasury |
| [`src/voiceprint/`](src/voiceprint) | SpeechBrain scoring service |
| [`infra/`](infra) | Bicep infrastructure |
| [`scripts/`](scripts) | Deployment, federation, calibration and smoke tests |
| [`tests/`](tests) | xUnit backend tests |
| [`docs/`](docs) | Architecture, deployment, demo, threat model and roadmap |

The separate top-level `portal/` is a vinext/Cloudflare-oriented project and is **not** the frontend built by the Azure deployment script. `docs/plans/` and `docs/blog/` contain historical design/publication artifacts; use the current docs above for implementation status.

### Historical measurements

Earlier scripted replays recorded help-desk and remote-access attacks at peak risk 100, with benign/ambiguous controls at 15/10. Reported analyst latency was 5.6–9.9 seconds, mean 6.9 seconds on `gpt-5-mini`. These are previous demo observations, not current benchmarks, guarantees or population-level accuracy results. The configured three-second analysis timer does not mean a verdict every three seconds.
