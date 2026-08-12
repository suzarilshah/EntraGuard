# EntraGuard

**Real-time voice verification and anti-scam defence for Microsoft Entra ID authentication.**
Microsoft Garage Hackathon MVP.

Entra ID Protection sees the *sign-in*. It is blind to the *phone call* that caused it.
EntraGuard puts the voice channel inside the Zero Trust perimeter: it intercepts calls
placed during authentication, transcribes them live, scores them for social engineering as
they happen, and acts — inside the window where acting still prevents something.

```
Attacker ──call──▶ Azure Communication Services ──IncomingCall──▶ EntraGuard
                            │ bidirectional audio (WSS)               │
                            ▼                                          ▼
                    Azure AI Speech  ──transcript──▶  Azure OpenAI (Analyst)
                            ▲                                          │
                    spoken warning                              risk + evidence
                            │                                          ▼
                            └──────────────────────────────────  Policy Gate
                                                                       │
                                          ┌────────────────────────────┼──────────────┐
                                          ▼                            ▼              ▼
                                  Entra ID Protection          Sentinel incident   Hang up
                                  revoke / quarantine
```

## What makes it different from a transcript classifier

**The Policy Gate.** A language model reads a live conversation and proposes remediation —
that is the agentic part, and it is also the dangerous part. A model that misreads an
angry-but-legitimate help-desk call could revoke a real person's sessions in the middle of
their workday.

So the model never acts. It produces a `RiskAssessment`; a deterministic, exhaustively
tested gate decides what may actually happen. Irreversible actions require ≥75% analyst
confidence. Hanging up requires both near-certainty *and* a victim seconds from approving.
Every withheld action is recorded with its reason, so "why didn't it act?" and "why did it
lock out my user?" are both answerable.

Autonomy lives upstream. Authority lives in [`PolicyGate.cs`](src/EntraGuard.Shared/Policy/PolicyGate.cs).

**Compliance stage, not just risk score.** The Analyst reports how far the victim has been
drawn in — `unaware → engaged → about_to_approve → approved`. Identical evidence warrants
more force when approval is seconds away. That is what makes EntraGuard preventive rather
than forensic.

**A spoken warning back into the live call.** ACS bidirectional streaming means the
synthesised warning travels back down the same socket the call audio arrives on. The user
hears it while the attacker is still talking. Every other remediation is invisible to the
person currently being manipulated.

## Measured on the deployed system

Not projections — these come from replaying scripted conversations through the live
pipeline on `gpt-5-mini`:

| Scenario | Peak risk | Confidence | Acted? |
|---|---|---|---|
| Help-desk impersonation | **100** | 0.90 | Yes — warning, incident, terminate |
| Remote-access tooling | **100** | 0.90 | Yes |
| **Legitimate help-desk call** | **15** | 0.90 | **No** |
| **Ambiguous support call** | **10** | 0.90 | **No** |

Analyst latency: **5.6–9.9 s, mean 6.9 s** per assessment. At the model's *default*
reasoning effort the same call took **15–16 s** — long enough that the warning would arrive
after the victim had already approved. Low reasoning effort is what makes this preventive
rather than forensic; see [`AnalystClient`](src/EntraGuard.MediaService/Agents/AnalystClient.cs).

## Testing it

No phone call required. From the portal: **Live calls → Test the pipeline → Run simulation**.

The Analyst, policy gate, Actuator, Graph calls and Sentinel writes are all real; only ACS
and Speech are bypassed, because the transcript is supplied rather than recognised. Sessions
started this way are labelled **Simulated** everywhere they appear — a security tool that
lets a replay pass for an interception is worse than one that cannot replay at all.

```bash
curl -X POST "$PORTAL/api/simulate" -H 'Content-Type: application/json' -d '{"scenario":"benign-helpdesk"}'
```

Run `benign-helpdesk` as well as `helpdesk-fraud`. The benign control is the one that
matters: it has every surface feature of the attack and must score low.

## Repository

| Path | What it is |
|---|---|
| [`infra/`](infra) | Bicep. ACS, AI Speech, Azure OpenAI, Log Analytics + Sentinel, DCE/DCR, Container Apps |
| [`src/EntraGuard.Shared/`](src/EntraGuard.Shared) | Domain, scam taxonomy, ACS frame codec, **the Policy Gate** |
| [`src/EntraGuard.MediaService/`](src/EntraGuard.MediaService) | ASP.NET Core: interception, transcription, the agent loop, remediation tools |
| [`src/portal/`](src/portal) | Next.js console reading live Graph / KQL / Resource Graph |
| [`tests/`](tests) | xUnit. The gate's decision matrix and the wire-format edge cases |
| [`scripts/`](scripts) | Preflight, deploy, Entra plumbing, Event Grid wiring |
| [`docs/`](docs) | [Architecture](docs/architecture.md) · [Standards & threat model](docs/standards-and-threat-model.md) · [Demo runbook](docs/demo-runbook.md) · [Pitch](docs/pitch.md) |

## Deploying

```bash
az login --scope https://management.core.windows.net//.default
```

Then, in order:

```bash
./scripts/00-preflight.sh
```

Preflight is the go/no-go gate. It probes what this subscription can actually do — Azure
OpenAI model availability and quota, Entra ID P2, ACS Entra preview eligibility — and
writes the resolved feature flags to `.env.deploy`. Everything downstream reads that file,
so the degradation decisions are made once, up front, rather than discovered mid-demo.

```bash
./scripts/01-deploy-infra.sh
./scripts/02-entra-apps.sh
./scripts/deploy-apps.sh
./scripts/04-eventgrid-subscribe.sh
./scripts/smoke-test.sh
```

`02-entra-apps.sh` prints an admin-consent URL. A Global Administrator has to click it.

## The degradation ladder

`identityProtection/riskyUsers/confirmCompromised` requires **Entra ID P2**, which most
demo and sponsorship tenants do not have. EntraGuard is built for that from the start
rather than pretending otherwise:

| Rung | Action | Requires |
|---|---|---|
| 1 | `confirmCompromised` — risk state → High | Entra ID **P2** |
| 2 | `revokeSignInSessions` — invalidate all tokens | Any tier |
| 3 | Conditional Access quarantine group | Any tier |
| 4 | Sentinel incident + telemetry | Always |

Rung 4 always runs. When rung 1 returns 403 the portal says so in the API's own words
instead of showing a green tick. A demo that fakes a successful risk elevation is a demo
that falls apart under the first informed question.

**Positioning:** Entra's "Report suspicious activity" is a *human* reporting an unexpected
MFA prompt. `confirmCompromised` is the supported programmatic equivalent. EntraGuard files
that report autonomously, from evidence the user does not have — because the user is, at
that moment, being actively manipulated.

## Local development

```bash
export PATH="$(brew --prefix dotnet@9)/bin:$PATH"
dotnet test
```

```bash
cd src/portal && npm install && npm run dev
```

ACS dials your callback and media socket from the public internet, so local interception
needs a tunnel:

```bash
devtunnel host -p 8080 --allow-anonymous
```

Set `PUBLIC_BASE_URL` to the tunnel URL. If it is not publicly reachable over `wss`, ACS
fails the call with error subcode **8581** — reported against the answer, not against the
setting that caused it.

## Notable constraints this design is built around

Each of these was verified against current Microsoft documentation, and each one changed a
decision:

- **`IncomingCall` fires for ACS-identity → ACS-identity calls**, not only PSTN. Interception
  is fully demonstrable over VoIP with no phone number purchase and no regulatory lead time.
- **Do not host the answer path on consumption Azure Functions.** A call rings for ~30
  seconds and a cold start can consume that whole window. Functions also cannot accept an
  inbound WebSocket upgrade, which ACS media streaming requires. Hence Container Apps with
  `minReplicas: 1` — a correctness requirement, not a performance tweak.
- **The HTTP Data Collector API retires 2026-09-14.** Custom Sentinel ingestion uses the
  Logs Ingestion API with DCE + DCR. Consequently the custom-table columns carry no type
  suffix: it is `RiskScore`, not `RiskScore_d`.
- **ACS direct Entra ID user auth is public preview.** Shipped behind a feature flag with a
  GA-supported server-side token broker as the fallback.
