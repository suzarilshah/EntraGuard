# Conversational verification

> Historical design proposal, not current operating guidance. Much of the coordinator/follow-up work is implemented; the optional realtime agent is now constrained to supplied prompts. See [current architecture](../architecture.md) and [README](../../README.md) for actual behavior and remaining gaps.

*Design, 2026-09-13*

## The problem

The verification call sounds like a machine because it is one. [`VoiceAgent`](../../src/EntraGuard.MediaService/Agents/VoiceAgent.cs)
reads a question word for word, says "Thank you. Your response has been recorded," and
stops. A user who says "sorry, what?" or "I wasn't expecting a call" gets the question
again, unchanged.

That terseness is not an oversight. Every sentence a model may improvise is a sentence a
caller can steer, and the guardrail catches the answers, not the steering. So the question
is not whether the agent can be chatty. It is what the chattiness buys.

It buys three things:

- A call people do not dread, which is what keeps the factor switched on.
- More speech for the Analyst to score for coaching.
- Evidence the scripted call cannot produce, described below.

## 1. Authority stays where it is

The model proposes; a deterministic component disposes. That rule is why
[`VoiceGuardrail`](../../src/EntraGuard.Shared/Voice/VoiceGuardrail.cs) and
[`PolicyGate`](../../src/EntraGuard.Shared/Policy/PolicyGate.cs) are defensible. A chatty
agent widens the attack surface, so it needs the rule more, not less.

A third deterministic component joins them: the **ConversationDirector**. It owns
turn-taking. After each scored answer it emits the next move:

- ask the primary question
- ask a follow-up, and which one
- accept and close
- hand off to the gate

The agent never chooses to go deeper. It receives one move and renders it in natural
language. Chattiness describes the surface of a turn. The shape of the call stays
deterministic, bounded, and testable.

The agent's instructions become a persona plus hard limits; a per-turn message carries the
current move. The agent is still never given the match code, the expected answers, or the
risk score. Not instructed to withhold them — never given them.

## 2. Follow-ups recover the assurance that leniency spends

The location question accepts city, state, or country, and
[`TelemetryChallenge`](../../src/EntraGuard.MediaService/Agents/TelemetryChallenge.cs)
explains why: Entra records the city an IP resolves to, nobody says their suburb aloud, and
refusing a true answer refuses the genuine user.

That leniency is correct and expensive. "Malaysia" is true and nearly worthless — a caller
who dialled a `+60` number can produce it. The question passes either way, so strong
evidence and worthless evidence collapse into one boolean.

The follow-up reopens that gap. When the caller satisfies the question only at its coarsest
level, the agent asks for the granularity the leniency gave away:

> "Malaysia, got it — whereabouts, roughly?"

A real person answers without thinking. Someone working from a phone prefix has nothing.
The follow-up cannot refuse an honest user, because that user has already passed.

Three facets, in strength order, all precomputed from the sign-in record already fetched:

| Facet | Follow-up asks | Ground truth | Entropy |
|---|---|---|---|
| Location | the finer place, when they gave the coarse one | `City`, `State` | high |
| Device | the half they did not say | `Os`, `Browser` | medium |
| Time | roughly what time of day | `At` | low |

Low entropy is safe here, and only here. See section 3.

A follow-up is generated during the call from a sentence produced during the call. Nothing
researchable contains it, no recording predates it, and an accomplice who missed the earlier
turns cannot supply it. One mechanism closes three attack classes, and the third is the one
the ECAPA voiceprint explicitly cannot close.

The Director picks a facet the caller has not already covered. No model invents a question.
[`TelemetryChallenge`](../../src/EntraGuard.MediaService/Agents/TelemetryChallenge.cs) warns
what happens when one does: a question with no single correct answer, or a question that
leaks its own answer, and every such case is a legitimate user refused.

### Why the question bank does not grow

Stored security questions stay at one, as a fallback for tenants with no P1 and no consent.

Growing them to fifteen or twenty looks like more security and is less.
[`KnowledgeStore`](../../src/EntraGuard.MediaService/Sinks/KnowledgeStore.cs) writes
`verificationAnswer` in plaintext, because the judge needs the answer recoverable to accept
"Saint Mary's" for "St Mary's". That trade is defensible once per user. Twenty times per
user builds a readable database of pets, schools, and maiden names — reused at every user's
bank, and impossible to rotate.

It also strengthens the wrong half of the threat model. Knowledge questions defend against
an impostor. They do nothing against coercion, where the real user answers correctly
because they believe they are talking to IT support. Coercion is the threat this product
exists for.

Two primary questions are enough. New facets arrive as follow-ups.

## 3. Follow-ups raise confidence and never refuse

The primary question keeps its authority: it can fail a verification. A follow-up cannot. A
wrong or vague follow-up feeds `RiskScore` and may set `RequiresStepUp`. Nothing else.

Voice already works this way — it never denies access alone, it asks for a stronger factor.
The reasoning applies harder here, because people genuinely forget whether they used Edge or
Chrome at eight in the morning. A factor that refuses correct users is a factor that gets
switched off.

This is also what makes the weak facets usable. A question that can only add confidence
costs nothing when it is easy to guess.

## 4. Two registers, not a gradient

The agent has two modes, `Warm` and `Protective`, and it chooses neither. The register is
handed to it, and the gate authorises the transition.

Warmth tracking the risk score would demonstrate well and fail in the field. It turns the
agent into a live readout of the detector: a scammer runs three calls, learns which phrasing
turns the voice cold, drops it, and leaves the detector blind to exactly the tactics that
work.

Two discrete states leak only at the moment the gate has already decided to act — and the
spoken warning exists precisely because the victim is in the room now, and a warning they
can hear beats a tell kept in reserve.

Below the threshold, rising risk buys more conversation, not colder conversation. Another
follow-up reads as ordinary thoroughness, because that is what it is, and every extra second
of speech feeds the Analyst.

## 5. Budget

At most two follow-ups, inside the wall-clock allowance
[`VerificationCoordinator`](../../src/EntraGuard.MediaService/Sessions/VerificationCoordinator.cs)
already computes. A genuine user's call must not run longer because a model found them
interesting.

## 6. Audit trail

`VerificationSession` records how deep the conversation went, which follow-ups were asked,
how each was scored, and when the register changed. The trail should answer "why did this
call take ninety seconds?" without anyone reading container logs from the right replica at
the right moment.

## Out of scope

A **duress answer** — a second registered answer meaning "I am being made to do this",
returning `BlockedCoercion` while the call sounds normal — is the one enrollment addition
that attacks the threat a question bank cannot. It has real failure modes: people forget it
under stress, and it is another plaintext secret. It deserves its own decision.
