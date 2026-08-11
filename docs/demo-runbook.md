# Demo runbook

> **Telephony reality.** No PSTN number is provisioned. ACS number purchase returns
> `403 InsufficientPermissions` on this subscription, and ACS does not sell outbound-capable
> numbers in Malaysia at all — both verified against the live API. Verification calls are
> therefore placed to an ACS identity over VoIP. Opening `/phone` on a real handset makes
> the call ring **on that handset**; it travels over data rather than the carrier network.
> Everything else — the call, the audio, the DTMF, the Analyst monitoring — is real.

## Step-up verification demo (`/app`)

1. **Desktop** → `/app` → sign in as `demo.user@contoso.com` / `Passw0rd!`
2. **Set up verification** → **My phone** → scan the QR with the phone camera
3. **On the phone** → *Connect this device* → allow the microphone
4. Wait for **Continue** to become enabled. It stays disabled until the handset reports
   presence — if it never enables, the phone is not registered and no call will be placed.
5. **Continue** → the phone rings → answer → enter the two digits shown on the desktop

The desktop shows live call progress driven by real ACS callbacks:
`Calling → Answered → Listening for your number → Checking`. **If it stalls, the step it
stalls on is the fault** — stuck on *Calling* means the handset is unreachable, stuck on
*Answered* means the challenge failed to start.

**Coercion beat:** have someone say *"just press four seven"* while the call is live. The
correct code is entered and access is refused anyway.

### If the room's network is hostile

Run the simulator instead — **Live calls → Test the pipeline**, or
`POST /api/verify/simulate {"scenario":"coerced"}`. It exercises the same Analyst, policy
gate, Actuator and Sentinel writes, deterministically, with no microphone or wifi
dependency. Sessions are labelled **Simulated** so it is never mistaken for a live
interception.


Four minutes, two browser tabs, one scripted call.

## Before you start

```bash
./scripts/smoke-test.sh
```

Everything must be green. If the media service is not reachable, Event Grid will not
deliver `IncomingCall` and nothing downstream happens — with no error visible on stage.

Have open:

| Window | URL | Why |
|---|---|---|
| **A** | `https://<portal-fqdn>/live` | The main screen. Everything happens here. |
| **B** | `https://<portal-fqdn>/sentinel` | Switch to it at the end for the incident. |
| Terminal | `az containerapp logs show -n ca-entraguard-media -g rg-entraguard-demo --follow` | Backup evidence if the UI stalls. |

Check the header chip on **A**. It reads `Degraded remediation` on a tenant without Entra
ID P2 — that is expected, and you should mention it rather than hope nobody asks.

---

## The run

### 0:00 — Frame it (20 seconds)

> "Every major identity breach in the last three years started with a phone call, not a
> zero-day. Entra ID sees the sign-in. It cannot see the conversation that caused it.
> That's the gap."

Screen **A** shows *No call in progress*. The system is listening.

### 0:20 — Place the call

Call the monitored ACS identity. Interception starts the moment it rings — you do not
press anything.

Within two seconds the transcript panel starts filling. **Say this out loud:** nothing here
is pre-recorded; ACS is streaming the audio to a WebSocket and Azure AI Speech is
transcribing per participant channel.

### 0:30–2:30 — Run the script

Read the caller lines from [`fixtures/transcripts/helpdesk-fraud.md`](../fixtures/transcripts/helpdesk-fraud.md).
Have a second person read the user lines, or read both.

Watch the meter climb through the bands. Call out what is happening:

| Beat | What to point at |
|---|---|
| Caller claims to be IT | `authority_impersonation` chip appears; risk enters *Elevated* |
| "Don't hang up, this is time-sensitive" | `urgency_pretexting`; the Analyst quotes it verbatim in Evidence |
| "You'll see a prompt — just approve it" | `mfa_fatigue_coaching`; stage moves to **about_to_approve** |
| **Stage flips** | **This is the moment.** Same evidence, more urgency, because the window is closing. |
| Risk crosses 80 | Policy Gate authorises containment. The Actions panel starts filling. |

### ~2:00 — The intervention

You will **hear** it: EntraGuard synthesises a warning and streams it back into the live
call, over the top of the attacker.

> "That warning went back down the same WebSocket the call audio arrives on. The user hears
> it while the scammer is still talking. Everything else we do is invisible to the person
> being manipulated right now."

### 2:30 — The Policy Gate panel

This is the part that separates it from a transcript classifier.

> "The model proposed remediation. It did not perform it. A deterministic gate decided what
> was allowed — and it withheld things."

Point at a withheld line. On a non-P2 tenant you will have at least one:

> *withheld ElevateUserRisk — Tenant lacks Entra ID P2…*

> "That's real. This tenant has no P2, so `confirmCompromised` would 403. We don't fake it.
> Remediation falls through to session revocation and Conditional Access quarantine, and
> the ledger records the attempt with its actual outcome."

### 3:00 — Sentinel

Switch to window **B**. The incident is there, with evidence quotes and the affected
account attached.

> "This lands in the same workspace as the rest of the estate — so a scam call is
> correlatable with the sign-in it was trying to subvert, not stranded in a tool of its own."

### 3:30 — Close

> "Detect to remediate, under thirty seconds, inside the attack rather than after it. Entra
> ID, Communication Services, AI Speech, Azure OpenAI, Sentinel. No third-party services,
> no client secrets — managed identity end to end."

---

## Questions you will get

**"What stops it locking out innocent users?"**
The Policy Gate. Irreversible actions need ≥75% analyst confidence; terminating a call
needs near-certainty *and* an imminent approval. The prompt carries an explicit benign
baseline, because legitimate help-desk calls and social engineering share nearly every
surface feature — the discriminator is whether the caller is steering the user toward an
irreversible credential action they did not initiate. Show
[`PolicyGateTests.cs`](../tests/EntraGuard.UnitTests/PolicyGateTests.cs) if pressed; the
decision matrix is tested at every boundary.

**"Is the transcription real?"**
Open the terminal window. `MediaStreamingStarted`, then per-frame processing. Or run the
KQL on the Sentinel page — those rows were written by the Logs Ingestion API during the
call you just watched.

**"Could the attacker just avoid the trigger phrases?"**
The Analyst reasons over the conversation; it is not keyword matching. The phrase list only
biases *recognition* toward vocabulary that is easy to mis-transcribe. That said — yes, an
adaptive attacker is the honest limitation, and speaker verification against an enrolled
voiceprint is the next layer rather than more prompt tuning.

**"Why not Azure Functions?"**
Microsoft's own guidance: a call rings for 30 seconds and consumption-plan cold start can
eat that window. Functions also cannot accept an inbound WebSocket upgrade, which the media
stream requires.

**"What about consent and privacy?"**
Real deployment needs call-recording notification, which ACS supports natively. Full
transcripts go to blob storage with a retention policy; Sentinel receives only the
structured verdict and evidence spans — a SIEM is the wrong place to accumulate raw
conversation.

---

## If the demo breaks

**No transcript appears.** Check `MediaStreamingFailed` in the logs. Almost always ACS
subcode 8581 — `PUBLIC_BASE_URL` is not reachable over `wss`. Not fixable on stage; switch
to the recording.

**Risk stays at zero.** The Analyst needs ~40 characters before it will score. Keep talking.
If it persists, Azure OpenAI is likely rate-limited — check the logs and narrate the
recorded run instead.

**Call never connects.** Event Grid did not deliver. `az eventgrid system-topic
event-subscription show` to confirm the subscription still points at the current FQDN — it
breaks if the container app was recreated after `04-eventgrid-subscribe.sh` ran.

Keep a screen recording of a successful run on the laptop. Conference wifi is the single
most likely thing to end this demo, and it is not worth improvising through.

---

## Pre-flight: prove it works before anyone watches

Three commands, no phone, about two minutes. Each answers a different question, and if any
fails the demo will fail in the same place.

```bash
# 1. Is the scorer alive and is storage ready?
curl -s https://$MEDIA_SERVICE_FQDN/api/voice-profile/selftest | python3 -m json.tool

# 2. Does the model separate speakers, and are the thresholds still right?
./scripts/07-voice-calibration.sh

# 3. Does the whole enrolment pipeline work — gates, template, encryption, scoring, deletion?
curl -s -X POST https://$MEDIA_SERVICE_FQDN/api/voice-profile/rehearse --max-time 300 \
  | python3 -m json.tool
```

Expected: `selftest` reports 192 dimensions and `storageReady: true`; calibration shows
genuine and impostor distributions that do not overlap; the rehearsal returns
`"passed": true` with all eight steps green.

The rehearsal covers everything except ACS itself — placing the call, PlayCompleted timing,
the media socket. Those only a live call can prove, which is why they are step 3 of the
live test below rather than assumed.

## Live test order

Run these in order. Each depends on the one before, so a failure early makes the later
results meaningless.

1. **Sign-in** — Treasury hostname, Microsoft work account. Confirms multitenant SSO.
2. **Verification without voice** — the flow that already works: code, identity questions,
   verdict. Establishes the baseline before voice is involved.
3. **Voice enrolment** — Settings, consent, three phrases. The one path never executed live.
4. **Verification with voice** — same as 2, now scored. `VOICE_MODE=observe`, so the score
   is recorded and changes nothing.
5. **Impostor** — somebody else answers on the enrolled user's device. Compare their score
   to the genuine one from step 4.
6. **Deletion** — Settings, delete, confirm it is gone.

Only after step 5 has produced real numbers is there any basis for `VOICE_MODE=enforce`.
