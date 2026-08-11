# Architecture

## Interception path

```
 1.  Attacker calls the monitored ACS identity
 2.  ACS → Event Grid: Microsoft.Communication.IncomingCall
 3.  POST /api/events/incoming-call  ── must AnswerCall within the ~30s ring window
 4.  AnswerCall(MediaStreamingOptions { Unmixed, Bidirectional, Pcm24KMono, Dtmf })
 5.  ACS dials back:  WSS /ws/media/{sessionId}
 6.  PCM frames → per-channel SpeechRecognizer → attributed transcript
 7.  Every 3s:  transcript window → Analyst (Azure OpenAI, structured output)
 8.  RiskAssessment → PolicyGate → ordered action list
 9.  ActuatorAgent executes; each outcome recorded verbatim
10.  Warning audio streams back down the same socket in step 5
```

Steps 3 and 4 are latency-critical. Nothing slow belongs between them — no database
lookups, no Graph calls. Subject resolution happens after the call is up.

## Component responsibilities

| Component | Owns | Deliberately does not |
|---|---|---|
| `IncomingCallEndpoint` | Answering fast, deduping, speaker attribution from call topology | Any analysis |
| `MediaSocketEndpoint` | Frame assembly, feeding recognition, hosting the analysis loop | Deciding anything |
| `PerceptionAgent` | Audio → attributed text, one recogniser per channel | Judging content |
| `AnalystAgent` | Text → risk, evidence, compliance stage | Choosing an action |
| **`PolicyGate`** | **What may happen** | Any I/O — it is pure |
| `ActuatorAgent` | Executing in order, recording real outcomes | Re-litigating the decision |

The split between Analyst and Gate is the load-bearing design decision. The model has
autonomy; it does not have authority. If the Actuator could override the gate, "an LLM
decided to lock out this user" would be a true statement about the system.

## The three concurrency paths

They run simultaneously on one call and must not block each other:

1. **Receive loop** — pulls frames off the WebSocket. Blocking here backs up audio and the
   transcript falls behind the live conversation.
2. **Analysis loop** — `PeriodicTimer`, every 3s. Model latency lives here, off the media path.
3. **SignalR fan-out** — fire-and-forget to the portal. A slow browser must never affect a call.

Session state is `ConcurrentQueue` / `ConcurrentDictionary`, with executed actions behind a
lock. `TryMarkExecuted` returns false on the second caller, which settles the race when two
assessments land close enough together to propose the same action.

## Why in-process state is acceptable

Container Apps runs with `stickySessions: sticky` and `minReplicas: 1`, so a call's
WebSocket, analysis loop, and remediation all land on the replica that answered it. History
goes to Table Storage and Log Analytics; a restart loses in-flight calls but no record.

Scaling past this means Azure SignalR Service for the fan-out and a distributed session
store. Neither changes the component boundaries.

## Data flow and where PII lives

| Data | Destination | Why there |
|---|---|---|
| Structured verdict + evidence spans | `EntraGuard_CallAnalysis_CL` | Sentinel correlation, KQL |
| Remediation attempts and outcomes | `EntraGuard_Remediation_CL` | Audit, including refusals |
| Full transcript | Blob storage | Too large and too sensitive for a SIEM |
| Live transcript | SignalR only, never persisted | Ephemeral by design |

Sentinel gets the verdict, not the conversation. A SIEM is the wrong place to accumulate
raw call content — both for ingestion cost and because retention policy there is set for
security telemetry, not for recordings of people's phone calls.

## Authentication

Every Azure dependency uses the user-assigned managed identity. There is no client secret
in the system.

| Dependency | Grant | Scope |
|---|---|---|
| ACS Call Automation | Contributor | The ACS resource only |
| Azure AI Speech | Cognitive Services Speech User | The Speech account |
| Azure OpenAI | Cognitive Services OpenAI User | The OpenAI account |
| Logs Ingestion | Monitoring Metrics Publisher | **The DCR only** — two streams, nothing else |
| Log Analytics | Log Analytics Reader | The workspace |
| Sentinel | Microsoft Sentinel Contributor | The workspace |
| Microsoft Graph | Six app roles | Tenant (see `02-entra-apps.sh`) |

Local auth is disabled on both Cognitive Services accounts and shared-key access is
disabled on the storage account, so there is no key path to fall back to — and no
connection string that could exist to be leaked.

## Failure behaviour

Nothing in the analysis path may take down a call:

- Analyst failure → previous verdict stands, next pass retries in 3s
- Telemetry failure → logged, call continues; a dropped row costs a Sentinel gap
- Malformed frame → `UnknownFrame`, ignored; an exception here drops a live call
- Graph 403 → `Unavailable`, ladder continues to the next rung
- Speech cancellation → logged per channel; other channels keep recognising

The distinction between `Failed` and `Unavailable` is deliberate. `Unavailable` is a known
limitation — no P2, no resolved subject, no live media. `Failed` is a fault. The portal
renders them differently because an operator can act on one and not the other.

## Extension points

- **New remediation** — implement `IRemediationTool`, register it, add a gate rule and its
  tests. No dispatch switch to edit.
- **New scam vector** — add to `ScamVector`, the prompt's schema enum, and the wire-name map.
- **Different model** — `AOAI_DEPLOYMENT`. Preflight already picks the best available.
- **Shadow mode** — `ENTRAGUARD_SHADOW_MODE=true`. Full pipeline, no outward action, the
  gate records everything it would have done. This is how you would pilot it in a real tenant.

---

## Voice biometrics

The fourth factor, and the only one that speaks to *who* is on the call rather than what
they hold or know.

```
enrolment (once)                     verification (every call)
  Entra SSO + MFA                      answers to the identity questions
  explicit versioned consent           are already speech — scored passively,
  ACS call, 3 random phrases           no extra prompt, no extra time
  quality + consistency gates                 │
  template, encrypted, stored                 ▼
  raw audio discarded            cosine vs enrolled template → three bands
```

**SpeechBrain ECAPA-TDNN** (Apache 2.0) in a Python sidecar on internal ingress, model baked
into the image. It embeds and compares; it decides nothing. The decision lives in the media
service where it is auditable.

**Three bands, not two.** Accept 0.60, reject 0.35, and a middle band that escalates to
interactive Entra re-authentication instead of guessing. Over a phone codec the genuine and
impostor distributions overlap, and a single threshold forces every ambiguous call into
either admitting a stranger or locking the owner out of their own money.

**Measured, not inherited.** `scripts/07-voice-calibration.sh` synthesises several neural
voices, treats each as a speaker, and reports same-speaker against different-speaker scores.
On this deployment: genuine 0.652–0.865, impostor −0.039–0.297, margin 0.355. The enrolment
rehearsal additionally scores an impostor speaking the *same sentence* as the enrolled
speaker — 0.879 versus 0.011, which is what shows the model keys on the voice and not the
words.

**Why it cannot deny access.** Published equal error rates come from studio recordings. A
weak match asks for a stronger factor; only failing *that* refuses. `VOICE_MODE` stays
`observe` — scores recorded to Sentinel, nothing acted on — until real calls justify
enforcement.

**What is stored.** A 192-dimension unit vector, AES-GCM encrypted, on a storage account
that already refuses shared-key access. Never the audio: an embedding cannot be replayed as
speech, a recording can. Consent is versioned, deletion is immediate and user-initiated, and
an absent profile is invisible to the user.

**What it does not defend against.** SpeechBrain has no anti-spoofing. A high-quality clone
would score as the speaker. What limits replay is that the challenge is unpredictable — an
attacker cannot pre-record an answer to a question that did not exist until the call began.
