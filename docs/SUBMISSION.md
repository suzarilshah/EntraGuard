![EntraGuard](https://raw.githubusercontent.com/suzarilshah/EntraGuard/main/docs/assets/entraguard-poster-horizontal.png)

# EntraGuard

**Your MFA proves someone has the phone. It cannot tell you who is standing next to them.**

EntraGuard places the verification call, asks about things only you could know from your own sign-in activity minutes earlier — and *listens to the room while you answer*. If somebody is coaching you through it, the verification is refused even though every digit was correct.

It plugs into Microsoft Entra ID as an **External Authentication Method**, so one Conditional Access policy makes it a second factor for **every application in a tenant** — Microsoft 365, the Azure portal, gallery SaaS, your own apps — with no change to any of them.

---

## Try it live

| | |
|---|---|
| 🏦 **[placeholders.my](https://placeholders.my)** | **Contoso Treasury** — a customer app that integrated EntraGuard. Sign in with a work account, move money, get called. |
| 🛡️ **[entraguard.my](https://entraguard.my)** | **Operator console** — live calls, risk trajectories, verification ledger. |
| 📖 **[docs.entraguard.my](https://docs.entraguard.my)** | **The handbook** — user manual, decision internals, full API reference, all nine flow diagrams. |
| 🔌 **[api.entraguard.my](https://api.entraguard.my/.well-known/openid-configuration)** | The **OIDC discovery document** Entra reads to use EntraGuard as an MFA method. |
| 💻 **[github.com/suzarilshah/EntraGuard](https://github.com/suzarilshah/EntraGuard)** | Source — 99 commits, 426 passing tests, MIT. |

*Work or school accounts only. EntraGuard authenticates people against **directory** facts, and a personal account has no directory behind it.*

---

## The attack this exists for

In 2023, attackers walked into MGM Resorts and Caesars by **phoning the help desk**. No malware, no zero-day. They knew enough about an employee to sound like them, and asked for an MFA reset. Scattered Spider has repeated it across dozens of organisations since.

Every control in that flow worked exactly as designed:

- Entra ID Protection saw a sign-in. It was normal.
- The MFA prompt was approved. By the real user.
- The help-desk agent followed procedure.

**Entra ID Protection sees the sign-in. It is blind to the phone call that caused it.** That call is where the compromise actually happens, and nothing in the identity stack is listening to it.

---

## Three things here that are not ordinary MFA

### 1. Questions an attacker could not have researched

Not "your mother's maiden name". EntraGuard reads *your own sign-in activity from the last few hours* and asks about it — which city you were in, which browser you used, who you last exchanged Teams messages with, which team you are in.

Nothing is stored, so there is nothing to breach. The answers expire by themselves. And an attacker who phoned you twenty minutes ago could not have prepared: **the question did not exist until the call started.**

### 2. It listens for coercion while you answer

Number matching proves the person holding the phone is the person at the browser. It cannot prove you are acting *freely* — somebody standing over you saying "press four seven" satisfies it perfectly.

So every three seconds, an Analyst scores the live transcript. Because the audio is **unmixed**, a second voice on your own line is visible as a second voice. Measured on a real coercion test on this deployment, with a "help desk" reading answers aloud:

```
17:02:54   risk=5    Unaware
17:03:17   risk=45   Engaged          ← "just say Kuala Lumpur, that's what it wants"
17:04:01   risk=85   AboutToApprove
```

The model never acts. It produces a `RiskAssessment`; a pure, exhaustively tested policy gate decides what is permitted. **Autonomy lives upstream; authority lives in `PolicyGate.cs`.**

![Monitored-call analysis and response](https://raw.githubusercontent.com/suzarilshah/EntraGuard/main/docs/diagrams/flows/06-monitored-call-response.png)

### 3. It is a real Entra authentication method, not an app integration

| | SAML IdP proxy | External Authentication Method |
|---|---|---|
| Apps to change | every one | **none** |
| In the sign-in path | always | only when a policy asks for a second factor |
| If it is down | nobody signs in | the policy fails; first factor untouched |
| Configuration | per application | one Conditional Access policy, tenant-wide |

![Entra External Authentication Method](https://raw.githubusercontent.com/suzarilshah/EntraGuard/main/docs/diagrams/flows/02-entra-external-authentication.png)

---

## The honesty that cost us a feature

Microsoft's `amr` vocabulary contains `vbm` — *"biometric with voiceprint"*. It describes this product almost too well, and **we do not send it.**

Voice runs in observe mode. It is scored, recorded and shown, and it cannot refuse anybody — the same enrolled speaker scored **0.278** on one call and **0.7401** on another. Sending `vbm` would tell Entra a biometric factor was verified, and Entra would grant MFA on the strength of it, while nothing about the voice can fail a sign-in.

So EntraGuard claims `tel` — confirmation by telephone, therefore *possession* — which is what ringing an enrolled endpoint and demanding a number match actually proves. One consequence: if your **first** factor was already possession-based (a passkey), Entra asks for inherence, a phone call cannot supply it, and EntraGuard **declines before ringing anybody** rather than interrupting you for a result that could never be accepted.

---

## Architecture

![EntraGuard logical architecture](https://raw.githubusercontent.com/suzarilshah/EntraGuard/main/docs/diagrams/flows/00-logical-architecture.png)

Nine step-by-step flow diagrams are published at **[docs.entraguard.my#diagrams](https://docs.entraguard.my#diagrams)** — the EAM handshake, verification decision logic, transaction-bound approval, monitored-call response, voice enrolment, and the durable evidence path.

![Verification decision logic](https://raw.githubusercontent.com/suzarilshah/EntraGuard/main/docs/diagrams/flows/03-shared-verification-decisions.png)

---

## See it work in three minutes, without a phone

Every simulation runs the **real** Analyst, the real policy gate, the real Actuator and the real Sentinel writes. Only ACS and Speech are bypassed, because the transcript is supplied rather than recognised.

```bash
export MEDIA="https://api.entraguard.my"

# The attack: help-desk impersonation, coached MFA approval
curl -s -X POST "$MEDIA/api/simulate" -H 'Content-Type: application/json' \
  -d '{"scenario":"helpdesk-fraud","subjectUpn":"demo.user@contoso.com","paceMs":900}'

# The control that matters more. Same surface features — a help desk, a password
# reset, urgency — and it MUST stay under 40. A detector you only ever watch fire
# is a detector you cannot evaluate.
curl -s -X POST "$MEDIA/api/simulate" -H 'Content-Type: application/json' \
  -d '{"scenario":"benign-helpdesk","paceMs":900}'
```

---

## What we measured on this deployment

| | |
|---|---|
| Call duration, 23 real calls | median **108s**, worst **156s** (Entra abandons a sign-in at ~300s) |
| Coercion test | risk climbed **5 → 85** as a scripted "help desk" fed answers |
| Voice, same enrolled speaker | **0.278** and **0.7401** on different calls — why it cannot gate anything yet |
| Unit tests | **426**, covering the policy gate, question selection, assurance and EAM claim mapping |
| Analyst latency | 5.6–9.9s, mean 6.9s on `gpt-5-mini` |

---

## Built on Azure

**Sixteen Azure resource types, no secrets in the identity path** — every Azure dependency authenticates with a user-assigned managed identity.

| | |
|---|---|
| **Communication Services** | Places and answers calls; unmixed bidirectional media over WebSocket |
| **Entra ID** | External Authentication Method, Conditional Access, cross-tenant Graph |
| **Azure OpenAI** | The Analyst — structured risk assessment every three seconds |
| **AI Speech** | Live transcription, per-channel, with phrase-list biasing |
| **Container Apps** | Five apps, three images; one image serves three products via `APP_MODE` |
| **Key Vault** | RS256 signing certificate for EAM responses — non-exportable, used through the managed identity |
| **Event Grid** | `IncomingCall` interception inside the ~30-second ring window |
| **Table Storage** | Receipts, devices, policy, encrypted voiceprint templates |
| **Log Analytics + Sentinel** | Verdicts, evidence, remediation and fault records |
| **Container Registry · Managed Identity · App Insights · DCE/DCR** | Build, auth and telemetry pipeline |

Plus **SpeechBrain ECAPA-TDNN** on an internal-ingress Container App for speaker embeddings — deliberately unreachable from the internet.

---

## What this does not do

Written plainly, because a security tool that overstates itself is the thing it is supposed to prevent.

- **Not a certified assurance level.** The `None/Low/Substantial/High` scale is EntraGuard's own, deliberately not labelled AAL or eIDAS.
- **Voice cannot refuse anybody.** It stays in observe mode until a genuine speaker reliably lands in the genuine band.
- **No presentation-attack detection.** Nothing here detects a voice clone.
- **A pass is not proof coercion was absent.** It is proof none was *detected*, at a stated confidence.
- **Reach is not coverage.** The method is available to every application in a consenting tenant, but EntraGuard still has to *phone the user* — which needs that tenant to allow-list the Communication Services resource.
- **State is in-process.** Verification records live in memory with five-minute retention.

The [threat model](https://github.com/suzarilshah/EntraGuard/blob/main/docs/standards-and-threat-model.md) lists what each signal contributes **and what it does not prove**, signal by signal.

---

## What building it taught us

**Degradation is invisible by construction.** The most expensive bug in this project was a `SPEECH_LANGUAGE` of `en-MY` — a reasonable-looking locale that Azure Speech does not have. The recogniser was rejected at connect, every spoken answer became "nothing heard", and three consecutive real callers were refused for questions they had answered correctly out loud. Nothing in the verdict, the record or the portal said recognition had died. That is why this codebase has a `Fault` type that will not let you record a failure without naming what the *user* experienced, the probable cause, and the next action.

**Causes that need opposite fixes must never look identical.** "Not consented" and "no mailbox" are both a missing question. "Never ran" and "scored zero" are both a zero. Each pair is now distinguishable in the record, because each pair has a different fix.

**Claiming a factor you do not enforce is the one unforgivable lie.** It would have been one string to send `vbm` and inherit an inherence factor we have not earned.

---

**Repository:** [github.com/suzarilshah/EntraGuard](https://github.com/suzarilshah/EntraGuard) · MIT
**Handbook:** [docs.entraguard.my](https://docs.entraguard.my)
