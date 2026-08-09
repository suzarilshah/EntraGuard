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
