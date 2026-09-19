# EntraGuard demo runbook

Current source flow, reviewed 20 September 2026. Use the deployed environment's hostnames and an authorized test account. Historical hardcoded demo passwords and `/sentinel` portal links are no longer applicable.

## What the demonstration proves

Treasury consumes EntraGuard as an application-level step-up service. Its sign-in and call use real integrations when configured. Its **payment portfolio is illustrative**, with no banking backend or payment-execution operation.

The documented estate uses Teams or ACS VoIP. No PSTN number is provisioned in the recorded demo. “My phone” means a handset web page connected through ACS, not a carrier phone call.

## Prepare

- Treasury hostname (root opens `/app`).
- Operator console `/live`, `/verification`, and `/` overview.
- A Microsoft work/school account and configured relying-party redirect URIs.
- For Teams: the user's tenant must permit the ACS resource, and the user must be signed into a reachable Teams client. See [Teams setup](teams-setup.md).
- For browser/handset: microphone permissions and the connected ACS endpoint.
- Confirm runtime modes: normally degraded risk tier, voice observation, scripted prompts unless realtime has been deliberately enabled.

Use `scripts/smoke-test.sh` for the configured deployment. Treat health/configuration checks separately from proof that a real call works.

## 1. Sign in and verify

1. Open Treasury and select **Sign in with Microsoft**. Use a real work/school account; no application-owned password form exists.
2. Select a verification endpoint:
   - **Microsoft Teams:** calls the signed-in directory identity.
   - **This computer:** use **Test this device**, then continue.
   - **My phone:** scan the QR code, connect the handset page and allow the microphone. Continue waits for presence.
3. Answer the call and enter the two digits shown on the requesting browser into the call keypad.
4. Answer the spoken questions the service can construct. Subject IDs, permissions and available data affect the question set; do not promise the same challenge on every channel or tenant.
5. Watch progress and media evidence. If it stalls, retain the verification ID and inspect diagnostics rather than assuming a successful call.

Optional voice enrollment is available during setup and in Settings. It requires consent and fresh Microsoft authentication with MFA evidence by default. Voice observation records a score without voice-driven refusal.

## 2. Explore the signed-in Treasury workspace

- **Overview:** sample totals and portfolio composition; all derived from the displayed illustrative records.
- **Payment runs:** search beneficiary/reference, filter status, sort, open a record and export the current filtered set as CSV. There is no approval or transfer endpoint.
- **Session security:** actual verification result, reported assurance, voice outcome, detected risk and explanation. “Not reported” means the service supplied no value.
- **Settings:** identity, voice enrollment/re-record/delete, explanation of verification methods, optional backup question and recent activity with refresh/outcome filtering.

Settings history comes from recent in-memory records, not a permanent ledger. Full navigation back to Treasury can require verification again; no persistent relying-party authorization session is implemented yet.

## 3. Demonstrate a refusal

For a controlled live rehearsal, have a second person coach the test user while the verification questions are being answered. The current Analyst prompt considers a second speaker on the user's own channel, and the coordinator checks while waiting for speech as well as at final adjudication.

Detection is model-dependent, not guaranteed by speaking one exact phrase. A correct code may still result in `BlockedCoercion`; the screen must show the actual returned reason. Do not present a failed detection as a successful intervention.

## 4. Monitored-call detection

Use calls deliberately routed through the monitored ACS identity, or the operator console's **Live calls → Test the pipeline** replay. The receive/transcribe/analyze/policy/action pipeline is distinct from Treasury's sample payments.

When warranted, show evidence, withheld actions and actual remediation outcomes. Sentinel incidents are visible in the overview and Azure Sentinel; there is no separate `/sentinel` page in the current frontend.

## Two simulators with different guarantees

| Entry point | What is real | What is supplied/simulated |
|---|---|---|
| Operator `POST /api/simulate` → media `/api/simulate` | Analyst model, policy, actuator and configured integrations | Transcript/audio source; unavailable call-control actions cannot affect a nonexistent call |
| Media `POST /api/verify/simulate` | Verification adjudicator, risk calculation, telemetry and broadcasts | Call, digits, Analyst assessment and optional voice assessment; **does not call the live Analyst** |

Verification scenarios include `pass`, `wrong-code`, `coerced`, `timeout` and `voice-mismatch`. Simulated application/session labels distinguish rehearsals where projected; do not assume every endpoint/table has a dedicated simulation flag.

Simulation can write real telemetry and, on the interception path, invoke configured remediation. `ENTRAGUARD_SHADOW_MODE=true` suppresses call/identity remediation but still allows notification and warranted incidents. It is not a switch that makes all paths side-effect free.

## Voice validation

Synthetic calibration and enrollment rehearsals are separate from human live-call tests. Verify in order:

1. Baseline call without an enrolled profile.
2. Consent and three-phrase enrollment on a real endpoint.
3. Another verification in observe mode, with actual score/outcome.
4. An authorized impostor rehearsal and noisy-channel checks.
5. User-initiated deletion and confirmation that the template is absent.

A successful synthetic rehearsal does not establish real-call false accept/reject rates. No voice-clone/PAD protection is implemented.

## Troubleshooting cues

| Symptom | Investigate |
|---|---|
| No Microsoft sign-in | RP client/scope, allowed redirect URI, popup restrictions, tenant consent |
| Teams never rings | Federation, licensing/voice enablement and registered endpoints; 480 can mean no reachable Teams client |
| ACS media error 8581 | Public HTTPS/WSS callback host and routing |
| Connected but no prompt | AI Services linkage; optional realtime session health and scripted fallback |
| Number enters twice / answer discarded | Prompt-round and echo diagnostics; retain call correlation IDs |
| Only weak/few questions | Profile/telemetry probes, Graph permissions, subject IDs, source data |
| Missing historical record | In-memory retention/restart; check KQL for ingested audit data |
| Empty new telemetry columns | Table **and** DCR schema plus a populated end-to-end row |

Historical benchmark numbers belong in [README](../README.md), not in promises about the current demonstration. Record the runtime configuration and actual outcomes of each rehearsal.
