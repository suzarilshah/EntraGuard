# Standards, compliance, and threat model

What EntraGuard claims, which published standard each claim answers to, and — at the end —
what it does not defend against. The last section is the important one. A security product
that lists only its strengths is describing marketing, not a threat model.

---

## 1. Where voice sits in the design

EntraGuard's verification call proves three different things, and they are not equal:

| Factor | What it proves | Strength |
|---|---|---|
| **Number match** — a two-digit code shown in the browser, keyed on the phone | The person holding the phone is looking at the browser session that is signing in | Strong. **A cloned voice cannot see the screen.** |
| **Live telemetry questions** — drawn from the user's own Entra sign-in logs, seconds old | The caller knows things only this account's real activity would tell them | Strong, and unpredictable by construction |
| **Voice comparison** — ECAPA-TDNN against an enrolled template | The speaker resembles the enrolled speaker | **Weakest.** Degrades over telephony, and has no anti-spoofing |
| **Coercion detection** — an LLM analyst over the live transcript | The user is not being *coached* through the call | The property no other MFA channel has |

The ordering matters and is deliberate. Voice biometrics is the part a demo notices and the
part an attacker defeats most easily; the number match is unglamorous and is what actually
resists a cloned voice. Any pitch that leads with the voiceprint is describing the weakest
component.

Coercion detection is the genuinely novel claim: every other MFA channel verifies *who* is
approving. EntraGuard also asks *whether they mean it*. Number matching cannot tell a free
approval from one made under instruction — that is what `BlockedCoercion` covers.

---

## 2. NIST SP 800-63B-4 (final, July 2025)

Revision 4 was finalised in July 2025 and tightened the biometric requirements materially.

| Requirement | EntraGuard | Status |
|---|---|---|
| Biometrics **must be paired with a possession factor** — never used alone | Voice is only ever scored *during* a call to an enrolled device, after a number match on that device | **Met.** Voice cannot pass anything by itself; see `VerificationAdjudicator` |
| **Presentation Attack Detection** mandatory at AAL3, recommended below | Not yet shipped. See §3 and the roadmap below | **Not met** — stated plainly rather than glossed |
| **FMR of 1 in 1000 or better** | Not yet measured on real telephony. Measured on synthesised voices: genuine 0.652–0.865, impostor −0.039–0.297 | **Unproven.** Thresholds are explicitly labelled optimistic in `VoiceDecision.cs` |
| Alternative methods for users who cannot or will not enrol | Verification works fully without a voiceprint; a missing profile is `NotAssessed` and never blocks | **Met**, and unit-tested |
| Sensor/endpoint authenticated before capture | Enrolment requires an Entra sign-in **with MFA proven from a validated ID token**, and the call goes to the enrolled Teams identity | **Met** |
| Rate limiting on authentication attempts | Three attempts per verification; ten verification starts per ten minutes; five enrolments per hour | **Met** |

**Stored secrets.** SP 800-63 rejects knowledge-based authentication using personal
information — it "does not constitute an acceptable secret for digital authentication". This
is why EntraGuard asks about *live sign-in telemetry* (where you signed in from an hour ago)
rather than static security questions. The answer is not memorised, not guessable from public
records, and expires on its own.

---

## 3. ISO/IEC 30107-3:2023 — presentation attack detection

The international standard for testing and reporting biometric PAD. It applies to voice
alongside face, fingerprint, iris and palm, and it defines the vocabulary this project uses
rather than inventing its own:

- **PAI** (presentation attack instrument) — the artefact used to attack: for voice, a
  recording, a replayed clip, or synthetic/converted speech.
- **APCER** — the proportion of attack presentations wrongly accepted as genuine.
- **BPCER** — the proportion of genuine presentations wrongly rejected.

**EntraGuard is not PAD-certified, and does not currently implement PAD.** SpeechBrain's
ECAPA-TDNN is a speaker-verification model with no anti-spoofing component: a high-quality
clone, or a recording of the enrolled speaker, scores *as* the enrolled speaker, because by
the model's definition it is. This is stated in the code
(`Agents/EnrollmentPhrases.cs`) and in `docs/architecture.md`, and is repeated here because
it is the single most important limitation of the voice factor.

Two mitigations, in order of value:

1. **Liveness through unpredictability** (planned; the primary defence). A phrase generated
   *during* the call cannot appear in a recording made before it. This is the layer that
   works even when a detector is fooled.
2. **A PAD model** (planned). An ASVspoof-trained detector (wav2vec2 or AASIST family) as a
   second head in the voiceprint sidecar, reported as a distinct signal rather than folded
   into the similarity score.

An honest caveat on the second: detectors trained on ASVspoof corpora degrade substantially
over real communication channels — codec, packet loss and narrowband filtering strip exactly
the high-frequency artefacts they key on. Published equal error rates do not survive a Teams
call. A PAD model is a layer, not a solution, and shipping one as *the* answer would repeat
the mistake of trusting VoxCeleb thresholds on telephony audio.

---

## 4. GDPR — Article 9 special category data

A voiceprint used to identify a person is biometric data processed for unique
identification, which Article 9 prohibits unless a condition applies. For a commercial
product the only workable basis is **explicit consent**: freely given, specific, informed,
unambiguous, and revocable.

| Obligation | Implementation |
|---|---|
| Explicit, informed consent before processing | A consent panel that states, before the checkbox, that a voiceprint cannot be changed after a breach, that audio is discarded, that verification works without it, and that it can be deleted at any time |
| Consent must be **versioned and recorded** | `ConsentVersion` + `ConsentAt` stored with the profile and written to `EntraGuard_Biometric_CL` |
| Right to erasure, without friction | "Delete my voice profile" in settings; immediate, user-initiated, no support ticket |
| Data minimisation | Raw audio is **never** persisted. Buffers are cleared after each phrase; only a 192-dimension template is stored, and it cannot be played back as speech |
| Security of processing | Template encrypted with AES-GCM above the storage layer; the storage account has `allowSharedKeyAccess: false`, so the data plane is identity-only |
| Auditability of consent and withdrawal | `EntraGuard_Biometric_CL` records enrolled / re-enrolled / failed / deleted with the consent version |

Consent obtained without those first four facts would not be *informed*, which is why the
panel states them rather than linking to them.

---

## 5. EU AI Act (Regulation 2024/1689)

Biometric provisions apply from **2 August 2026**. The Act distinguishes:

- **Biometric identification** (1-to-many: who is this, out of everyone?) — high-risk.
- **Biometric verification** (1-to-1: is this the person they claim to be?) — **not**
  high-risk.
- **Biometric categorisation** inferring sensitive characteristics — prohibited.

**EntraGuard performs 1-to-1 verification only.** It compares one utterance against one
enrolled template belonging to the already-identified account. It never searches a population,
never identifies an unknown speaker, and infers nothing about the speaker beyond similarity to
their own template. It therefore falls outside the high-risk category and squarely within the
same carve-out as phone-unlock and voice banking — while remaining fully subject to GDPR
Article 9.

Worth stating precisely, because "you're doing biometrics, so you're high-risk under the AI
Act" is the first challenge this design will receive, and it is wrong.

---

## 6. Threat model — what this does not defend against

### Defeated by design

| Attack | Why it fails |
|---|---|
| Stolen password | Verification call still required |
| Push-notification fatigue | There is no "approve" button; a two-digit code must be read from the browser |
| Help-desk social engineering (the Scattered Spider pattern) | The caller cannot see the victim's screen, so cannot supply the number match |
| Pre-recorded voice replay of enrolment phrases | Enrolment phrases are drawn per call from an 18-entry bank by CSPRNG |
| Coached victim reading the correct code aloud | Coercion detection refuses despite a correct code — `BlockedCoercion` |
| Attacker enrolling their own voice against your account | Enrolment requires sign-in **plus** proven MFA; identity comes from a validated token, never a request body |

### Not defended against — stated plainly

1. **A real-time voice clone, once the attacker also controls the enrolled device.** If an
   attacker holds the victim's phone *and* can synthesise their voice live, the voice factor
   contributes nothing. The number match and telemetry questions are what still stand.
2. **No PAD today.** A recording of the enrolled speaker scores as the enrolled speaker. See
   §3.
3. **A shoulder-surfer or an insider with screen access.** Someone who can see the browser can
   read the number. Voice may catch them; a coerced legitimate user reading it aloud is
   detected by the coercion analyst, not by the biometric.
4. **Thresholds not yet validated on real telephony.** 0.60/0.35 come from synthesised voices,
   which are cleaner and more mutually distinct than two colleagues sharing an accent. Real
   calls will narrow the margin. Until real-call scores accumulate in
   `EntraGuard_Verification_CL`, `VOICE_MODE=enforce` risks refusing genuine users.
5. **Refusal can be worn down.** Voice blocks a sign-in; it does not lock the account. An
   attacker can retry, subject to rate limits. Repeated `BlockedVoiceMismatch` rows for one
   subject should raise a Sentinel incident — not yet implemented.
6. **Media socket authentication.** `/ws/media/{sessionId}` is gated only by knowing an
   unguessable session id. A leaked id grants live call audio *and* the ability to inject
   synthesised speech into an authentication call. Session ids are 16 hex characters and are
   no longer disclosed by any API, but this is capability-by-obscurity and warrants a signed
   ACS callback.
7. **Single-region, in-process state.** Verification state lives in memory, so the deployment
   requires sticky sessions and does not survive a replica restart mid-call.

### Fixed, and worth recording because the fix was not obvious

- **The live match code was world-readable.** `GET /api/verify` returned it, unauthenticated,
  for every in-flight verification alongside the target's UPN, and the same projection was
  broadcast over SignalR to every connected client. An attacker needed no voice cloning at
  all. Closed by making redaction the default and disclosure opt-in via a per-verification
  capability token.
- **Voice scores never reached the SIEM.** The DCR stream declaration omitted the columns and
  the Logs Ingestion API silently drops undeclared columns, so the calibration dataset that
  §6.4 depends on did not exist while appearing to.

---

## 7. Roadmap against these standards

| Gap | Standard | Planned |
|---|---|---|
| No PAD | NIST 800-63B-4, ISO/IEC 30107-3 | Liveness challenge first, then an ASVspoof-trained detector in the sidecar |
| FMR unmeasured on telephony | NIST 800-63B-4 | Accumulate real-call scores, then set thresholds from the observed distribution |
| No alerting on repeated biometric refusals | — | Sentinel analytics rule on `BlockedVoiceMismatch` per subject |
| Media socket unauthenticated | — | Signed ACS callback or per-session secret in the callback URI |

---

## References

- [NIST SP 800-63B-4, Digital Identity Guidelines](https://www.nist.gov/publications/nist-sp-800-63b-4digital-identity-guidelines-authentication-and-authenticator) (final, July 2025)
- [ISO/IEC 30107-3:2023](https://www.iso.org/standard/79520.html) — Biometric presentation attack detection, testing and reporting
- [FIDO Alliance Biometrics Requirements v4.0](https://fidoalliance.org/specs/biometric/requirements/Biometrics-Requirements-v4.0-fd-20240522.html)
- [ASVspoof 5](https://arxiv.org/abs/2502.08857) — spoofing, deepfake and adversarial attack detection
- [Benchmarking audio deepfake detection in real communication scenarios](https://arxiv.org/pdf/2504.12423)
- [Biometrics in the EU: navigating the GDPR and AI Act](https://iapp.org/news/a/biometrics-in-the-eu-navigating-the-gdpr-ai-act) (IAPP)
- [Scattered Spider TTPs](https://www.group-ib.com/masked-actors/scatteredspider/) — the help-desk attack pattern this product targets
