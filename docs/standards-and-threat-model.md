# Security boundaries, standards and threat model

Source review: 20 September 2026. This is an implementation inventory and threat model, **not a certification, legal determination or claim of NIST/eIDAS assurance conformance**. Older versions overstated guarantees and described some planned controls as complete.

## Signals used by the prototype

| Signal | What it contributes | What it does not prove |
|---|---|---|
| Number match | Links knowledge of the displayed digits to an entry on the call/device path | That the browser/device is uncompromised, the API caller is authorized, or the user is acting freely |
| Sign-in and activity questions | Checks recall against available account/activity facts | An independent certified authenticator or protection from a researched/account-compromising attacker |
| Directory/profile questions | Adds context such as manager, office or direct reports | Secrecy: these facts can be public or researchable |
| Speaker comparison | Similarity to an enrolled ECAPA-TDNN template | Liveness, absence of a clone or absence of coercion |
| Analyst coercion assessment | Evidence of manipulation that deterministic rules can act on | That an unflagged or unassessed call is safe |

The `None/Low/Substantial/High` scale is EntraGuard's own scale, not a certified assurance standard. It now derives from actual asked/correct source evidence, with directory-only evidence staying Low. Tenant policy enforces minimum level, freshness, channels and optional Analyst presence against durable receipts.

## Implemented controls

- Deterministic remediation thresholds, confidence checks and recorded withheld actions.
- Separate verification adjudicator; detected coercion can refuse correct digits. The coordinator checks during answer waits and final adjudication.
- Number-match attempt limits and rate-limited start/enrollment routes. Partitioning and distributed abuse resistance still require review.
- Token-authenticated voice-profile ownership and MFA evidence for enrollment by default.
- Versioned consent, three-phrase enrollment consistency checks, encrypted stored voice templates and user-initiated deletion.
- Internal ingress for the separately deployed speaker service.
- Azure managed-identity data access, disabled shared-key Storage access in Bicep, and scoped Azure roles.
- Default redaction of match codes in common list/broadcast projections.

Controls must be evaluated together with the gaps below. UI sign-in and client-side filtering do not authorize the underlying API.

## Current gaps and limitations

### Authorization, ownership and replay

The Treasury migration enforces validated tenant/object ownership, revocable sessions, exact tenant issuers, API scopes and role-gated operator access. Match-code polling requires both the requesting session and viewer token. Grants require a durable receipt for the same session and policy. Conditional approval checks include payment digest, receipt consumption, session and policy versions. These boundaries have automated tests but still require independent review and live rollout validation.

ACS callbacks/media now use path-bound HMAC capabilities with expiry; Event Grid has a separate webhook secret. These are application-issued capabilities, not provider-signed JWTs. Keys and callback URLs must be protected, request-query credentials are redacted from telemetry, and rotation invalidates outstanding URLs. `MEDIA_WS_SIGNING_KEY` is obsolete; use `CALLBACK_SIGNING_KEY`.

### State and availability

Call sockets/coordinators remain process-local; durable sessions, presence, receipts and history use Table Storage. Media is pinned to one replica. Interrupted calls fail by deadline rather than resume. Distributed live-call routing/backplane and zero-downtime in-flight handoff remain unimplemented. Outbox delivery is at least once, so consumers must handle duplicate telemetry.

### Voice and coercion

The SpeechBrain model has **no presentation attack detection (PAD)**. A recording or high-quality clone may resemble the enrolled speaker. Random enrollment phrases and changing knowledge questions are not a validated liveness/PAD protocol. Real-telephony false-match/non-match rates have not been established by synthetic voice tests.

Voice observation is the default. Enforced weak scores produce `StepUpRequired`, which grants no access. The service must validate fresh same-owner MFA (`amr` and `auth_time`) before grant checks can succeed. Coercion/wrong-answer refusals cannot be overridden. Legacy `BlockedVoiceMismatch` remains denied.

The Analyst is fallible and may miss coaching or confuse legitimate speech. Unmixed ACS channels identify call participants, not every physical speaker near a microphone. A local coercer can share the protected user's channel.

### Stored data and confidentiality

- Voice templates are AES-GCM encrypted using `VOICEPRINT_KEY`; key lifecycle management remains an operational requirement.
- Registered knowledge answers are stored in readable form **and** as salted hashes. Older hash-only claims are incorrect.
- Log Analytics receives evidence and transcript excerpts (up to 4,000 characters). It is not exclusively metadata.
- Full transcript Blob archival and historical `Sessions` writers are not implemented despite provisioned resources.
- Raw enrollment audio is handled in memory, but embeddings remain sensitive biometric data; an embedding is not a harmless identifier merely because the API cannot play it as audio.

### Other boundaries

- Transaction-bound approval exists for the protected sample ledger only. Bank execution, settlement and dual approval are not implemented.
- Tenant-wide Entra integration is implemented as an External Authentication Method and is **off until configured**. It asserts `amr: tel` (possession, "confirmation by telephone") and never `vbm` ("biometric with voiceprint"), because voice runs in observe mode and cannot refuse a sign-in; asserting a factor that is not enforced would let Entra grant MFA on the strength of it. A sign-in whose first factor was already possession-based asks for inherence, which a call cannot supply, and is declined before anybody is rung.
- Conditional Access quarantine requires a policy targeting the group; group creation alone has no blocking effect.
- Repeated biometric/coercion refusal correlation is proposed; the existing Sentinel scheduled rule targets high-risk call analysis.
- Shadow mode still allows telemetry, SOC notifications and warranted incidents, and does not disable verification decisions.

## Standards and legal evaluation

NIST digital identity guidance, ISO biometric PAD evaluation and applicable privacy law are useful requirements inputs. This project has not demonstrated an AAL level, PAD certification, population-level false-match target or complete regulatory compliance. Dynamic knowledge questions should not be described as a NIST-approved exception merely because the source changes.

Enrollment consent/versioning, deletion and minimization are implemented mechanisms, not a complete legal basis analysis. Voiceprints can constitute special-category biometric data; deployments require context-specific assessment of lawful basis, retention, access, international transfers and data-subject rights. One-to-one matching alone is insufficient to declare an entire deployment outside all AI Act high-risk obligations.

## Validation priorities

1. Token/tenant/owner enforcement across APIs, callback authentication, replay protection and scoped event access.
2. Durable result storage and restart/scale-out failure tests.
3. Reconcile source provenance, readiness, voice refusal/recovery and minimum assurance policy.
4. Reduce readable answer storage; define transcript/key/biometric retention and deletion behavior.
5. Evaluate real-channel speaker and coercion accuracy, replay attacks and adversarial inputs.
6. Add durable incident correlation and operational alerts.

See [backend roadmap](backend-roadmap.md) for incremental delivery and the UI capabilities each item enables.

## References

- [NIST SP 800-63B-4](https://pages.nist.gov/800-63-4/sp800-63b.html)
- [ISO/IEC 30107-3:2023](https://www.iso.org/standard/79520.html)
- [ASVspoof research](https://www.asvspoof.org/)
- [GDPR](https://eur-lex.europa.eu/eli/reg/2016/679/oj)
- [EU AI Act](https://eur-lex.europa.eu/eli/reg/2024/1689/oj)
