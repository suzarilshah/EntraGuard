# What the call actually proved

> Historical design. Assurance fields and the readiness endpoint are implemented. The newer profile question pool still needs source-aware alignment with that scale; this document's original semantics are not proof that current readiness covers every source. See [architecture](../architecture.md).

*Design, 2026-09-15*

## The problem

`Passed` means one thing today and covers four very different events.

A user in a tenant with Entra ID P1, who answered two live telemetry questions, confirmed a
follow-up, and matched their enrolled voice, gets `Passed`.

A user in a tenant with no P1, who has never enrolled a question, gets `Passed` for keying two
digits — because [`VerificationCoordinator`](../../src/EntraGuard.MediaService/Sessions/VerificationCoordinator.cs)
returns `false` when there is nothing to ask, and `false` skips the knowledge factor entirely.

That skip is correct. Inventing a factor somebody never enrolled in locks out everyone who
did not, and `/v1.0/auditLogs/signIns` is a premium endpoint, so every tenant without P1
yields nothing. What is wrong is that the two calls are indistinguishable to the relying
party deciding whether to move money.

## The answer: report what was proved, decide nothing

`VerificationRisk` already answers *"how much did this look like an attack?"* and changes no
access decision. Nothing answers *"how much did we actually prove?"*.

`AssuranceLevel` answers that, on the same terms: deterministic, model-free, and with no
authority of its own. Voice established the pattern — it never denies access alone, it asks
for a stronger factor. This is the same shape, generalised.

| Level | What it means |
|---|---|
| `None` | The verification did not pass. |
| `Low` | Possession only. The number match was the whole check; no question was asked. |
| `Substantial` | Possession plus knowledge — but a stored secret, or telemetry with nothing corroborating it. |
| `High` | Possession plus live telemetry answered, plus a second signal: a confirmed probe or a voice match. |

EntraGuard's own scale, deliberately not labelled AAL or eIDAS. Borrowing those names would
claim conformance nobody has assessed.

## The pre-call half: ask before you call

If a call can only reach `Low`, the relying party should learn that **before** placing it, not
after. `GET /api/verify/readiness/{tenantId}/{objectId}` reports the best level a call could
currently reach and what is missing to do better.

That is the right answer to "what must the user provide before the call": nothing is demanded
at call time, and the relying party is told in advance what a call is worth, so it can send
somebody to enrolment instead of spending a phone call to prove two digits.

It extends the existing `telemetry-probe` diagnostic, which already reports whether live
questions are available — but returns the question TEXT, which is fine for an operator
debugging and wrong for a pre-call contract. Readiness returns levels and gaps, never
questions.

## What does not change

The verdict. `GrantsAccess` is still `Result == Passed`, decided by `VerificationAdjudicator`,
which does not see this. Assurance is recorded, surfaced and reported. A relying party may
refuse a `Low` call for a large transfer; that is its decision and its policy, taken with
information it does not have today.
