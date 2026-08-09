# EntraGuard — pitch

## The gap

Every large identity intrusion of the last three years started the same way: not with a
zero-day, but with a phone call. Someone rang a help desk and impersonated an employee, or
rang an employee and impersonated the help desk. MFA held. The human did not.

Microsoft Entra ID has excellent telemetry on this — after the fact. It sees the sign-in,
the impossible-travel signal, the MFA approval. What it cannot see is the sixty seconds of
conversation that *caused* the approval, because that conversation happens on a channel no
security product is watching.

**The voice channel is the last unmonitored side-channel into an otherwise well-defended
identity plane.** EntraGuard closes it.

---

## Four angles

### 1. Agentic autonomy with a deterministic safety rail

Most "agentic security" demos hand a model a set of tools and hope. The interesting
question is not *can an LLM decide to lock out a user* — it obviously can. It is *should it
be allowed to, and how do you prove what it was and was not permitted to do*.

EntraGuard separates the three responsibilities that usually get collapsed:

| Layer | Nature | Failure mode it owns |
|---|---|---|
| **Analyst agent** | Model-driven, fallible | Misreads a conversation |
| **Policy Gate** | Deterministic, pure, exhaustively tested | Nothing — it is the invariant |
| **Actuator agent** | Mechanical, ordered, audited | An API call fails |

The Analyst never acts. It produces a verdict; the gate decides what may happen. The gate
is ~200 lines of pure C# with no I/O, no clock, and a test for every boundary — small
enough that a security reviewer can read all of it and know what the system can do to an
account.

Concretely: irreversible actions require ≥75% analyst confidence. Hanging up a call
requires *both* near-certainty *and* a victim seconds from approving — high risk alone is
never enough, because while there is still time to warn someone, warning them is strictly
better than cutting them off. Every action the risk level warranted but policy withheld is
recorded with its reason.

That is the difference between an agent with autonomy and an agent with authority. Ours has
the first and not the second.

### 2. Preventive, not forensic — the compliance-stage signal

A risk score says *this is an attack*. It does not say *how long until it succeeds*.

The Analyst reports both. Alongside risk it emits a compliance stage — `unaware`,
`engaged`, `about_to_approve`, `approved` — describing how far the victim has been drawn
in. That drives urgency independently of confidence: identical evidence justifies more
force when approval is seconds away, because the window is closing.

It also drives restraint in the other direction. Once the victim has already complied,
EntraGuard stops proposing to terminate the call — hanging up prevents nothing at that
point and destroys evidence. It contains instead.

**Measured latency, on the deployed system:**

| | |
|---|---|
| Analyst assessment | **5.6–9.9 s**, mean **6.9 s** |
| Effective scoring cadence | ~7 s (configured 3 s; the model is the floor) |
| First high-risk verdict | within ~3 exchanges of the coaching starting |

These are numbers from `gpt-5-mini` on the demo tenant, not projections. At default
reasoning effort the same assessment took **15–16 seconds** — long enough that a warning
would arrive after the victim had already approved. Dropping to low reasoning effort is
what makes the system preventive rather than forensic, and it is the single most
consequential tuning decision in the build.

Being precise about it: detect-to-remediate lands in the **10–20 second** range, not the
sub-5-second range. That is inside a two-minute social-engineering call, which is what
matters — but it is not instant, and a faster path (streaming partial verdicts, or a
smaller model for a first-pass triage) is the obvious next optimisation.

### 3. The intervention the user actually experiences

Revoking sessions, elevating risk, raising an incident — all invisible to the person being
manipulated right now.

ACS bidirectional audio streaming means EntraGuard can synthesise speech and stream it back
into the live call. The user hears a warning while the attacker is still mid-sentence. The
script is deliberately plain and instruction-led rather than alarming: the listener is
already being told a confident story by someone who sounds authoritative, and competing on
urgency loses. It names the specific actions to refuse, then tells them to hang up and dial
a number they already know — the one instruction that defeats the attack regardless of how
the rest of the call goes.

This is also why the warning is classified as *reversible* and therefore not
confidence-gated. Interrupting a legitimate support call with a caution costs a moment of
confusion. Staying silent through a real attack costs the account.

### 4. Composed entirely from the Microsoft platform

No third-party dependency anywhere in the detection or response path.

| Layer | Service | Doing the real work |
|---|---|---|
| Identity | **Microsoft Entra ID** + ID Protection | Auth, and the risk state EntraGuard writes |
| Telephony | **Azure Communication Services** | Call Automation, unmixed bidirectional media |
| Perception | **Azure AI Speech** | Continuous recognition, per-channel, phrase-biased |
| Reasoning | **Azure OpenAI** | Structured-output risk assessment with evidence |
| SIEM | **Microsoft Sentinel** | Incidents, KQL, analytics rule, correlation |
| Runtime | **Azure Container Apps** | WebSocket ingress, managed identity end to end |

Every credential is a managed identity. There is no client secret in this system — nothing
to rotate, nothing to leak, nothing to commit by accident.

---

## Two engineering decisions worth defending

**Unmixed audio, not mixed.** ACS unmixed streaming gives one channel per participant with
a `participantRawID`. Speaker attribution therefore comes from the *transport*, not from a
diarisation guess. That matters more than it sounds: "read me the code" from the caller is
elicitation; the same sentence from the user is confusion. Getting attribution from the
media stream is what makes acting on it defensible.

**Container Apps, not Functions.** Microsoft's own Call Automation guidance warns that a
call rings for ~30 seconds and consumption-plan compute can spend that window cold-starting.
Functions also cannot accept an inbound WebSocket upgrade at all. `minReplicas: 1` is a
correctness requirement here, and it is commented as such so nobody later "optimises" it to
zero.

---

## Measured detection results

Four scripted conversations replayed through the deployed pipeline — the real Analyst
deployment, the real policy gate, the real Sentinel writes:

| Scenario | Peak risk | Confidence | Vectors detected | Acted? |
|---|---|---|---|---|
| Help-desk impersonation | **100** | 0.90 | 5, incl. `otp_elicitation`, `mfa_method_registration` | Yes |
| Remote-access tooling | **100** | 0.90 | 3, incl. `remote_access_tooling` | Yes |
| **Legitimate help-desk call** | **15** | 0.90 | none | **No** |
| **Ambiguous support call** | **10** | 0.90 | none | **No** |

The bottom two rows carry the most weight. Both have every surface feature of the attack —
an authoritative IT voice, MFA vocabulary, technical language, mild time pressure — and both
score in the teens with no action taken. A detector you only ever watch fire is a detector
nobody can evaluate, and EntraGuard's false positives land on people who have done nothing
wrong.

The separation is clean: 100 versus 15, with no overlap.

## What we will not claim

The demo tenant has no Entra ID P2, so `confirmCompromised` returns 403 and real risk
elevation does not fire. EntraGuard was built for that from the start: remediation
degrades to session revocation, Conditional Access quarantine, and a Sentinel incident,
and the portal states the limitation in the API's own words rather than showing a green
tick.

We are showing you that on purpose. A hackathon demo that fakes a successful risk elevation
falls apart under the first informed question, and the degradation ladder is the part of
this design most likely to survive contact with a real tenant.

---

## Where it goes

- **Teams interop.** ACS already federates with Teams. The same interception applies to
  internal help-desk calls, which is where the actual attack surface is.
- **Speaker verification.** Azure AI Speaker Recognition against an enrolled voiceprint
  turns "is this call a scam" into "is this caller who they claim to be".
- **Help-desk side deployment.** The strongest position is not on the victim's phone, it is
  on the help desk's — where one agent takes hundreds of calls and every one is an
  identity-verification decision.
- **Feeding Entra ID Protection as a signal source.** Today EntraGuard reacts to risk.
  The end state is voice-channel evidence becoming a first-class risk detection type
  alongside impossible travel and anonymous IP.
